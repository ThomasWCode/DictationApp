using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.History;
using DictationApp.Core.Insertion;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DictationApp.Core.Tests;

/// <summary>End-to-end orchestration with in-memory fakes for every platform seam.</summary>
public sealed class DictationOrchestratorTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHotkey _hotkey = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSinkFactory _sinks = new();
    private readonly FakeTranscriber _transcriber = new();
    private readonly FakeForeground _foreground = new();
    private readonly FakeInserter _inserter = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FakePostProcessor _post = new();
    private readonly InMemoryHistory _history = new();
    private readonly JsonSettingsStore _settings;
    private readonly DictationStatusHub _hub = new();
    private readonly DictationOrchestrator _orchestrator;

    public DictationOrchestratorTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new JsonSettingsStore(Path.Combine(_dir, "settings.json"), NullLogger<JsonSettingsStore>.Instance);
        _orchestrator = new DictationOrchestrator(
            _hotkey,
            new FakeCaptureFactory(_capture),
            _sinks,
            new FakeTranscriberFactory(_transcriber),
            _foreground,
            _inserter,
            _clipboard,
            _notifier,
            _post,
            _history,
            _settings,
            _hub,
            new InsertionTextFormatter(_clock),
            new AppPaths(_dir),
            NullLogger<DictationOrchestrator>.Instance,
            _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _orchestrator.StopAsync(CancellationToken.None);
        _orchestrator.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task StartAsync()
    {
        await _orchestrator.StartAsync(CancellationToken.None);
        await WaitFor(() => _hotkey.Configured, "hotkey configured");
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    private Task WaitForState(DictationState state) => WaitFor(() => _orchestrator.State == state, "state " + state);

    private async Task WaitForIdleSession()
    {
        await WaitFor(() => _orchestrator.State == DictationState.Idle, "idle");
        if (_orchestrator.CurrentSessionTask is { } t)
        {
            await t.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Happy_path_inserts_cleaned_text_and_records_history()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        Assert.True(_capture.IsRunning);
        Assert.Equal(DictationState.Arming, _hub.Current.State);

        // Audio captured before the socket is ready must be buffered, not lost.
        _capture.Emit(1);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        await WaitFor(() => _transcriber.FramesReceived == 1, "buffered frame flushed");

        _capture.Emit(2);
        _transcriber.RaiseTurn(0, "hello world", endOfTurn: false);
        await WaitFor(() => _hub.Current.LiveText == "hello world", "live text");
        _transcriber.RaiseTurn(0, "Hello world.", endOfTurn: true, formatted: true);

        _clock.Advance(TimeSpan.FromSeconds(3));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.True(_transcriber.ShutdownCalled);
        Assert.Equal(2, _transcriber.FramesReceived);
        Assert.Equal("Hello world. [cleaned]", _inserter.LastText);
        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Inserted, record.Status);
        Assert.Equal("Hello world.", record.RawTranscript);
        Assert.Equal("Hello world. [cleaned]", record.InsertedText);
        Assert.Equal("fake-model", record.LlmModel);
        Assert.Equal("notepad", record.ProcessName);
        Assert.NotNull(record.AudioPath);
        Assert.True(_sinks.Last!.Completed);
        Assert.Equal(2 * AudioFrame.BytesPerFrame, _sinks.Last.BytesWritten);
        Assert.Empty(_notifier.Toasts);
        Assert.Equal(DictationState.Idle, _hub.Current.State);
    }

    [Fact]
    public async Task Short_tap_cancels_before_billing()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.True(_transcriber.Aborted);
        Assert.False(_transcriber.ShutdownCalled);
        Assert.True(_sinks.Last!.Discarded);
        Assert.Empty(_history.Records);
        Assert.Null(_inserter.LastText);
        Assert.False(_capture.IsRunning);
    }

    [Fact]
    public async Task Escape_discards_the_dictation()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Secret.", endOfTurn: true, formatted: true);
        _hotkey.Escape();
        await WaitForIdleSession();

        Assert.True(_transcriber.Aborted);
        Assert.True(_sinks.Last!.Discarded);
        Assert.Empty(_history.Records);
        Assert.Null(_inserter.LastText);
        Assert.Equal("Discarded", _hub.Current.Badge);
    }

    [Fact]
    public async Task Non_editable_target_goes_to_clipboard_with_toast()
    {
        _foreground.Context = _foreground.Context with { IsEditable = false, EditableReason = "uia:Button" };
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Copy me.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Null(_inserter.LastText);
        Assert.Equal("Copy me. [cleaned]", _clipboard.Text);
        Assert.Equal(RecordStatus.CopiedOnly, Assert.Single(_history.Records).Status);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Copied to clipboard");
    }

    [Fact]
    public async Task Elevated_target_is_never_pasted_into()
    {
        _foreground.Context = _foreground.Context with { IsElevated = true };
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Admin text.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Null(_inserter.LastText);
        Assert.Equal("Admin text. [cleaned]", _clipboard.Text);
        Assert.Contains(_notifier.Toasts, t => t.Message.Contains("elevated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Connect_failure_keeps_audio_and_records_failed()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _capture.Emit(1);
        _capture.Emit(2);
        _transcriber.FailConnect(new System.Net.WebSockets.WebSocketException("offline"));
        await WaitForIdleSession();

        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.NotNull(record.AudioPath);
        Assert.True(_sinks.Last!.Completed);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Network error");
        Assert.Equal("Failed", _hub.Current.Badge);
    }

    [Fact]
    public async Task Nothing_heard_writes_no_record_and_removes_audio()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Empty(_history.Records);
        Assert.True(_sinks.Last!.Discarded);
        Assert.Equal("Nothing heard", _hub.Current.Badge);
    }

    [Fact]
    public async Task Cleanup_failure_inserts_raw_text_with_badge()
    {
        _post.Fail = true;
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Raw stays raw.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Equal("Raw stays raw.", _inserter.LastText);
        Assert.Equal("cleanup skipped", _hub.Current.Badge);
        var record = Assert.Single(_history.Records);
        Assert.Equal(string.Empty, record.CleanedText);
        Assert.Equal("gateway-down", record.FailureReason);
    }

    [Fact]
    public async Task Arrow_keys_override_tone_and_level_for_this_dictation_only()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _hotkey.Arrow(ArrowDirection.Right);
        await WaitFor(() => _hub.Current.Tone == Tone.Formal, "tone override");
        _hotkey.Arrow(ArrowDirection.Up);
        await WaitFor(() => _hub.Current.CleanupLevel == CleanupLevel.Medium, "level override");
        _transcriber.RaiseTurn(0, "Text.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Equal(Tone.Formal, _post.LastRequest!.Tone);
        Assert.Equal(CleanupLevel.Medium, _post.LastRequest.Level);
        Assert.Equal(Tone.Formal, _history.Records[0].Tone);
        Assert.Equal(Tone.Neutral, _hub.Current.Tone); // back to defaults
    }

    [Fact]
    public async Task Second_chord_press_during_dictation_flashes_and_is_ignored()
    {
        await StartAsync();
        var flashes = 0;
        _hub.Flashed += () => flashes++;
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _hotkey.PressChord();
        await WaitFor(() => flashes == 1, "flash");
        Assert.Equal(DictationState.Recording, _orchestrator.State);
        _hotkey.Escape();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task Retry_re_streams_audio_and_copies_result()
    {
        await StartAsync();
        var wav = Path.Combine(_dir, "old.wav");
        await File.WriteAllBytesAsync(wav, new byte[100]);
        var id = await _history.InsertAsync(new DictationRecord { Status = RecordStatus.Failed, AudioPath = wav, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() });
        _transcriber.CompleteConnect();
        _transcriber.OnConnected = () => _transcriber.RaiseTurn(0, "Recovered text.", endOfTurn: true, formatted: true);
        _capture.CompleteOnStart = true;

        var ok = await _orchestrator.RetryAsync(id, CancellationToken.None);

        Assert.True(ok);
        var record = await _history.GetAsync(id);
        Assert.Equal(RecordStatus.CopiedOnly, record!.Status);
        Assert.Equal("Recovered text. [cleaned]", record.InsertedText);
        Assert.Equal("Recovered text. [cleaned]", _clipboard.Text);
    }

    // ---- fakes -------------------------------------------------------------------------------------

    private sealed class FakeHotkey : IHotkeyService
    {
        public event Action? ChordDown;

        public event Action? ChordUp;

        public event Action? EscapePressed;

        public event Action<ArrowDirection>? ArrowPressed;

        public bool IsChordHeld { get; private set; }

        public bool Enabled { get; set; } = true;

        public bool Configured { get; private set; }

        public void Configure(HotkeyChord chord, HotkeyMode mode) => Configured = true;

        public void PressChord()
        {
            IsChordHeld = true;
            ChordDown?.Invoke();
        }

        public void ReleaseChord()
        {
            IsChordHeld = false;
            ChordUp?.Invoke();
        }

        public void Escape() => EscapePressed?.Invoke();

        public void Arrow(ArrowDirection d) => ArrowPressed?.Invoke(d);
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public event Action<AudioFrame>? FrameCaptured;

        public event Action<Exception>? Faulted;

        public event Action? Completed;

        public bool IsRunning { get; private set; }

        public bool CompleteOnStart { get; set; }

        public void Start()
        {
            IsRunning = true;
            if (CompleteOnStart)
            {
                Task.Run(() =>
                {
                    Emit(1);
                    Completed?.Invoke();
                });
            }
        }

        public void Stop() => IsRunning = false;

        public void Dispose() => Stop();

        public void Emit(byte fill)
        {
            var bytes = new byte[AudioFrame.BytesPerFrame];
            Array.Fill(bytes, fill);
            FrameCaptured?.Invoke(new AudioFrame(bytes, 0.5f, TimeSpan.Zero));
        }

        public void Fault(Exception ex) => Faulted?.Invoke(ex);
    }

    private sealed class FakeCaptureFactory(FakeCapture capture) : IAudioCaptureFactory
    {
        public IAudioCapture CreateMicrophone(string? deviceId) => capture;

        public IAudioCapture CreateWavReplay(string wavPath, double speed) => capture;
    }

    private sealed class MemorySink(string path) : IAudioSink
    {
        public string Path { get; } = path;

        public long BytesWritten { get; private set; }

        public bool Completed { get; private set; }

        public bool Discarded { get; private set; }

        public void Write(ReadOnlySpan<byte> pcm16) => BytesWritten += pcm16.Length;

        public void Complete() => Completed = true;

        public void Discard() => Discarded = true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSinkFactory : IAudioSinkFactory
    {
        public MemorySink? Last { get; private set; }

        public IAudioSink Create(string path) => Last = new MemorySink(path);
    }

    private sealed class FakeTranscriber : IStreamingTranscriber
    {
        private TaskCompletionSource<BeginMessage> _connect = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<TurnMessage>? TurnReceived;

        public event Action<Exception>? Faulted;

        public bool IsConnected { get; private set; }

        public TimeSpan? ConnectLatency => TimeSpan.FromMilliseconds(5);

        public int FramesReceived { get; private set; }

        public bool ShutdownCalled { get; private set; }

        public bool Aborted { get; private set; }

        public Action? OnConnected { get; set; }

        public void CompleteConnect() => _connect.TrySetResult(new BeginMessage { Id = "s1" });

        public void FailConnect(Exception ex) => _connect.TrySetException(ex);

        public void RaiseTurn(int order, string text, bool endOfTurn, bool formatted = false) =>
            TurnReceived?.Invoke(new TurnMessage { TurnOrder = order, Utterance = text, Transcript = text, EndOfTurn = endOfTurn, TurnIsFormatted = formatted });

        public void Fault(Exception ex) => Faulted?.Invoke(ex);

        public async Task<BeginMessage> ConnectAsync(SessionOptions options, CancellationToken ct)
        {
            var begin = await _connect.Task.WaitAsync(ct);
            IsConnected = true;
            OnConnected?.Invoke();
            return begin;
        }

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            if (pcm16.Length == AudioFrame.BytesPerFrame && pcm16.Span[0] != 0)
            {
                FramesReceived++;
            }

            return ValueTask.CompletedTask;
        }

        public Task UpdateConfigurationAsync(IReadOnlyList<string>? keyterms, string? prompt, CancellationToken ct) => Task.CompletedTask;

        public Task<TerminationMessage?> ShutdownAsync(TimeSpan hardCap, CancellationToken ct)
        {
            ShutdownCalled = true;
            IsConnected = false;
            return Task.FromResult<TerminationMessage?>(new TerminationMessage { AudioDurationSeconds = 1.5, SessionDurationSeconds = 2 });
        }

        public Task AbortAsync()
        {
            Aborted = true;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _connect = new TaskCompletionSource<BeginMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTranscriberFactory(FakeTranscriber transcriber) : IStreamingTranscriberFactory
    {
        public IStreamingTranscriber Create() => transcriber;
    }

    private sealed class FakeForeground : IForegroundContextProvider
    {
        public ForegroundContext Context { get; set; } = new(42, 7, "notepad", "Untitled - Notepad", null, true, false, "uia:Edit");

        public ForegroundContext Capture() => Context;
    }

    private sealed class FakeInserter : ITextInserter
    {
        public string? LastText { get; private set; }

        public Task<InsertionResult> InsertAsync(string text, ForegroundContext target, PasteMode pasteMode, CancellationToken ct)
        {
            LastText = text;
            return Task.FromResult(new InsertionResult(InsertionOutcome.Inserted));
        }
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken ct = default)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync(CancellationToken ct = default) => Task.FromResult(Text);
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<(string Title, string Message, ToastKind Kind)> Toasts { get; } = [];

        public void Toast(string title, string message, ToastKind kind = ToastKind.Info, string? actionUri = null) => Toasts.Add((title, message, kind));
    }

    private sealed class FakePostProcessor : ITextPostProcessor
    {
        public bool Fail { get; set; }

        public PostProcessRequest? LastRequest { get; private set; }

        public Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Fail
                ? new PostProcessResult(rawTranscript, false, null, "gateway-down")
                : new PostProcessResult(rawTranscript + " [cleaned]", true, "fake-model", null, 50, 10));
        }
    }

    private sealed class InMemoryHistory : IHistoryRepository
    {
        private long _next = 1;

        public List<DictationRecord> Records { get; } = [];

        public Task InitialiseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<long> InsertAsync(DictationRecord record, CancellationToken ct = default)
        {
            record.Id = _next++;
            Records.Add(record);
            return Task.FromResult(record.Id);
        }

        public Task UpdateAsync(DictationRecord record, CancellationToken ct = default)
        {
            var i = Records.FindIndex(r => r.Id == record.Id);
            if (i >= 0)
            {
                Records[i] = record;
            }

            return Task.CompletedTask;
        }

        public Task<DictationRecord?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Records.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<DictationRecord>> SearchAsync(string? query, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DictationRecord>>(Records.ToArray());

        public Task<DictationRecord?> GetLatestAsync(CancellationToken ct = default) => Task.FromResult(Records.LastOrDefault());

        public Task DeleteAsync(long id, CancellationToken ct = default)
        {
            Records.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> DeleteOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ClearAudioOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HistoryStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new HistoryStats(Records.Count, 0, 0, 0));

        public Task<IReadOnlyList<string>> DeleteAllAsync(CancellationToken ct = default)
        {
            Records.Clear();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
