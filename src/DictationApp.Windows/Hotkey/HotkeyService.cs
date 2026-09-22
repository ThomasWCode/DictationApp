using System.Threading.Channels;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Hotkey;

/// <summary>
/// Tracks the configured chord on top of <see cref="LowLevelKeyboardHook"/>. All state is touched only on
/// the hook thread; events are dispatched to the thread pool so the hook returns immediately.
///
/// Win-key handling: when a chord containing Win fires we inject the unassigned virtual key 0xE8 (down+up).
/// Windows then treats the Win press as "used in a combination" and does not open Start on release.
/// A Win press that is followed by any other key (Win+E etc.) marks Win as consumed and is left alone.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(400);

    private readonly LowLevelKeyboardHook _hook;
    private readonly ILogger<HotkeyService> _logger;
    private readonly HashSet<int> _down = [];
    private readonly Channel<Action> _dispatch = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private HotkeyChord _chord = HotkeyChord.Default;
    private HotkeyMode _mode = HotkeyMode.Hold;
    private bool _chordActive;        // Hold mode: chord physically held and dictation running
    private bool _toggledOn;          // DoubleTap mode: dictation running
    private bool _chordPressed;       // all chord keys currently down (either mode)
    private bool _winConsumed;        // Win was combined with a non-chord key; leave it to Windows
    private bool _suppressedThisPress;
    private long _lastTapTicks;

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

    public bool IsChordHeld => _chordActive || _toggledOn;

    public bool Enabled { get; set; } = true;

    /// <summary>Debug aid: treat SendInput events from other processes as physical keys.</summary>
    public bool AcceptInjectedKeys { get; set; }

    public HotkeyChord Chord => _chord;

    public HotkeyMode Mode => _mode;

    /// <summary>Replaceable so unit tests do not inject real keystrokes.</summary>
    internal Action SuppressWinKey { get; set; } = WinKeySuppressor.Suppress;

    public void Start() => _hook.Start();

    public void Configure(HotkeyChord chord, HotkeyMode mode)
    {
        _chord = chord;
        _mode = mode;
        _logger.LogInformation("Hotkey configured: {Chord} ({Mode})", chord, mode);
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
                // Auto-repeat. Swallow repeats of non-chord keys while dictating so nothing leaks into the target.
                return _chordActive && !isChordKey;
            }

            if (_chordActive)
            {
                if (isChordKey)
                {
                    return false;
                }

                return HandleKeyWhileDictating(vk);
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

    private bool HandleKeyWhileDictating(int vk)
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

        switch (_mode)
        {
            case HotkeyMode.Hold:
                _chordActive = true;
                Raise(ChordDown);
                break;
            case HotkeyMode.DoubleTapToggle:
                if (_toggledOn)
                {
                    _toggledOn = false;
                    Raise(ChordUp);
                    break;
                }

                var now = Environment.TickCount64;
                if (now - _lastTapTicks <= DoubleTapWindow.TotalMilliseconds)
                {
                    _lastTapTicks = 0;
                    _toggledOn = true;
                    Raise(ChordDown);
                }
                else
                {
                    _lastTapTicks = now;
                }

                break;
        }
    }

    private void OnChordReleased()
    {
        if (_chordActive)
        {
            _chordActive = false;
            Raise(ChordUp);
        }
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
