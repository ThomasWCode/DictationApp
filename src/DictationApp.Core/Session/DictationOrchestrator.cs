using System.Diagnostics;
using System.Threading.Channels;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.History;
using DictationApp.Core.Insertion;
using DictationApp.Core.Rules;
using DictationApp.Core.Settings;
using DictationApp.Core.Transcription;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.Session;

/// <summary>
/// Hosted service that owns the dictation state machine and runs one <see cref="DictationSession"/> at a
/// time. Hotkey events arrive on arbitrary threads and are funnelled through a single-reader channel so
/// every decision is made sequentially. Audio frames flow through a second channel to the socket sender.
/// </summary>
public sealed class DictationOrchestrator : BackgroundService
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan HandshakeCap = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan SilentMicWarningAfter = TimeSpan.FromSeconds(2);
    public const string MicrophonePrivacyUri = "ms-settings:privacy-microphone";

    /// <summary>
    /// Replay pace for Retry: real time. AssemblyAI processes streamed audio at about 1.25x real time at most and then
    /// ends the session with a short handshake, so the old 4x replay left the end of any recording longer than a few
    /// seconds unprocessed and the retry came back silently cut off (and a recording over ~7 minutes could overflow
    /// the server's 5-minute buffer). A retry therefore takes about as long as the recording.
    /// </summary>
    public const double RetryReplaySpeed = 1.0;

    /// <summary>Failure reason of a dictation that was still running when the app closed.</summary>
    public const string InterruptedReason = "Interrupted: DictationApp was closed during the dictation";

    /// <summary>
    /// Silence before AssemblyAI may end a turn at terminal punctuation. Its default (100 ms) suits voice agents:
    /// a pause to think mid-sentence became a full stop and a new capitalised sentence. Dictation needs no early
    /// turn ends, because releasing the hotkey force-ends the last turn at once.
    /// </summary>
    public const int DictationMinTurnSilenceMs = 1000;

    /// <summary>Silence after which a turn ends even without terminal punctuation (default 1000 ms).</summary>
    public const int DictationMaxTurnSilenceMs = 3600;

    private readonly IHotkeyService _hotkeys;
    private readonly IAudioCaptureFactory _captures;
    private readonly IAudioSinkFactory _sinks;
    private readonly IStreamingTranscriberFactory _transcribers;
    private readonly IForegroundContextProvider _foreground;
    private readonly ITextInserter _inserter;
    private readonly IClipboard _clipboard;
    private readonly INotifier _notifier;
    private readonly ITextPostProcessor _postProcessor;
    private readonly IHistoryRepository _history;
    private readonly ISettingsStore _settings;
    private readonly DictationStatusHub _hub;
    private readonly InsertionTextFormatter _formatter;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<DictationOrchestrator> _logger;
    private readonly DictationStateMachine _machine;
    private readonly Channel<ControlEvent> _events = Channel.CreateUnbounded<ControlEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly HashSet<string> _reportedCleanupProblems = [];

    // Clipboard writers take turns (a paste with its snapshot and restore, Re-insert, a Retry's copy), so one can never
    // restore the clipboard over another's text.
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);

    // A dictation and a Retry both stream to AssemblyAI and end on the clipboard: one at a time. A press during a
    // retry flashes busy, and a retry during a dictation is refused. Guards _session's start and _retrying.
    private readonly object _startLock = new();
    private bool _retrying;
    private DictationSession? _session;
    private Task? _sessionTask;

    public DictationOrchestrator(
        IHotkeyService hotkeys,
        IAudioCaptureFactory captures,
        IAudioSinkFactory sinks,
        IStreamingTranscriberFactory transcribers,
        IForegroundContextProvider foreground,
        ITextInserter inserter,
        IClipboard clipboard,
        INotifier notifier,
        ITextPostProcessor postProcessor,
        IHistoryRepository history,
        ISettingsStore settings,
        DictationStatusHub hub,
        InsertionTextFormatter formatter,
        AppPaths paths,
        ILogger<DictationOrchestrator> logger,
        TimeProvider? time = null,
        DictationStateMachine? machine = null)
    {
        _hotkeys = hotkeys;
        _captures = captures;
        _sinks = sinks;
        _transcribers = transcribers;
        _foreground = foreground;
        _inserter = inserter;
        _clipboard = clipboard;
        _notifier = notifier;
        _postProcessor = postProcessor;
        _history = history;
        _settings = settings;
        _hub = hub;
        _formatter = formatter;
        _paths = paths;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _machine = machine ?? new DictationStateMachine();
        _machine.ChordDownIgnored += () => _hub.Flash();
        _machine.Transitioned += t => _logger.LogDebug("State {From} --{Trigger}--> {To}", t.From, t.Trigger, t.To);
    }

    public DictationState State => _machine.State;

    /// <summary>Exposed for tests and the tray: the task running the current session, if any.</summary>
    public Task? CurrentSessionTask => _sessionTask;

    /// <summary>Programmatic chord press (tray menu, tests).</summary>
    public void SimulateChordDown() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordDown));

    /// <summary>Programmatic release (tests); always a hold. The tray stops through the hotkey service.</summary>
    public void SimulateChordUp() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordUp, Release: ChordRelease.Hold));

    public void SimulateEscape() => _events.Writer.TryWrite(new ControlEvent(ControlKind.Escape));

    /// <summary>
    /// Runs a full dictation (context capture, streaming, cleanup, insertion) with a WAV file standing in
    /// for the microphone. The session releases itself when the file ends. Used by <c>--simulate</c>.
    /// </summary>
    public void SimulateDictationFromWav(string wavPath) => _events.Writer.TryWrite(new ControlEvent(ControlKind.SimulatedStart, WavPath: wavPath));

    /// <summary>
    /// Re-streams a stored WAV through the transcriber (at <see cref="RetryReplaySpeed"/>), cleans it up and copies the
    /// result to the clipboard. Used by "Retry" in History for Failed/Pending records. A retry whose streaming session
    /// ended without its Termination summary may be missing its end, so it counts as failed: the record keeps its
    /// earlier words and stays retryable.
    /// </summary>
    public async Task<bool> RetryAsync(long recordId, CancellationToken ct)
    {
        var record = await _history.GetAsync(recordId, ct).ConfigureAwait(false);
        if (record is null || string.IsNullOrEmpty(record.AudioPath) || !File.Exists(record.AudioPath))
        {
            _notifier.Toast("Retry", "No audio is stored for this dictation.", ToastKind.Warning);
            return false;
        }

        string? busy = null;
        lock (_startLock)
        {
            if (_retrying)
            {
                busy = "A retry is already running.";
            }
            else if (!_machine.IsIdle || _session is not null)
            {
                busy = "Finish the current dictation first.";
            }
            else
            {
                _retrying = true; // until this retry ends; closing History does not end it
            }
        }

        if (busy is not null)
        {
            _notifier.Toast("Retry", busy, ToastKind.Warning);
            return false;
        }

        try
        {
            var settings = _settings.Current;
            var transcript = await TranscribeWavAsync(record.AudioPath, RetryReplaySpeed, null, ct).ConfigureAwait(false);
            record.CostEstimate = (record.CostEstimate ?? 0m) + CostEstimator.SttCost(settings.SpeechModel, BilledDuration(transcript.Termination, transcript.WallClock));
            record.UpdatedAt = _time.GetUtcNow();
            if (transcript.Termination is null)
            {
                _logger.LogWarning("Retry of record {Id} ended without a Termination summary; keeping the earlier transcript", recordId);
                record.FailureReason = "retry: the transcription did not finish";
                await _history.UpdateAsync(record, ct).ConfigureAwait(false);
                _notifier.Toast("Retry failed", "The transcription did not finish. The recording is kept; try Retry again.", ToastKind.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                record.FailureReason = "retry: nothing heard";
                await _history.UpdateAsync(record, ct).ConfigureAwait(false);
                _notifier.Toast("Retry", "Nothing was heard in the recording.", ToastKind.Warning);
                return false;
            }

            var request = new PostProcessRequest(record.Level, record.Tone, KeytermsSelector.Select(settings.Dictionary), record.ProcessName ?? string.Empty, record.Url, null, PauseMarkedTranscript: transcript.PauseMarkedText);
            var result = await _postProcessor.ProcessAsync(transcript.Text, request, ct).ConfigureAwait(false);
            ReportCleanupProblem(result);
            await _clipboardGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _clipboard.SetTextAsync(result.Text, ct).ConfigureAwait(false);
            }
            finally
            {
                _clipboardGate.Release();
            }

            record.RawTranscript = transcript.Text;
            record.CleanedText = result.Applied ? result.Text : string.Empty;
            record.InsertedText = result.Text;
            record.LlmModel = result.Model;
            record.Status = RecordStatus.CopiedOnly;
            record.FailureReason = result.Applied ? null : result.FailureReason;
            record.UpdatedAt = _time.GetUtcNow();
            record.CostEstimate += CostEstimator.LlmCost(result.Model, result.PromptTokens, result.CompletionTokens);
            await _history.UpdateAsync(record, ct).ConfigureAwait(false);
            _notifier.Toast("Retry complete", "Text copied to the clipboard.", ToastKind.Success);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retry of record {Id} failed", recordId);
            record.FailureReason = "retry: " + ex.Message;
            await _history.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            _notifier.Toast("Retry failed", ex.Message, ToastKind.Error);
            return false;
        }
        finally
        {
            lock (_startLock)
            {
                _retrying = false;
            }
        }
    }

    /// <summary>Streams a WAV file through a fresh transcriber session. Used by retry and <c>--stream-test</c>.</summary>
    public async Task<WavTranscription> TranscribeWavAsync(string wavPath, double speed, Action<TurnMessage>? onTurn, CancellationToken ct)
    {
        await _replayGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings.Current;
            var assembler = new TranscriptAssembler();
            using var capture = _captures.CreateWavReplay(wavPath, speed);
            await using var transcriber = _transcribers.Create();
            var frames = Channel.CreateUnbounded<AudioFrame>(new UnboundedChannelOptions { SingleReader = true });
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long bytes = 0;
            capture.FrameCaptured += f =>
            {
                Interlocked.Add(ref bytes, f.Pcm16.Length);
                frames.Writer.TryWrite(new AudioFrame(f.Pcm16.ToArray(), f.Peak, f.Position));
            };
            capture.Completed += () => { frames.Writer.TryComplete(); done.TrySetResult(); };
            capture.Faulted += ex => { frames.Writer.TryComplete(ex); done.TrySetException(ex); };
            transcriber.TurnReceived += t =>
            {
                assembler.Ingest(t);
                onTurn?.Invoke(t);
            };
            transcriber.Faulted += ex => done.TrySetException(ex);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout + TimeSpan.FromSeconds(2));
            var options = BuildSessionOptions(settings);
            var sw = Stopwatch.StartNew();
            await transcriber.ConnectAsync(options, connectCts.Token).ConfigureAwait(false);
            capture.Start();
            var sender = SendFramesAsync(frames.Reader, transcriber, ct);
            await done.Task.WaitAsync(ct).ConfigureAwait(false);
            await sender.ConfigureAwait(false);
            var termination = await transcriber.ShutdownAsync(HandshakeCap, ct).ConfigureAwait(false);
            var duration = TimeSpan.FromSeconds((double)Interlocked.Read(ref bytes) / (AudioFrame.SampleRate * 2));
            return new WavTranscription(assembler.FinalText, duration, transcriber.ConnectLatency, termination, sw.Elapsed, assembler.PauseMarkedText);
        }
        finally
        {
            _replayGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ApplyHotkeySettings(_settings.Current);
        _settings.Changed += OnSettingsChanged;
        _hotkeys.ChordDown += OnChordDown;
        _hotkeys.ChordUp += OnChordUp;
        _hotkeys.EscapePressed += OnEscape;
        _hotkeys.ArrowPressed += OnArrow;
        _hub.ToneOverrideRequested += OnToneOverride;
        _hub.LevelOverrideRequested += OnLevelOverride;
        _hub.Publish(DictationStatus.Idle with { Tone = _settings.Current.DefaultTone, CleanupLevel = _settings.Current.DefaultCleanupLevel, HotkeyEnabled = _hotkeys.Enabled });

        try
        {
            await _history.InitialiseAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "History database could not be initialised; dictations will not be recorded");
        }

        try
        {
            await foreach (var ev in _events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Dispatch(ev, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Unhandled error dispatching {Event}", ev.Kind);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            _hotkeys.ChordDown -= OnChordDown;
            _hotkeys.ChordUp -= OnChordUp;
            _hotkeys.EscapePressed -= OnEscape;
            _hotkeys.ArrowPressed -= OnArrow;
            _hub.ToneOverrideRequested -= OnToneOverride;
            _hub.LevelOverrideRequested -= OnLevelOverride;

            // Closing the app is not a discard: the running session sees the cancelled token and keeps what it has
            // (a Failed record with its audio and the words heard, for Retry after the restart).
            if (_sessionTask is { } t)
            {
                try
                {
                    await t.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down; best effort.
                }
            }
        }
    }

    private void Dispatch(ControlEvent ev, CancellationToken ct)
    {
        switch (ev.Kind)
        {
            case ControlKind.ChordDown:
                lock (_startLock)
                {
                    if (_retrying)
                    {
                        _hub.Flash(); // a Retry is streaming (see _startLock)
                    }
                    else if (_machine.IsIdle || _session is { ReleasedAsTap: true })
                    {
                        // A press that arrives while the previous session is being torn down (socket abort, WAV discard),
                        // or while a tap is still being cancelled, chains behind it instead of flashing "busy".
                        var previous = _sessionTask;
                        var session = new DictationSession(_time.GetUtcNow());
                        _session = session;
                        _sessionTask = Task.Run(async () =>
                        {
                            if (previous is { IsCompleted: false })
                            {
                                try
                                {
                                    await previous.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                                }
                                catch (Exception)
                                {
                                    // Previous session is stuck or faulted; start anyway.
                                }
                            }

                            await RunSessionAsync(session, ct).ConfigureAwait(false);
                        }, CancellationToken.None);
                    }
                    else
                    {
                        _machine.Fire(DictationTrigger.ChordDown); // ignored + flash
                    }
                }

                break;
            case ControlKind.SimulatedStart:
                lock (_startLock)
                {
                    if (!_retrying && _machine.IsIdle && (_sessionTask is null || _sessionTask.IsCompleted) && ev.WavPath is not null)
                    {
                        var simulated = new DictationSession(_time.GetUtcNow()) { SimulatedWavPath = ev.WavPath };
                        _session = simulated;
                        _sessionTask = Task.Run(() => RunSessionAsync(simulated, ct), CancellationToken.None);
                    }
                }

                break;
            case ControlKind.ChordUp:
                _session?.Release(ev.Release == ChordRelease.Tap);
                break;
            case ControlKind.Escape:
                _session?.Escape();
                break;
            case ControlKind.Arrow:
                if (_session is { } s && _machine.State is DictationState.Arming or DictationState.Recording)
                {
                    bool remember;
                    lock (s.StyleLock)
                    {
                        switch (ev.Arrow)
                        {
                            case ArrowDirection.Left:
                                s.Tone = s.Tone.Previous();
                                break;
                            case ArrowDirection.Right:
                                s.Tone = s.Tone.Next();
                                break;
                            case ArrowDirection.Up:
                                s.Level = s.Level.Next();
                                break;
                            case ArrowDirection.Down:
                                s.Level = s.Level.Previous();
                                break;
                        }

                        var toneKey = ev.Arrow is ArrowDirection.Left or ArrowDirection.Right;
                        remember = CanRememberStyleNow(s, tone: toneKey, level: !toneKey);
                    }

                    PublishSession(s, _machine.State, null);
                    if (remember)
                    {
                        RememberStyle(s);
                    }
                }

                break;
            case ControlKind.ToneOverride:
                if (_session is { } ts && ev.Tone is { } tone)
                {
                    bool remember;
                    lock (ts.StyleLock)
                    {
                        ts.Tone = tone;
                        remember = CanRememberStyleNow(ts, tone: true, level: false);
                    }

                    PublishSession(ts, _machine.State, null);
                    if (remember)
                    {
                        RememberStyle(ts);
                    }
                }

                break;
            case ControlKind.LevelOverride:
                if (_session is { } ls && ev.Level is { } level)
                {
                    bool remember;
                    lock (ls.StyleLock)
                    {
                        ls.Level = level;
                        remember = CanRememberStyleNow(ls, tone: false, level: true);
                    }

                    PublishSession(ls, _machine.State, null);
                    if (remember)
                    {
                        RememberStyle(ls);
                    }
                }

                break;
        }
    }

    private async Task RunSessionAsync(DictationSession session, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _machine.Fire(DictationTrigger.ChordDown) != DictationState.Arming)
        {
            // The app is closing, or a chained press found the previous session still running after the wait.
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            return;
        }

        var settings = _settings.Current;
        IAudioCapture? capture = null;
        IAudioSink? sink = null;
        IStreamingTranscriber? transcriber = null;
        Task? sender = null;
        var record = new DictationRecord { CreatedAt = session.StartedAt, UpdatedAt = session.StartedAt, Status = RecordStatus.Pending };
        try
        {
            // The press is answered before anything that can fail: the hotkey service takes an Idle for this dictation's
            // end only once it has answered, so a microphone that cannot open would otherwise leave a tap or a double tap
            // latched (the next press then swallowed as a "stop").
            session.Tone = settings.DefaultTone;
            session.Level = settings.DefaultCleanupLevel;
            PublishSession(session, DictationState.Arming, "Listening");

            // 1. Microphone first: anything said before it starts is lost. Frames buffer in the channel until the
            // socket is ready, and nothing here moves focus, so the window lookup below still sees the target.
            if (settings.StoreAudio)
            {
                sink = _sinks.Create(_paths.NewAudioPath(session.StartedAt));
            }

            capture = session.SimulatedWavPath is null
                ? _captures.CreateMicrophone(settings.MicrophoneDeviceId)
                : _captures.CreateWavReplay(session.SimulatedWavPath, 1.0);
            if (session.SimulatedWavPath is not null)
            {
                capture.Completed += () => session.Release();
            }

            capture.FrameCaptured += frame =>
            {
                var copy = new AudioFrame(frame.Pcm16.ToArray(), frame.Peak, frame.Position);
                sink?.Write(copy.Pcm16.Span);
                session.Frames.Writer.TryWrite(copy);
                session.OnFrame(copy);
                if (Publishes(session))
                {
                    _hub.Update(s => s with { Level = copy.Peak });
                }
            };
            capture.Faulted += session.Fault;
            capture.Start();
            session.Mark("mic");

            // 2. Socket: its ~0.8 s TLS and session setup overlaps the window lookup below.
            transcriber = _transcribers.Create();
            session.AttachTranscriber(transcriber);
            transcriber.TurnReceived += turn =>
            {
                if (session.Assembler.Ingest(turn) && Publishes(session))
                {
                    _hub.Update(s => s with { LiveText = session.Assembler.LiveText });
                }
            };
            transcriber.Faulted += session.Fault;
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            var connectTask = transcriber.ConnectAsync(BuildSessionOptions(settings), connectCts.Token);

            // 3. Where the user is: target window, editability, browser URL and app rule (tone and level). The lookup can
            // be slow (an app that answers UI Automation late), so a release, a discard or a fault meanwhile stops the
            // microphone at once rather than when the lookup returns: nothing said after the release is recorded.
            var lookup = Task.Run(SafeCapture, CancellationToken.None);
            if (await Task.WhenAny(lookup, session.Released, session.Escaped, session.Faulted).ConfigureAwait(false) != lookup)
            {
                capture.Stop();
            }

            session.Context = await lookup.ConfigureAwait(false);
            session.Mark("lookup");
            // The bar already shows Listening, so an arrow key or chip may have changed the tone or level meanwhile:
            // that choice wins over the rule's, and is remembered now that it is known which rule it belongs to.
            var rule = AppRulesResolver.Resolve(session.Context, settings.AppRules, settings.DefaultTone, settings.DefaultCleanupLevel, settings.PasteMode);
            bool rememberEarlyChange;
            lock (session.StyleLock)
            {
                session.Rule = rule;
                if (!session.ToneChangedBeforeRule)
                {
                    session.Tone = rule.Tone;
                }

                if (!session.LevelChangedBeforeRule)
                {
                    session.Level = rule.Level;
                }

                session.RuleResolved = true;
                rememberEarlyChange = session.ToneChangedBeforeRule || session.LevelChangedBeforeRule;
            }

            if (rememberEarlyChange)
            {
                RememberStyle(session);
            }

            record.ProcessName = session.Context.ProcessName;
            record.WindowTitle = session.Context.WindowTitle;
            record.Url = session.Context.Url;
            _logger.LogInformation("Dictation started in {Process} ({Title}) rule={Rule} editable={Editable}/{Reason} elevated={Elevated}", session.Context.ProcessName, session.Context.WindowTitle, session.Rule.MatchedBy, session.Context.IsEditable, session.Context.EditableReason, session.Context.IsElevated);
            PublishSession(session, DictationState.Arming, "Listening");
            var silentMicTask = WatchForSilentMicrophoneAsync(session, ct);

            var first = await Task.WhenAny(connectTask, session.Released, session.Escaped, session.Faulted).ConfigureAwait(false);

            // A discard wins over a release that arrived with it (Escape, then letting go, during a slow start).
            if (session.Escaped.IsCompleted)
            {
                await DiscardAsync(session, capture, sink, transcriber, "Discarded").ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested(); // the app is closing: kept as an interrupted dictation below
            if (first == session.Faulted)
            {
                await FailAsync(session, record, capture, sink, transcriber, session.Faulted.Result, ct).ConfigureAwait(false);
                return;
            }

            if (first == session.Released)
            {
                if (session.ReleasedAsTap)
                {
                    _machine.Fire(DictationTrigger.Tap);
                    _logger.LogInformation("Short tap: cancelled before the streaming session began");
                    await DiscardAsync(session, capture, sink, transcriber, null).ConfigureAwait(false);
                    return;
                }

                // Released after a real hold but before Begin. The release ends the recording: stop the microphone now
                // (its last partial frame is delivered by Stop), then give the connection its remaining budget and
                // send what was recorded. Nothing said after the release is recorded or uploaded.
                session.Mark("release");
                _machine.Fire(DictationTrigger.ChordUp);
                capture.Stop();
                session.Frames.Writer.TryComplete();
                PublishSession(session, DictationState.Finalising, "Connecting");
                try
                {
                    await connectTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || connectCts.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    _machine.Fire(DictationTrigger.Failed);
                    await FailAsync(session, record, capture, sink, transcriber, ex is OperationCanceledException ? new TimeoutException("Connection timed out") : ex, ct).ConfigureAwait(false);
                    return;
                }

                session.Mark("begin");
                await SendFramesAsync(session.Frames.Reader, transcriber, ct).ConfigureAwait(false);
            }
            else
            {
                // Begin arrived while the chord is still held.
                try
                {
                    await connectTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ct.ThrowIfCancellationRequested();
                    _machine.Fire(ex is OperationCanceledException ? DictationTrigger.ConnectTimeout : DictationTrigger.SocketFault);
                    await FailAsync(session, record, capture, sink, transcriber, ex is OperationCanceledException ? new TimeoutException("Connection timed out") : ex, ct).ConfigureAwait(false);
                    return;
                }

                _machine.Fire(DictationTrigger.BeginReceived);
                session.Mark("begin");
                _logger.LogInformation("Connect latency {Latency} ms", transcriber.ConnectLatency?.TotalMilliseconds ?? -1);
                PublishSession(session, DictationState.Recording, null);
                sender = SendFramesAsync(session.Frames.Reader, transcriber, ct);

                using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var capTask = Task.Delay(settings.MaxDictationDuration, _time, capCts.Token);
                var end = await Task.WhenAny(session.Released, session.Escaped, session.Faulted, capTask).ConfigureAwait(false);
                capCts.Cancel();
                session.Mark("release");
                if (session.Escaped.IsCompleted)
                {
                    await DiscardAsync(session, capture, sink, transcriber, "Discarded").ConfigureAwait(false);
                    return;
                }

                // The cap task is linked to the app's token, so closing the app lands here too: not the time limit.
                ct.ThrowIfCancellationRequested();
                if (end == session.Faulted)
                {
                    _machine.Fire(DictationTrigger.SocketFault);
                    await FailAsync(session, record, capture, sink, transcriber, session.Faulted.Result, ct).ConfigureAwait(false);
                    return;
                }

                if (end == capTask)
                {
                    _machine.Fire(DictationTrigger.CapReached);
                    _notifier.Toast("Dictation limit", $"Stopped after {settings.MaxDictationMinutes} minutes.", ToastKind.Info);
                }
                else if (session.ReleasedAsTap)
                {
                    // The streaming session began before a lone tap was reported: still a tap, still cancelled.
                    _machine.Fire(DictationTrigger.Tap);
                    _logger.LogInformation("Short tap: cancelled just after the streaming session began");
                    await DiscardAsync(session, capture, sink, transcriber, null).ConfigureAwait(false);
                    return;
                }
                else
                {
                    _machine.Fire(DictationTrigger.ChordUp);
                }

                capture.Stop();
                session.Frames.Writer.TryComplete();
                try
                {
                    await sender.WaitAsync(TimeSpan.FromSeconds(1.5), _time, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Audio tail did not drain within budget");
                }
            }

            // 4. Finalising: graceful handshake bounded by the hard cap.
            PublishSession(session, DictationState.Finalising, "Finishing");
            var termination = await transcriber.ShutdownAsync(HandshakeCap, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); // shutdown treats the app closing like its time cap and returns null
            session.Mark("handshake");
            if (termination is null && !session.Faulted.IsCompleted)
            {
                _logger.LogWarning("No Termination summary from the streaming session; the transcript may be missing its last words");
            }

            // A socket fault after the release is no longer being awaited; without a Termination summary the
            // transcript may be missing its tail, so save it as Failed (audio kept for Retry) rather than paste it.
            if (termination is null && session.Faulted.IsCompleted)
            {
                await FailAsync(session, record, capture, sink, transcriber, session.Faulted.Result, ct).ConfigureAwait(false);
                return;
            }
            var audioSeconds = termination?.AudioDurationSeconds ?? session.AudioDuration.TotalSeconds;
            record.DurationMs = (int)(session.AudioDuration.TotalMilliseconds);
            record.CostEstimate = CostEstimator.SttCost(settings.SpeechModel, BilledDuration(termination, TimeSpan.FromSeconds(audioSeconds)));
            var rawText = session.Assembler.FinalText;
            record.RawTranscript = rawText;
            if (!session.Assembler.HasOpenTurn && session.Assembler.ClosedText != rawText)
            {
                _logger.LogInformation("Trailing partial included in final text");
            }

            // The recording is finished (Complete) only once its record is saved: until then its header keeps the
            // "unfinished" marker, so a crash or kill during cleanup or insertion leaves a file the next start
            // recovers into History instead of an orphan the sweep deletes.
            record.AudioPath = sink?.Path;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _machine.Fire(DictationTrigger.NothingHeard);
                sink?.Discard();
                record.AudioPath = null;
                _logger.LogInformation("Nothing heard (ms since the press: {Timings})", session.Timings());
                PublishIdle(session, "Nothing heard");
                return;
            }

            _machine.Fire(DictationTrigger.HandshakeComplete);

            // 5. Post-processing.
            PublishSession(session, DictationState.PostProcessing, "Cleaning up");
            record.Tone = session.Tone;
            record.Level = session.Level;
            var keyterms = KeytermsSelector.Select(settings.Dictionary);
            var request = new PostProcessRequest(session.Level, session.Tone, keyterms, session.Context.ProcessName, session.Context.Url, session.Rule.Hint, PauseMarkedTranscript: session.Assembler.PauseMarkedText);
            var result = await _postProcessor.ProcessAsync(rawText, request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            session.Mark("cleanup");
            ReportCleanupProblem(result);
            record.CleanedText = result.Applied ? result.Text : string.Empty;
            record.LlmModel = result.Model;
            record.FailureReason = result.Applied ? null : result.FailureReason;
            record.CostEstimate += CostEstimator.LlmCost(result.Model, result.PromptTokens, result.CompletionTokens);
            var badge = result.Applied || !PostProcessorRouter.NeedsLlm(session.Level, session.Tone) ? null : "cleanup skipped";
            _machine.Fire(DictationTrigger.PostProcessed);

            // 6. Inserting. Re-verify the foreground window; keep the tone chosen at the start.
            PublishSession(session, DictationState.Inserting, "Inserting");
            var target = SafeCapture();
            var pasteMode = session.Rule.PasteMode;
            if (target.WindowHandle == 0 || target.WindowHandle == session.Context.WindowHandle)
            {
                target = session.Context with { IsEditable = target.WindowHandle == 0 ? session.Context.IsEditable : target.IsEditable, EditableReason = target.WindowHandle == 0 ? session.Context.EditableReason : target.EditableReason };
            }
            else
            {
                // Tone and cleanup stay as chosen at the start, but the paste keystroke belongs to the window the text
                // lands in (e.g. Ctrl+Shift+V for an app whose rule asks for plain-text paste).
                _logger.LogInformation("Foreground changed during dictation: {From} -> {To}", session.Context.ProcessName, target.ProcessName);
                pasteMode = PasteModeFor(target, settings);
            }

            var text = _formatter.Format(result.Text, target.WindowHandle, target.IsEditable ? SafeReadTextBeforeCaret(target) : null);
            record.InsertedText = text;

            // Insertion is the commit point: once it starts it runs to the end (paste, then clipboard restore), even if
            // the app is closing meanwhile, so it takes no cancellation token.
            var insertion = await DeliverAsync(text, target, pasteMode).ConfigureAwait(false);
            session.Mark("insert");
            switch (insertion.Outcome)
            {
                case InsertionOutcome.Inserted:
                    record.Status = RecordStatus.Inserted;
                    if (insertion.LeftOnClipboard)
                    {
                        badge = badge is null ? "also copied" : badge + " · also copied";
                    }

                    _machine.Fire(DictationTrigger.Inserted);
                    break;
                case InsertionOutcome.CopiedOnly:
                    record.Status = RecordStatus.CopiedOnly;
                    _machine.Fire(DictationTrigger.CopiedOnly);
                    break;
                default:
                    record.Status = RecordStatus.Failed;
                    record.FailureReason = AppendReason(record.FailureReason, insertion.Reason);
                    _machine.Fire(DictationTrigger.Failed);
                    break;
            }

            record.UpdatedAt = _time.GetUtcNow();
            if (await SaveRecordAsync(record).ConfigureAwait(false))
            {
                sink?.Complete();
            }

            _ = BumpDictionaryUsageAsync(keyterms, rawText);
            PublishIdle(session, badge);
            _logger.LogInformation("Dictation {Status}: {Words} words, {Duration:0.0}s audio, llm={Model} (ms since the press: {Timings})", record.Status, CountWords(text), audioSeconds, result.Model ?? "none", session.Timings());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SaveInterruptedAsync(session, record, capture, sink, transcriber).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dictation session crashed in state {State}", _machine.State);
            if (!_machine.IsIdle)
            {
                _machine.Fire(DictationTrigger.Failed);
            }

            await FailAsync(session, record, capture, sink, transcriber, ex, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                capture?.Dispose();
                sink?.Dispose();
                if (transcriber is not null)
                {
                    await transcriber.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cleanup error");
            }

            if (!_machine.IsIdle)
            {
                _machine.Fire(DictationTrigger.Failed);
            }

            var publishes = Publishes(session);
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            if (publishes && _hub.Current.State != DictationState.Idle)
            {
                PublishIdle(null);
            }
        }
    }

    private async Task DiscardAsync(DictationSession session, IAudioCapture? capture, IAudioSink? sink, IStreamingTranscriber? transcriber, string? badge)
    {
        if (!_machine.IsIdle)
        {
            _machine.Fire(DictationTrigger.Escape);
        }

        capture?.Stop();
        session.Frames.Writer.TryComplete();
        sink?.Discard();
        if (transcriber is not null)
        {
            await transcriber.AbortAsync().ConfigureAwait(false);
        }

        PublishIdle(session, badge);
    }

    /// <summary>
    /// The app is closing (tray Quit, sign-out, shutdown) while a dictation runs: keep what was recorded as a Failed
    /// record with its audio and the words heard so far, so Retry can finish it after the restart. No network call, no
    /// paste, no toast; this has to fit in the host's shutdown time.
    /// </summary>
    private async Task SaveInterruptedAsync(DictationSession session, DictationRecord record, IAudioCapture? capture, IAudioSink? sink, IStreamingTranscriber? transcriber)
    {
        _logger.LogWarning("DictationApp closed during a dictation (state {State}); keeping it for Retry", _machine.State);
        try
        {
            capture?.Stop();
            session.Frames.Writer.TryComplete();
            if (transcriber is not null)
            {
                await transcriber.AbortAsync().ConfigureAwait(false);
            }

            if (sink is not null && sink.BytesWritten > 0)
            {
                record.AudioPath = sink.Path;
            }
            else
            {
                sink?.Discard();
            }

            record.Status = RecordStatus.Failed;
            record.FailureReason = InterruptedReason;
            record.RawTranscript = session.Assembler.FinalText;
            record.Tone = session.Tone;
            record.Level = session.Level;
            record.DurationMs = (int)session.AudioDuration.TotalMilliseconds;
            record.UpdatedAt = _time.GetUtcNow();
            if ((record.AudioPath is not null || !string.IsNullOrWhiteSpace(record.RawTranscript)) && await SaveRecordAsync(record).ConfigureAwait(false) && record.AudioPath is not null)
            {
                sink?.Complete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep the interrupted dictation");
        }

        if (!_machine.IsIdle)
        {
            _machine.Fire(DictationTrigger.Failed);
        }
    }

    private async Task FailAsync(DictationSession session, DictationRecord record, IAudioCapture? capture, IAudioSink? sink, IStreamingTranscriber? transcriber, Exception error, CancellationToken ct)
    {
        _logger.LogWarning(error, "Dictation failed");
        capture?.Stop();
        session.Frames.Writer.TryComplete();
        if (transcriber is not null)
        {
            await transcriber.AbortAsync().ConfigureAwait(false);
        }

        if (sink is not null && sink.BytesWritten > 0)
        {
            record.AudioPath = sink.Path; // finished below once the record is saved
        }
        else
        {
            sink?.Discard();
        }

        record.Status = RecordStatus.Failed;
        record.FailureReason = error.Message;
        record.RawTranscript = session.Assembler.FinalText;
        if (transcriber?.ConnectLatency is not null)
        {
            // The streaming session was open and billed; no summary, so the audio sent is the best estimate.
            record.CostEstimate ??= CostEstimator.SttCost(_settings.Current.SpeechModel, session.AudioDuration);
        }

        record.Tone = session.Tone;
        record.Level = session.Level;
        record.DurationMs = (int)session.AudioDuration.TotalMilliseconds;
        record.UpdatedAt = _time.GetUtcNow();
        if (!_machine.IsIdle)
        {
            _machine.Fire(DictationTrigger.Failed);
        }

        var isNetwork = error is System.Net.WebSockets.WebSocketException or TimeoutException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException || error.InnerException is System.Net.Sockets.SocketException;
        if (error is MicrophoneAccessDeniedException)
        {
            _notifier.Toast("Microphone blocked", "Allow microphone access in Windows privacy settings.", ToastKind.Error, MicrophonePrivacyUri);
        }
        else if (record.AudioPath is not null)
        {
            if (await SaveRecordAsync(record).ConfigureAwait(false))
            {
                sink?.Complete();
                _notifier.Toast(
                    isNetwork ? "Network error" : "Dictation failed",
                    isNetwork ? "Saved to History. Use Retry when you are back online." : error.Message + " The recording is saved in History.",
                    ToastKind.Warning);
            }
            else
            {
                // Left unfinished on purpose: the next start recovers it into History for Retry.
                _notifier.Toast("Dictation failed", error.Message + " History could not be written; the recording is kept and will be listed in History after a restart.", ToastKind.Warning);
            }
        }
        else if (!string.IsNullOrWhiteSpace(record.RawTranscript))
        {
            // No audio ("Store audio" off, or nothing recorded) but some words were heard: keep them in History,
            // where they can still be copied. Retry needs audio, so it stays unavailable for this record.
            _notifier.Toast(
                "Dictation failed",
                error.Message + (await SaveRecordAsync(record).ConfigureAwait(false) ? " What was heard so far is saved in History." : " History could not be written (see the log)."),
                ToastKind.Warning);
        }
        else
        {
            _notifier.Toast("Dictation failed", error.Message, ToastKind.Error);
        }

        PublishIdle(session, "Failed");
    }

    /// <summary>
    /// History's Re-insert: into the window that has focus now, with the same checks, spacing, paste keystroke and outcome
    /// toasts as a live dictation. The record itself is not changed.
    /// </summary>
    public async Task<InsertionResult> ReInsertAsync(string text, CancellationToken ct)
    {
        if (!_machine.IsIdle)
        {
            _notifier.Toast("Re-insert", "Finish the current dictation first.", ToastKind.Warning);
            return new InsertionResult(InsertionOutcome.Failed, "A dictation is running");
        }

        ct.ThrowIfCancellationRequested();
        var target = SafeCapture();

        // The stored text carries the spacing of where it first went; the new field decides afresh.
        var formatted = _formatter.Format(text.TrimStart(), target.WindowHandle, target.IsEditable ? SafeReadTextBeforeCaret(target) : null);
        var insertion = await DeliverAsync(formatted, target, PasteModeFor(target, _settings.Current)).ConfigureAwait(false);
        _logger.LogInformation("Re-insert into {Process}: {Outcome}", target.ProcessName, insertion.Outcome);
        return insertion;
    }

    /// <summary>
    /// Delivers <paramref name="text"/> into <paramref name="target"/>: a paste when it looks editable and is not
    /// elevated, the clipboard otherwise, and the clipboard as well when the paste fails. Shows the toast for a copy or a
    /// failure, and forgets the spacing memory of a window the text did not reach. Never cancelled half-way.
    /// </summary>
    private async Task<InsertionResult> DeliverAsync(string text, ForegroundContext target, PasteMode pasteMode)
    {
        InsertionResult insertion;
        var copiedAfterFailure = false;
        await _clipboardGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Copied text will be pasted by hand somewhere else, so it carries no joining space.
            insertion = target.IsEditable && !target.IsElevated
                ? await _inserter.InsertAsync(text, target, pasteMode, CancellationToken.None).ConfigureAwait(false)
                : await CopyAsync(text.TrimStart(), target.IsElevated ? "Target is elevated, text copied" : "No text box has focus, text copied").ConfigureAwait(false);
            if (insertion.Outcome == InsertionOutcome.Failed && target.IsEditable && !target.IsElevated)
            {
                copiedAfterFailure = (await CopyAsync(text.TrimStart(), null).ConfigureAwait(false)).Outcome == InsertionOutcome.CopiedOnly;
            }
        }
        finally
        {
            _clipboardGate.Release();
        }

        switch (insertion.Outcome)
        {
            case InsertionOutcome.CopiedOnly:
                _notifier.Toast("Copied to clipboard", insertion.Reason ?? "Paste it where you need it.", ToastKind.Info);
                break;
            case InsertionOutcome.Failed:
                _notifier.Toast("Could not insert", (insertion.Reason ?? "Unknown error") + (copiedAfterFailure ? ". Text copied to the clipboard." : ". Use Copy in History."), ToastKind.Warning);
                break;
        }

        if (insertion.Outcome != InsertionOutcome.Inserted)
        {
            _formatter.Reset(target.WindowHandle); // what is before the caret there is not what was remembered
        }

        return insertion;
    }

    private async Task<InsertionResult> CopyAsync(string text, string? reason)
    {
        try
        {
            await _clipboard.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false);
            return new InsertionResult(InsertionOutcome.CopiedOnly, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not put the text on the clipboard");
            return new InsertionResult(InsertionOutcome.Failed, AppendReason(reason, "the clipboard could not be set: " + ex.Message));
        }
    }

    /// <summary>
    /// What AssemblyAI bills: the time the streaming session was open, from its Termination summary; otherwise the
    /// measured fallback. The audio length alone misses the connection and handshake time and, for a paced replay,
    /// is not what was billed either.
    /// </summary>
    public static TimeSpan BilledDuration(TerminationMessage? termination, TimeSpan fallback) =>
        termination is { SessionDurationSeconds: > 0 } t ? TimeSpan.FromSeconds(t.SessionDurationSeconds) : fallback;

    /// <summary>A failure reason added after another (cleanup, then insertion) is appended, never overwrites it.</summary>
    private static string? AppendReason(string? existing, string? added) =>
        string.IsNullOrEmpty(existing) ? added : string.IsNullOrEmpty(added) ? existing : existing + "; " + added;

    /// <summary>
    /// A cleanup settings problem that will recur (rejected key, a model the key may not use, a retired model) is shown
    /// once per app run, even when a fallback model covered for it: otherwise the preferred model could silently stop
    /// being used, and with the Minimal Flow bar a rejected key would show nothing at all.
    /// </summary>
    private void ReportCleanupProblem(PostProcessResult result)
    {
        if (result.ConfigurationProblem is not { } problem)
        {
            return;
        }

        lock (_reportedCleanupProblems)
        {
            if (!_reportedCleanupProblems.Add(problem))
            {
                return;
            }
        }

        _notifier.Toast(result.Applied ? "Cleanup model problem" : "Cleanup skipped", problem, ToastKind.Warning);
    }

    /// <summary>False when History could not be written (logged); callers then say so instead of "saved".</summary>
    private async Task<bool> SaveRecordAsync(DictationRecord record)
    {
        try
        {
            if (record.Id == 0)
            {
                await _history.InsertAsync(record, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _history.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write history record");
            return false;
        }
    }

    private async Task BumpDictionaryUsageAsync(IReadOnlyList<string> keyterms, string text)
    {
        if (keyterms.Count == 0 || string.IsNullOrEmpty(text))
        {
            return;
        }

        var used = keyterms.Where(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (used.Count == 0)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            await _settings.UpdateAsync(s =>
            {
                foreach (var term in s.Dictionary.Where(t => used.Contains(t.Term.Trim())))
                {
                    term.UseCount++;
                    term.LastUsedAt = now;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update dictionary usage");
        }
    }

    private async Task WatchForSilentMicrophoneAsync(DictationSession session, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SilentMicWarningAfter, _time, ct).ConfigureAwait(false);
            if (_machine.State is DictationState.Arming or DictationState.Recording && session.FrameCount > 10 && session.MaxPeak < 1e-4f)
            {
                _logger.LogWarning("Microphone delivered {Frames} silent frames", session.FrameCount);
                _notifier.Toast("No sound from microphone", "Check the input device or Windows microphone privacy settings.", ToastKind.Warning, MicrophonePrivacyUri);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SendFramesAsync(ChannelReader<AudioFrame> frames, IStreamingTranscriber transcriber, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await transcriber.SendAudioAsync(frame.Pcm16, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>The paste mode the app rules give <paramref name="target"/> (used when text lands somewhere other than where it started).</summary>
    public static PasteMode PasteModeFor(ForegroundContext target, AppSettings settings) =>
        AppRulesResolver.Resolve(target, settings.AppRules, settings.DefaultTone, settings.DefaultCleanupLevel, settings.PasteMode).PasteMode;

    private SessionOptions BuildSessionOptions(AppSettings settings) => new()
    {
        SpeechModel = settings.SpeechModel,
        Keyterms = KeytermsSelector.Select(settings.Dictionary),
        LanguageCodes = string.IsNullOrWhiteSpace(settings.LanguageCodes) ? null : settings.LanguageCodes,
        MinTurnSilenceMs = DictationMinTurnSilenceMs,
        MaxTurnSilenceMs = DictationMaxTurnSilenceMs,
    };

    private string? SafeReadTextBeforeCaret(ForegroundContext target)
    {
        try
        {
            return _foreground.ReadTextBeforeCaret(target);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the text before the caret failed");
            return null;
        }
    }

    private ForegroundContext SafeCapture()
    {
        try
        {
            return _foreground.Capture();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foreground capture failed; assuming editable");
            return ForegroundContext.Unknown;
        }
    }

    /// <summary>
    /// True while <paramref name="session"/> may publish status: it is the current session, or there is none (a
    /// replacement gave up). A session a newer press has replaced (a tap still being cancelled) stays silent: the hotkey
    /// service reads the status stream, and that session's level updates and closing Idle would otherwise look like the
    /// new dictation answering its press and then ending, so the new press's tap was dropped and it recorded on with
    /// nothing able to stop it.
    /// </summary>
    private bool Publishes(DictationSession session) => Volatile.Read(ref _session) is not { } current || ReferenceEquals(current, session);

    private void PublishSession(DictationSession session, DictationState state, string? badge)
    {
        if (Publishes(session))
        {
            _hub.Publish(new DictationStatus(state, session.Assembler.LiveText, session.LastPeak, session.Tone, session.Level, badge, session.StartedAt, session.Context.ProcessName, _hotkeys.Enabled));
        }
    }

    /// <summary>A session's closing Idle, unless a newer press has replaced it (see <see cref="Publishes"/>).</summary>
    private void PublishIdle(DictationSession session, string? badge)
    {
        if (Publishes(session))
        {
            PublishIdle(badge);
        }
    }

    private void PublishIdle(string? badge)
    {
        var s = _settings.Current;
        _hub.Publish(DictationStatus.Idle with { Tone = s.DefaultTone, CleanupLevel = s.DefaultCleanupLevel, Badge = badge, HotkeyEnabled = _hotkeys.Enabled });
    }

    private void ApplyHotkeySettings(AppSettings settings)
    {
        try
        {
            _hotkeys.Configure(settings.HotkeyChord);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not apply hotkey {Chord}", settings.Hotkey);
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        ApplyHotkeySettings(settings);
        if (_session is { } s && _machine.State is DictationState.Recording)
        {
            _ = s.PushKeytermsAsync(KeytermsSelector.Select(settings.Dictionary), _logger);
        }

        if (_machine.IsIdle)
        {
            PublishIdle(_hub.Current.Badge);
        }
    }

    /// <summary>
    /// True when a style change can be remembered at once. Before the app rule is resolved it is only noted, and
    /// the session remembers it against the right rule (or the defaults) once resolved. Call under StyleLock.
    /// </summary>
    private static bool CanRememberStyleNow(DictationSession session, bool tone, bool level)
    {
        if (session.RuleResolved)
        {
            return true;
        }

        session.ToneChangedBeforeRule |= tone;
        session.LevelChangedBeforeRule |= level;
        return false;
    }

    /// <summary>
    /// Persists a tone/level change so the next dictation in the same context starts from it: the matched
    /// app rule is updated, or the global defaults when no rule matched. Off when RememberStyleChanges is false.
    /// </summary>
    private void RememberStyle(DictationSession session)
    {
        if (!_settings.Current.RememberStyleChanges)
        {
            return;
        }

        var matched = session.Rule.MatchedRule;
        _ = Task.Run(async () =>
        {
            // Each change starts one of these saves and they may finish in any order, so each writes the session's
            // latest tone and level rather than the values of the change that started it: the last to run wins with
            // the newest choice, never an older one.
            var tone = session.Tone;
            var level = session.Level;
            try
            {
                await _settings.UpdateAsync(s =>
                {
                    tone = session.Tone;
                    level = session.Level;
                    var rule = matched is null
                        ? null
                        : s.AppRules.FirstOrDefault(r =>
                            string.Equals(r.ProcessGlob, matched.ProcessGlob, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.UrlHost, matched.UrlHost, StringComparison.OrdinalIgnoreCase));
                    if (rule is not null)
                    {
                        rule.Tone = tone;
                        rule.Level = level;
                    }
                    else
                    {
                        s.DefaultTone = tone;
                        s.DefaultCleanupLevel = level;
                    }
                }).ConfigureAwait(false);
                _logger.LogInformation("Remembered tone={Tone} level={Level} for {Scope}", tone, level, matched?.DisplayTarget ?? "defaults");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remember the style change");
            }
        });
    }

    private void OnChordDown() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordDown));

    private void OnChordUp(ChordRelease release) => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordUp, Release: release));

    private void OnEscape() => _events.Writer.TryWrite(new ControlEvent(ControlKind.Escape));

    private void OnArrow(ArrowDirection direction) => _events.Writer.TryWrite(new ControlEvent(ControlKind.Arrow, direction));

    private void OnToneOverride(Tone tone) => _events.Writer.TryWrite(new ControlEvent(ControlKind.ToneOverride, Tone: tone));

    private void OnLevelOverride(CleanupLevel level) => _events.Writer.TryWrite(new ControlEvent(ControlKind.LevelOverride, Level: level));

    private static int CountWords(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private enum ControlKind
    {
        ChordDown,
        ChordUp,
        Escape,
        Arrow,
        ToneOverride,
        LevelOverride,
        SimulatedStart,
    }

    private sealed record ControlEvent(ControlKind Kind, ArrowDirection? Arrow = null, Tone? Tone = null, CleanupLevel? Level = null, string? WavPath = null, ChordRelease Release = ChordRelease.Hold);

    /// <summary>Mutable per-dictation state shared between the control loop and the session task.</summary>
    private sealed class DictationSession(DateTimeOffset startedAt)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _escaped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(string Phase, long Ms)> _marks = [];
        private int _releaseKind = -1; // -1 not released, 0 hold, 1 tap
        private long _bytes;
        private IStreamingTranscriber? _transcriber;

        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>When set, this WAV replaces the microphone (test/simulation mode).</summary>
        public string? SimulatedWavPath { get; init; }

        public Channel<AudioFrame> Frames { get; } = Channel.CreateUnbounded<AudioFrame>(new UnboundedChannelOptions { SingleReader = true });

        public TranscriptAssembler Assembler { get; } = new();

        public ForegroundContext Context { get; set; } = ForegroundContext.Unknown;

        public ResolvedRule Rule { get; set; } = new(Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV, null, "default");

        /// <summary>Guards Rule, Tone and Level while the rule is resolved alongside arrow keys and chips.</summary>
        public object StyleLock { get; } = new();

        public bool RuleResolved { get; set; }

        /// <summary>The user changed the tone before the app rule was known, so the rule's tone does not apply.</summary>
        public bool ToneChangedBeforeRule { get; set; }

        /// <summary>The user changed the cleanup level before the app rule was known.</summary>
        public bool LevelChangedBeforeRule { get; set; }

        public Tone Tone { get; set; }

        public CleanupLevel Level { get; set; }

        public float LastPeak { get; private set; }

        public float MaxPeak { get; private set; }

        public int FrameCount { get; private set; }

        public TimeSpan AudioDuration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _bytes) / (AudioFrame.SampleRate * 2));

        public Task Released => _released.Task;

        public Task Escaped => _escaped.Task;

        public Task<Exception> Faulted => _faulted.Task;

        /// <summary>Notes when a phase was reached, for the one timing line logged per dictation (no text).</summary>
        public void Mark(string phase)
        {
            lock (_marks)
            {
                if (!_marks.Exists(m => m.Phase == phase))
                {
                    _marks.Add((phase, _clock.ElapsedMilliseconds));
                }
            }
        }

        /// <summary>For example "mic 21, lookup 180, begin 902, release 3410, handshake 4120, cleanup 4800, insert 5130".</summary>
        public string Timings()
        {
            lock (_marks)
            {
                return string.Join(", ", _marks.Select(m => $"{m.Phase} {m.Ms}"));
            }
        }

        /// <summary>True when the first release of this session was a lone tap (the dictation is being cancelled).</summary>
        public bool ReleasedAsTap => Volatile.Read(ref _releaseKind) == 1;

        /// <summary>The first release decides; later ones (a stray key-up, the tray) change nothing.</summary>
        public void Release(bool tap = false)
        {
            if (Interlocked.CompareExchange(ref _releaseKind, tap ? 1 : 0, -1) == -1)
            {
                _released.TrySetResult();
            }
        }

        public void Escape() => _escaped.TrySetResult();

        public void Fault(Exception ex) => _faulted.TrySetResult(ex);

        public void OnFrame(AudioFrame frame)
        {
            Interlocked.Add(ref _bytes, frame.Pcm16.Length);
            LastPeak = frame.Peak;
            MaxPeak = Math.Max(MaxPeak, frame.Peak);
            FrameCount++;
        }

        public void AttachTranscriber(IStreamingTranscriber transcriber) => _transcriber = transcriber;

        public async Task PushKeytermsAsync(IReadOnlyList<string> keyterms, ILogger logger)
        {
            if (_transcriber is not { IsConnected: true } t)
            {
                return;
            }

            try
            {
                await t.UpdateConfigurationAsync(keyterms, null, CancellationToken.None).ConfigureAwait(false);
                logger.LogInformation("Pushed {Count} keyterms mid-session", keyterms.Count);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "UpdateConfiguration failed");
            }
        }
    }
}

public sealed record WavTranscription(string Text, TimeSpan AudioDuration, TimeSpan? ConnectLatency, TerminationMessage? Termination, TimeSpan WallClock, string? PauseMarkedText = null);
