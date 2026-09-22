using DictationApp.Core.Settings;

namespace DictationApp.Core.Abstractions;

public enum HotkeyMode
{
    /// <summary>Hold the chord to dictate, release to finish.</summary>
    Hold,

    /// <summary>Tap the chord twice quickly to start, tap once to stop.</summary>
    DoubleTapToggle,
}

public enum ArrowDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// Global hotkey source. Events are raised on a background thread and must not block.
/// While the chord is held, arrow keys and Escape are swallowed so that OS shortcuts such as
/// Win+Ctrl+Left (switch virtual desktop) do not fire mid-dictation.
/// </summary>
public interface IHotkeyService
{
    event Action? ChordDown;

    event Action? ChordUp;

    event Action? EscapePressed;

    event Action<ArrowDirection>? ArrowPressed;

    bool IsChordHeld { get; }

    /// <summary>When false the hook keeps running but no events are raised (tray "Pause hotkey").</summary>
    bool Enabled { get; set; }

    void Configure(HotkeyChord chord, HotkeyMode mode);
}
