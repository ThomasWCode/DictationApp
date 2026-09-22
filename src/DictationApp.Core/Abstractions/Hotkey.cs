using DictationApp.Core.Settings;

namespace DictationApp.Core.Abstractions;

public enum ArrowDirection
{
    Up,
    Down,
    Left,
    Right,
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

    event Action? ChordUp;

    event Action? EscapePressed;

    event Action<ArrowDirection>? ArrowPressed;

    bool IsChordHeld { get; }

    /// <summary>When false the hook keeps running but no events are raised (tray "Pause hotkey").</summary>
    bool Enabled { get; set; }

    void Configure(HotkeyChord chord);
}
