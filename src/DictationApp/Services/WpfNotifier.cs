using System.Windows;
using DictationApp.Core.Abstractions;
using DictationApp.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DictationApp.Services;

/// <summary>Routes toasts to the tray icon on the UI thread. Resolves the tray lazily to avoid a DI cycle.</summary>
public sealed class WpfNotifier(IServiceProvider services, ILogger<WpfNotifier> logger) : INotifier
{
    public void Toast(string title, string message, ToastKind kind = ToastKind.Info, string? actionUri = null)
    {
        logger.LogInformation("Toast [{Kind}] {Title}: {Message}", kind, title, message);
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                services.GetRequiredService<TrayIcon>().ShowToast(title, message, kind, actionUri);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Toast failed");
            }
        });
    }
}
