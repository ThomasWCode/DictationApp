using DictationApp.Core.Abstractions;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DictationApp.Services;

/// <summary>
/// Velopack update flow against GitHub Releases:
/// <list type="number">
/// <item>Check (daily, or tray "Check for updates"): reads the releases feed. A private repository needs a
/// GitHub token (Settings › General), otherwise GitHub answers 404.</item>
/// <item>Download: the delta or full package goes to Velopack's <c>packages</c> folder in the background.</item>
/// <item>Apply: tray "Restart to update" applies it now; otherwise it is applied when the app next exits
/// (<see cref="ApplyPendingOnExit"/>), so the next launch, including the autostart at sign-in, is the new version.</item>
/// </list>
/// Only <c>%LOCALAPPDATA%\DictationApp\current</c> is replaced; settings, history and audio live under
/// <c>%LOCALAPPDATA%\ThomasWCode\DictationApp</c> and survive every update.
/// Only active when the app was installed by Velopack; a <c>dotnet run</c> build is never "installed".
/// </summary>
public sealed class UpdateCheckService : BackgroundService
{
    public const string RepositoryUrl = "https://github.com/ThomasWCode/DictationApp";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly INotifier _notifier;
    private readonly DictationStatusHub _hub;
    private readonly ILogger<UpdateCheckService> _logger;
    private UpdateManager? _manager;
    private string? _managerToken;
    private UpdateInfo? _pending;

    public UpdateCheckService(ISettingsStore settings, ISecretStore secrets, INotifier notifier, DictationStatusHub hub, ILogger<UpdateCheckService> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _notifier = notifier;
        _hub = hub;
        _logger = logger;
    }

    public event Action? PendingChanged;

    /// <summary>A token given on the command line for a one-off check (never stored).</summary>
    public string? TokenOverride { get; set; }

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
                return Manager.CurrentVersion?.ToString() ?? AssemblyVersion;
            }
            catch (Exception)
            {
                return AssemblyVersion;
            }
        }
    }

    public bool HasPendingUpdate => _pending is not null;

    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    private static string AssemblyVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private UpdateManager Manager
    {
        get
        {
            var token = ResolveToken();
            if (_manager is null || !string.Equals(_managerToken, token, StringComparison.Ordinal))
            {
                _manager = new UpdateManager(new GithubSource(RepositoryUrl, token, false));
                _managerToken = token;
            }

            return _manager;
        }
    }

    /// <summary>Interactive check. Downloads a newer release if there is one. Returns a human-readable outcome.</summary>
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

            _logger.LogInformation("Update {Version} available; downloading", info.TargetFullRelease.Version);
            await Manager.DownloadUpdatesAsync(info, cancelToken: ct);
            _pending = info;
            PendingChanged?.Invoke();
            return $"Version {info.TargetFullRelease.Version} downloaded. Use \"Restart to update\" in the tray menu, or it will be applied the next time DictationApp starts.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            var hint = ex.Message.Contains("404", StringComparison.Ordinal) && string.IsNullOrEmpty(ResolveToken())
                ? " The repository is private: add a GitHub token under Settings > General."
                : string.Empty;
            return "Update check failed: " + ex.Message + hint;
        }
    }

    /// <summary>Applies the downloaded update and restarts. Refused while a dictation is running.</summary>
    public bool RestartToUpdate()
    {
        if (_pending is null || _hub.Current.IsActive)
        {
            return false;
        }

        _logger.LogInformation("Applying update {Version} and restarting", _pending.TargetFullRelease.Version);
        Manager.ApplyUpdatesAndRestart(_pending);
        return true;
    }

    /// <summary>Called on exit: hands the downloaded update to Velopack so the next start is the new version.</summary>
    public void ApplyPendingOnExit()
    {
        if (_pending is null)
        {
            return;
        }

        try
        {
            Manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
            _logger.LogInformation("Update {Version} will be applied after exit", _pending.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not schedule the update");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_settings.Current.CheckForUpdates && IsInstalled)
                {
                    var message = await CheckNowAsync(stoppingToken);
                    if (_pending is not null)
                    {
                        _notifier.Toast("Update ready", message, ToastKind.Info);
                    }
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(TokenOverride))
        {
            return TokenOverride.Trim();
        }

        var stored = _settings.Current.GitHubTokenProtected;
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        var token = _secrets.Unprotect(stored);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
