using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Plays a WAV file into the pipeline as if it were a microphone, at <c>speed</c>× real time (4× for
/// history Retry, 1× for <c>--stream-test</c>). Raises <see cref="Completed"/> at end of file.
/// </summary>
public sealed class WavReplayAudioCapture : IAudioCapture
{
    private readonly string _path;
    private readonly double _speed;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public WavReplayAudioCapture(string path, double speed, ILogger logger)
    {
        _path = path;
        _speed = speed <= 0 ? 1.0 : speed;
        _logger = logger;
    }

    public event Action<AudioFrame>? FrameCaptured;

    public event Action<Exception>? Faulted;

    public event Action? Completed;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "WavReplay" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private void Run(CancellationToken ct)
    {
        try
        {
            using var reader = new AudioFileReader(_path);
            var pipeline = new Pcm16Pipeline(reader.WaveFormat);
            var chunk = new byte[reader.WaveFormat.AverageBytesPerSecond / 10]; // 100 ms of source audio
            var frameInterval = TimeSpan.FromMilliseconds(100 / _speed);
            var next = DateTime.UtcNow;
            int read;
            while (!ct.IsCancellationRequested && (read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                pipeline.Push(chunk, 0, read, frame => FrameCaptured?.Invoke(frame));
                next += frameInterval;
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                pipeline.Flush(frame => FrameCaptured?.Invoke(frame));
                IsRunning = false;
                Completed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAV replay failed for {Path}", _path);
            IsRunning = false;
            Faulted?.Invoke(ex);
        }
    }
}
