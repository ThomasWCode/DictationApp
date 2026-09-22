using System.Runtime.InteropServices;
using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Shared-mode WASAPI capture on the selected device (default communications device when null),
/// converted to 16 kHz mono PCM16 100 ms frames by <see cref="Pcm16Pipeline"/>.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private WasapiCapture? _capture;
    private Pcm16Pipeline? _pipeline;
    private MMDevice? _device;

    public WasapiAudioCapture(string? deviceId, ILogger logger)
    {
        _deviceId = deviceId;
        _logger = logger;
    }

    public event Action<AudioFrame>? FrameCaptured;

    public event Action<Exception>? Faulted;

    /// <summary>A microphone never completes; satisfied explicitly so the compiler does not flag an unused event.</summary>
    public event Action? Completed
    {
        add { }
        remove { }
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(_deviceId);
            _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 20);
            _pipeline = new Pcm16Pipeline(_capture.WaveFormat);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRunning = true;
            _logger.LogDebug("WASAPI capture started on {Device} ({Format})", _device.FriendlyName, _capture.WaveFormat);
        }
        catch (COMException ex) when (ex.HResult == E_ACCESSDENIED)
        {
            Cleanup();
            throw new MicrophoneAccessDeniedException("Microphone access is blocked by Windows privacy settings.", ex);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Cleanup();
            throw new InvalidOperationException("Could not start the microphone: " + ex.Message, ex);
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopRecording threw");
        }
    }

    public void Dispose()
    {
        Stop();
        Cleanup();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || _pipeline is null)
        {
            return;
        }

        try
        {
            _pipeline.Push(e.Buffer, 0, e.BytesRecorded, frame => FrameCaptured?.Invoke(frame));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio pipeline error");
            Faulted?.Invoke(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "WASAPI capture stopped with error");
            IsRunning = false;
            Faulted?.Invoke(e.Exception);
        }
    }

    private void Cleanup()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _pipeline = null;
    }
}
