namespace DictationApp.Core.Transcription;

/// <summary>
/// One streaming session. Create → Connect → Send audio → Shutdown. Events are raised on the receive loop
/// thread and must return quickly.
/// </summary>
public interface IStreamingTranscriber : IAsyncDisposable
{
    event Action<TurnMessage>? TurnReceived;

    /// <summary>Raised when the socket closes for a reason other than our own shutdown.</summary>
    event Action<Exception>? Faulted;

    bool IsConnected { get; }

    /// <summary>Wall-clock time from socket connect to the server's Begin message, for latency logging.</summary>
    TimeSpan? ConnectLatency { get; }

    /// <summary>Opens the socket and waits for the Begin message.</summary>
    Task<BeginMessage> ConnectAsync(SessionOptions options, CancellationToken ct);

    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);

    Task UpdateConfigurationAsync(IReadOnlyList<string>? keyterms, string? prompt, CancellationToken ct);

    /// <summary>
    /// Graceful shutdown: silence tail → ForceEndpoint → wait for end_of_turn → Terminate → wait for
    /// Termination, bounded by <paramref name="hardCap"/>. Returns the Termination summary when received.
    /// </summary>
    Task<TerminationMessage?> ShutdownAsync(TimeSpan hardCap, CancellationToken ct);

    /// <summary>Closes the socket immediately without the handshake (cancel / short tap).</summary>
    Task AbortAsync();
}

public interface IStreamingTranscriberFactory
{
    IStreamingTranscriber Create();
}
