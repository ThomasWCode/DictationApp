using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // No default device (no microphone attached).
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }
        }
        catch (Exception)
        {
            // Audio subsystem unavailable; return what we have.
        }

        return list;
    }
}

public sealed class WindowsAudioCaptureFactory(ILoggerFactory loggerFactory, WarmMicrophone warm) : IAudioCaptureFactory
{
    public IAudioCapture CreateMicrophone(string? deviceId) => new WasapiAudioCapture(deviceId, loggerFactory.CreateLogger<WasapiAudioCapture>(), warm);

    public IAudioCapture CreateWavReplay(string wavPath, double speed) => new WavReplayAudioCapture(wavPath, speed, loggerFactory.CreateLogger<WavReplayAudioCapture>());
}

/// <summary>Plays a history WAV through the default output device. One playback at a time.</summary>
public sealed class WavPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public event Action? PlaybackStopped;

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public string? CurrentPath { get; private set; }

    public void Play(string path)
    {
        Stop();
        lock (_lock)
        {
            _reader = new AudioFileReader(path);
            _output = new WaveOutEvent();
            _output.PlaybackStopped += (_, _) =>
            {
                Stop();
                PlaybackStopped?.Invoke();
            };
            _output.Init(_reader);
            _output.Play();
            CurrentPath = path;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_output is null)
            {
                return;
            }

            var output = _output;
            var reader = _reader;
            _output = null;
            _reader = null;
            CurrentPath = null;
            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            output.Dispose();
            reader?.Dispose();
        }
    }

    public void Dispose() => Stop();
}
