using DictationApp.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace DictationApp.Windows.Audio;

/// <summary>
/// Keeps one WASAPI capture client initialised while idle, so a dictation only has to call <c>Start()</c>.
/// <para>
/// On DSP microphones such as Intel Smart Sound, <c>IAudioClient.Initialize</c> takes 0.4–1 s, which cut off the
/// first words of every dictation. Measured on such a laptop: first audio 1061 ms after a cold start, 13–96 ms
/// from an initialised client. An initialised but stopped client does not make Windows show the microphone as in
/// use (checked in the privacy registry), so the indicator still appears only while dictating.
/// </para>
/// After each dictation the client is stopped and reset and kept for the next one. Device changes, errors and the
/// "Keep the microphone ready" setting drop it; <see cref="WasapiAudioCapture"/> then falls back to a cold start
/// and a fresh client is prepared in the background.
/// </summary>
public sealed class WarmMicrophone : IMMNotificationClient, IHostedService, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<WarmMicrophone> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private MMDeviceEnumerator? _notifications;
    private PreparedMicrophone? _ready;
    private bool _inUse;
    private bool _stale;
    private bool _started;
    private bool _disposed;

    public WarmMicrophone(ISettingsStore settings, ILogger<WarmMicrophone> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private bool Enabled => _settings.Current.KeepMicrophoneReady;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _started = true;
        }

        _settings.Changed += OnSettingsChanged;
        try
        {
            _notifications = new MMDeviceEnumerator();
            _notifications.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device change notifications unavailable");
        }

        PrepareInBackground();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands the prepared client for <paramref name="deviceId"/> (null: default communications device) to one
    /// capture, or returns null when none is ready (disabled, in use, preparing, or prepared for another device).
    /// </summary>
    public PreparedMicrophone? TryAcquire(string? deviceId)
    {
        lock (_lock)
        {
            if (!_started || _disposed || !Enabled || _inUse || _stale || _ready is null || !string.Equals(_ready.RequestedId, deviceId, StringComparison.Ordinal))
            {
                return null;
            }

            _inUse = true;
            return _ready;
        }
    }

    /// <summary>
    /// Prepares a client if none is ready. Called after a dictation had to open the device itself, which also
    /// retries a preparation that failed because the device was busy or blocked at the time.
    /// </summary>
    public void RequestPreparation() => PrepareInBackground();

    /// <summary>Takes the client back after a dictation; an unhealthy or outdated one is replaced in the background.</summary>
    public void Release(PreparedMicrophone microphone, bool healthy)
    {
        lock (_lock)
        {
            _inUse = false;
            if (!healthy || _stale || !ReferenceEquals(microphone, _ready))
            {
                if (ReferenceEquals(microphone, _ready))
                {
                    _ready = null;
                }

                _stale = false;
                microphone.Dispose();
            }
        }

        PrepareInBackground();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_inUse)
            {
                _ready?.Dispose();
                _ready = null;
            }
        }

        _settings.Changed -= OnSettingsChanged;
        if (_notifications is not null)
        {
            try
            {
                _notifications.UnregisterEndpointNotificationCallback(this);
            }
            catch (Exception)
            {
                // Shutting down.
            }

            _notifications.Dispose();
            _notifications = null;
        }
    }

    private void PrepareInBackground() => Task.Run(Prepare);

    private void Prepare()
    {
        // One at a time: a request that waited finds the client ready, or prepares the newer device selection.
        _prepareGate.Wait();
        try
        {
            PrepareCore();
        }
        finally
        {
            _prepareGate.Release();
        }
    }

    private void PrepareCore()
    {
        string? deviceId;
        lock (_lock)
        {
            if (!_started || _disposed || _inUse)
            {
                return;
            }

            deviceId = _settings.Current.MicrophoneDeviceId;
            if (!Enabled)
            {
                _ready?.Dispose();
                _ready = null;
                return;
            }

            if (_ready is not null && !_stale && string.Equals(_ready.RequestedId, deviceId, StringComparison.Ordinal))
            {
                return;
            }
        }

        PreparedMicrophone fresh;
        var started = Environment.TickCount64;
        try
        {
            fresh = PreparedMicrophone.Create(deviceId);
        }
        catch (Exception ex)
        {
            // No microphone, access blocked, or the device is busy: dictations use the cold path, which reports it.
            _logger.LogDebug(ex, "Could not prepare the microphone");
            return;
        }

        bool superseded;
        bool installed;
        lock (_lock)
        {
            // The device selection may have changed while this one was opening.
            superseded = !string.Equals(_settings.Current.MicrophoneDeviceId, deviceId, StringComparison.Ordinal);
            installed = !_disposed && !_inUse && Enabled && !superseded;
            if (installed)
            {
                _ready?.Dispose();
                _ready = fresh;
                _stale = false;
            }
            else
            {
                fresh.Dispose();
            }
        }

        if (superseded)
        {
            _logger.LogDebug("Prepared microphone discarded: the device selection changed meanwhile");
            PrepareInBackground();
            return;
        }

        if (!installed)
        {
            return;
        }

        _logger.LogInformation("Microphone ready on {Device} ({Elapsed} ms)", fresh.Name, Environment.TickCount64 - started);
    }

    /// <summary>Drops the prepared client (now, or when the current dictation releases it) and prepares a new one.</summary>
    private void Invalidate(string why)
    {
        lock (_lock)
        {
            if (_ready is null)
            {
                return;
            }

            if (_inUse)
            {
                _stale = true;
                return;
            }

            _ready.Dispose();
            _ready = null;
        }

        _logger.LogDebug("Prepared microphone dropped: {Reason}", why);
        PrepareInBackground();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        PreparedMicrophone? ready;
        lock (_lock)
        {
            ready = _ready;
        }

        if (!settings.KeepMicrophoneReady || (ready is not null && !string.Equals(ready.RequestedId, settings.MicrophoneDeviceId, StringComparison.Ordinal)))
        {
            Invalidate("settings changed");
        }
        else if (ready is null)
        {
            PrepareInBackground();
        }
    }

    // IMMNotificationClient: called on COM threads, so each handler only records the change and returns.
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && role == Role.Communications && _ready is { RequestedId: null })
        {
            Invalidate("default microphone changed");
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState != DeviceState.Active && string.Equals(_ready?.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            Invalidate("microphone disconnected");
        }
        else if (newState == DeviceState.Active && _ready is null)
        {
            PrepareInBackground();
        }
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (string.Equals(_ready?.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            Invalidate("microphone removed");
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}

/// <summary>An initialised, stopped WASAPI capture client (shared mode, event driven, 20 ms buffer).</summary>
public sealed class PreparedMicrophone : IDisposable
{
    private PreparedMicrophone(string? requestedId, MMDevice device, AudioClient client, AudioCaptureClient captureClient, EventWaitHandle bufferReady)
    {
        RequestedId = requestedId;
        Device = device;
        DeviceId = device.ID;
        Name = device.FriendlyName;
        Client = client;
        CaptureClient = captureClient;
        BufferReady = bufferReady;

        // The mix format is usually WAVEFORMATEXTENSIBLE; the PCM pipeline (like NAudio's WasapiCapture) wants the
        // plain equivalent, e.g. 32-bit IEEE float, 48 kHz, 2 channels.
        var mix = client.MixFormat;
        Format = mix is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : mix;
    }

    /// <summary>The device the settings asked for; null means the default communications device.</summary>
    public string? RequestedId { get; }

    public string DeviceId { get; }

    public string Name { get; }

    public MMDevice Device { get; }

    public AudioClient Client { get; }

    public AudioCaptureClient CaptureClient { get; }

    public EventWaitHandle BufferReady { get; }

    public WaveFormat Format { get; }

    /// <summary>Opens and initialises the device: the slow part (0.4–1 s on DSP microphones), done while idle.</summary>
    public static PreparedMicrophone Create(string? requestedId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(requestedId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(requestedId);
        AudioClient? client = null;
        EventWaitHandle? bufferReady = null;
        try
        {
            client = device.AudioClient;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 200_000, 0, client.MixFormat, Guid.Empty);
            bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle());
            return new PreparedMicrophone(requestedId, device, client, client.AudioCaptureClient, bufferReady);
        }
        catch
        {
            bufferReady?.Dispose();
            client?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Client.Stop();
        }
        catch (Exception)
        {
            // Already stopped or the device is gone.
        }

        Client.Dispose();
        BufferReady.Dispose();
        Device.Dispose();
    }
}
