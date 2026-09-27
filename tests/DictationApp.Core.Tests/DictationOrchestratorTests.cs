using System.Collections.Concurrent;
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
    private readonly FakeCaptureFactory _captures;
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
        _captures = new FakeCaptureFactory(_capture);
        _settings = new JsonSettingsStore(Path.Combine(_dir, "settings.json"), NullLogger<JsonSettingsStore>.Instance);
        _orchestrator = new DictationOrchestrator(
            _hotkey,
            _captures,
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
        _hotkey.ReleaseChord(ChordRelease.Tap);
        await WaitForIdleSession();

        Assert.True(_transcriber.Aborted);
        Assert.False(_transcriber.ShutdownCalled);
        Assert.True(_sinks.Last!.Discarded);
        Assert.Empty(_history.Records);
        Assert.Null(_inserter.LastText);
        Assert.False(_capture.IsRunning);
    }

    [Fact]
    public async Task Tap_is_cancelled_even_when_the_session_began_first()
    {
        // The tap verdict comes from the key times, so a fast connection cannot turn it into a dictation.
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Accidental.", endOfTurn: true, formatted: true);
        _hotkey.ReleaseChord(ChordRelease.Tap);
        await WaitForIdleSession();

        Assert.True(_transcriber.Aborted);
        Assert.Null(_inserter.LastText);
        Assert.Empty(_history.Records);
        Assert.True(_sinks.Last!.Discarded);
        Assert.Null(_hub.Current.Badge);
    }

    [Fact]
    public async Task Slow_window_lookup_does_not_turn_a_hold_into_a_tap_or_a_tap_into_a_hold()
    {
        // The release is classified by the hotkey service when the key goes up; how long the start-up work (window
        // lookup, microphone) takes no longer matters.
        using var lookup = new ManualResetEventSlim();
        _foreground.OnCapture = () => lookup.Wait(TimeSpan.FromSeconds(5));
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _hotkey.ReleaseChord(ChordRelease.Hold);
        _clock.Advance(TimeSpan.FromSeconds(1));
        lookup.Set();
        await WaitFor(() => _orchestrator.State == DictationState.Finalising, "hold finalising");
        _transcriber.CompleteConnect();
        await WaitForIdleSession();

        Assert.True(_transcriber.ShutdownCalled);
        Assert.False(_transcriber.Aborted);
    }

    [Fact]
    public async Task A_dictation_that_fails_to_start_answers_the_press_before_its_idle()
    {
        // The hotkey service takes an Idle for a dictation's end only once that dictation has answered the press with
        // Arming; a microphone that cannot open must still answer first, or a tap or double tap stays latched.
        await StartAsync();
        _capture.StartError = new InvalidOperationException("no microphone");
        var seen = new ConcurrentQueue<DictationState>();
        _hub.Changed += s => seen.Enqueue(s.State);

        _hotkey.PressChord();
        await WaitFor(() => seen.Contains(DictationState.Idle), "the failed start ended");

        var states = seen.ToArray();
        Assert.Equal(DictationState.Arming, states[0]);
        Assert.Equal(DictationState.Idle, states[^1]);
        Assert.Equal("Failed", _hub.Current.Badge);
    }

    [Fact]
    public async Task A_release_during_a_slow_window_lookup_stops_the_microphone_at_once()
    {
        using var lookup = new ManualResetEventSlim();
        _foreground.OnCapture = () => lookup.Wait(TimeSpan.FromSeconds(5));
        await StartAsync();
        _hotkey.PressChord();
        await WaitFor(() => _capture.IsRunning, "microphone started");

        _hotkey.ReleaseChord(ChordRelease.Hold);
        await WaitFor(() => !_capture.IsRunning, "microphone stopped while the lookup is still running");

        lookup.Set();
        _transcriber.CompleteConnect();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task A_window_lookup_that_never_returns_does_not_hold_the_dictation()
    {
        // A hung app must not keep a dictation recording before its time limit is even running.
        using var hung = new ManualResetEventSlim();
        var lookups = 0;
        _foreground.OnCapture = () =>
        {
            Interlocked.Increment(ref lookups);
            hung.Wait(TimeSpan.FromSeconds(30));
        };
        await StartAsync();
        _hotkey.PressChord();
        await WaitFor(() => Volatile.Read(ref lookups) == 1, "the lookup started");
        _clock.Advance(DictationOrchestrator.LookupTimeout);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Still here.", endOfTurn: true, formatted: true);
        _hotkey.ReleaseChord();
        await WaitFor(() => Volatile.Read(ref lookups) == 2, "the insertion's lookup started");
        _clock.Advance(DictationOrchestrator.LookupTimeout);
        await WaitForIdleSession();
        hung.Set();

        Assert.Equal("Still here.", Assert.Single(_history.Records).RawTranscript);
    }

    [Fact]
    public async Task Deleting_a_dictation_stops_its_retry()
    {
        await StartAsync();
        var wav = Path.Combine(_dir, "old.wav");
        await File.WriteAllBytesAsync(wav, new byte[100]);
        var id = await _history.InsertAsync(new DictationRecord { Status = RecordStatus.Failed, AudioPath = wav, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() });
        _transcriber.CompleteConnect();
        _transcriber.OnConnected = () => _transcriber.RaiseTurn(0, "Deleted text.", endOfTurn: true, formatted: true);
        _capture.CompleteOnStart = true;
        _post.Gate = new TaskCompletionSource();
        var retry = _orchestrator.RetryAsync(id, CancellationToken.None);
        await WaitFor(() => _post.LastRequest is not null, "the retry is cleaning up");

        _orchestrator.CancelRetry(id);

        Assert.False(await retry.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(_clipboard.Text);
        Assert.DoesNotContain(_notifier.Toasts, t => t.Title.StartsWith("Retry", StringComparison.Ordinal));
        _post.Gate.SetResult();
    }

    [Fact]
    public async Task A_retry_whose_connection_times_out_reports_a_failure()
    {
        await StartAsync();
        var wav = Path.Combine(_dir, "old.wav");
        await File.WriteAllBytesAsync(wav, new byte[100]);
        var id = await _history.InsertAsync(new DictationRecord { Status = RecordStatus.Failed, AudioPath = wav, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() });
        // What the connection's own time limit raises: a cancellation, though nobody cancelled the Retry.
        _transcriber.FailConnect(new OperationCanceledException());

        Assert.False(await _orchestrator.RetryAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(_notifier.Toasts, t => t.Title == "Retry failed");
        Assert.Equal("retry: Connection timed out", (await _history.GetAsync(id, CancellationToken.None))!.FailureReason);
    }

    [Fact]
    public async Task A_press_while_a_retry_streams_flashes_busy_and_starts_nothing()
    {
        await StartAsync();
        var wav = Path.Combine(_dir, "old.wav");
        await File.WriteAllBytesAsync(wav, new byte[100]);
        var id = await _history.InsertAsync(new DictationRecord { Status = RecordStatus.Failed, AudioPath = wav, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() });
        _transcriber.CompleteConnect();
        _transcriber.OnConnected = () => _transcriber.RaiseTurn(0, "Recovered text.", endOfTurn: true, formatted: true);
        _capture.CompleteOnStart = true;
        _post.Gate = new TaskCompletionSource();
        var flashes = 0;
        _hub.Flashed += () => Interlocked.Increment(ref flashes);

        var retry = _orchestrator.RetryAsync(id, CancellationToken.None);
        await WaitFor(() => _post.LastRequest is not null, "the retry is cleaning up");
        _hotkey.PressChord();
        await WaitFor(() => Volatile.Read(ref flashes) == 1, "busy flash");
        Assert.Equal(DictationState.Idle, _orchestrator.State);

        _post.Gate.SetResult();
        Assert.True(await retry);
        _hotkey.ReleaseChord();
    }

    [Fact]
    public async Task A_tap_being_cancelled_publishes_nothing_once_a_new_press_has_replaced_it()
    {
        // The hotkey service reads the status stream: the replaced session's level updates and closing Idle must not look
        // like the new dictation answering its press and then ending, or the new press's tap would be dropped and it
        // would record on with nothing able to stop it.
        using var lookup = new ManualResetEventSlim();
        var lookups = 0;
        _foreground.OnCapture = () =>
        {
            if (Interlocked.Increment(ref lookups) == 1)
            {
                lookup.Wait(TimeSpan.FromSeconds(5)); // the tap's session starts slowly
            }
        };
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        await WaitFor(() => _hub.Current.StartedAt is not null, "the tap's dictation published");
        var tapStartedAt = _hub.Current.StartedAt;
        _hotkey.ReleaseChord(ChordRelease.Tap);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _hotkey.PressChord();
        await Task.Delay(50);

        var seen = new ConcurrentQueue<DictationStatus>();
        _hub.Changed += seen.Enqueue;
        _capture.Emit(1); // the tap's microphone is still running
        lookup.Set();
        await WaitFor(() => seen.Any(s => s.State == DictationState.Arming && s.StartedAt is { } at && at != tapStartedAt), "the new dictation answered");

        Assert.DoesNotContain(seen, s => s.State == DictationState.Idle);
        Assert.DoesNotContain(seen, s => s.StartedAt == tapStartedAt);
        _hotkey.ReleaseChord(ChordRelease.Tap);
        await WaitForIdleSession();
    }

    [Fact]
    public async Task A_new_press_while_a_tap_is_being_cancelled_starts_a_dictation_instead_of_flashing_busy()
    {
        using var lookup = new ManualResetEventSlim();
        var lookups = 0;
        _foreground.OnCapture = () =>
        {
            if (Interlocked.Increment(ref lookups) == 1)
            {
                lookup.Wait(TimeSpan.FromSeconds(5)); // the tap's session is stuck in a slow window lookup
            }
        };
        var flashes = 0;
        _hub.Flashed += () => flashes++;
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _hotkey.ReleaseChord(ChordRelease.Tap);
        await Task.Delay(50);
        _hotkey.PressChord();                     // a new press after the double-tap window
        await Task.Delay(50);
        lookup.Set();

        await WaitFor(() => Volatile.Read(ref lookups) == 2, "second dictation started");
        Assert.Equal(0, flashes);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Second.", endOfTurn: true, formatted: true);
        _hotkey.ReleaseChord();
        await WaitForIdleSession();
        Assert.Equal("Second. [cleaned]", _inserter.LastText);
    }

    [Fact]
    public async Task Release_before_begin_stops_the_microphone_at_once()
    {
        // Nothing said after the release is recorded or uploaded, even while the connection is still being set up.
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _capture.Emit(1);
        _hotkey.ReleaseChord();
        await WaitFor(() => !_capture.IsRunning, "microphone stopped at the release");
        Assert.False(_transcriber.IsConnected);
        _capture.Emit(2);                         // a real microphone delivers nothing after Stop
        _transcriber.RaiseTurnsOnConnect("Pre-release words.");
        _transcriber.CompleteConnect();
        await WaitForIdleSession();

        Assert.Equal(1, _transcriber.FramesReceived);
        Assert.Equal(AudioFrame.BytesPerFrame, _sinks.Last!.BytesWritten);
        Assert.Equal("Pre-release words. [cleaned]", _inserter.LastText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_last_partial_frame_delivered_by_stop_reaches_the_stream_and_the_recording(bool releaseBeforeBegin)
    {
        _capture.FrameOnStop = 9;
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _capture.Emit(1);
        if (!releaseBeforeBegin)
        {
            _transcriber.CompleteConnect();
            await WaitForState(DictationState.Recording);
        }

        _transcriber.RaiseTurnsOnConnect("Last word.");
        _hotkey.ReleaseChord();
        if (releaseBeforeBegin)
        {
            await WaitFor(() => !_capture.IsRunning, "stopped");
            _transcriber.CompleteConnect();
        }
        else
        {
            _transcriber.RaiseTurn(0, "Last word.", endOfTurn: true, formatted: true);
        }

        await WaitForIdleSession();
        Assert.Equal(2, _transcriber.FramesReceived);
        Assert.Equal(2 * AudioFrame.BytesPerFrame, _sinks.Last!.BytesWritten);
    }

    [Fact]
    public async Task Escape_then_release_during_a_slow_start_discards()
    {
        // Both arrive before the session looks: the discard wins over the release.
        using var lookup = new ManualResetEventSlim();
        _foreground.OnCapture = () => lookup.Wait(TimeSpan.FromSeconds(5));
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _hotkey.Escape();
        _hotkey.ReleaseChord();
        await Task.Delay(50);
        lookup.Set();
        await WaitForIdleSession();

        Assert.True(_transcriber.Aborted);
        Assert.Empty(_history.Records);
        Assert.Null(_inserter.LastText);
        Assert.Equal("Discarded", _hub.Current.Badge);
    }

    [Fact]
    public async Task Escape_after_the_release_is_ignored_and_the_dictation_finishes()
    {
        _post.Gate = new TaskCompletionSource();
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Keep me.", endOfTurn: true, formatted: true);
        _hotkey.ReleaseChord();
        await WaitForState(DictationState.PostProcessing);
        _hotkey.Escape();
        await Task.Delay(50);
        _post.Gate.SetResult();
        await WaitForIdleSession();

        Assert.Equal("Keep me. [cleaned]", _inserter.LastText);
        Assert.Equal(RecordStatus.Inserted, Assert.Single(_history.Records).Status);
    }

    [Fact]
    public async Task Escape_after_the_time_cap_is_ignored_and_the_dictation_finishes()
    {
        await _settings.UpdateAsync(s => s.MaxDictationMinutes = 1);
        _post.Gate = new TaskCompletionSource();
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "A long one.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForState(DictationState.PostProcessing);
        _hotkey.Escape();
        await Task.Delay(50);
        _post.Gate.SetResult();
        await WaitForIdleSession();

        Assert.Equal("A long one. [cleaned]", _inserter.LastText);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Dictation limit");
    }

    [Fact]
    public async Task Closing_the_app_mid_dictation_keeps_it_for_retry()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _capture.Emit(1);
        _transcriber.RaiseTurn(0, "half a thought", endOfTurn: false);
        await WaitFor(() => _hub.Current.LiveText == "half a thought", "live text");

        await _orchestrator.StopAsync(CancellationToken.None);

        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.Equal(DictationOrchestrator.InterruptedReason, record.FailureReason);
        Assert.Equal("half a thought", record.RawTranscript);
        Assert.NotNull(record.AudioPath);
        Assert.True(_sinks.Last!.Completed);
        Assert.Null(_inserter.LastText);
        Assert.DoesNotContain(_notifier.Toasts, t => t.Title == "Dictation limit");
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
    public async Task Sessions_wait_for_a_real_pause_before_ending_a_turn()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitFor(() => _transcriber.Options is not null, "connect");

        // AssemblyAI's 100 ms default split sentences at every pause to think.
        Assert.Equal(DictationOrchestrator.DictationMinTurnSilenceMs, _transcriber.Options!.MinTurnSilenceMs);
        Assert.Equal(DictationOrchestrator.DictationMaxTurnSilenceMs, _transcriber.Options.MaxTurnSilenceMs);
        Assert.Contains("min_turn_silence=1000", _transcriber.Options.BuildQueryString());
        _hotkey.Escape();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task No_leading_space_when_the_field_is_empty_again()
    {
        await StartAsync();
        await DictateAsync("First message.");
        Assert.StartsWith("First message.", _inserter.LastText);

        // The message was sent: the control reports an empty field, so the memory of the last insertion is ignored.
        _foreground.TextBeforeCaret = "";
        await DictateAsync("Second message.");
        Assert.StartsWith("Second message.", _inserter.LastText);
        Assert.Equal(_foreground.Context.WindowHandle, _foreground.CaretTarget?.WindowHandle);

        // Unreadable controls keep the old behaviour.
        _foreground.TextBeforeCaret = null;
        await DictateAsync("Third.");
        Assert.StartsWith(" Third.", _inserter.LastText);
    }

    [Fact]
    public async Task A_paste_that_could_not_be_confirmed_leaves_the_text_on_the_clipboard_too()
    {
        _inserter.Result = new InsertionResult(InsertionOutcome.Inserted, "Pasted into notepad; also on the clipboard", LeftOnClipboard: true);
        await StartAsync();
        await DictateAsync("Unsure.");

        Assert.Equal(RecordStatus.Inserted, Assert.Single(_history.Records).Status);
        Assert.Equal("also copied", _hub.Current.Badge);
        Assert.Empty(_notifier.Toasts);
    }

    [Fact]
    public async Task A_paste_the_target_refused_is_copied_with_a_toast_and_forgets_the_spacing_memory()
    {
        await StartAsync();
        await DictateAsync("First.");                  // remembered: the next text would get a leading space
        _inserter.Result = new InsertionResult(InsertionOutcome.CopiedOnly, "Could not bring notepad to the front, text copied");
        await DictateAsync("Second.");
        Assert.Equal(RecordStatus.CopiedOnly, _history.Records[^1].Status);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Copied to clipboard" && t.Message.Contains("front", StringComparison.Ordinal));

        _inserter.Result = new InsertionResult(InsertionOutcome.Inserted);
        await DictateAsync("Third.");
        Assert.Equal("Third. [cleaned]", _inserter.LastText); // "Second." never landed there, so no leading space
    }

    [Fact]
    public async Task A_failed_paste_puts_the_text_on_the_clipboard_and_says_so()
    {
        _inserter.Result = new InsertionResult(InsertionOutcome.Failed, "Clipboard busy");
        await StartAsync();
        await DictateAsync("Keep me.");

        Assert.Equal("Keep me. [cleaned]", _clipboard.Text);
        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.Contains("Clipboard busy", record.FailureReason);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Could not insert" && t.Message.Contains("copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Re_insert_uses_the_same_checks_and_toasts_as_a_live_dictation()
    {
        await StartAsync();
        _foreground.TextBeforeCaret = "";
        var ok = await _orchestrator.ReInsertAsync(" Earlier text.", CancellationToken.None);
        Assert.Equal(InsertionOutcome.Inserted, ok.Outcome);
        Assert.Equal("Earlier text.", _inserter.LastText); // the stored leading space is decided afresh

        _foreground.Context = _foreground.Context with { IsEditable = false, EditableReason = "uia:Button" };
        var copied = await _orchestrator.ReInsertAsync("Earlier text.", CancellationToken.None);
        Assert.Equal(InsertionOutcome.CopiedOnly, copied.Outcome);
        Assert.Equal("Earlier text.", _clipboard.Text);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Copied to clipboard");
        Assert.Empty(_history.Records); // the record is not touched
    }

    [Fact]
    public async Task Re_insert_waits_for_a_running_dictation()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);

        var result = await _orchestrator.ReInsertAsync("Text.", CancellationToken.None);

        Assert.Equal(InsertionOutcome.Failed, result.Outcome);
        Assert.Contains(_notifier.Toasts, t => t.Message.Contains("Finish the current dictation", StringComparison.Ordinal));
        _hotkey.Escape();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task The_recording_is_finished_only_after_its_record_is_saved()
    {
        // Until then its header keeps the "unfinished" marker, so a crash during cleanup or insertion leaves a file the
        // next start recovers into History instead of an orphan the sweep deletes.
        _sinks.Probe = () => _history.Records.Count;
        await StartAsync();
        await DictateAsync("Saved first.");

        Assert.Equal(1, _sinks.Last!.ProbeAtComplete);
    }

    [Fact]
    public async Task A_recurring_cleanup_problem_is_reported_once_per_run()
    {
        // Even when a fallback model covered for it: otherwise the preferred model could silently stop being used.
        _post.Problem = "The Groq key may not use a.";
        await StartAsync();
        await DictateAsync("One.");
        await DictateAsync("Two.");

        Assert.Single(_notifier.Toasts, t => t.Message == "The Groq key may not use a.");
        Assert.All(_history.Records, r => Assert.Null(r.FailureReason));
    }

    private async Task DictateAsync(string text)
    {
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, text, endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task Microphone_and_connection_start_before_the_window_lookup()
    {
        bool? micRunning = null;
        bool? connecting = null;
        _foreground.OnCapture = () =>
        {
            micRunning ??= _capture.IsRunning;
            connecting ??= _transcriber.ConnectStarted;
        };
        await StartAsync();
        _hotkey.PressChord();
        await WaitFor(() => micRunning is not null, "window lookup");

        // Anything said before the microphone starts is lost, so the lookup (UI Automation, browser URL) comes after.
        Assert.True(micRunning);
        Assert.True(connecting);
        _hotkey.Escape();
        await WaitForIdleSession();
    }

    [Fact]
    public async Task Socket_failure_during_shutdown_keeps_audio_as_failed_instead_of_pasting()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _capture.Emit(1);
        _transcriber.RaiseTurn(0, "the start of a longer", endOfTurn: false);
        _transcriber.ShutdownError = new System.Net.WebSockets.WebSocketException("Streaming connection lost during shutdown");
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Null(_inserter.LastText);
        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.NotNull(record.AudioPath);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Network error");
    }

    [Fact]
    public async Task Fault_after_release_without_summary_is_not_pasted()
    {
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _capture.Emit(1);
        _transcriber.RaiseTurn(0, "cut off", endOfTurn: false);
        _transcriber.FaultBeforeShutdown = true;
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Null(_inserter.LastText);
        Assert.Equal(RecordStatus.Failed, Assert.Single(_history.Records).Status);
    }

    [Fact]
    public async Task Paste_mode_follows_the_window_the_text_lands_in()
    {
        await _settings.UpdateAsync(s => s.AppRules = [new Core.Rules.AppRule { ProcessGlob = "WINWORD", PasteMode = PasteMode.CtrlShiftV }]);
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _capture.Emit(1);
        _transcriber.RaiseTurn(0, "Moved windows.", endOfTurn: true, formatted: true);
        _foreground.Context = new ForegroundContext(99, 8, "WINWORD", "Document1 - Word", null, true, false, "uia:Document");
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Equal(PasteMode.CtrlShiftV, _inserter.LastPasteMode);
    }

    [Fact]
    public async Task Failure_without_audio_keeps_the_partial_transcript()
    {
        await _settings.UpdateAsync(s => s.StoreAudio = false);
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _capture.Emit(1);
        _transcriber.RaiseTurn(0, "half a sentence", endOfTurn: false);
        await WaitFor(() => _hub.Current.LiveText == "half a sentence", "live text");
        _transcriber.Fault(new System.Net.WebSockets.WebSocketException("dropped"));
        await WaitForIdleSession();

        var record = Assert.Single(_history.Records);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.Equal("half a sentence", record.RawTranscript);
        Assert.Null(record.AudioPath);
        Assert.Null(_sinks.Last);
        Assert.Contains(_notifier.Toasts, t => t.Message.Contains("saved in History", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_with_neither_audio_nor_words_only_toasts()
    {
        await _settings.UpdateAsync(s => s.StoreAudio = false);
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.FailConnect(new System.Net.WebSockets.WebSocketException("offline"));
        await WaitForIdleSession();

        Assert.Empty(_history.Records);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Dictation failed");
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
    public async Task Arrow_keys_override_tone_and_level_and_are_remembered_as_defaults()
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

        // No app rule matched "notepad", so the change becomes the new global default.
        await WaitFor(() => _settings.Current.DefaultTone == Tone.Formal && _settings.Current.DefaultCleanupLevel == CleanupLevel.Medium, "remembered defaults");
        await WaitFor(() => _hub.Current.Tone == Tone.Formal, "idle chips show remembered tone");
    }

    [Fact]
    public async Task Style_changes_are_per_dictation_when_remembering_is_off()
    {
        await _settings.UpdateAsync(s => s.RememberStyleChanges = false);
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _hotkey.Arrow(ArrowDirection.Right);
        await WaitFor(() => _hub.Current.Tone == Tone.Formal, "tone override");
        _transcriber.RaiseTurn(0, "Text.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        Assert.Equal(Tone.Formal, _post.LastRequest!.Tone);
        Assert.Equal(Tone.Neutral, _settings.Current.DefaultTone);
        Assert.Equal(Tone.Neutral, _hub.Current.Tone);
    }

    [Fact]
    public async Task Style_change_under_an_app_rule_updates_that_rule_not_the_defaults()
    {
        await _settings.UpdateAsync(s => s.AppRules = [new Core.Rules.AppRule { ProcessGlob = "notepad", Tone = Tone.Casual, Level = CleanupLevel.High }]);
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        await WaitFor(() => _hub.Current.Tone == Tone.Casual, "rule applied");
        _hotkey.Arrow(ArrowDirection.Right); // Casual -> Neutral
        await WaitFor(() => _hub.Current.Tone == Tone.Neutral, "tone override");
        _transcriber.RaiseTurn(0, "Text.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        await WaitFor(() => _settings.Current.AppRules[0].Tone == Tone.Neutral, "rule updated");
        Assert.Equal(CleanupLevel.High, _settings.Current.AppRules[0].Level);
        Assert.Equal(Tone.Neutral, _settings.Current.DefaultTone);
        Assert.Equal(CleanupLevel.Light, _settings.Current.DefaultCleanupLevel);
    }

    [Fact]
    public async Task Style_change_during_the_window_lookup_wins_over_the_rule_and_is_remembered_for_it()
    {
        await _settings.UpdateAsync(s => s.AppRules = [new Core.Rules.AppRule { ProcessGlob = "notepad", Tone = Tone.Casual, Level = CleanupLevel.High }]);
        var pressed = false;
        _foreground.OnCapture = () =>
        {
            if (pressed)
            {
                return;
            }

            // The bar shows Listening (default Neutral) before the lookup finishes; Right makes it Formal.
            pressed = true;
            _hotkey.Arrow(ArrowDirection.Right);
            Assert.True(SpinWait.SpinUntil(() => _hub.Current.Tone == Tone.Formal, TimeSpan.FromSeconds(5)), "arrow handled during lookup");
        };
        await StartAsync();
        _hotkey.PressChord();
        await WaitForState(DictationState.Arming);
        _transcriber.CompleteConnect();
        await WaitForState(DictationState.Recording);
        _transcriber.RaiseTurn(0, "Text.", endOfTurn: true, formatted: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _hotkey.ReleaseChord();
        await WaitForIdleSession();

        // The tone chosen during the lookup is kept; the rule still sets the level, which was not touched.
        Assert.Equal(Tone.Formal, _post.LastRequest!.Tone);
        Assert.Equal(CleanupLevel.High, _post.LastRequest.Level);
        await WaitFor(() => _settings.Current.AppRules[0].Tone == Tone.Formal, "rule updated");
        Assert.Equal(CleanupLevel.High, _settings.Current.AppRules[0].Level);
        Assert.Equal(Tone.Neutral, _settings.Current.DefaultTone);
        Assert.Equal(CleanupLevel.Light, _settings.Current.DefaultCleanupLevel);
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

        // Paced at real time (AssemblyAI cannot keep up with a faster stream) and billed for the session, not the audio.
        Assert.Equal(DictationOrchestrator.RetryReplaySpeed, _captures.LastReplaySpeed);
        Assert.Equal(1.0, DictationOrchestrator.RetryReplaySpeed);
        Assert.Equal(CostEstimator.SttCost(_settings.Current.SpeechModel, TimeSpan.FromSeconds(2)), record.CostEstimate);
    }

    [Fact]
    public async Task Retry_that_ends_without_a_summary_keeps_the_earlier_words_and_stays_retryable()
    {
        await StartAsync();
        var wav = Path.Combine(_dir, "old.wav");
        await File.WriteAllBytesAsync(wav, new byte[100]);
        var id = await _history.InsertAsync(new DictationRecord { Status = RecordStatus.Failed, AudioPath = wav, RawTranscript = "the earlier, longer words", CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() });
        _transcriber.CompleteConnect();
        _transcriber.OnConnected = () => _transcriber.RaiseTurn(0, "The earlier", endOfTurn: false);
        _transcriber.NoTermination = true;
        _capture.CompleteOnStart = true;

        var ok = await _orchestrator.RetryAsync(id, CancellationToken.None);

        Assert.False(ok);
        var record = await _history.GetAsync(id);
        Assert.Equal(RecordStatus.Failed, record!.Status);
        Assert.Equal("the earlier, longer words", record.RawTranscript);
        Assert.Contains("did not finish", record.FailureReason);
        Assert.Null(_clipboard.Text);
        Assert.Contains(_notifier.Toasts, t => t.Title == "Retry failed");
    }

    [Fact]
    public async Task Cost_is_estimated_from_the_billed_session_not_the_audio_length()
    {
        await StartAsync();
        await DictateAsync("Billed.");

        // The fake session reports 1.5 s of audio in a 2 s session: AssemblyAI bills the session.
        Assert.Equal(CostEstimator.SttCost(_settings.Current.SpeechModel, TimeSpan.FromSeconds(2)) + CostEstimator.LlmCost("fake-model", 50, 10), Assert.Single(_history.Records).CostEstimate);
    }

    // ---- fakes -------------------------------------------------------------------------------------

    private sealed class FakeHotkey : IHotkeyService
    {
        public event Action? ChordDown;

        public event Action<ChordRelease>? ChordUp;

        public event Action? EscapePressed;

        public event Action<ArrowDirection>? ArrowPressed;

        public bool IsChordHeld { get; private set; }

        public bool Enabled { get; set; } = true;

        public bool Configured { get; private set; }

        public void Configure(HotkeyChord chord) => Configured = true;

        public void PressChord()
        {
            IsChordHeld = true;
            ChordDown?.Invoke();
        }

        public void ReleaseChord(ChordRelease release = ChordRelease.Hold)
        {
            IsChordHeld = false;
            ChordUp?.Invoke(release);
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

        /// <summary>When set, Start throws it, like a microphone that cannot be opened.</summary>
        public Exception? StartError { get; set; }

        /// <summary>When set, Stop delivers one last frame of this fill first, like the microphone's partial tail.</summary>
        public byte? FrameOnStop { get; set; }

        public void Start()
        {
            if (StartError is { } error)
            {
                throw error;
            }

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

        public void Stop()
        {
            if (IsRunning && FrameOnStop is { } fill)
            {
                Emit(fill);
            }

            IsRunning = false;
        }

        public void Dispose() => Stop();

        /// <summary>A frame from the microphone; like a real one, nothing arrives once it is stopped.</summary>
        public void Emit(byte fill)
        {
            if (!IsRunning)
            {
                return;
            }

            var bytes = new byte[AudioFrame.BytesPerFrame];
            Array.Fill(bytes, fill);
            FrameCaptured?.Invoke(new AudioFrame(bytes, 0.5f, TimeSpan.Zero));
        }

        public void Fault(Exception ex) => Faulted?.Invoke(ex);
    }

    private sealed class FakeCaptureFactory(FakeCapture capture) : IAudioCaptureFactory
    {
        public double? LastReplaySpeed { get; private set; }

        public IAudioCapture CreateMicrophone(string? deviceId) => capture;

        public IAudioCapture CreateWavReplay(string wavPath, double speed)
        {
            LastReplaySpeed = speed;
            return capture;
        }
    }

    private sealed class MemorySink(string path, Func<int>? probe) : IAudioSink
    {
        public string Path { get; } = path;

        public long BytesWritten { get; private set; }

        public bool Completed { get; private set; }

        public bool Discarded { get; private set; }

        /// <summary>What the factory's probe said when the recording was finished (the number of saved records, say).</summary>
        public int? ProbeAtComplete { get; private set; }

        public void Write(ReadOnlySpan<byte> pcm16) => BytesWritten += pcm16.Length;

        public void Complete()
        {
            Completed = true;
            ProbeAtComplete = probe?.Invoke();
        }

        public void Discard() => Discarded = true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSinkFactory : IAudioSinkFactory
    {
        public MemorySink? Last { get; private set; }

        public Func<int>? Probe { get; set; }

        public IAudioSink Create(string path) => Last = new MemorySink(path, Probe);
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

        /// <summary>The transcript the server returns for audio that was buffered before Begin.</summary>
        public void RaiseTurnsOnConnect(string text) => OnConnected = () => RaiseTurn(0, text, endOfTurn: true, formatted: true);

        public void FailConnect(Exception ex) => _connect.TrySetException(ex);

        public void RaiseTurn(int order, string text, bool endOfTurn, bool formatted = false) =>
            TurnReceived?.Invoke(new TurnMessage { TurnOrder = order, Utterance = text, Transcript = text, EndOfTurn = endOfTurn, TurnIsFormatted = formatted });

        public void Fault(Exception ex) => Faulted?.Invoke(ex);

        public bool ConnectStarted { get; private set; }

        public SessionOptions? Options { get; private set; }

        public async Task<BeginMessage> ConnectAsync(SessionOptions options, CancellationToken ct)
        {
            ConnectStarted = true;
            Options = options;
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

        /// <summary>When set, shutdown fails like a socket that dropped during the handshake.</summary>
        public Exception? ShutdownError { get; set; }

        /// <summary>Raise a socket fault after the release, when the session no longer awaits it, and return no summary.</summary>
        public bool FaultBeforeShutdown { get; set; }

        /// <summary>The handshake hits its cap without a Termination summary (and without a fault).</summary>
        public bool NoTermination { get; set; }

        public Task<TerminationMessage?> ShutdownAsync(TimeSpan hardCap, CancellationToken ct)
        {
            ShutdownCalled = true;
            IsConnected = false;
            if (ShutdownError is { } error)
            {
                return Task.FromException<TerminationMessage?>(error);
            }

            if (FaultBeforeShutdown)
            {
                Fault(new System.Net.WebSockets.WebSocketException("socket dropped"));
                return Task.FromResult<TerminationMessage?>(null);
            }

            if (NoTermination)
            {
                return Task.FromResult<TerminationMessage?>(null);
            }

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

        /// <summary>Called on every lookup, so a test can see what was already running at that moment.</summary>
        public Action? OnCapture { get; set; }

        public ForegroundContext Capture()
        {
            OnCapture?.Invoke();
            return Context;
        }

        /// <summary>What the focused control reports before its caret; null means it does not say.</summary>
        public string? TextBeforeCaret { get; set; }

        public ForegroundContext? CaretTarget { get; private set; }

        public string? ReadTextBeforeCaret(ForegroundContext target)
        {
            CaretTarget = target;
            return TextBeforeCaret;
        }
    }

    private sealed class FakeInserter : ITextInserter
    {
        public string? LastText { get; private set; }

        public PasteMode? LastPasteMode { get; private set; }

        /// <summary>What the paste reports (Inserted by default).</summary>
        public InsertionResult Result { get; set; } = new(InsertionOutcome.Inserted);

        public Task<InsertionResult> InsertAsync(string text, ForegroundContext target, PasteMode pasteMode, CancellationToken ct)
        {
            LastText = text;
            LastPasteMode = pasteMode;
            return Task.FromResult(Result);
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

        /// <summary>When set, cleanup waits for it, holding the session in PostProcessing.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>A recurring settings problem the cleanup reports (rejected key, blocked or retired model).</summary>
        public string? Problem { get; set; }

        public PostProcessRequest? LastRequest { get; private set; }

        public async Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            return Fail
                ? new PostProcessResult(rawTranscript, false, null, "gateway-down", ConfigurationProblem: Problem)
                : new PostProcessResult(rawTranscript + " [cleaned]", true, "fake-model", null, 50, 10, Problem);
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

        public Task<IReadOnlyList<string>> GetReferencedAudioPathsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Records.Where(r => r.AudioPath is not null).Select(r => r.AudioPath!).ToArray());
    }
}
