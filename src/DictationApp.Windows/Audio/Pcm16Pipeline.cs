using DictationApp.Core.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Converts any input format to 16 kHz mono PCM16 and slices it into 100 ms frames. Shared by the
/// microphone capture and the WAV replay so both produce byte-identical framing.
/// </summary>
internal sealed class Pcm16Pipeline
{
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _pcm16;
    private readonly byte[] _frame = new byte[AudioFrame.BytesPerFrame];
    private readonly byte[] _scratch = new byte[AudioFrame.BytesPerFrame * 4];
    private int _frameFill;
    private long _emittedBytes;

    public Pcm16Pipeline(WaveFormat inputFormat)
    {
        _buffer = new BufferedWaveProvider(inputFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
            ReadFully = false,
        };
        if (inputFormat.Encoding == WaveFormatEncoding.Pcm && inputFormat.BitsPerSample == 16 && inputFormat.Channels == 1 && inputFormat.SampleRate == AudioFrame.SampleRate)
        {
            // Already the wire format (our own WAV recordings): no float round trip, byte-exact.
            _pcm16 = _buffer;
            return;
        }

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (inputFormat.Channels > 1)
        {
            samples = new MonoMixSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != AudioFrame.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, AudioFrame.SampleRate);
        }

        _pcm16 = samples.ToWaveProvider16();
    }

    public TimeSpan Position => TimeSpan.FromSeconds((double)_emittedBytes / (AudioFrame.SampleRate * 2));

    /// <summary>Feeds raw input bytes and emits every complete 100 ms frame produced.</summary>
    public void Push(byte[] data, int offset, int count, Action<AudioFrame> emit)
    {
        _buffer.AddSamples(data, offset, count);
        Drain(emit, flush: false);
    }

    /// <summary>Emits whatever is left, padding the last frame with silence.</summary>
    public void Flush(Action<AudioFrame> emit) => Drain(emit, flush: true);

    private void Drain(Action<AudioFrame> emit, bool flush)
    {
        int read;
        while ((read = _pcm16.Read(_scratch, 0, _scratch.Length)) > 0)
        {
            var offset = 0;
            while (offset < read)
            {
                var take = Math.Min(_frame.Length - _frameFill, read - offset);
                Buffer.BlockCopy(_scratch, offset, _frame, _frameFill, take);
                _frameFill += take;
                offset += take;
                if (_frameFill == _frame.Length)
                {
                    Emit(emit);
                }
            }
        }

        if (flush && _frameFill > 0)
        {
            Array.Clear(_frame, _frameFill, _frame.Length - _frameFill);
            _frameFill = _frame.Length;
            Emit(emit);
        }
    }

    private void Emit(Action<AudioFrame> emit)
    {
        var copy = new byte[_frame.Length];
        Buffer.BlockCopy(_frame, 0, copy, 0, _frame.Length);
        _frameFill = 0;
        _emittedBytes += copy.Length;
        emit(new AudioFrame(copy, Peak(copy), Position));
    }

    public static float Peak(ReadOnlySpan<byte> pcm16)
    {
        var max = 0;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            var abs = Math.Abs((int)sample);
            if (abs > max)
            {
                max = abs;
            }
        }

        return max / 32768f;
    }

    /// <summary>Averages all channels into one. NAudio's StereoToMono only handles two channels.</summary>
    private sealed class MonoMixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _sourceBuffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_sourceBuffer.Length < needed)
            {
                _sourceBuffer = new float[needed];
            }

            var read = source.Read(_sourceBuffer, 0, needed);
            var frames = read / _channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++)
                {
                    sum += _sourceBuffer[i * _channels + c];
                }

                buffer[offset + i] = sum / _channels;
            }

            return frames;
        }
    }
}
