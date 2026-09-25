using System.IO;
using DictationApp.Cli;
using DictationApp.Core;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Dictionary;
using DictationApp.FirstRun;
using DictationApp.History;
using DictationApp.Overlay;
using DictationApp.Services;
using DictationApp.Settings;
using DictationApp.Tray;
using DictationApp.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace DictationApp;

/// <summary>Builds the Generic Host for both the tray app and the headless CLI modes.</summary>
public static class HostFactory
{
    public static IHost Build(CommandLineOptions options, bool ui)
    {
        var paths = AppPaths.Default;
        paths.EnsureDirectories();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "dictation-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(options);
        builder.Services.AddDictationCore(paths);
        builder.Services.AddDictationWindows();

        if (ui)
        {
            builder.Services.AddSingleton<INotifier, WpfNotifier>();
            builder.Services.AddSingleton<TrayIcon>();
            builder.Services.AddSingleton<Shell>();
            builder.Services.AddSingleton<FlowBarViewModel>();
            builder.Services.AddSingleton<FlowBarWindow>();
            builder.Services.AddTransient<SettingsViewModel>();
            builder.Services.AddTransient<SettingsWindow>();
            builder.Services.AddTransient<HistoryViewModel>();
            builder.Services.AddTransient<HistoryWindow>();
            builder.Services.AddTransient<FirstRunViewModel>();
            builder.Services.AddTransient<FirstRunWindow>();
            builder.Services.AddTransient<CorrectionViewModel>();
            builder.Services.AddTransient<CorrectionWindow>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateCheckService>());
            builder.Services.AddHostedService<HistoryRetentionService>();

            // Keeps the microphone initialised so a key press starts recording at once (tray app only).
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DictationApp.Windows.Audio.WarmMicrophone>());
        }
        else
        {
            builder.Services.AddSingleton<INotifier, ConsoleNotifier>();
        }

        // Available in both modes: the tray uses it as a hosted service, --check-updates calls it directly.
        builder.Services.AddSingleton<UpdateCheckService>();

        return builder.Build();
    }
}
