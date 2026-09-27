using System.IO;
using DictationApp.Core.Abstractions;
using DictationApp.Core.History;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Writes 16 kHz mono PCM16 frames to a WAV file. Thread-safe; frames arrive on the capture thread.
/// <para>
/// The header's sizes are written only by <see cref="Complete"/>, which the session calls once the History record that
/// points to the file is saved. A file left by a crash, a kill or a power cut therefore still carries sizes of zero,
/// and <see cref="HistoryMaintenance"/> recovers it at the next start instead of deleting it as an orphan.
/// <see cref="Dispose"/> without <see cref="Complete"/> keeps that marker: it only flushes and closes.
/// </para>
/// </summary>
public sealed class WavFileSink : IAudioSink
{
    private readonly object _lock = new();
    private FileStream? _stream;
    private bool _closed;

    public WavFileSink(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _stream.Write(WavHeader.Create(dataBytes: 0)); // sizes of zero until Complete: the "unfinished" marker
    }

    public string Path { get; }

    public long BytesWritten { get; private set; }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        lock (_lock)
        {
            if (_closed || _stream is null)
            {
                return;
            }

            _stream.Write(pcm16);
            BytesWritten += pcm16.Length;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            if (_closed || _stream is null)
            {
                return;
            }

            _closed = true;
            var stream = _stream;
            _stream = null;
            try
            {
                stream.Position = 0;
                stream.Write(WavHeader.Create(BytesWritten));
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    public void Discard()
    {
        lock (_lock)
        {
            _closed = true;
            _stream?.Dispose();
            _stream = null;
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

    /// <summary>Closes the file. Without <see cref="Complete"/> its header keeps the "unfinished" marker (see above).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _stream?.Flush();
            _stream?.Dispose();
            _stream = null;
        }
    }
}

public sealed class WavFileSinkFactory : IAudioSinkFactory
{
    public IAudioSink Create(string path) => new WavFileSink(path);
}
