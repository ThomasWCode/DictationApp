using DictationApp.Core.Cleanup;

namespace DictationApp.Core.Session;

/// <summary>Immutable snapshot of what the Flow bar should show.</summary>
public sealed record DictationStatus(
    DictationState State,
    string LiveText,
    float Level,
    Tone Tone,
    CleanupLevel CleanupLevel,
    string? Badge,
    DateTimeOffset? StartedAt,
    string? AppName,
    bool HotkeyEnabled)
{
    public static DictationStatus Idle { get; } = new(DictationState.Idle, string.Empty, 0f, Tone.Neutral, CleanupLevel.Light, null, null, null, true);

    public bool IsActive => State != DictationState.Idle;
}

/// <summary>
/// Thread-safe pub/sub between the orchestrator (publisher) and the UI (subscriber), plus the reverse
/// channel for chip clicks on the Flow bar. Keeps the orchestrator free of any UI dependency.
/// </summary>
public sealed class DictationStatusHub
{
    private readonly object _lock = new();
    private DictationStatus _current = DictationStatus.Idle;

    public event Action<DictationStatus>? Changed;

    /// <summary>A brief visual flash: the user pressed the chord while a dictation was already running.</summary>
    public event Action? Flashed;

    public event Action<Tone>? ToneOverrideRequested;

    public event Action<CleanupLevel>? LevelOverrideRequested;

    public DictationStatus Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Publish(DictationStatus status)
    {
        lock (_lock)
        {
            _current = status;
        }

        Changed?.Invoke(status);
    }

    public void Update(Func<DictationStatus, DictationStatus> mutate)
    {
        DictationStatus next;
        lock (_lock)
        {
            next = mutate(_current);
            _current = next;
        }

        Changed?.Invoke(next);
    }

    public void Flash() => Flashed?.Invoke();

    public void RequestTone(Tone tone) => ToneOverrideRequested?.Invoke(tone);

    public void RequestLevel(CleanupLevel level) => LevelOverrideRequested?.Invoke(level);
}
