using DictationApp.Core.Abstractions;
using DictationApp.Windows.Audio;
using DictationApp.Windows.Focus;
using DictationApp.Windows.Hotkey;
using DictationApp.Windows.Insertion;
using DictationApp.Windows.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DictationApp.Windows;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Win32/WASAPI/UIA adapters behind the Core abstractions. INotifier stays with the UI host.</summary>
    public static IServiceCollection AddDictationWindows(this IServiceCollection services)
    {
        services.TryAddSingleton<LowLevelKeyboardHook>();
        services.TryAddSingleton<HotkeyService>();
        services.TryAddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        services.TryAddSingleton<WarmMicrophone>();
        services.TryAddSingleton<IAudioCaptureFactory, WindowsAudioCaptureFactory>();
        services.TryAddSingleton<IAudioSinkFactory, WavFileSinkFactory>();
        services.TryAddSingleton<FocusedEditableDetector>();
        services.TryAddSingleton<IForegroundContextProvider, ForegroundContextProvider>();
        services.TryAddSingleton<ClipboardService>();
        services.TryAddSingleton<IClipboard>(sp => sp.GetRequiredService<ClipboardService>());
        services.TryAddSingleton<ITextInserter, ClipboardPasteInserter>();
        services.TryAddSingleton<ISecretStore, DpapiSecretStore>();
        services.TryAddSingleton<WavPlayer>();
        return services;
    }
}
