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
    [ObservableProperty] private bool _showFlowBar;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private int _maxDictationMinutes;
    [ObservableProperty] private string _languageCodes = string.Empty;

    // API
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _hasStoredKey;
    [ObservableProperty] private string _speechModel = AppSettings.SpeechModelPro;
    [ObservableProperty] private string _llmModel = AppSettings.DefaultLlmModel;
    [ObservableProperty] private string _llmFallbackModels = string.Empty;
    [ObservableProperty] private string _testKeyStatus = string.Empty;
    [ObservableProperty] private bool _testingKey;

    // Hotkey
    [ObservableProperty] private string _hotkeyText = HotkeyChord.Default.ToString();
    [ObservableProperty] private HotkeyMode _hotkeyMode;
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

    public IReadOnlyList<HotkeyMode> HotkeyModes { get; } = Enum.GetValues<HotkeyMode>();

    public IReadOnlyList<RetentionPolicy> RetentionPolicies { get; } = Enum.GetValues<RetentionPolicy>();

    public string SettingsPath => _store.FilePath;

    private void Load(AppSettings s)
    {
        ShowFlowBar = s.ShowFlowBar;
        Autostart = RunKeyAutostart.IsEnabled() || s.Autostart;
        CheckForUpdates = s.CheckForUpdates;
        MaxDictationMinutes = s.MaxDictationMinutes;
        LanguageCodes = s.LanguageCodes ?? string.Empty;
        HasStoredKey = s.HasApiKey;
        SpeechModel = s.SpeechModel;
        LlmModel = s.LlmModel;
        LlmFallbackModels = string.Join(", ", s.LlmFallbackModels);
        HotkeyText = s.Hotkey;
        HotkeyMode = s.HotkeyMode;
        AcceptInjectedKeys = s.AcceptInjectedKeys;
        SelectedMicrophoneId = s.MicrophoneDeviceId ?? string.Empty;
        DefaultTone = s.DefaultTone;
        DefaultCleanupLevel = s.DefaultCleanupLevel;
        RememberStyleChanges = s.RememberStyleChanges;
        StoreAudio = s.StoreAudio;
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
        try
        {
            await _store.UpdateAsync(s =>
            {
                s.ShowFlowBar = ShowFlowBar;
                s.Autostart = Autostart;
                s.CheckForUpdates = CheckForUpdates;
                s.MaxDictationMinutes = Math.Clamp(MaxDictationMinutes, 1, 180);
                s.LanguageCodes = string.IsNullOrWhiteSpace(LanguageCodes) ? null : LanguageCodes.Trim();
                if (key.Length > 0)
                {
                    s.ApiKeyProtected = _secrets.Protect(key);
                }

                s.SpeechModel = SpeechModel;
                s.LlmModel = string.IsNullOrWhiteSpace(LlmModel) ? AppSettings.DefaultLlmModel : LlmModel.Trim();
                s.LlmFallbackModels = LlmFallbackModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                s.Hotkey = chord.ToString();
                s.HotkeyMode = HotkeyMode;
                s.AcceptInjectedKeys = AcceptInjectedKeys;
                s.MicrophoneDeviceId = string.IsNullOrEmpty(SelectedMicrophoneId) ? null : SelectedMicrophoneId;
                s.DefaultTone = DefaultTone;
                s.DefaultCleanupLevel = DefaultCleanupLevel;
                s.RememberStyleChanges = RememberStyleChanges;
                s.StoreAudio = StoreAudio;
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
            HasStoredKey = _store.Current.HasApiKey;
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
            TestKeyStatus = "Enter an API key first.";
            return;
        }

        TestingKey = true;
        TestKeyStatus = "Testing…";
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            // 1. Streaming: a short-lived token proves the key is valid for the core feature.
            using var tokenReq = new HttpRequestMessage(HttpMethod.Get, "https://streaming.assemblyai.com/v3/token?expires_in_seconds=60");
            tokenReq.Headers.Authorization = new AuthenticationHeaderValue(key);
            using var tokenResp = await client.SendAsync(tokenReq);
            if (!tokenResp.IsSuccessStatusCode)
            {
                TestKeyStatus = $"Key rejected by the streaming API (HTTP {(int)tokenResp.StatusCode}).";
                return;
            }

            // 2. LLM Gateway: a one-token completion tells us whether cleanup will work on this account.
            using var llmReq = new HttpRequestMessage(HttpMethod.Post, "https://llm-gateway.assemblyai.com/v1/chat/completions");
            llmReq.Headers.Authorization = new AuthenticationHeaderValue(key);
            var model = string.IsNullOrWhiteSpace(LlmModel) ? AppSettings.DefaultLlmModel : LlmModel.Trim();
            llmReq.Content = new StringContent("{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"Reply OK\"}],\"max_tokens\":2}", System.Text.Encoding.UTF8, "application/json");
            using var llmResp = await client.SendAsync(llmReq);
            if (llmResp.IsSuccessStatusCode)
            {
                TestKeyStatus = $"Key valid. Streaming OK, LLM cleanup OK ({model}).";
            }
            else
            {
                var body = await llmResp.Content.ReadAsStringAsync();
                var hint = body.Contains("does not have access", StringComparison.OrdinalIgnoreCase)
                    ? "this account has no LLM Gateway access, so cleanup levels fall back to raw text"
                    : $"HTTP {(int)llmResp.StatusCode}";
                TestKeyStatus = $"Key valid for streaming. LLM cleanup unavailable: {hint}.";
            }
        }
        catch (Exception ex)
        {
            TestKeyStatus = "Test failed: " + ex.Message;
        }
        finally
        {
            TestingKey = false;
        }
    }

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
    private void AddRule() => AppRules.Add(new AppRule { ProcessGlob = "newapp", Tone = Tone.Neutral, Level = CleanupLevel.Light });

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
        AppRules.Clear();
        foreach (var r in DefaultAppRules.Seed())
        {
            AppRules.Add(r);
        }
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
