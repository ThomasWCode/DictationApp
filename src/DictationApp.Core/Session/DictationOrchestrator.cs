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
        _machine = machine ?? new DictationStateMachine(_time);
        _machine.ChordDownIgnored += () => _hub.Flash();
        _machine.Transitioned += t => _logger.LogDebug("State {From} --{Trigger}--> {To}", t.From, t.Trigger, t.To);
    }

    public DictationState State => _machine.State;

    /// <summary>Exposed for tests and the tray: the task running the current session, if any.</summary>
    public Task? CurrentSessionTask => _sessionTask;

    /// <summary>Programmatic chord press (tray menu, tests).</summary>
    public void SimulateChordDown() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordDown));

    public void SimulateChordUp() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordUp));

    public void SimulateEscape() => _events.Writer.TryWrite(new ControlEvent(ControlKind.Escape));

    /// <summary>
    /// Runs a full dictation (context capture, streaming, cleanup, insertion) with a WAV file standing in
    /// for the microphone. The session releases itself when the file ends. Used by <c>--simulate</c>.
    /// </summary>
    public void SimulateDictationFromWav(string wavPath) => _events.Writer.TryWrite(new ControlEvent(ControlKind.SimulatedStart, WavPath: wavPath));

    /// <summary>
    /// Re-streams a stored WAV through the transcriber (4× real time), cleans it up and copies the result to
    /// the clipboard. Used by "Retry" in History for Failed/Pending records.
    /// </summary>
    public async Task<bool> RetryAsync(long recordId, CancellationToken ct)
    {
        var record = await _history.GetAsync(recordId, ct).ConfigureAwait(false);
        if (record is null || string.IsNullOrEmpty(record.AudioPath) || !File.Exists(record.AudioPath))
        {
            _notifier.Toast("Retry", "No audio is stored for this dictation.", ToastKind.Warning);
            return false;
        }

        if (!_machine.IsIdle)
        {
            _notifier.Toast("Retry", "Finish the current dictation first.", ToastKind.Warning);
            return false;
        }

        try
        {
            var settings = _settings.Current;
            var transcript = await TranscribeWavAsync(record.AudioPath, 4.0, null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                record.FailureReason = "retry: nothing heard";
                await _history.UpdateAsync(record, ct).ConfigureAwait(false);
                _notifier.Toast("Retry", "Nothing was heard in the recording.", ToastKind.Warning);
                return false;
            }

            var request = new PostProcessRequest(record.Level, record.Tone, KeytermsSelector.Select(settings.Dictionary), record.ProcessName ?? string.Empty, record.Url, null, PauseMarkedTranscript: transcript.PauseMarkedText);
            var result = await _postProcessor.ProcessAsync(transcript.Text, request, ct).ConfigureAwait(false);
            await _clipboard.SetTextAsync(result.Text, ct).ConfigureAwait(false);
            record.RawTranscript = transcript.Text;
            record.CleanedText = result.Applied ? result.Text : string.Empty;
            record.InsertedText = result.Text;
            record.LlmModel = result.Model;
            record.Status = RecordStatus.CopiedOnly;
            record.FailureReason = result.Applied ? null : result.FailureReason;
            record.UpdatedAt = _time.GetUtcNow();
            record.CostEstimate = (record.CostEstimate ?? 0m) + CostEstimator.SttCost(settings.SpeechModel, transcript.AudioDuration) + CostEstimator.LlmCost(result.Model, result.PromptTokens, result.CompletionTokens);
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
            _session?.Escape();
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
                if (_machine.IsIdle)
                {
                    // The second tap of a double tap arrives while the first tap's session is still being torn
                    // down (socket abort, WAV discard). Chain behind it instead of flashing "busy".
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

                break;
            case ControlKind.SimulatedStart:
                if (_machine.IsIdle && (_sessionTask is null || _sessionTask.IsCompleted) && ev.WavPath is not null)
                {
                    var simulated = new DictationSession(_time.GetUtcNow()) { SimulatedWavPath = ev.WavPath };
                    _session = simulated;
                    _sessionTask = Task.Run(() => RunSessionAsync(simulated, ct), CancellationToken.None);
                }

                break;
            case ControlKind.ChordUp:
                _session?.Release();
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
        var settings = _settings.Current;
        IAudioCapture? capture = null;
        IAudioSink? sink = null;
        IStreamingTranscriber? transcriber = null;
        Task? sender = null;
        var record = new DictationRecord { CreatedAt = session.StartedAt, UpdatedAt = session.StartedAt, Status = RecordStatus.Pending };
        try
        {
            _machine.Fire(DictationTrigger.ChordDown);

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
                capture.Completed += session.Release;
            }

            capture.FrameCaptured += frame =>
            {
                var copy = new AudioFrame(frame.Pcm16.ToArray(), frame.Peak, frame.Position);
                sink?.Write(copy.Pcm16.Span);
                session.Frames.Writer.TryWrite(copy);
                session.OnFrame(copy);
                _hub.Update(s => s with { Level = copy.Peak });
            };
            capture.Faulted += session.Fault;
            capture.Start();
            session.Tone = settings.DefaultTone;
            session.Level = settings.DefaultCleanupLevel;
            PublishSession(session, DictationState.Arming, "Listening");

            // 2. Socket: its ~0.8 s TLS and session setup overlaps the window lookup below.
            transcriber = _transcribers.Create();
            session.AttachTranscriber(transcriber);
            transcriber.TurnReceived += turn =>
            {
                if (session.Assembler.Ingest(turn))
                {
                    _hub.Update(s => s with { LiveText = session.Assembler.LiveText });
                }
            };
            transcriber.Faulted += session.Fault;
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            var connectTask = transcriber.ConnectAsync(BuildSessionOptions(settings), connectCts.Token);

            // 3. Where the user is: target window, editability, browser URL and app rule (tone and level).
            session.Context = SafeCapture();
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
            if (first == session.Escaped)
            {
                await DiscardAsync(session, capture, sink, transcriber, "Discarded").ConfigureAwait(false);
                return;
            }

            if (first == session.Faulted)
            {
                await FailAsync(session, record, capture, sink, transcriber, session.Faulted.Result, ct).ConfigureAwait(false);
                return;
            }

            if (first == session.Released)
            {
                var next = _machine.Fire(DictationTrigger.ChordUp);
                if (next == DictationState.Idle)
                {
                    _logger.LogInformation("Short tap ({Held} ms): cancelled before billing", (int)(_time.GetUtcNow() - session.StartedAt).TotalMilliseconds);
                    await DiscardAsync(session, capture, sink, transcriber, null).ConfigureAwait(false);
                    return;
                }

                // Released after a real hold but before Begin: give the connection its remaining budget.
                PublishSession(session, DictationState.Finalising, "Connecting");
                try
                {
                    await connectTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || connectCts.IsCancellationRequested)
                {
                    _machine.Fire(DictationTrigger.Failed);
                    await FailAsync(session, record, capture, sink, transcriber, ex is OperationCanceledException ? new TimeoutException("Connection timed out") : ex, ct).ConfigureAwait(false);
                    return;
                }

                capture.Stop();
                session.Frames.Writer.TryComplete();
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
                    _machine.Fire(ex is OperationCanceledException ? DictationTrigger.ConnectTimeout : DictationTrigger.SocketFault);
                    await FailAsync(session, record, capture, sink, transcriber, ex is OperationCanceledException ? new TimeoutException("Connection timed out") : ex, ct).ConfigureAwait(false);
                    return;
                }

                _machine.Fire(DictationTrigger.BeginReceived);
                _logger.LogInformation("Connect latency {Latency} ms", transcriber.ConnectLatency?.TotalMilliseconds ?? -1);
                PublishSession(session, DictationState.Recording, null);
                sender = SendFramesAsync(session.Frames.Reader, transcriber, ct);

                using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var capTask = Task.Delay(settings.MaxDictationDuration, _time, capCts.Token);
                var end = await Task.WhenAny(session.Released, session.Escaped, session.Faulted, capTask).ConfigureAwait(false);
                capCts.Cancel();
                if (end == session.Escaped)
                {
                    await DiscardAsync(session, capture, sink, transcriber, "Discarded").ConfigureAwait(false);
                    return;
                }

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

            // A socket fault after the release is no longer being awaited; without a Termination summary the
            // transcript may be missing its tail, so save it as Failed (audio kept for Retry) rather than paste it.
            if (termination is null && session.Faulted.IsCompleted)
            {
                await FailAsync(session, record, capture, sink, transcriber, session.Faulted.Result, ct).ConfigureAwait(false);
                return;
            }
            var audioSeconds = termination?.AudioDurationSeconds ?? session.AudioDuration.TotalSeconds;
            record.DurationMs = (int)(session.AudioDuration.TotalMilliseconds);
            record.CostEstimate = CostEstimator.SttCost(settings.SpeechModel, TimeSpan.FromSeconds(audioSeconds));
            var rawText = session.Assembler.FinalText;
            record.RawTranscript = rawText;
            if (!session.Assembler.HasOpenTurn && session.Assembler.ClosedText != rawText)
            {
                _logger.LogInformation("Trailing partial included in final text");
            }

            if (sink is not null)
            {
                sink.Complete();
                record.AudioPath = sink.Path;
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _machine.Fire(DictationTrigger.NothingHeard);
                sink?.Discard();
                record.AudioPath = null;
                _logger.LogInformation("Nothing heard");
                PublishIdle("Nothing heard");
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
            InsertionResult insertion;
            if (target.IsEditable && !target.IsElevated)
            {
                insertion = await _inserter.InsertAsync(text, target, pasteMode, ct).ConfigureAwait(false);
            }
            else
            {
                await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
                insertion = new InsertionResult(InsertionOutcome.CopiedOnly, target.IsElevated ? "Target is elevated, text copied" : "No text box has focus, text copied");
            }

            switch (insertion.Outcome)
            {
                case InsertionOutcome.Inserted:
                    record.Status = RecordStatus.Inserted;
                    _machine.Fire(DictationTrigger.Inserted);
                    break;
                case InsertionOutcome.CopiedOnly:
                    record.Status = RecordStatus.CopiedOnly;
                    _notifier.Toast("Copied to clipboard", insertion.Reason ?? "Paste it where you need it.", ToastKind.Info);
                    _machine.Fire(DictationTrigger.CopiedOnly);
                    break;
                default:
                    record.Status = RecordStatus.Failed;
                    record.FailureReason = insertion.Reason;
                    await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
                    _notifier.Toast("Could not insert", (insertion.Reason ?? "Unknown error") + ". Text copied to the clipboard.", ToastKind.Warning);
                    _machine.Fire(DictationTrigger.Failed);
                    break;
            }

            record.UpdatedAt = _time.GetUtcNow();
            await SaveRecordAsync(record).ConfigureAwait(false);
            _ = BumpDictionaryUsageAsync(keyterms, rawText);
            PublishIdle(badge);
            _logger.LogInformation("Dictation {Status}: {Words} words, {Duration:0.0}s audio, llm={Model}", record.Status, CountWords(text), audioSeconds, result.Model ?? "none");
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

            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            if (_hub.Current.State != DictationState.Idle)
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

        PublishIdle(badge);
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
            sink.Complete();
            record.AudioPath = sink.Path;
        }
        else
        {
            sink?.Discard();
        }

        record.Status = RecordStatus.Failed;
        record.FailureReason = error.Message;
        record.RawTranscript = session.Assembler.FinalText;
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
            await SaveRecordAsync(record).ConfigureAwait(false);
            if (isNetwork)
            {
                _notifier.Toast("Network error", "Saved to history. Use Retry when you are back online.", ToastKind.Warning);
            }
            else
            {
                _notifier.Toast("Dictation failed", error.Message + " The recording is saved in History.", ToastKind.Warning);
            }
        }
        else if (!string.IsNullOrWhiteSpace(record.RawTranscript))
        {
            // No audio ("Store audio" off, or nothing recorded) but some words were heard: keep them in History,
            // where they can still be copied. Retry needs audio, so it stays unavailable for this record.
            await SaveRecordAsync(record).ConfigureAwait(false);
            _notifier.Toast("Dictation failed", error.Message + " What was heard so far is saved in History.", ToastKind.Warning);
        }
        else
        {
            _notifier.Toast("Dictation failed", error.Message, ToastKind.Error);
        }

        PublishIdle("Failed");
    }

    private async Task SaveRecordAsync(DictationRecord record)
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write history record");
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

    private void PublishSession(DictationSession session, DictationState state, string? badge) =>
        _hub.Publish(new DictationStatus(state, session.Assembler.LiveText, session.LastPeak, session.Tone, session.Level, badge, session.StartedAt, session.Context.ProcessName, _hotkeys.Enabled));

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

    private void OnChordUp() => _events.Writer.TryWrite(new ControlEvent(ControlKind.ChordUp));

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

    private sealed record ControlEvent(ControlKind Kind, ArrowDirection? Arrow = null, Tone? Tone = null, CleanupLevel? Level = null, string? WavPath = null);

    /// <summary>Mutable per-dictation state shared between the control loop and the session task.</summary>
    private sealed class DictationSession(DateTimeOffset startedAt)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _escaped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        public void Release() => _released.TrySetResult();

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
