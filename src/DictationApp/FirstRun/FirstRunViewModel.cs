using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Hotkey;

namespace DictationApp.FirstRun;

/// <summary>Three-step wizard: API key → microphone → hotkey. Each step can be skipped; Finish saves.</summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly IHttpClientFactory _http;
    private readonly IAudioCaptureFactory _captures;
    private readonly DictationStatusHub _hub;
    private readonly HotkeyService _hotkeys;
    private IAudioCapture? _mic;
    private bool _wasEnabled;

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _keyStatus = string.Empty;
    [ObservableProperty] private bool _keyValid;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private string _micStatus = "Say something…";
    [ObservableProperty] private bool _micOk;
    [ObservableProperty] private string _chordText = HotkeyChord.Default.ToString();
    [ObservableProperty] private string _hotkeyStatus = string.Empty;
    [ObservableProperty] private bool _hotkeyOk;

    public FirstRunViewModel(ISettingsStore store, ISecretStore secrets, IHttpClientFactory http, IAudioCaptureFactory captures, DictationStatusHub hub, HotkeyService hotkeys)
    {
        _store = store;
        _secrets = secrets;
        _http = http;
        _captures = captures;
        _hub = hub;
        _hotkeys = hotkeys;
        ChordText = store.Current.Hotkey;
        KeyValid = store.Current.HasApiKey;
        if (KeyValid)
        {
            KeyStatus = "A key is already stored.";
        }

        _hub.Changed += OnStatus;
    }

    public event Action? RequestClose;

    public bool IsStep0 => Step == 0;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public string NextLabel => Step == 2 ? "Finish" : "Next";

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep0));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(NextLabel));
        if (value == 1)
        {
            StartMic();
        }
        else
        {
            StopMic();
        }

        if (value == 2)
        {
            // Pause real dictation while the user tests the chord; we only want to see it fire.
            _wasEnabled = _hotkeys.Enabled;
            HotkeyStatus = "Press and hold " + ChordText + " now…";
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (Step < 2)
        {
            Step++;
            return;
        }

        await FinishAsync();
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    [RelayCommand]
    private async Task TestKeyAsync()
    {
        var key = ApiKey.Trim();
        if (key.Length == 0)
        {
            KeyStatus = "Paste your AssemblyAI API key first.";
            return;
        }

        KeyStatus = "Testing…";
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://streaming.assemblyai.com/v3/token?expires_in_seconds=60");
            req.Headers.Authorization = new AuthenticationHeaderValue(key);
            using var resp = await client.SendAsync(req);
            KeyValid = resp.IsSuccessStatusCode;
            KeyStatus = KeyValid ? "Key accepted." : $"Key rejected (HTTP {(int)resp.StatusCode}).";
        }
        catch (Exception ex)
        {
            KeyValid = false;
            KeyStatus = "Test failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void UseDefaultChord() => ChordText = HotkeyChord.Default.ToString();

    [RelayCommand]
    private void UseAlternativeChord() => ChordText = HotkeyChord.Alternative.ToString();

    partial void OnChordTextChanged(string value)
    {
        if (HotkeyChord.TryParse(value, out var chord))
        {
            _hotkeys.Configure(chord, _store.Current.HotkeyMode);
            HotkeyOk = false;
            HotkeyStatus = "Press and hold " + chord + " now…";
        }
    }

    private void OnStatus(DictationStatus status)
    {
        if (Step != 2)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (status.State is DictationState.Arming or DictationState.Recording)
            {
                HotkeyOk = true;
                HotkeyStatus = "Detected! Release the keys. (Text from this test is discarded.)";
            }
        });
    }

    private void StartMic()
    {
        try
        {
            _mic = _captures.CreateMicrophone(_store.Current.MicrophoneDeviceId);
            _mic.FrameCaptured += frame => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                MicLevel = frame.Peak;
                if (frame.Peak > 0.02f)
                {
                    MicOk = true;
                    MicStatus = "Microphone works.";
                }
            });
            _mic.Faulted += ex => Application.Current?.Dispatcher.BeginInvoke(() => MicStatus = "Microphone error: " + ex.Message);
            _mic.Start();
        }
        catch (Exception ex)
        {
            MicStatus = ex is MicrophoneAccessDeniedException ? "Blocked by Windows privacy settings. Open Settings > Privacy > Microphone." : "Could not start: " + ex.Message;
        }
    }

    private void StopMic()
    {
        _mic?.Dispose();
        _mic = null;
        MicLevel = 0;
    }

    private async Task FinishAsync()
    {
        StopMic();
        var key = ApiKey.Trim();
        var chord = HotkeyChord.TryParse(ChordText, out var c) ? c : HotkeyChord.Default;
        await _store.UpdateAsync(s =>
        {
            if (key.Length > 0)
            {
                s.ApiKeyProtected = _secrets.Protect(key);
            }

            s.Hotkey = chord.ToString();
            s.FirstRunCompleted = true;
        });
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        _hub.Changed -= OnStatus;
        StopMic();
        if (Step == 2)
        {
            _hotkeys.Enabled = _wasEnabled || _hotkeys.Enabled;
        }
    }
}
