using DictationApp.Core.Settings;

namespace DictationApp.Core.Abstractions;

public enum ArrowDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>How a press of the chord ended, classified by the hotkey service from the physical key times.</summary>
public enum ChordRelease
{
    /// <summary>A hold was released, or a hands-free dictation was stopped: finish the dictation and insert it.</summary>
    Hold,

    /// <summary>A lone press shorter than the tap threshold that no second tap followed: cancel the dictation.</summary>
    Tap,
}

/// <summary>
/// Global hotkey source. Events are raised on a background thread and must not block.
/// Holding the chord dictates until release; two quick taps start a hands-free dictation that the next
/// press of the chord stops. While the chord is physically held, arrow keys and Escape are swallowed so
/// that OS shortcuts such as Win+Ctrl+Left (switch virtual desktop) do not fire mid-dictation.
/// </summary>
public interface IHotkeyService
{
    event Action? ChordDown;

    /// <summary>
    /// The press that raised <see cref="ChordDown"/> ended. The classification comes from the physical key times, so it
    /// does not depend on how long the dictation's start-up work took.
    /// </summary>
    event Action<ChordRelease>? ChordUp;

    event Action? EscapePressed;

    event Action<ArrowDirection>? ArrowPressed;

    bool IsChordHeld { get; }

    /// <summary>When false the hook keeps running but no events are raised (tray "Pause hotkey").</summary>
    bool Enabled { get; set; }

    void Configure(HotkeyChord chord);
}
