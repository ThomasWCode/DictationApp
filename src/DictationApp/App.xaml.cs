using System.Windows;
using System.Windows.Threading;
using DictationApp.Cli;
using DictationApp.Core.Settings;
using DictationApp.Overlay;
using DictationApp.Tray;
using DictationApp.Windows.Hotkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DictationApp;

public partial class App : Application
{
    private readonly CommandLineOptions _options;
    private IHost? _host;
    private ILogger<App>? _logger;

    /// <summary>Used only by WPF's generated entry point, which <see cref="Program"/> supersedes.</summary>
    public App()
        : this(CommandLineOptions.Parse([]))
    {
    }

    public App(CommandLineOptions options)
    {
        _options = options;
    }

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _host = HostFactory.Build(_options, ui: true);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("DictationApp {Version} starting", typeof(App).Assembly.GetName().Version);
            await _host.StartAsync();

            var settings = _host.Services.GetRequiredService<ISettingsStore>();
            var hotkeys = _host.Services.GetRequiredService<HotkeyService>();
            HotkeyDebug.Apply(hotkeys, settings, _options);
            settings.Changed += _ => HotkeyDebug.Apply(hotkeys, settings, _options);
            hotkeys.Start();

            _host.Services.GetRequiredService<TrayIcon>().Show();
            _host.Services.GetRequiredService<FlowBarWindow>().Attach();

            var shell = _host.Services.GetRequiredService<Shell>();
            var needsSetup = !settings.Current.FirstRunCompleted || (!settings.Current.HasApiKey && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable)));
            if (needsSetup && !_options.Minimized)
            {
                shell.ShowFirstRun();
            }
            else if (_options.ShowSettings)
            {
                shell.ShowSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show("DictationApp could not start:\n\n" + ex.Message, "DictationApp", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.Services.GetService<TrayIcon>()?.Dispose();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await _host.StopAsync(cts.Token);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown error");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
