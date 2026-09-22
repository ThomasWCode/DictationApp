using System.IO;
using DictationApp.Core.Abstractions;
using DictationApp.Windows.Audio;
using DictationApp.Windows.Security;
using NAudio.Wave;

namespace DictationApp.Windows.Tests;

public class DpapiSecretStoreTests
{
    [Fact]
    public void Round_trips_and_rejects_garbage()
    {
        var store = new DpapiSecretStore();
        var blob = store.Protect("sk-test-key-123");
        Assert.NotEqual("sk-test-key-123", blob);
        Assert.Equal("sk-test-key-123", store.Unprotect(blob));
        Assert.Null(store.Unprotect("not base64!"));
        Assert.Null(store.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
    }
}

public sealed class WavFileSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Writes_valid_16k_mono_wav_with_expected_length()
    {
        var path = Path.Combine(_dir, "a.wav");
        using (var sink = new WavFileSink(path))
        {
            var frame = new byte[AudioFrame.BytesPerFrame];
            for (var i = 0; i < 10; i++)
            {
                sink.Write(frame);
            }

            Assert.Equal(10 * AudioFrame.BytesPerFrame, sink.BytesWritten);
            sink.Complete();
        }

        using var reader = new WaveFileReader(path);
        Assert.Equal(16_000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(10 * AudioFrame.BytesPerFrame, reader.Length);
        Assert.Equal(1.0, reader.TotalTime.TotalSeconds, 3);
    }

    [Fact]
    public void Discard_deletes_the_file()
    {
        var path = Path.Combine(_dir, "b.wav");
        var sink = new WavFileSink(path);
        sink.Write(new byte[100]);
        sink.Discard();
        Assert.False(File.Exists(path));
        sink.Write(new byte[100]); // no-op after close
        Assert.Equal(100, sink.BytesWritten);
    }
}

public class Pcm16PipelineTests
{
    [Fact]
    public void Converts_stereo_48k_float_to_100ms_frames()
    {
        var pipeline = new Pcm16Pipeline(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var frames = new List<AudioFrame>();
        // 1 second of a 440 Hz tone at 48 kHz stereo float = 48000 * 2 * 4 bytes
        var samples = new float[48_000 * 2];
        for (var i = 0; i < 48_000; i++)
        {
            var v = (float)Math.Sin(2 * Math.PI * 440 * i / 48_000.0) * 0.5f;
            samples[2 * i] = v;
            samples[2 * i + 1] = v;
        }

        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        pipeline.Push(bytes, 0, bytes.Length, frames.Add);
        pipeline.Flush(frames.Add);

        Assert.InRange(frames.Count, 9, 11);
        Assert.All(frames, f => Assert.Equal(AudioFrame.BytesPerFrame, f.Pcm16.Length));
        Assert.InRange(frames[5].Peak, 0.4f, 0.55f);
    }

    [Fact]
    public void Passes_16k_mono_pcm16_through_unchanged()
    {
        var pipeline = new Pcm16Pipeline(new WaveFormat(16_000, 16, 1));
        var frames = new List<AudioFrame>();
        var input = new byte[AudioFrame.BytesPerFrame * 3];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 251);
        }

        pipeline.Push(input, 0, input.Length, frames.Add);
        Assert.Equal(3, frames.Count);
        Assert.Equal(input.AsSpan(0, AudioFrame.BytesPerFrame).ToArray(), frames[0].Pcm16.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(300), frames[2].Position);
    }

    [Fact]
    public void Peak_is_normalised()
    {
        var full = new byte[4];
        BitConverter.GetBytes((short)-32768).CopyTo(full, 0);
        Assert.Equal(1.0f, Pcm16Pipeline.Peak(full));
        Assert.Equal(0f, Pcm16Pipeline.Peak(new byte[10]));
    }
}
