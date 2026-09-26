using System.Diagnostics;
using DictationApp.Core.Abstractions;
using DictationApp.Core.History;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DictationApp.Services;

/// <summary>
/// Applies the retention policies at startup and hourly (<see cref="HistoryMaintenance.RunRetentionPassAsync"/>):
/// records older than the history retention are deleted (with their WAVs); audio older than the audio retention is
/// deleted while the text stays; orphan WAVs older than a day are swept. The first successful pass of a run first
/// recovers recordings a previous run left unfinished (killed, crashed, power cut) into History for Retry.
/// </summary>
public sealed class HistoryRetentionService(HistoryMaintenance maintenance, INotifier notifier, ILogger<HistoryRetentionService> logger, TimeProvider time) : BackgroundService
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
            // Recovery is marked done only once a pass succeeds: a database error leaves it for the next pass.
            var pass = await maintenance.RunRetentionPassAsync(_recovered ? null : RunStartedAt(), ct);
            _recovered = true;
            if (pass.Recovered > 0)
            {
                notifier.Toast(
                    "Dictation recovered",
                    pass.Recovered == 1
                        ? "A dictation interrupted when DictationApp last stopped is in History. Use Retry to transcribe it."
                        : $"{pass.Recovered} dictations interrupted when DictationApp last stopped are in History. Use Retry to transcribe them.",
                    ToastKind.Info);
            }

            if (pass.RemovedFiles > 0)
            {
                logger.LogInformation("Retention removed {Count} audio files", pass.RemovedFiles);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retention pass failed");
        }
    }

    /// <summary>Files last written before this are from an earlier run; this run's may still be recording.</summary>
    private static DateTimeOffset RunStartedAt()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
