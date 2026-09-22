using System.Diagnostics;
using System.Windows;
using DictationApp.Core.Settings;
using DictationApp.Dictionary;
using DictationApp.FirstRun;
using DictationApp.History;
using DictationApp.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DictationApp;

/// <summary>Opens the app's windows, one instance of each. Resolved lazily to avoid DI cycles with the tray.</summary>
public sealed class Shell(IServiceProvider services, AppPaths paths)
{
    private readonly Dictionary<Type, Window> _open = [];

    public void ShowSettings() => ShowSingle<SettingsWindow>();

    public void ShowHistory() => ShowSingle<HistoryWindow>();

    public void ShowFirstRun() => ShowSingle<FirstRunWindow>();

    public void ShowCorrection() => ShowSingle<CorrectionWindow>();

    public void OpenDataFolder() => OpenUri(paths.Root);

    public void OpenLogsFolder() => OpenUri(paths.LogDirectory);

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing sensible to do if the shell refuses.
        }
    }

    private void ShowSingle<T>() where T : Window
    {
        if (_open.TryGetValue(typeof(T), out var existing) && existing.IsLoaded)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = services.GetRequiredService<T>();
        _open[typeof(T)] = window;
        window.Closed += (_, _) => _open.Remove(typeof(T));
        window.Show();
        window.Activate();
    }
}
