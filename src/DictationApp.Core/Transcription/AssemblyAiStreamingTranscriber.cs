using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.Transcription;

/// <summary>
/// ClientWebSocket wrapper for <c>wss://streaming.assemblyai.com/v3/ws</c>. Sends are serialised through a
/// semaphore because ClientWebSocket allows one outstanding send at a time. The receive loop parses
/// messages and raises events; it never blocks on user code beyond the event handlers.
/// </summary>
public sealed class AssemblyAiStreamingTranscriber : IStreamingTranscriber
{
    public static readonly Uri DefaultEndpoint = new("wss://streaming.assemblyai.com/v3/ws");
    private static readonly TimeSpan SilenceTail = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EndOfTurnWaitOpen = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan EndOfTurnWaitClosed = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TerminationWait = TimeSpan.FromMilliseconds(1000);

    private readonly IApiKeyProvider _keys;
    private readonly ILogger _logger;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<BeginMessage> _beginTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TerminationMessage> _terminationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? _ws;
    private Task? _receiveLoop;
    private volatile TaskCompletionSource<bool>? _endOfTurnTcs;
    private volatile bool _hasOpenTurn;
    private volatile bool _closing;

    /// <summary>The socket failure, remembered even while closing so shutdown can report a truncated session.</summary>
    private volatile Exception? _failure;
    private int _disposed;

    public AssemblyAiStreamingTranscriber(IApiKeyProvider keys, ILogger<AssemblyAiStreamingTranscriber> logger, Uri? endpoint = null)
    {
        _keys = keys;
        _logger = logger;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    public event Action<TurnMessage>? TurnReceived;

    public event Action<Exception>? Faulted;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public TimeSpan? ConnectLatency { get; private set; }

    public string? SessionId { get; private set; }

    public async Task<BeginMessage> ConnectAsync(SessionOptions options, CancellationToken ct)
    {
        var key = _keys.GetApiKey() ?? throw new InvalidOperationException("No AssemblyAI API key is configured.");
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", key);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _ws = ws;

        var uri = new Uri(_endpoint + options.BuildQueryString());
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Connecting to {Endpoint} model={Model} keyterms={Keyterms}", _endpoint, options.SpeechModel, options.Keyterms.Count);
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(ws, _lifetime.Token), CancellationToken.None);

        var begin = await _beginTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        ConnectLatency = sw.Elapsed;
        SessionId = begin.Id;
        _logger.LogInformation("Streaming session {Session} began after {Latency} ms", begin.Id, sw.ElapsedMilliseconds);
        return begin;
    }

    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open } || pcm16.IsEmpty)
        {
            return;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(pcm16, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (WebSocketException ex)
        {
            OnFault(ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task UpdateConfigurationAsync(IReadOnlyList<string>? keyterms, string? prompt, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["type"] = "UpdateConfiguration" };
        if (keyterms is not null)
        {
            payload["keyterms_prompt"] = keyterms;
        }

        if (prompt is not null)
        {
            payload["prompt"] = prompt.Length > SessionOptions.MaxPromptLength ? prompt[..SessionOptions.MaxPromptLength] : prompt;
        }

        return SendJsonAsync(payload, ct);
    }

    public async Task<TerminationMessage?> ShutdownAsync(TimeSpan hardCap, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
        {
            // The socket died after the user released: the transcript may be missing its tail.
            if (_failure is { } lost)
            {
                throw new WebSocketException("Streaming connection lost: " + lost.Message, lost);
            }

            return null;
        }

        _closing = true;
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(hardCap);
        var token = cap.Token;
        var sw = Stopwatch.StartNew();
        TerminationMessage? termination = null;
        try
        {
            // 1. Silence tail so the server's VAD sees the end of speech.
            var silence = new byte[AudioFrame.SampleRate * 2 * (int)SilenceTail.TotalMilliseconds / 1000];
            await SendAudioAsync(silence, token).ConfigureAwait(false);

            // 2. ForceEndpoint and wait for the closing Turn.
            var hadOpenTurn = _hasOpenTurn;
            var eot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _endOfTurnTcs = eot;
            await SendJsonAsync(new { type = "ForceEndpoint" }, token).ConfigureAwait(false);
            try
            {
                await eot.Task.WaitAsync(hadOpenTurn ? EndOfTurnWaitOpen : EndOfTurnWaitClosed, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("No end_of_turn after ForceEndpoint within budget (open turn: {Open})", hadOpenTurn);
            }

            // 3. Terminate and wait for the summary.
            await SendJsonAsync(new { type = "Terminate" }, token).ConfigureAwait(false);
            try
            {
                termination = await _terminationTcs.Task.WaitAsync(TerminationWait, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation("No Termination message within budget");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Shutdown handshake hit the {Cap} ms hard cap", hardCap.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Shutdown handshake failed");
            _failure ??= ex;
        }
        finally
        {
            _logger.LogDebug("Shutdown handshake took {Elapsed} ms", sw.ElapsedMilliseconds);
            await CloseSocketAsync(ws).ConfigureAwait(false);
        }

        // No summary and the socket failed on the way: report it so the session is saved as Failed with its audio
        // (Retry) instead of pasting a possibly truncated transcript.
        if (termination is null && _failure is { } failure)
        {
            throw new WebSocketException("Streaming connection lost during shutdown: " + failure.Message, failure);
        }

        return termination;
    }

    public Task AbortAsync()
    {
        _closing = true;
        try
        {
            _ws?.Abort();
        }
        catch (ObjectDisposedException)
        {
        }

        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await AbortAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Receive loop is best-effort on dispose.
            }
        }

        _ws?.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseSocketAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The server may already have closed; nothing to do.
        }
        finally
        {
            _lifetime.Cancel();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var message = new ArrayBufferWriter<byte>(16 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                message.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogDebug("Server closed the socket: {Status} {Description}", ws.CloseStatus, ws.CloseStatusDescription);
                        if (ws.CloseStatus is not WebSocketCloseStatus.NormalClosure)
                        {
                            _failure ??= new WebSocketException($"Server closed the stream: {ws.CloseStatus} {ws.CloseStatusDescription}");
                        }

                        if (!_closing)
                        {
                            OnFault(new WebSocketException($"Server closed the stream: {ws.CloseStatus} {ws.CloseStatusDescription}"));
                        }

                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                Dispatch(message.WrittenSpan);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _failure ??= ex;
            if (!_closing)
            {
                OnFault(ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _beginTcs.TrySetException(new WebSocketException("Socket closed before Begin"));
            _terminationTcs.TrySetCanceled();
            _endOfTurnTcs?.TrySetCanceled();
        }
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        StreamingMessage? msg;
        try
        {
            msg = StreamingMessageParser.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unparseable streaming message ({Bytes} bytes)", json.Length);
            return;
        }

        switch (msg)
        {
            case BeginMessage begin:
                _beginTcs.TrySetResult(begin);
                break;
            case TurnMessage turn:
                // Length only: log files are kept 14 days, longer than the history retention may allow for dictated text.
                _logger.LogDebug("Turn {Order} eot={Eot} fmt={Fmt} conf={Conf:0.00} chars={Chars}", turn.TurnOrder, turn.EndOfTurn, turn.TurnIsFormatted, turn.EndOfTurnConfidence, turn.BestText.Length);
                _hasOpenTurn = !turn.EndOfTurn;
                if (turn.EndOfTurn)
                {
                    _endOfTurnTcs?.TrySetResult(true);
                }

                TurnReceived?.Invoke(turn);
                break;
            case TerminationMessage termination:
                _logger.LogInformation("Session ended: audio {Audio:0.0}s, session {Session:0.0}s", termination.AudioDurationSeconds, termination.SessionDurationSeconds);
                _terminationTcs.TrySetResult(termination);
                break;
            case ErrorMessage error:
                OnFault(new InvalidOperationException("AssemblyAI streaming error: " + error.Error));
                break;
        }
    }

    private void OnFault(Exception ex)
    {
        _logger.LogWarning(ex, "Streaming transcriber fault");
        _failure ??= ex;
        _beginTcs.TrySetException(ex);

        // A fault (e.g. a typed error frame) during the shutdown handshake must end it now, not after its waits
        // time out; ShutdownAsync then reports the failure instead of returning a partial transcript as complete.
        _endOfTurnTcs?.TrySetException(ex);
        _terminationTcs.TrySetException(ex);
        Faulted?.Invoke(ex);
    }
}
