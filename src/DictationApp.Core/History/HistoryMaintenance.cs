using System.Globalization;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.History;

/// <summary>
/// Keeps the recordings folder and History consistent: the hourly orphan sweep, "Delete all history", and the recovery
/// of recordings an interrupted run left behind. Used by the retention service and by Settings.
/// </summary>
public sealed class HistoryMaintenance(IHistoryRepository history, ISettingsStore settings, AppPaths paths, TimeProvider time, ILogger<HistoryMaintenance> logger)
{
    /// <summary>The hourly sweep leaves recordings with no History record alone for this long.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(1);

    /// <summary>"Delete all" spares a file written this recently: a dictation that is recording or being saved.</summary>
    public static readonly TimeSpan ActiveRecordingGrace = TimeSpan.FromMinutes(1);

    /// <summary>An interrupted recording shorter than this is dropped rather than recovered: nothing to retry.</summary>
    public static readonly TimeSpan MinimumRecovered = TimeSpan.FromMilliseconds(300);

    public const string RecoveredReason = "Interrupted: DictationApp stopped during this dictation (recovered at the next start)";

    /// <summary>
    /// Deletes recordings no History record refers to, once older than <see cref="OrphanGrace"/>. The referenced set
    /// comes from every record, not a page of the newest ones (with more than 5,000 records the audio of older ones was
    /// deleted). An empty History (a lost or recreated database) skips the sweep rather than treat every recording as
    /// an orphan; "Delete all" empties the folder itself. <paramref name="historyWasEmpty"/> is that judgement made
    /// before a retention pass deleted anything (checked now when null).
    /// </summary>
    public async Task<int> SweepOrphansAsync(CancellationToken ct, bool? historyWasEmpty = null)
    {
        if (!Directory.Exists(paths.AudioDirectory))
        {
            return 0;
        }

        HashSet<string> referenced;
        try
        {
            if (historyWasEmpty ?? (await history.GetStatsAsync(ct).ConfigureAwait(false)).Count == 0)
            {
                logger.LogDebug("History is empty; orphan sweep skipped");
                return 0;
            }

            referenced = (await history.GetReferencedAudioPathsAsync(ct).ConfigureAwait(false)).Select(FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not list referenced audio; skipping orphan sweep");
            return 0;
        }

        return DeleteRecordings(referenced, time.GetUtcNow() - OrphanGrace).Deleted;
    }

    /// <summary>
    /// "Delete all history": every record, every recording in the folder (not only those History refers to, so orphans
    /// left by a crash or a failed save go too) and the logs. A recording written within the last minute (a dictation
    /// in progress) and files Windows will not let go of are kept and counted.
    /// </summary>
    public async Task<DeleteAllResult> DeleteAllAsync(CancellationToken ct)
    {
        var referenced = await history.DeleteAllAsync(ct).ConfigureAwait(false);
        var removed = 0;
        var kept = 0;
        foreach (var file in referenced)
        {
            // Its record is gone, so the recording goes however recently it was saved; only playback or a retry can
            // still hold it open.
            switch (TryDelete(file, DateTime.MinValue, time.GetUtcNow()))
            {
                case DeleteOutcome.Deleted:
                    removed++;
                    break;
                case DeleteOutcome.Kept:
                    kept++;
                    break;
            }
        }

        var rest = DeleteRecordings(new HashSet<string>(StringComparer.OrdinalIgnoreCase), time.GetUtcNow() - ActiveRecordingGrace);
        var (logsRemoved, logsKept) = ClearLogs();
        logger.LogInformation("Delete all: {Removed} recordings removed, {Kept} kept, {Logs} log files cleared, {LogsKept} not", removed + rest.Deleted, kept + rest.Kept, logsRemoved, logsKept);
        return new DeleteAllResult(removed + rest.Deleted, kept + rest.Kept, logsRemoved, logsKept);
    }

    /// <summary>
    /// Recordings a previous run left unfinished (killed, crashed or lost power while recording or before saving) are
    /// repaired and listed in History as Failed, so Retry can transcribe them. Nothing is uploaded or pasted. Only files
    /// last written before <paramref name="runStartedAt"/> are touched, never the file of a dictation now running. A
    /// recording shorter than <see cref="MinimumRecovered"/>, or older than the retention settings keep, is deleted.
    /// Returns the number of recordings added to History.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(DateTimeOffset runStartedAt, CancellationToken ct)
    {
        if (!Directory.Exists(paths.AudioDirectory))
        {
            return 0;
        }

        var s = settings.Current;
        var now = time.GetUtcNow();
        var keepFor = new[] { s.HistoryRetention.ToTimeSpan(), s.AudioRetention.ToTimeSpan() }.Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(TimeSpan.MaxValue).Min();
        HashSet<string>? referenced = null;
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(paths.AudioDirectory, "*.wav"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= runStartedAt.UtcDateTime || !WavHeader.TryRepairUnfinished(file, out var pcmBytes))
                {
                    continue;
                }

                var duration = TimeSpan.FromSeconds(pcmBytes / (double)WavHeader.BytesPerSecond);
                var recordedAt = RecordingTime(file) ?? new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero);
                if (duration < MinimumRecovered || (keepFor != TimeSpan.MaxValue && recordedAt < now - keepFor))
                {
                    File.Delete(file);
                    logger.LogInformation("Deleted an interrupted recording ({Seconds:0.0} s) that is too short or past retention", duration.TotalSeconds);
                    continue;
                }

                referenced ??= (await history.GetReferencedAudioPathsAsync(ct).ConfigureAwait(false)).Select(FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (referenced.Contains(FullPath(file)))
                {
                    logger.LogInformation("Repaired an unfinished recording that History already lists ({Seconds:0.0} s)", duration.TotalSeconds);
                    continue;
                }

                await history.InsertAsync(
                    new DictationRecord
                    {
                        CreatedAt = recordedAt,
                        UpdatedAt = now,
                        Status = RecordStatus.Failed,
                        FailureReason = RecoveredReason,
                        AudioPath = file,
                        DurationMs = (int)duration.TotalMilliseconds,
                        Tone = s.DefaultTone,
                        Level = s.DefaultCleanupLevel,
                    },
                    ct).ConfigureAwait(false);
                added++;
                logger.LogInformation("Recovered an interrupted recording ({Seconds:0.0} s) into History for Retry", duration.TotalSeconds);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not check {File} for recovery", Path.GetFileName(file));
            }
        }

        return added;
    }

    /// <summary>The time in a recording's file name (yyyyMMdd-HHmmss, UTC), or null.</summary>
    internal static DateTimeOffset? RecordingTime(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.Length >= 15 && DateTimeOffset.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    private static string FullPath(string path) => Path.GetFullPath(path);

    private (int Deleted, int Kept) DeleteRecordings(HashSet<string> referenced, DateTimeOffset olderThan)
    {
        if (!Directory.Exists(paths.AudioDirectory))
        {
            return (0, 0);
        }

        var deleted = 0;
        var kept = 0;
        foreach (var file in Directory.EnumerateFiles(paths.AudioDirectory, "*.wav"))
        {
            if (referenced.Contains(FullPath(file)))
            {
                continue;
            }

            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            switch (TryDelete(file, written, olderThan))
            {
                case DeleteOutcome.Deleted:
                    deleted++;
                    break;
                case DeleteOutcome.Kept:
                    kept++;
                    break;
            }
        }

        return (deleted, kept);
    }

    private DeleteOutcome TryDelete(string file, DateTime writtenUtc, DateTimeOffset olderThan)
    {
        if (!File.Exists(file))
        {
            return DeleteOutcome.Gone;
        }

        if (writtenUtc >= olderThan.UtcDateTime)
        {
            return DeleteOutcome.Kept; // still being written or saved
        }

        try
        {
            File.Delete(file);
            return DeleteOutcome.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete {File}", Path.GetFileName(file));
            return DeleteOutcome.Kept;
        }
    }

    /// <summary>
    /// Deletes every log file. The file the logger has open today cannot be deleted, so it is emptied instead (the
    /// logger appends at the end of the file, wherever that is).
    /// </summary>
    private (int Cleared, int Kept) ClearLogs()
    {
        if (!Directory.Exists(paths.LogDirectory))
        {
            return (0, 0);
        }

        var cleared = 0;
        var kept = 0;
        foreach (var file in Directory.EnumerateFiles(paths.LogDirectory))
        {
            try
            {
                File.Delete(file);
                cleared++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.SetLength(0);
                    cleared++;
                }
                catch (Exception truncateEx) when (truncateEx is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(truncateEx, "Could not clear log {File} ({First})", Path.GetFileName(file), ex.Message);
                    kept++;
                }
            }
        }

        return (cleared, kept);
    }

    private enum DeleteOutcome
    {
        Deleted,
        Kept,
        Gone,
    }
}

/// <param name="RecordingsRemoved">Recordings deleted, referenced by History or not.</param>
/// <param name="RecordingsKept">Recordings in use (a dictation in progress, a file Windows would not release).</param>
/// <param name="LogsCleared">Log files deleted or emptied.</param>
/// <param name="LogsKept">Log files that could be neither deleted nor emptied.</param>
public sealed record DeleteAllResult(int RecordingsRemoved, int RecordingsKept, int LogsCleared, int LogsKept);
