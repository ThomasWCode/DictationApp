using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.History;
using DictationApp.Core.Rules;
using DictationApp.Core.Settings;
using DictationApp.Windows.Audio;
using DictationApp.Windows.Startup;
using Microsoft.Extensions.Logging;

namespace DictationApp.Settings;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly IHttpClientFactory _http;
    private readonly IAudioCaptureFactory _captures;
    private readonly IHistoryRepository _history;
    private readonly Shell _shell;
    private readonly ILogger<SettingsViewModel> _logger;
    private IAudioCapture? _micTest;

    // General
    [ObservableProperty] private FlowBarMode _flowBarMode;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _gitHubToken = string.Empty;
    [ObservableProperty] private bool _hasGitHubToken;
    [ObservableProperty] private int _maxDictationMinutes;
    [ObservableProperty] private string _languageCodes = string.Empty;

    // API
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _hasStoredKey;
    [ObservableProperty] private string _groqApiKey = string.Empty;
    [ObservableProperty] private bool _hasGroqKey;
    [ObservableProperty] private string _speechModel = AppSettings.SpeechModelPro;
    [ObservableProperty] private string _llmBaseUrl = AppSettings.DefaultLlmBaseUrl;
    [ObservableProperty] private string _llmModel = AppSettings.DefaultLlmModel;
    [ObservableProperty] private string _llmFallbackModels = string.Empty;
    [ObservableProperty] private string _testKeyStatus = string.Empty;
    [ObservableProperty] private bool _testingKey;

    // Hotkey
    [ObservableProperty] private string _hotkeyText = HotkeyChord.Default.ToString();
    [ObservableProperty] private bool _acceptInjectedKeys;
    [ObservableProperty] private string _hotkeyError = string.Empty;

    // Audio
    [ObservableProperty] private string? _selectedMicrophoneId;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private bool _micTestRunning;
    [ObservableProperty] private string _micTestStatus = string.Empty;

    // Style
    [ObservableProperty] private Tone _defaultTone;
    [ObservableProperty] private CleanupLevel _defaultCleanupLevel;
    [ObservableProperty] private bool _rememberStyleChanges;

    // Dictionary
    [ObservableProperty] private string _newTerm = string.Empty;
    [ObservableProperty] private DictionaryTerm? _selectedTerm;

    // Rules
    [ObservableProperty] private AppRule? _selectedRule;

    // History & privacy
    [ObservableProperty] private bool _storeAudio;
    [ObservableProperty] private bool _keepMicrophoneReady;
    [ObservableProperty] private RetentionPolicy _historyRetention;
    [ObservableProperty] private RetentionPolicy _audioRetention;
    [ObservableProperty] private string _historyStatus = string.Empty;

    public SettingsViewModel(
        ISettingsStore store,
        ISecretStore secrets,
        IHttpClientFactory http,
        IAudioCaptureFactory captures,
        IHistoryRepository history,
        Shell shell,
        ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _secrets = secrets;
        _http = http;
        _captures = captures;
        _history = history;
        _shell = shell;
        _logger = logger;
        Microphones = new ObservableCollection<AudioDeviceInfo>(AudioDeviceEnumerator.ListCaptureDevices());
        Microphones.Insert(0, new AudioDeviceInfo(string.Empty, "Default communications device", false));
        Load(store.Current);
    }

    public event Action? RequestClose;

    public ObservableCollection<AudioDeviceInfo> Microphones { get; }

    public ObservableCollection<DictionaryTerm> Dictionary { get; } = [];

    public ObservableCollection<AppRule> AppRules { get; } = [];

    public IReadOnlyList<string> SpeechModels { get; } = [AppSettings.SpeechModelPro, AppSettings.SpeechModelStandard];

    public IReadOnlyList<Tone> Tones { get; } = Enum.GetValues<Tone>();

    public IReadOnlyList<CleanupLevel> Levels { get; } = Enum.GetValues<CleanupLevel>();

    public IReadOnlyList<PasteMode> PasteModes { get; } = Enum.GetValues<PasteMode>();

    public IReadOnlyList<FlowBarMode> FlowBarModes { get; } = Enum.GetValues<FlowBarMode>();

    public IReadOnlyList<RetentionPolicy> RetentionPolicies { get; } = Enum.GetValues<RetentionPolicy>();

    public string SettingsPath => _store.FilePath;

    private void Load(AppSettings s)
    {
        FlowBarMode = s.FlowBarMode;
        HasGitHubToken = s.HasGitHubToken;
        Autostart = RunKeyAutostart.IsEnabled() || s.Autostart;
        CheckForUpdates = s.CheckForUpdates;
        MaxDictationMinutes = s.MaxDictationMinutes;
        LanguageCodes = s.LanguageCodes ?? string.Empty;
        HasStoredKey = s.HasApiKey;
        HasGroqKey = s.HasGroqKey;
        SpeechModel = s.SpeechModel;
        LlmBaseUrl = s.LlmBaseUrl;
        LlmModel = s.LlmModel;
        LlmFallbackModels = string.Join(", ", s.LlmFallbackModels);
        HotkeyText = s.Hotkey;
        AcceptInjectedKeys = s.AcceptInjectedKeys;
        SelectedMicrophoneId = s.MicrophoneDeviceId ?? string.Empty;
        DefaultTone = s.DefaultTone;
        DefaultCleanupLevel = s.DefaultCleanupLevel;
        RememberStyleChanges = s.RememberStyleChanges;
        StoreAudio = s.StoreAudio;
        KeepMicrophoneReady = s.KeepMicrophoneReady;
        HistoryRetention = s.HistoryRetention;
        AudioRetention = s.AudioRetention;
        Dictionary.Clear();
        foreach (var t in s.Dictionary.OrderByDescending(t => t.Starred).ThenBy(t => t.Term))
        {
            Dictionary.Add(t.Clone());
        }

        AppRules.Clear();
        foreach (var r in s.AppRules)
        {
            AppRules.Add(r.Clone());
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!HotkeyChord.TryParse(HotkeyText, out var chord))
        {
            HotkeyError = "Unrecognised hotkey. Use names such as Ctrl+Win, Ctrl+Alt, F8 or CapsLock.";
            return;
        }

        HotkeyError = string.Empty;
        StopMicTest();
        var key = ApiKey.Trim();
        var groqKey = GroqApiKey.Trim();
        var gitHubToken = GitHubToken.Trim();
        try
        {
            await _store.UpdateAsync(s =>
            {
                s.FlowBarMode = FlowBarMode;
                if (gitHubToken.Length > 0)
                {
                    s.GitHubTokenProtected = _secrets.Protect(gitHubToken);
                }

                s.Autostart = Autostart;
                s.CheckForUpdates = CheckForUpdates;
                s.MaxDictationMinutes = Math.Clamp(MaxDictationMinutes, 1, 180);
                s.LanguageCodes = string.IsNullOrWhiteSpace(LanguageCodes) ? null : LanguageCodes.Trim();
                if (key.Length > 0)
                {
                    s.ApiKeyProtected = _secrets.Protect(key);
                }

                if (groqKey.Length > 0)
                {
                    s.GroqApiKeyProtected = _secrets.Protect(groqKey);
                }

                s.SpeechModel = SpeechModel;
                s.LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? AppSettings.DefaultLlmBaseUrl : LlmBaseUrl.Trim();
                s.LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? AppSettings.DefaultLlmModel : LlmModel.Trim();
                s.LlmFallbackModels = LlmFallbackModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                s.Hotkey = chord.ToString();
                s.AcceptInjectedKeys = AcceptInjectedKeys;
                s.MicrophoneDeviceId = string.IsNullOrEmpty(SelectedMicrophoneId) ? null : SelectedMicrophoneId;
                s.DefaultTone = DefaultTone;
                s.DefaultCleanupLevel = DefaultCleanupLevel;
                s.RememberStyleChanges = RememberStyleChanges;
                s.StoreAudio = StoreAudio;
                s.KeepMicrophoneReady = KeepMicrophoneReady;
                s.HistoryRetention = HistoryRetention;
                s.AudioRetention = AudioRetention;
                s.Dictionary = Dictionary.Where(t => !string.IsNullOrWhiteSpace(t.Term)).Select(t => t.Clone()).ToList();
                s.AppRules = AppRules.Where(r => !string.IsNullOrWhiteSpace(r.ProcessGlob) || !string.IsNullOrWhiteSpace(r.UrlHost)).Select(r => r.Clone()).ToList();
            });

            try
            {
                RunKeyAutostart.Set(Autostart, Environment.ProcessPath ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not update autostart");
            }

            ApiKey = string.Empty;
            GroqApiKey = string.Empty;
            GitHubToken = string.Empty;
            HasStoredKey = _store.Current.HasApiKey;
            HasGroqKey = _store.Current.HasGroqKey;
            HasGitHubToken = _store.Current.HasGitHubToken;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
            MessageBox.Show("Could not save settings: " + ex.Message, "DictationApp", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        StopMicTest();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task TestKeyAsync()
    {
        var key = ApiKey.Trim();
        if (key.Length == 0 && HasStoredKey)
        {
            key = _secrets.Unprotect(_store.Current.ApiKeyProtected!) ?? string.Empty;
        }

        if (key.Length == 0)
        {
            key = Environment.GetEnvironmentVariable(SettingsApiKeyProvider.EnvironmentVariable)?.Trim() ?? string.Empty;
        }

        var groqKey = GroqApiKey.Trim();
        if (groqKey.Length == 0 && HasGroqKey)
        {
            groqKey = _secrets.Unprotect(_store.Current.GroqApiKeyProtected!) ?? string.Empty;
        }

        if (groqKey.Length == 0)
        {
            groqKey = Environment.GetEnvironmentVariable(SettingsApiKeyProvider.LlmEnvironmentVariable)?.Trim() ?? string.Empty;
        }

        if (key.Length == 0 && groqKey.Length == 0)
        {
            TestKeyStatus = "Enter at least one key first.";
            return;
        }

        TestingKey = true;
        TestKeyStatus = "Testing…";
        var parts = new List<string>();
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            // 1. AssemblyAI streaming: a short-lived token proves the key is valid for transcription.
            if (key.Length == 0)
            {
                parts.Add("AssemblyAI: no key (transcription will not work).");
            }
            else
            {
                using var tokenReq = new HttpRequestMessage(HttpMethod.Get, "https://streaming.assemblyai.com/v3/token?expires_in_seconds=60");
                tokenReq.Headers.Authorization = new AuthenticationHeaderValue(key);
                using var tokenResp = await client.SendAsync(tokenReq);
                parts.Add(tokenResp.IsSuccessStatusCode ? "AssemblyAI: OK." : $"AssemblyAI: rejected (HTTP {(int)tokenResp.StatusCode}).");
            }

            // 2. Groq: a two-token completion with the configured model proves key and model together.
            if (groqKey.Length == 0)
            {
                parts.Add("Groq: no key (cleanup levels fall back to raw text).");
            }
            else
            {
                var baseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? AppSettings.DefaultLlmBaseUrl : LlmBaseUrl.Trim().TrimEnd('/') + "/";
                var model = string.IsNullOrWhiteSpace(LlmModel) ? AppSettings.DefaultLlmModel : LlmModel.Trim();
                using var llmReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "chat/completions");
                llmReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", groqKey);
                var effort = Core.Cleanup.LlmPostProcessor.ReasoningEffortFor(model) is { } e ? ",\"reasoning_effort\":\"" + e + "\"" : string.Empty;
                llmReq.Content = new StringContent("{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"Reply OK\"}],\"max_tokens\":8" + effort + "}", System.Text.Encoding.UTF8, "application/json");
                using var llmResp = await client.SendAsync(llmReq);
                if (llmResp.IsSuccessStatusCode)
                {
                    parts.Add($"Groq: OK ({model}).");
                }
                else
                {
                    var body = await llmResp.Content.ReadAsStringAsync();
                    var reason = (int)llmResp.StatusCode switch
                    {
                        401 => "key rejected",
                        404 => "model not found; pick one from the free tier",
                        429 => "rate limited right now (free tier); it will work again shortly",
                        _ => $"HTTP {(int)llmResp.StatusCode}",
                    };
                    parts.Add($"Groq: {reason}. {Truncate(body, 120)}");
                }
            }

            TestKeyStatus = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            TestKeyStatus = string.Join(" ", parts) + " Test failed: " + ex.Message;
        }
        finally
        {
            TestingKey = false;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [RelayCommand]
    private void UseDefaultChord() => HotkeyText = HotkeyChord.Default.ToString();

    [RelayCommand]
    private void UseAlternativeChord() => HotkeyText = HotkeyChord.Alternative.ToString();

    [RelayCommand]
    private void TestMicrophone()
    {
        if (MicTestRunning)
        {
            StopMicTest();
            return;
        }

        try
        {
            _micTest = _captures.CreateMicrophone(string.IsNullOrEmpty(SelectedMicrophoneId) ? null : SelectedMicrophoneId);
            var maxPeak = 0f;
            _micTest.FrameCaptured += frame =>
            {
                maxPeak = Math.Max(maxPeak, frame.Peak);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    MicLevel = frame.Peak;
                    MicTestStatus = maxPeak < 0.001f ? "Listening… no signal yet" : $"Peak {maxPeak:P0}";
                });
            };
            _micTest.Faulted += ex => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                MicTestStatus = "Microphone error: " + ex.Message;
                StopMicTest();
            });
            _micTest.Start();
            MicTestRunning = true;
            MicTestStatus = "Listening…";
        }
        catch (Exception ex)
        {
            MicTestStatus = ex is MicrophoneAccessDeniedException
                ? "Blocked by Windows privacy settings (ms-settings:privacy-microphone)."
                : "Could not start: " + ex.Message;
            StopMicTest();
        }
    }

    public void StopMicTest()
    {
        _micTest?.Dispose();
        _micTest = null;
        MicTestRunning = false;
        MicLevel = 0;
    }

    [RelayCommand]
    private void AddTerm()
    {
        var term = NewTerm.Trim();
        if (term.Length == 0 || term.Length > DictionaryTerm.MaxLength)
        {
            return;
        }

        if (Dictionary.Any(t => string.Equals(t.Term, term, StringComparison.OrdinalIgnoreCase)))
        {
            NewTerm = string.Empty;
            return;
        }

        Dictionary.Insert(0, new DictionaryTerm { Term = term, AddedAt = DateTimeOffset.UtcNow });
        NewTerm = string.Empty;
    }

    [RelayCommand]
    private void RemoveTerm(DictionaryTerm? term)
    {
        if (term is not null)
        {
            Dictionary.Remove(term);
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        // A new rule starts from the current defaults so the user only changes what should differ for this app.
        var rule = new AppRule { ProcessGlob = "newapp", Tone = DefaultTone, Level = DefaultCleanupLevel };
        AppRules.Add(rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void RemoveRule(AppRule? rule)
    {
        if (rule is not null)
        {
            AppRules.Remove(rule);
        }
    }

    [RelayCommand]
    private void ResetRules()
    {
        // The default is no rules at all: every app follows the Style defaults.
        AppRules.Clear();
    }

    [RelayCommand]
    private async Task DeleteAllHistoryAsync()
    {
        if (MessageBox.Show("Delete every dictation and audio file? This cannot be undone.", "DictationApp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var files = await _history.DeleteAllAsync();
            var deleted = 0;
            foreach (var f in files)
            {
                try
                {
                    if (System.IO.File.Exists(f))
                    {
                        System.IO.File.Delete(f);
                        deleted++;
                    }
                }
                catch (System.IO.IOException)
                {
                }
            }

            HistoryStatus = $"History cleared ({deleted} audio files removed).";
        }
        catch (Exception ex)
        {
            HistoryStatus = "Could not clear history: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenDataFolder();
}
