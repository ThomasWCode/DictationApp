using DictationApp.Core.Abstractions;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DictationApp.Services;

/// <summary>
/// Daily Velopack update check against GitHub Releases. Only active when the app was installed by
/// Velopack (a plain <c>dotnet run</c> build is never "installed"). Updates are downloaded in the background
/// and applied on the next restart, never while a dictation is in progress.
/// </summary>
public sealed class UpdateCheckService(ISettingsStore settings, INotifier notifier, DictationStatusHub hub, ILogger<UpdateCheckService> logger) : BackgroundService
{
    public const string RepositoryUrl = "https://github.com/ThomasWCode/DictationApp";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public bool IsInstalled
    {
        get
        {
            try
            {
                return Manager.IsInstalled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                return Manager.CurrentVersion?.ToString() ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
            }
            catch (Exception)
            {
                return typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
            }
        }
    }

    public bool HasPendingUpdate => _pending is not null;

    private UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(RepositoryUrl, null, false));

    /// <summary>Interactive check from the tray menu. Returns a human-readable outcome.</summary>
    public async Task<string> CheckNowAsync(CancellationToken ct)
    {
        if (!IsInstalled)
        {
            return "Updates are only available for the installed version (not for a development build).";
        }

        try
        {
            var info = await Manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (info is null)
            {
                return $"You are on the latest version ({CurrentVersion}).";
            }

            await Manager.DownloadUpdatesAsync(info, cancelToken: ct);
            _pending = info;
            return $"Version {info.TargetFullRelease.Version} downloaded. It will be applied when you restart DictationApp.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed");
            return "Update check failed: " + ex.Message;
        }
    }

    public void ApplyPendingAndRestart()
    {
        if (_pending is null || hub.Current.IsActive)
        {
            return;
        }

        Manager.ApplyUpdatesAndRestart(_pending);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (settings.Current.CheckForUpdates && IsInstalled)
                {
                    var message = await CheckNowAsync(stoppingToken);
                    if (_pending is not null)
                    {
                        notifier.Toast("Update ready", message, ToastKind.Info);
                    }
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
