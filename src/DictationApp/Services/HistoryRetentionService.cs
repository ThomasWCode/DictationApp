using System.IO;
using DictationApp.Core.History;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DictationApp.Services;

/// <summary>
/// Applies the retention policies at startup and hourly: records older than the history retention are
/// deleted (with their WAVs); audio older than the audio retention is deleted while the text stays.
/// Orphan WAVs (no record references them) older than a day are swept too.
/// </summary>
public sealed class HistoryRetentionService(IHistoryRepository history, ISettingsStore settings, AppPaths paths, ILogger<HistoryRetentionService> logger, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

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

            deleted += SweepOrphans(now);
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

    private int SweepOrphans(DateTimeOffset now)
    {
        if (!Directory.Exists(paths.AudioDirectory))
        {
            return 0;
        }

        var cutoff = now - TimeSpan.FromDays(1);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var record in history.SearchAsync(null, 5000).GetAwaiter().GetResult())
            {
                if (record.AudioPath is not null)
                {
                    referenced.Add(Path.GetFullPath(record.AudioPath));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list referenced audio; skipping orphan sweep");
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(paths.AudioDirectory, "*.wav"))
        {
            try
            {
                if (!referenced.Contains(Path.GetFullPath(file)) && File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    count++;
                }
            }
            catch (IOException)
            {
                // In use; next pass.
            }
        }

        return count;
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
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not delete {File}", file);
            }
        }

        return count;
    }
}
