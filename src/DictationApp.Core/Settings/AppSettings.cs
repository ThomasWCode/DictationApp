using System.Text.Json.Serialization;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.History;
using DictationApp.Core.Rules;

namespace DictationApp.Core.Settings;

/// <summary>How much of the overlay to show while dictating.</summary>
public enum FlowBarMode
{
    /// <summary>State, live transcript, level meter and tone/level chips.</summary>
    Full,

    /// <summary>A tiny pill with only the microphone level.</summary>
    Minimal,

    /// <summary>No overlay at all.</summary>
    Hidden,
}

/// <summary>Everything the user can configure. Serialised to <c>%LOCALAPPDATA%\ThomasWCode\DictationApp\settings.json</c>.</summary>
public sealed class AppSettings
{
    public const string SpeechModelPro = "universal-3-5-pro";
    public const string SpeechModelStandard = "universal-streaming";
    public const string DefaultLlmBaseUrl = "https://api.groq.com/openai/v1/";
    public const string DefaultLlmModel = "openai/gpt-oss-120b";

    public int SchemaVersion { get; set; } = 3;

    /// <summary>AssemblyAI key (transcription). DPAPI-protected, base64. Never the plaintext key.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>Groq key (cleanup and tone). DPAPI-protected, base64.</summary>
    public string? GroqApiKeyProtected { get; set; }

    /// <summary>Optional GitHub token so the updater can read releases of a private repository. DPAPI-protected.</summary>
    public string? GitHubTokenProtected { get; set; }

    public string SpeechModel { get; set; } = SpeechModelPro;

    /// <summary>Chord text such as "Ctrl+Win". Parsed by <see cref="HotkeyChord.Parse"/>.</summary>
    public string Hotkey { get; set; } = HotkeyChord.Default.ToString();

    /// <summary>WASAPI device ID, or null for the default communications device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public Tone DefaultTone { get; set; } = Tone.Neutral;

    public CleanupLevel DefaultCleanupLevel { get; set; } = CleanupLevel.Light;

    /// <summary>OpenAI-compatible chat completions base URL. Groq by default.</summary>
    public string LlmBaseUrl { get; set; } = DefaultLlmBaseUrl;

    public string LlmModel { get; set; } = DefaultLlmModel;

    /// <summary>Tried in order when the primary model fails or is rate-limited. Free-tier Groq models only.</summary>
    public List<string> LlmFallbackModels { get; set; } = ["qwen/qwen3.8-27b", "openai/gpt-oss-20b"];

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

    public FlowBarMode FlowBarMode { get; set; } = FlowBarMode.Full;

    public bool CheckForUpdates { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    /// <summary>Comma-separated language codes for the streaming session, or null for auto/English.</summary>
    public string? LanguageCodes { get; set; }

    /// <summary>Debug: treat injected key events (SendInput from other processes) as real. Off by default.</summary>
    public bool AcceptInjectedKeys { get; set; }

    public List<DictionaryTerm> Dictionary { get; set; } = [];

    /// <summary>Per-app overrides. Empty by default: every app follows the Style defaults until the user adds one.</summary>
    public List<AppRule> AppRules { get; set; } = [];

    [JsonIgnore]
    public HotkeyChord HotkeyChord => HotkeyChord.TryParse(Hotkey, out var chord) ? chord : HotkeyChord.Default;

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKeyProtected);

    [JsonIgnore]
    public bool HasGroqKey => !string.IsNullOrEmpty(GroqApiKeyProtected);

    [JsonIgnore]
    public bool HasGitHubToken => !string.IsNullOrEmpty(GitHubTokenProtected);

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
