using System.Text.Json.Serialization;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.History;
using DictationApp.Core.Rules;

namespace DictationApp.Core.Settings;

/// <summary>Everything the user can configure. Serialised to <c>%LOCALAPPDATA%\ThomasWCode\DictationApp\settings.json</c>.</summary>
public sealed class AppSettings
{
    public const string SpeechModelPro = "universal-3-5-pro";
    public const string SpeechModelStandard = "universal-streaming";
    public const string DefaultLlmModel = "gemini-2.5-flash-lite";

    public int SchemaVersion { get; set; } = 1;

    /// <summary>DPAPI-protected, base64. Never the plaintext key.</summary>
    public string? ApiKeyProtected { get; set; }

    public string SpeechModel { get; set; } = SpeechModelPro;

    /// <summary>Chord text such as "Ctrl+Win". Parsed by <see cref="HotkeyChord.Parse"/>.</summary>
    public string Hotkey { get; set; } = HotkeyChord.Default.ToString();

    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Hold;

    /// <summary>WASAPI device ID, or null for the default communications device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public Tone DefaultTone { get; set; } = Tone.Neutral;

    public CleanupLevel DefaultCleanupLevel { get; set; } = CleanupLevel.Light;

    public string LlmModel { get; set; } = DefaultLlmModel;

    public List<string> LlmFallbackModels { get; set; } = ["gemini-2.5-flash", "claude-haiku-4-5-20251001"];

    public bool StoreAudio { get; set; } = true;

    public RetentionPolicy HistoryRetention { get; set; } = RetentionPolicy.Days14;

    public RetentionPolicy AudioRetention { get; set; } = RetentionPolicy.Days14;

    /// <summary>Start with Windows. Installed builds register the Run key themselves when this is true.</summary>
    public bool Autostart { get; set; } = true;

    /// <summary>
    /// When true, changing the tone or cleanup level during a dictation (arrow keys or chips) is remembered:
    /// it updates the app rule that matched, or the global defaults when no rule matched.
    /// </summary>
    public bool RememberStyleChanges { get; set; } = true;

    public PasteMode PasteMode { get; set; } = PasteMode.CtrlV;

    public int MaxDictationMinutes { get; set; } = 20;

    public bool ShowFlowBar { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    /// <summary>Comma-separated language codes for the streaming session, or null for auto/English.</summary>
    public string? LanguageCodes { get; set; }

    /// <summary>Debug: treat injected key events (SendInput from other processes) as real. Off by default.</summary>
    public bool AcceptInjectedKeys { get; set; }

    public List<DictionaryTerm> Dictionary { get; set; } = [];

    public List<AppRule> AppRules { get; set; } = DefaultAppRules.Seed();

    [JsonIgnore]
    public HotkeyChord HotkeyChord => HotkeyChord.TryParse(Hotkey, out var chord) ? chord : HotkeyChord.Default;

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKeyProtected);

    [JsonIgnore]
    public TimeSpan MaxDictationDuration => TimeSpan.FromMinutes(Math.Clamp(MaxDictationMinutes, 1, 180));

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.LlmFallbackModels = [.. LlmFallbackModels];
        copy.Dictionary = Dictionary.Select(d => d.Clone()).ToList();
        copy.AppRules = AppRules.Select(r => r.Clone()).ToList();
        return copy;
    }
}
