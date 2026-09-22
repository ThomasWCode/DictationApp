using System.IO;
using System.Windows;
using System.Windows.Controls;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Services;
using DictationApp.Windows.Hotkey;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DictationApp.Tray;

/// <summary>Tray-resident UI: icon, context menu, toasts. The app has no main window.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly DictationStatusHub _hub;
    private readonly ISettingsStore _settings;
    private readonly ILogger<TrayIcon> _logger;
    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _drawingIcon;
    private MenuItem? _startStopItem;
    private MenuItem? _pauseItem;
    private MenuItem? _restartToUpdateItem;
    private string? _pendingToastUri;
    private bool _dictating;

    public TrayIcon(IServiceProvider services, DictationStatusHub hub, ISettingsStore settings, ILogger<TrayIcon> logger)
    {
        _services = services;
        _hub = hub;
        _settings = settings;
        _logger = logger;
    }

    public void Show()
    {
        _drawingIcon = LoadIcon();
        _icon = new TaskbarIcon
        {
            ToolTipText = "DictationApp: hold " + _settings.Current.Hotkey + " to dictate",
            Icon = _drawingIcon,
            ContextMenu = BuildMenu(),
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.TrayMouseDoubleClick += (_, _) => Shell.ShowHistory();
        _icon.TrayBalloonTipClicked += (_, _) =>
        {
            if (_pendingToastUri is { } uri)
            {
                _pendingToastUri = null;
                Shell.OpenUri(uri);
            }
        };
        _icon.ForceCreate();
        _hub.Changed += OnStatusChanged;
        _settings.Changed += s => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_icon is not null)
            {
                _icon.ToolTipText = "DictationApp: hold " + s.Hotkey + " to dictate";
            }
        });
        _logger.LogInformation("Tray icon created");
    }

    public void ShowToast(string title, string message, ToastKind kind, string? actionUri)
    {
        if (_icon is null)
        {
            return;
        }

        _pendingToastUri = actionUri;
        var icon = kind switch
        {
            ToastKind.Success => NotificationIcon.Info,
            ToastKind.Warning => NotificationIcon.Warning,
            ToastKind.Error => NotificationIcon.Error,
            _ => NotificationIcon.Info,
        };
        try
        {
            _icon.ShowNotification(title, message, icon);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ShowNotification failed");
        }
    }

    public void Dispose()
    {
        _hub.Changed -= OnStatusChanged;
        _icon?.Dispose();
        _icon = null;
        _drawingIcon?.Dispose();
        _drawingIcon = null;
    }

    private Shell Shell => _services.GetRequiredService<Shell>();

    private DictationOrchestrator Orchestrator => _services.GetRequiredService<DictationOrchestrator>();

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        _startStopItem = Item("Start dictation", ToggleDictation);
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", () => Shell.ShowSettings()));
        menu.Items.Add(Item("History…", () => Shell.ShowHistory()));
        menu.Items.Add(Item("Correct last dictation…", () => Shell.ShowCorrection()));
        menu.Items.Add(new Separator());
        _pauseItem = new MenuItem { Header = "Pause hotkey", IsCheckable = true };
        _pauseItem.Click += (_, _) =>
        {
            var hotkeys = _services.GetRequiredService<HotkeyService>();
            hotkeys.Enabled = !_pauseItem.IsChecked;
            _hub.Update(s => s with { HotkeyEnabled = hotkeys.Enabled });
        };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(Item("Check for updates…", async () =>
        {
            var updates = _services.GetRequiredService<UpdateCheckService>();
            var result = await updates.CheckNowAsync(CancellationToken.None);
            ShowToast("DictationApp", result, ToastKind.Info, null);
        }));
        _restartToUpdateItem = Item("Restart to update", () =>
        {
            var updates = _services.GetRequiredService<UpdateCheckService>();
            if (!updates.RestartToUpdate())
            {
                ShowToast("DictationApp", "Finish the current dictation first.", ToastKind.Warning, null);
            }
        });
        _restartToUpdateItem.Visibility = Visibility.Collapsed;
        menu.Items.Add(_restartToUpdateItem);
        var updateService = _services.GetRequiredService<UpdateCheckService>();
        updateService.PendingChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _restartToUpdateItem.Header = $"Restart to update to {updateService.PendingVersion}";
            _restartToUpdateItem.Visibility = updateService.HasPendingUpdate ? Visibility.Visible : Visibility.Collapsed;
        });
        menu.Items.Add(Item("Open logs folder", () => Shell.OpenLogsFolder()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", () => Application.Current.Shutdown()));
        return menu;
    }

    private void ToggleDictation()
    {
        if (_dictating)
        {
            Orchestrator.SimulateChordUp();
        }
        else
        {
            Orchestrator.SimulateChordDown();
        }
    }

    private void OnStatusChanged(DictationStatus status)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _dictating = status.State is DictationState.Arming or DictationState.Recording;
            if (_startStopItem is not null)
            {
                _startStopItem.Header = _dictating ? "Stop dictation" : "Start dictation";
                _startStopItem.IsEnabled = status.State is DictationState.Idle or DictationState.Arming or DictationState.Recording;
            }

            if (_pauseItem is not null)
            {
                _pauseItem.IsChecked = !status.HotkeyEnabled;
            }
        });
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (resource is null)
        {
            return System.Drawing.SystemIcons.Application;
        }

        using var stream = resource.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }
}
