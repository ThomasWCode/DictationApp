using System.IO;
using DictationApp.Core.Abstractions;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

/// <summary>Writes 16 kHz mono PCM16 frames to a WAV file. Thread-safe; frames arrive on the capture thread.</summary>
public sealed class WavFileSink : IAudioSink
{
    private readonly object _lock = new();
    private WaveFileWriter? _writer;
    private bool _closed;

    public WavFileSink(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new WaveFileWriter(path, new WaveFormat(AudioFrame.SampleRate, 16, 1));
    }

    public string Path { get; }

    public long BytesWritten { get; private set; }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        lock (_lock)
        {
            if (_closed || _writer is null)
            {
                return;
            }

            _writer.Write(pcm16);
            BytesWritten += pcm16.Length;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Discard()
    {
        lock (_lock)
        {
            _closed = true;
            _writer?.Dispose();
            _writer = null;
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Best effort; retention will sweep it later.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_closed)
            {
                Complete();
            }
        }
    }
}

public sealed class WavFileSinkFactory : IAudioSinkFactory
{
    public IAudioSink Create(string path) => new WavFileSink(path);
}
