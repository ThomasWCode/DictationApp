using System.Threading.Channels;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Insertion;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Hotkey;

/// <summary>
/// Tracks the configured chord on top of <see cref="LowLevelKeyboardHook"/>. Key state is touched only on
/// the hook thread; the fields shared with the status hub, the double-tap timer and the tray are guarded by a lock.
/// Events are dispatched through an ordered channel so the hook returns immediately.
///
/// Both ways of dictating work at the same time, and this service is the only place that tells them apart, from the
/// physical key times:
/// <list type="bullet">
/// <item><b>Hold</b>: press the chord, speak, release (<see cref="ChordRelease.Hold"/>). A press shorter than
/// <see cref="TapThreshold"/> is a tap.</item>
/// <item><b>Double-tap</b>: a second press within <see cref="DoubleTapWindow"/> of a tap continues the dictation the
/// first press started; if it is a tap too, the dictation keeps running hands-free and any later press of the chord
/// stops it. A lone tap is only reported (<see cref="ChordRelease.Tap"/>, the orchestrator cancels it) once the
/// window has passed without a second press, so a double tap is a single dictation whose microphone has been running
/// since the first press. Hands-free mode swallows only Escape (discard); other keys pass through.</item>
/// </list>
/// A hands-free dictation can also end without a key press (the time cap, a network fault), so the service watches
/// the <see cref="DictationStatusHub"/>: once the dictation stops recording the flag is dropped, so Escape reaches the
/// focused app again (a discard only applies while recording) and the next press starts a new dictation instead of
/// being swallowed as a "stop". A press the orchestrator ignores because it is busy (the bar flashes) never latches
/// hands-free either.
///
/// Win-key handling: when a chord containing Win fires we inject the unassigned virtual key 0xE8 (down+up).
/// Windows then treats the Win press as "used in a combination" and does not open Start on release.
/// A Win press that is followed by any other key (Win+E etc.) marks Win as consumed and is left alone.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    public static readonly TimeSpan TapThreshold = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(400);

    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;

    private readonly LowLevelKeyboardHook _hook;
    private readonly DictationStatusHub _hub;
    private readonly ILogger<HotkeyService> _logger;
    private readonly HashSet<int> _down = [];
    private readonly Channel<Action> _dispatch = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _sync = new();
    private HotkeyChord _chord = HotkeyChord.Default;
    private bool _holding;            // chord physically held and a dictation running
    private bool _stopPress;          // the current press stopped a hands-free dictation; ignore its release
    private bool _observing;          // the current press is observed only (ObserveOnly), no dictation
    private bool _chordPressed;       // all chord keys currently down
    private bool _winConsumed;        // Win was combined with a non-chord key; leave it to Windows
    private bool _suppressedThisPress;
    private long _pressedAt;

    // Guarded by _sync: shared with the status hub callbacks, the double-tap timer and the tray.
    private bool _toggledOn;          // hands-free dictation running (double tap or tray)
    private bool _secondPress;        // the current press is the second of a double tap; the first press's dictation continues
    private bool _pressRejected;      // the orchestrator ignored the current press (busy): nothing runs for it
    private int _downsSinceActive;    // ChordDowns raised that no new dictation has answered with Listening yet
    private IDisposable? _pendingTap; // a lone tap waiting out the double-tap window
    private int _pendingTapGeneration;

    public HotkeyService(LowLevelKeyboardHook hook, DictationStatusHub hub, ILogger<HotkeyService> logger)
    {
        _hook = hook;
        _hub = hub;
        _logger = logger;
        _hook.Filter = Filter;
        _hub.Changed += OnStatusChanged;
        _hub.Flashed += OnPressIgnored;
        _ = Task.Run(DispatchLoopAsync);
    }

    public event Action? ChordDown;

    public event Action<ChordRelease>? ChordUp;

    public event Action? EscapePressed;

    public event Action<ArrowDirection>? ArrowPressed;

    public event Action<nint, TypedKey>? KeyTyped;

    /// <summary>Raised instead of ChordDown/ChordUp while <see cref="ObserveOnly"/> is set: true on press, false on release.</summary>
    public event Action<bool>? ChordObserved;

    public bool IsChordHeld => _holding || IsHandsFree;

    /// <summary>True while a double-tap (hands-free) or tray-started dictation is running.</summary>
    public bool IsHandsFree
    {
        get
        {
            lock (_sync)
            {
                return _toggledOn;
            }
        }
    }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chord tests (first-run wizard, Settings chord recorder): presses are reported through <see cref="ChordObserved"/>
    /// and never start a dictation, so testing the chord records, uploads, pastes and stores nothing.
    /// </summary>
    public bool ObserveOnly { get; set; }

    /// <summary>Debug aid: treat SendInput events from other processes as physical keys.</summary>
    public bool AcceptInjectedKeys { get; set; }

    public HotkeyChord Chord => _chord;

    /// <summary>Replaceable so unit tests do not inject real keystrokes.</summary>
    internal Action SuppressWinKey { get; set; } = WinKeySuppressor.Suppress;

    /// <summary>Millisecond clock, replaceable in tests to simulate holds and tap gaps.</summary>
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>Runs an action once after a delay (the double-tap window); replaceable so tests control time.</summary>
    internal Func<TimeSpan, Action, IDisposable> Schedule { get; set; } = static (due, action) => new Timer(_ => action(), null, due, Timeout.InfiniteTimeSpan);

    /// <summary>The window a key goes to, for <see cref="KeyTyped"/>; replaceable in tests.</summary>
    internal Func<nint> ForegroundWindow { get; set; } = NativeMethods.GetForegroundWindow;

    public void Start() => _hook.Start();

    public void Configure(HotkeyChord chord)
    {
        _chord = chord;
        _logger.LogInformation("Hotkey configured: {Chord} (hold, or double-tap for hands-free)", chord);
    }

    /// <summary>
    /// Starts a dictation from outside the keyboard (the tray menu). It behaves like a double-tap one: Escape discards it
    /// while it records and a press of the chord stops it.
    /// </summary>
    public void StartHandsFree()
    {
        lock (_sync)
        {
            CancelPendingTap();
            _toggledOn = true;
            _pressRejected = false;
            _downsSinceActive++;
            Raise(ChordDown);
        }
    }

    /// <summary>Stops a dictation started with <see cref="StartHandsFree"/> (or any running one) and inserts it.</summary>
    public void StopHandsFree()
    {
        lock (_sync)
        {
            _toggledOn = false;
            Raise(() => ChordUp?.Invoke(ChordRelease.Hold));
        }
    }

    public void Dispose()
    {
        _hub.Changed -= OnStatusChanged;
        _hub.Flashed -= OnPressIgnored;
        lock (_sync)
        {
            CancelPendingTap();
        }

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

            if (!isChordKey && vk == HotkeyChord.VkEscape && IsHandsFree)
            {
                // Discard ends the hands-free session; the next chord press must start a new one, not "stop".
                lock (_sync)
                {
                    _toggledOn = false;
                }

                Raise(EscapePressed);
                return true;
            }

            var shortcut = _down.Contains(HotkeyChord.VkControl) || _down.Contains(HotkeyChord.VkAlt) || _down.Contains(HotkeyChord.VkLWin);
            if (!isChordKey && Typed(vk, shortcut) is { } typed)
            {
                // The key reaches the app and changes what is before its caret. Called on the hook thread, so only the
                // window is looked up here; the event itself is raised from the dispatch loop.
                var window = ForegroundWindow();
                Raise(() => KeyTyped?.Invoke(window, typed));
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

    /// <summary>
    /// What <paramref name="vk"/> leaves before the caret, or null when the key alone does not tell: Enter (with any
    /// modifier) a line break; with Ctrl, Alt or Win held, anything else is a shortcut; Space and Tab a space; Backspace
    /// probably a character; letters, digits and punctuation (Shift allowed) a character. Arrows, Delete, Escape,
    /// function keys and modifiers change nothing that can be told.
    /// </summary>
    private static TypedKey? Typed(int vk, bool shortcut)
    {
        if (vk == VkReturn)
        {
            return TypedKey.LineBreak;
        }

        if (shortcut)
        {
            return null;
        }

        return vk switch
        {
            HotkeyChord.VkSpace or VkTab => TypedKey.Space,
            VkBack => TypedKey.Erase,
            (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2 => TypedKey.Character,
            _ => null,
        };
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

        if (ObserveOnly)
        {
            _observing = true;
            Raise(() => ChordObserved?.Invoke(true));
            return;
        }

        if (!Enabled)
        {
            return;
        }

        lock (_sync)
        {
            if (_toggledOn)
            {
                // Any press while hands-free stops the dictation; its release is not a new tap.
                _toggledOn = false;
                _stopPress = true;
                CancelPendingTap();
                Raise(() => ChordUp?.Invoke(ChordRelease.Hold));
                return;
            }

            if (_pendingTap is not null)
            {
                // Second press within the double-tap window: the first press's dictation is still running and simply
                // continues. This release decides: a tap makes it hands-free, a hold ends it like any hold.
                CancelPendingTap();
                _secondPress = true;
                _pressedAt = Clock();
                _holding = true;
                return;
            }

            _pressRejected = false;
            _downsSinceActive++;
            _pressedAt = Clock();
            _holding = true;
            Raise(ChordDown);
        }
    }

    private void OnChordReleased()
    {
        if (_observing)
        {
            _observing = false;
            Raise(() => ChordObserved?.Invoke(false));
            return;
        }

        if (_stopPress)
        {
            _stopPress = false;
            return;
        }

        if (!_holding)
        {
            return;
        }

        var held = Clock() - _pressedAt;
        _holding = false;
        var tap = held < TapThreshold.TotalMilliseconds;
        lock (_sync)
        {
            if (_pressRejected)
            {
                // The orchestrator was busy and started nothing for this press: nothing to end or latch.
                _secondPress = false;
                return;
            }

            if (_secondPress)
            {
                _secondPress = false;
                if (tap)
                {
                    _toggledOn = true; // double tap: keep dictating hands-free
                }
                else
                {
                    Raise(() => ChordUp?.Invoke(ChordRelease.Hold)); // a tap, then a hold: one dictation, now finished
                }

                return;
            }

            if (tap)
            {
                // A lone tap so far: report it once the double-tap window passes without a second press.
                var generation = ++_pendingTapGeneration;
                _pendingTap = Schedule(DoubleTapWindow, () => OnDoubleTapWindowPassed(generation));
                return;
            }

            Raise(() => ChordUp?.Invoke(ChordRelease.Hold));
        }
    }

    private void OnDoubleTapWindowPassed(int generation)
    {
        lock (_sync)
        {
            if (generation != _pendingTapGeneration || _pendingTap is null)
            {
                return; // a second press (or the end of the dictation) got there first
            }

            _pendingTap.Dispose();
            _pendingTap = null;

            // Raised under the lock so a press racing the timer is queued after this, never before it.
            Raise(() => ChordUp?.Invoke(ChordRelease.Tap));
        }
    }

    /// <summary>Call under _sync.</summary>
    private void CancelPendingTap()
    {
        _pendingTapGeneration++;
        _pendingTap?.Dispose();
        _pendingTap = null;
    }

    /// <summary>
    /// Tracks the orchestrator's state so the hands-free flag and a pending tap never outlive their dictation. An Idle
    /// that arrives while a ChordDown is still unanswered belongs to the previous dictation and is ignored.
    /// </summary>
    private void OnStatusChanged(DictationStatus status)
    {
        lock (_sync)
        {
            switch (status.State)
            {
                case DictationState.Arming:
                    _downsSinceActive = 0; // a new dictation answered the press
                    break;
                case DictationState.Recording:
                    break;
                case DictationState.Idle:
                    if (_downsSinceActive == 0 && (_toggledOn || _pendingTap is not null || _secondPress))
                    {
                        _toggledOn = false;
                        _secondPress = false;
                        CancelPendingTap();
                        _logger.LogDebug("Dictation ended by itself; chord re-armed");
                    }

                    break;
                default:
                    // Finishing, cleaning up or inserting: the dictation no longer records, so Escape belongs to the
                    // focused app again and the next press is a new dictation, not a "stop".
                    if (_toggledOn)
                    {
                        _toggledOn = false;
                        _logger.LogDebug("Hands-free dictation stopped recording; Escape released");
                    }

                    break;
            }
        }
    }

    /// <summary>The orchestrator ignored a ChordDown because a dictation is still finishing (the bar flashes).</summary>
    private void OnPressIgnored()
    {
        lock (_sync)
        {
            if (_downsSinceActive > 0)
            {
                _downsSinceActive--;
            }

            _pressRejected = true;
            _secondPress = false;
            CancelPendingTap();
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
