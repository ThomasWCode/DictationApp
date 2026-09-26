using System.Diagnostics;
using System.IO;
using DictationApp.Core.Abstractions;
using DictationApp.Core.History;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DictationApp.Services;

/// <summary>
/// Applies the retention policies at startup and hourly: records older than the history retention are
/// deleted (with their WAVs); audio older than the audio retention is deleted while the text stays.
/// Orphan WAVs (no record references them) older than a day are swept too. Before the first pass, recordings a
/// previous run left unfinished (killed, crashed, power cut) are recovered into History for Retry.
/// </summary>
public sealed class HistoryRetentionService(IHistoryRepository history, ISettingsStore settings, HistoryMaintenance maintenance, INotifier notifier, ILogger<HistoryRetentionService> logger, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private bool _recovered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            if (!_recovered)
            {
                _recovered = true;
                await RecoverInterruptedAsync(ct);
            }

            var s = settings.Current;
            var now = time.GetUtcNow();
            var deleted = 0;
            if (s.HistoryRetention.ToTimeSpan() is { } keep)
            {
                var paths = await history.DeleteOlderThanAsync(now - keep, ct);
                deleted += DeleteFiles(paths);
            }

            if (s.AudioRetention.ToTimeSpan() is { } keepAudio)
            {
                var paths = await history.ClearAudioOlderThanAsync(now - keepAudio, ct);
                deleted += DeleteFiles(paths);
            }

            deleted += await maintenance.SweepOrphansAsync(ct);
            if (deleted > 0)
            {
                logger.LogInformation("Retention removed {Count} audio files", deleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retention pass failed");
        }
    }

    /// <summary>Once per run, before the orphan sweep could delete them: files this run did not write.</summary>
    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        DateTimeOffset runStartedAt;
        using (var process = Process.GetCurrentProcess())
        {
            runStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }

        var recovered = await maintenance.RecoverInterruptedAsync(runStartedAt, ct);
        if (recovered > 0)
        {
            notifier.Toast(
                "Dictation recovered",
                recovered == 1
                    ? "A dictation interrupted when DictationApp last stopped is in History. Use Retry to transcribe it."
                    : $"{recovered} dictations interrupted when DictationApp last stopped are in History. Use Retry to transcribe them.",
                ToastKind.Info);
        }
    }

    private int DeleteFiles(IEnumerable<string> files)
    {
        var count = 0;
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete {File}", file);
            }
        }

        return count;
    }
}
