using System.Buffers.Binary;
using DictationApp.Core.Abstractions;

namespace DictationApp.Core.History;

/// <summary>
/// The header of the app's recordings (16 kHz, mono, PCM16) and the repair of an unfinished one. A recording's sizes
/// are written only when its session finishes, so a file whose sizes are still zero was left by a run that stopped
/// while recording or saving (killed, crashed, power cut).
/// </summary>
public static class WavHeader
{
    public const int Size = 44;
    public const int BytesPerSecond = AudioFrame.SampleRate * 2;

    /// <summary>A 44-byte header; <paramref name="dataBytes"/> 0 writes the "unfinished" marker (both sizes zero).</summary>
    public static byte[] Create(long dataBytes)
    {
        var h = new byte[Size];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), dataBytes == 0 ? 0u : (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(h.AsSpan(8));
        "fmt "u8.CopyTo(h.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), AudioFrame.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2); // block align
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }

    /// <summary>
    /// When <paramref name="path"/> is an unfinished recording, sets its sizes from the file length (cut to whole
    /// samples) and returns true with the audio byte count. Walks the chunks, so older recordings written by NAudio
    /// (an 18-byte format chunk) are handled too. False, and the file untouched, for anything else.
    /// </summary>
    public static bool TryRepairUnfinished(string path, out long pcmBytes)
    {
        pcmBytes = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Span<byte> head = stackalloc byte[12];
        if (stream.Read(head) < head.Length || !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]);
        Span<byte> chunk = stackalloc byte[8];
        long position = 12;
        long dataOffset = -1;
        uint dataSize = 0;
        while (position + chunk.Length <= stream.Length)
        {
            stream.Position = position;
            if (stream.Read(chunk) < chunk.Length)
            {
                break;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("data"u8))
            {
                dataOffset = position + chunk.Length;
                dataSize = size;
                break;
            }

            position += chunk.Length + size + (size & 1);
        }

        if (dataOffset < 0)
        {
            return false;
        }

        var pcm = (stream.Length - dataOffset) & ~1L;
        if (riffSize != 0 && !(dataSize == 0 && pcm > 0))
        {
            return false; // finished
        }

        stream.SetLength(dataOffset + pcm);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(dataOffset + pcm - 8));
        stream.Position = 4;
        stream.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)pcm);
        stream.Position = dataOffset - 4;
        stream.Write(value);
        stream.Flush(flushToDisk: true);
        pcmBytes = pcm;
        return true;
    }
}
