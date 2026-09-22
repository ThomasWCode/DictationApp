using System.Threading.Channels;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Hotkey;

/// <summary>
/// Tracks the configured chord on top of <see cref="LowLevelKeyboardHook"/>. All state is touched only on
/// the hook thread; events are dispatched through an ordered channel so the hook returns immediately.
///
/// Both ways of dictating work at the same time:
/// <list type="bullet">
/// <item><b>Hold</b>: press the chord, speak, release. A press shorter than <see cref="TapThreshold"/> is a tap.</item>
/// <item><b>Double-tap</b>: two taps within <see cref="DoubleTapWindow"/> start a hands-free dictation; any
/// later press of the chord stops it. Hands-free mode swallows only Escape (discard); other keys pass through.</item>
/// </list>
///
/// Win-key handling: when a chord containing Win fires we inject the unassigned virtual key 0xE8 (down+up).
/// Windows then treats the Win press as "used in a combination" and does not open Start on release.
/// A Win press that is followed by any other key (Win+E etc.) marks Win as consumed and is left alone.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    public static readonly TimeSpan TapThreshold = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(400);

    private readonly LowLevelKeyboardHook _hook;
    private readonly ILogger<HotkeyService> _logger;
    private readonly HashSet<int> _down = [];
    private readonly Channel<Action> _dispatch = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private HotkeyChord _chord = HotkeyChord.Default;
    private bool _holding;            // chord physically held and a dictation running
    private bool _toggledOn;          // hands-free dictation running after a double tap
    private bool _stopPress;          // the current press stopped a hands-free dictation; ignore its release
    private bool _chordPressed;       // all chord keys currently down
    private bool _winConsumed;        // Win was combined with a non-chord key; leave it to Windows
    private bool _suppressedThisPress;
    private long _pressedAt;
    private long _lastTapReleasedAt;

    public HotkeyService(LowLevelKeyboardHook hook, ILogger<HotkeyService> logger)
    {
        _hook = hook;
        _logger = logger;
        _hook.Filter = Filter;
        _ = Task.Run(DispatchLoopAsync);
    }

    public event Action? ChordDown;

    public event Action? ChordUp;

    public event Action? EscapePressed;

    public event Action<ArrowDirection>? ArrowPressed;

    public bool IsChordHeld => _holding || _toggledOn;

    /// <summary>True while a double-tap (hands-free) dictation is running.</summary>
    public bool IsHandsFree => _toggledOn;

    public bool Enabled { get; set; } = true;

    /// <summary>Debug aid: treat SendInput events from other processes as physical keys.</summary>
    public bool AcceptInjectedKeys { get; set; }

    public HotkeyChord Chord => _chord;

    /// <summary>Replaceable so unit tests do not inject real keystrokes.</summary>
    internal Action SuppressWinKey { get; set; } = WinKeySuppressor.Suppress;

    /// <summary>Millisecond clock, replaceable in tests to simulate holds and tap gaps.</summary>
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    public void Start() => _hook.Start();

    public void Configure(HotkeyChord chord)
    {
        _chord = chord;
        _logger.LogInformation("Hotkey configured: {Chord} (hold, or double-tap for hands-free)", chord);
    }

    public void Dispose()
    {
        _dispatch.Writer.TryComplete();
        _hook.Dispose();
    }

    private bool Filter(LowLevelKeyboardHook.KeyEvent ev)
    {
        if (ev.Injected && !AcceptInjectedKeys)
        {
            return false; // our own SendInput (0xE8, Ctrl+V) and other automation: never tracked, never swallowed
        }

        var vk = HotkeyChord.Normalise(ev.VirtualKey);
        var isChordKey = _chord.VirtualKeys.Contains(vk);

        if (ev.IsDown)
        {
            if (!_down.Add(vk))
            {
                // Auto-repeat. Swallow repeats of non-chord keys while holding so nothing leaks into the target.
                return _holding && !isChordKey;
            }

            if (_holding && !isChordKey)
            {
                return HandleKeyWhileHolding(vk);
            }

            if (_toggledOn && !isChordKey && vk == HotkeyChord.VkEscape)
            {
                Raise(EscapePressed);
                return true;
            }

            if (!isChordKey && (_down.Contains(HotkeyChord.VkLWin) || _down.Contains(HotkeyChord.VkControl) || _down.Contains(HotkeyChord.VkAlt) || _down.Contains(HotkeyChord.VkShift)))
            {
                // A modifier is combined with something else: this is an OS/app shortcut, not our chord.
                _winConsumed = true;
            }

            if (isChordKey && !_winConsumed && ChordFullyDown())
            {
                _chordPressed = true;
                OnChordPressed();
            }

            return false;
        }

        // Key up
        _down.Remove(vk);
        if (isChordKey && _chordPressed)
        {
            _chordPressed = false;
            OnChordReleased();
        }

        if (_down.Count == 0)
        {
            _winConsumed = false;
            _suppressedThisPress = false;
        }

        return false;
    }

    private bool HandleKeyWhileHolding(int vk)
    {
        switch (vk)
        {
            case HotkeyChord.VkEscape:
                Raise(EscapePressed);
                return true;
            case HotkeyChord.VkLeft:
                Raise(() => ArrowPressed?.Invoke(ArrowDirection.Left));
                return true;
            case HotkeyChord.VkRight:
                Raise(() => ArrowPressed?.Invoke(ArrowDirection.Right));
                return true;
            case HotkeyChord.VkUp:
                Raise(() => ArrowPressed?.Invoke(ArrowDirection.Up));
                return true;
            case HotkeyChord.VkDown:
                Raise(() => ArrowPressed?.Invoke(ArrowDirection.Down));
                return true;
            default:
                // Swallow everything else so Win+Ctrl+D / Win+Ctrl+Left etc. cannot fire mid-dictation.
                return true;
        }
    }

    private bool ChordFullyDown()
    {
        foreach (var k in _chord.VirtualKeys)
        {
            if (!_down.Contains(k))
            {
                return false;
            }
        }

        // Only the chord keys may be down; Win+Ctrl+E must not start dictation.
        return _down.Count == _chord.VirtualKeys.Count;
    }

    private void OnChordPressed()
    {
        if (_chord.ContainsWin && !_suppressedThisPress)
        {
            SuppressWinKey();
            _suppressedThisPress = true;
        }

        if (!Enabled)
        {
            return;
        }

        if (_toggledOn)
        {
            // Any press while hands-free stops the dictation; its release is not a new tap.
            _toggledOn = false;
            _stopPress = true;
            _lastTapReleasedAt = 0;
            Raise(ChordUp);
            return;
        }

        _pressedAt = Clock();
        _holding = true;
        Raise(ChordDown);
    }

    private void OnChordReleased()
    {
        if (_stopPress)
        {
            _stopPress = false;
            return;
        }

        if (!_holding)
        {
            return;
        }

        var now = Clock();
        var held = now - _pressedAt;
        _holding = false;
        if (held < TapThreshold.TotalMilliseconds)
        {
            if (_lastTapReleasedAt != 0 && _pressedAt - _lastTapReleasedAt <= DoubleTapWindow.TotalMilliseconds)
            {
                // Second tap: keep the dictation running hands-free. No ChordUp.
                _lastTapReleasedAt = 0;
                _toggledOn = true;
                return;
            }

            _lastTapReleasedAt = now;
            Raise(ChordUp); // a lone tap: the orchestrator cancels it as a short press
            return;
        }

        _lastTapReleasedAt = 0;
        Raise(ChordUp);
    }

    /// <summary>
    /// Handlers run on one background consumer so ChordDown can never be observed after its ChordUp,
    /// while the hook thread itself never blocks on user code.
    /// </summary>
    private void Raise(Action? handler)
    {
        if (handler is not null)
        {
            _dispatch.Writer.TryWrite(handler);
        }
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var handler in _dispatch.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hotkey handler threw");
            }
        }
    }
}

/// <summary>Injects VK 0xE8 so Windows treats the current Win press as part of a combination.</summary>
public static class WinKeySuppressor
{
    public static void Suppress()
    {
        NativeMethods.SendKeys(
            NativeMethods.KeyInput(NativeMethods.VK_UNASSIGNED_E8, up: false, useScanCode: false),
            NativeMethods.KeyInput(NativeMethods.VK_UNASSIGNED_E8, up: true, useScanCode: false));
    }
}
