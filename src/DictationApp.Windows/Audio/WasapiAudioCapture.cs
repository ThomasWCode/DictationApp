using System.Runtime.InteropServices;
using DictationApp.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Shared-mode WASAPI capture on the selected device (default communications device when null),
/// converted to 16 kHz mono PCM16 100 ms frames by <see cref="Pcm16Pipeline"/>. Uses the client
/// <see cref="WarmMicrophone"/> keeps initialised when one is ready (audio within ~15-100 ms of the key press),
/// otherwise opens the device from scratch (0.4-1 s on DSP microphones).
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private readonly WarmMicrophone? _warm;
    private WasapiCapture? _capture;
    private Pcm16Pipeline? _pipeline;
    private MMDevice? _device;
    private PreparedMicrophone? _prepared;
    private Thread? _warmThread;
    private volatile bool _stopWarm;
    private volatile bool _warmHealthy;

    public WasapiAudioCapture(string? deviceId, ILogger logger, WarmMicrophone? warm = null)
    {
        _deviceId = deviceId;
        _logger = logger;
        _warm = warm;
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

        if (_warm?.TryAcquire(_deviceId) is { } prepared)
        {
            try
            {
                StartWarm(prepared);
                return;
            }
            catch (Exception ex)
            {
                // Invalidated since it was prepared (sleep, format change, privacy switch): open it afresh below.
                _logger.LogDebug(ex, "Prepared microphone would not start; opening the device afresh");
                _prepared = null;
                _warm.Release(prepared, healthy: false);
            }
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
        // Checked first: a capture fault clears IsRunning before raising Faulted, and the prepared client must
        // still go back to WarmMicrophone.
        if (_prepared is { } prepared)
        {
            IsRunning = false;
            StopWarm(prepared);
            return;
        }

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

        // Nothing was ready for this dictation (preparation failed, say while another app held the device), so try
        // again for the next one.
        _warm?.RequestPreparation();
    }

    public void Dispose()
    {
        Stop();
        Cleanup();
    }

    private void StartWarm(PreparedMicrophone prepared)
    {
        _prepared = prepared;
        _pipeline = new Pcm16Pipeline(prepared.Format);
        _stopWarm = false;
        _warmHealthy = true;
        prepared.Client.Start();
        IsRunning = true;
        _warmThread = new Thread(() => WarmCaptureLoop(prepared)) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "mic-capture" };
        _warmThread.Start();
        _logger.LogDebug("WASAPI capture started on {Device} (prepared client, {Format})", prepared.Name, prepared.Format);
    }

    /// <summary>The same packet loop as NAudio's WasapiCapture, on a client that outlives this capture.</summary>
    private void WarmCaptureLoop(PreparedMicrophone prepared)
    {
        var bytesPerFrame = prepared.Format.BlockAlign;
        var buffer = new byte[Math.Max(bytesPerFrame, prepared.Format.AverageBytesPerSecond / 5)];
        try
        {
            while (true)
            {
                prepared.BufferReady.WaitOne(100);
                var offset = 0;
                var packet = prepared.CaptureClient.GetNextPacketSize();
                while (packet != 0)
                {
                    var data = prepared.CaptureClient.GetBuffer(out var frames, out var flags);
                    var bytes = frames * bytesPerFrame;
                    if (buffer.Length - offset < bytes)
                    {
                        Emit(buffer, offset);
                        offset = 0;
                        if (buffer.Length < bytes)
                        {
                            buffer = new byte[bytes];
                        }
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(buffer, offset, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, offset, bytes);
                    }

                    offset += bytes;
                    prepared.CaptureClient.ReleaseBuffer(frames);
                    packet = prepared.CaptureClient.GetNextPacketSize();
                }

                Emit(buffer, offset);

                // Checked after draining, so the last packets before the stop are still delivered.
                if (_stopWarm)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _warmHealthy = false;
            if (!_stopWarm)
            {
                _logger.LogWarning(ex, "WASAPI capture failed");
                IsRunning = false;
                Faulted?.Invoke(ex);
            }
        }
    }

    private void Emit(byte[] buffer, int count)
    {
        if (count <= 0 || _pipeline is null)
        {
            return;
        }

        try
        {
            _pipeline.Push(buffer, 0, count, frame => FrameCaptured?.Invoke(frame));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio pipeline error");
            Faulted?.Invoke(ex);
        }
    }

    private void StopWarm(PreparedMicrophone prepared)
    {
        _stopWarm = true;
        prepared.BufferReady.Set();
        if (_warmThread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }

        var healthy = _warmHealthy && _warmThread is not { IsAlive: true };
        try
        {
            // Stopped and reset, but still initialised: the next dictation starts instantly.
            prepared.Client.Stop();
            prepared.Client.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not reset the prepared microphone");
            healthy = false;
        }

        _prepared = null;
        _warmThread = null;
        _warm?.Release(prepared, healthy);
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
