namespace DictationApp.Core.Abstractions;

/// <summary>One frame of captured audio: 16 kHz, mono, PCM16 little-endian, nominally 100 ms (3200 bytes).</summary>
public readonly record struct AudioFrame(ReadOnlyMemory<byte> Pcm16, float Peak, TimeSpan Position)
{
    public const int SampleRate = 16_000;
    public const int BytesPerFrame = SampleRate * 2 / 10; // 100 ms of PCM16 mono
}

/// <summary>Source of 100 ms PCM16 frames. Implementations: WASAPI microphone, WAV replay (retry / tests).</summary>
public interface IAudioCapture : IDisposable
{
    event Action<AudioFrame>? FrameCaptured;

    /// <summary>Raised when capture cannot continue (device removed, access denied, replay finished with error).</summary>
    event Action<Exception>? Faulted;

    /// <summary>Raised once when the source has no more data (WAV replay). Never raised by a microphone.</summary>
    event Action? Completed;

    bool IsRunning { get; }

    void Start();

    void Stop();
}

public sealed class MicrophoneAccessDeniedException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAudioCaptureFactory
{
    /// <summary>Live microphone. <paramref name="deviceId"/> null means the default communications device.</summary>
    IAudioCapture CreateMicrophone(string? deviceId);

    /// <summary>Replays a WAV file as if it were spoken, at <paramref name="speed"/>× real time.</summary>
    IAudioCapture CreateWavReplay(string wavPath, double speed);
}

/// <summary>Persists PCM16 frames to a WAV file for history playback and retry.</summary>
public interface IAudioSink : IDisposable
{
    string Path { get; }

    long BytesWritten { get; }

    void Write(ReadOnlySpan<byte> pcm16);

    /// <summary>Flush and close; the file is kept.</summary>
    void Complete();

    /// <summary>Close and delete the file.</summary>
    void Discard();
}

public interface IAudioSinkFactory
{
    IAudioSink Create(string path);
}
