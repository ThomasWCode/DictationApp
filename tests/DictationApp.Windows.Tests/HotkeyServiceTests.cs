using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Session;
using DictationApp.Core.Settings;
using DictationApp.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;

namespace DictationApp.Windows.Tests;

/// <summary>Drives the hook filter directly with a fake clock and fake timers; no real hook is installed.</summary>
public sealed class HotkeyServiceTests : IDisposable
{
    private readonly LowLevelKeyboardHook _hook = new(NullLogger<LowLevelKeyboardHook>.Instance);
    private readonly DictationStatusHub _hub = new();
    private readonly HotkeyService _service;
    private readonly List<string> _events = [];
    private readonly List<FakeTimer> _timers = [];
    private long _now = 100_000;
    private int _winSuppressions;

    public HotkeyServiceTests()
    {
        _service = new HotkeyService(_hook, _hub, NullLogger<HotkeyService>.Instance)
        {
            SuppressWinKey = () => _winSuppressions++,
            Clock = () => _now,
            Schedule = (due, action) =>
            {
                var timer = new FakeTimer(_now + (long)due.TotalMilliseconds, action);
                _timers.Add(timer);
                return timer;
            },
        };
        _service.ChordDown += () => Add("down");
        _service.ChordUp += r => Add(r == ChordRelease.Tap ? "tap" : "up");
        _service.EscapePressed += () => Add("esc");
        _service.ArrowPressed += d => Add("arrow:" + d);
        _service.ChordObserved += down => Add(down ? "observed-down" : "observed-up");
    }

    public void Dispose() => _service.Dispose();

    private void Add(string e)
    {
        lock (_events)
        {
            _events.Add(e);
        }
    }

    private bool Key(int vk, bool down, bool injected = false) => _hook.Filter!(new LowLevelKeyboardHook.KeyEvent(vk, down, injected, 0));

    /// <summary>Moves the clock and fires the timers (the double-tap window) that became due.</summary>
    private void Advance(int ms)
    {
        _now += ms;
        foreach (var timer in _timers.ToArray())
        {
            if (!timer.Disposed && timer.Due <= _now)
            {
                timer.Disposed = true;
                timer.Action();
            }
        }
    }

    /// <summary>Press and release Ctrl+Alt, holding for <paramref name="holdMs"/>.</summary>
    private void Press(int holdMs)
    {
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Advance(holdMs);
        Key(HotkeyChord.VkLAlt, false);
        Key(HotkeyChord.VkLControl, false);
    }

    /// <summary>What the orchestrator publishes: an active state or Idle.</summary>
    private void Publish(DictationState state) =>
        _hub.Publish(new DictationStatus(state, string.Empty, 0f, Tone.Neutral, CleanupLevel.Light, null, null, null, true));

    private async Task<string[]> EventsAsync()
    {
        await Task.Delay(60); // events are dispatched on a background consumer
        lock (_events)
        {
            return [.. _events];
        }
    }

    /// <summary>A double tap: one dictation (started by the first press) that keeps running hands-free.</summary>
    private void StartHandsFree()
    {
        Press(120);
        Publish(DictationState.Arming);
        Advance(200);
        Press(120);
        Publish(DictationState.Recording);
    }

    [Fact]
    public async Task Hold_fires_down_on_full_chord_and_up_on_first_release()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"));
        Assert.False(Key(HotkeyChord.VkLControl, true));
        Assert.False(Key(HotkeyChord.VkLWin, true));
        Advance(1500);
        Assert.False(Key(HotkeyChord.VkLWin, false));
        Assert.False(Key(HotkeyChord.VkLControl, false));

        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.Equal(1, _winSuppressions);
        Assert.False(_service.IsChordHeld);
    }

    [Fact]
    public async Task Lone_tap_is_reported_as_a_tap_once_the_double_tap_window_has_passed()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Assert.Equal(["down"], await EventsAsync());

        Advance(399);
        Assert.Equal(["down"], await EventsAsync());
        Advance(1);
        Assert.Equal(["down", "tap"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Tap_classification_uses_the_key_times_not_when_the_events_are_handled()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(299);
        Advance(500);
        Press(300);
        Assert.Equal(["down", "tap", "down", "up"], await EventsAsync());
    }

    [Fact]
    public async Task Double_tap_is_one_dictation_that_keeps_running_hands_free_and_the_next_press_stops_it()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        StartHandsFree();

        // No second "down" and no cancelling "tap": the first press's dictation (and its microphone) simply continues.
        Assert.Equal(["down"], await EventsAsync());
        Assert.True(_service.IsHandsFree);
        Assert.True(_service.IsChordHeld);

        Advance(5000);
        Press(80);           // any press stops; its release is not a new tap
        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
        Publish(DictationState.Finalising);
        Publish(DictationState.Idle);

        Advance(2000);
        Press(120);          // a lone tap afterwards is just a tap again
        Advance(400);
        Assert.Equal(["down", "up", "down", "tap"], await EventsAsync());
    }

    [Fact]
    public async Task A_tap_then_a_hold_is_one_dictation_ended_by_the_hold()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Advance(200);
        Press(2000);
        Advance(1000);
        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Two_taps_too_far_apart_are_two_lone_taps()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Advance(900);
        Press(120);
        Advance(400);
        Assert.Equal(["down", "tap", "down", "tap"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Escape_in_hands_free_ends_it_and_the_next_press_starts_a_new_dictation()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        StartHandsFree();
        await EventsAsync();

        Assert.True(Key(HotkeyChord.VkEscape, true));
        Key(HotkeyChord.VkEscape, false);
        Assert.False(_service.IsHandsFree);
        Publish(DictationState.Idle);   // orchestrator discarded the session

        Advance(1000);
        Press(2000);                    // must be a fresh hold, not a swallowed "stop"
        Assert.Equal(["down", "esc", "down", "up"], await EventsAsync());
    }

    [Fact]
    public async Task Hands_free_that_stops_recording_by_itself_releases_escape_and_re_arms_the_chord()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        StartHandsFree();
        await EventsAsync();
        Assert.True(_service.IsHandsFree);

        // 20-minute cap or a network fault: the orchestrator finalises without any key press. From now on the dictation
        // no longer records, so Escape belongs to the focused app again (a discard only applies while recording).
        Publish(DictationState.Finalising);
        Assert.False(_service.IsHandsFree);
        Assert.False(Key(HotkeyChord.VkEscape, true));
        Key(HotkeyChord.VkEscape, false);
        Publish(DictationState.Idle);

        Advance(1000);
        Press(2000);
        Assert.Equal(["down", "down", "up"], await EventsAsync());
    }

    [Fact]
    public async Task A_press_the_orchestrator_ignores_never_latches_hands_free()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Publish(DictationState.PostProcessing); // a hold is still being cleaned up

        Press(120);
        _hub.Flash();                   // busy: the orchestrator ignored the press
        Advance(200);
        Press(120);                     // would have been the second tap
        _hub.Flash();
        Assert.False(_service.IsHandsFree);
        Publish(DictationState.Inserting);
        Publish(DictationState.Idle);

        Advance(1000);
        Press(2000);                    // a normal dictation, not a "stop" of a dictation that never ran
        Assert.Equal(["down", "down", "down", "up"], await EventsAsync());
    }

    [Fact]
    public async Task Idle_from_the_previous_dictation_does_not_cancel_a_new_press()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Publish(DictationState.Arming);  // a tap being cancelled
        Press(120);                      // a new press, chained by the orchestrator
        Publish(DictationState.Idle);    // the old dictation's Idle arrives late
        Advance(200);
        Press(120);                      // second tap of the new dictation
        Assert.True(_service.IsHandsFree);
        Publish(DictationState.Arming);  // the new dictation starts
        Publish(DictationState.Recording);
        Assert.True(_service.IsHandsFree);

        Advance(3000);
        Press(80);                       // stops it
        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Tray_dictation_is_hands_free_so_escape_discards_it_and_the_chord_stops_it()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        _service.StartHandsFree();
        Publish(DictationState.Arming);
        Assert.True(_service.IsHandsFree);
        Assert.True(Key(HotkeyChord.VkEscape, true));
        Key(HotkeyChord.VkEscape, false);
        Publish(DictationState.Idle);

        _service.StartHandsFree();
        Publish(DictationState.Arming);
        Publish(DictationState.Recording);
        Press(80);
        Assert.Equal(["down", "esc", "down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Observe_only_reports_the_chord_and_starts_nothing()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        _service.ObserveOnly = true;
        Press(120);
        Advance(1000);
        Press(1500);
        Assert.Equal(["observed-down", "observed-up", "observed-down", "observed-up"], await EventsAsync());

        _service.ObserveOnly = false;
        Press(1500);
        Assert.Equal("up", (await EventsAsync())[^1]);
    }

    [Fact]
    public async Task Hands_free_swallows_only_escape_and_lets_other_keys_through()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        StartHandsFree();
        await EventsAsync();

        Assert.False(Key('A', true));                 // typing passes through
        Assert.False(Key(HotkeyChord.VkLeft, true));  // arrows are not chip controls hands-free
        Assert.True(Key(HotkeyChord.VkEscape, true)); // Escape discards
        Key('A', false);
        Key(HotkeyChord.VkLeft, false);
        Key(HotkeyChord.VkEscape, false);
        var events = await EventsAsync();
        Assert.Equal("esc", events[^1]);
        Assert.DoesNotContain("arrow:Left", events);
    }

    [Fact]
    public async Task Win_plus_other_key_is_left_to_windows()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"));
        Key(HotkeyChord.VkLWin, true);
        Key('E', true);          // Win+E: explorer
        Key(HotkeyChord.VkLControl, true); // adding Ctrl now must not start dictation
        Key('E', false);
        Key(HotkeyChord.VkLControl, false);
        Key(HotkeyChord.VkLWin, false);

        Assert.Empty(await EventsAsync());
        Assert.Equal(0, _winSuppressions);
    }

    [Fact]
    public async Task Win_alone_is_not_suppressed()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"));
        Key(HotkeyChord.VkLWin, true);
        Key(HotkeyChord.VkLWin, false);
        Assert.Empty(await EventsAsync());
        Assert.Equal(0, _winSuppressions);
    }

    [Fact]
    public async Task Keys_that_reach_the_app_report_what_they_leave_before_the_caret()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        _service.ForegroundWindow = () => 42;
        _service.CharacterOf = (vk, shift, window) => window != 42 ? null : (vk, shift) switch
        {
            ('A', false) => 'a',
            ('A', true) => 'A',
            ('7', false) => '7',
            (0xBE, false) => '.',
            _ => null,
        };
        _service.KeyTyped += (w, k, c) => Add($"typed:{w}:{k}:{c}");

        Assert.False(Key('A', true));    // typed into the app, never swallowed
        Key('A', false);
        Tap('7');
        Tap(0xBE);                       // the full stop key
        Tap(HotkeyChord.VkSpace);
        Tap(0x08);                       // Backspace
        Tap(0x0D);                       // Enter: a chat app sends the message
        Key(HotkeyChord.VkLShift, true); // Shift+A and Shift+Enter: still a character and a line break
        Tap('A');
        Tap(0x0D);
        Key(HotkeyChord.VkLShift, false);
        Key(HotkeyChord.VkLControl, true); // Ctrl+Enter: the send key in some chat apps
        Tap(0x0D);
        Key(HotkeyChord.VkLControl, false);

        Assert.Equal(
            ["typed:42:Character:a", "typed:42:Character:7", "typed:42:Character:.", "typed:42:Space:", "typed:42:Erase:", "typed:42:LineBreak:", "typed:42:Character:A", "typed:42:LineBreak:", "typed:42:LineBreak:"],
            await EventsAsync());
    }

    [Fact]
    public void The_keyboard_layout_says_which_character_a_key_types()
    {
        // Whatever this machine's layout, a letter key types a letter, and Shift makes it a capital.
        Assert.True(HotkeyService.LayoutCharacter('A', shift: false, window: 0) is { } lower && char.IsLower(lower));
        Assert.True(HotkeyService.LayoutCharacter('A', shift: true, window: 0) is { } upper && char.IsUpper(upper));
    }

    [Fact]
    public async Task Keys_whose_effect_is_unknown_or_that_never_reach_the_app_report_nothing()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        _service.ForegroundWindow = () => 42;
        _service.KeyTyped += (w, k, c) => Add($"typed:{w}:{k}:{c}");

        Tap(HotkeyChord.VkLShift);
        Tap(HotkeyChord.VkCapsLock);
        Tap(0x09);                         // Tab: usually moves the focus to another control
        Key(HotkeyChord.VkLAlt, true);     // Alt+Enter and Win+Enter: shortcuts (full screen, properties)
        Tap(0x0D);
        Key(HotkeyChord.VkLAlt, false);
        Key(HotkeyChord.VkLWin, true);
        Tap(0x0D);
        Key(HotkeyChord.VkLWin, false);
        Tap(0x24);                         // Home
        Tap(HotkeyChord.VkLeft);
        Tap(0x2E);                         // Delete
        Tap(HotkeyChord.VkEscape);
        Tap(0x74);                         // F5
        Key(HotkeyChord.VkLControl, true); // Ctrl+C: a shortcut, whatever it does
        Tap('C');
        Key(HotkeyChord.VkLControl, false);
        Key('B', true, injected: true);    // our own paste keystrokes
        Key('B', false, injected: true);
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Assert.True(Key('D', true));       // blocked while the chord is held: it never reaches the app
        Key('D', false);
        Advance(1000);
        Key(HotkeyChord.VkLAlt, false);
        Key(HotkeyChord.VkLControl, false);

        Assert.Equal(["down", "up"], await EventsAsync());
    }

    private void Tap(int vk)
    {
        Key(vk, true);
        Key(vk, false);
    }

    [Fact]
    public async Task Arrows_and_escape_are_swallowed_while_holding_and_other_keys_blocked()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Assert.True(Key(HotkeyChord.VkLeft, true));
        Assert.True(Key(HotkeyChord.VkUp, true));
        Assert.True(Key('D', true));   // Win+Ctrl+D style shortcut blocked
        Assert.True(Key(HotkeyChord.VkEscape, true));
        Assert.False(Key(HotkeyChord.VkLControl, true)); // auto-repeat of a chord key passes
        Advance(1000);
        Key(HotkeyChord.VkLAlt, false);

        var events = await EventsAsync();
        Assert.Equal("down", events[0]);
        Assert.Contains("arrow:Left", events);
        Assert.Contains("arrow:Up", events);
        Assert.Contains("esc", events);
        Assert.Equal("up", events[^1]);
    }

    [Fact]
    public async Task Injected_events_are_ignored_unless_enabled()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Key(HotkeyChord.VkLControl, true, injected: true);
        Key(HotkeyChord.VkLAlt, true, injected: true);
        Assert.Empty(await EventsAsync());

        _service.AcceptInjectedKeys = true;
        Key(HotkeyChord.VkLControl, true, injected: true);
        Key(HotkeyChord.VkLAlt, true, injected: true);
        Assert.Equal(["down"], await EventsAsync());
    }

    [Fact]
    public async Task Disabled_service_tracks_keys_but_raises_nothing()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        _service.Enabled = false;
        Press(1000);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Single_key_chord_works()
    {
        _service.Configure(HotkeyChord.Parse("F8"));
        Key(0x77, true);
        Advance(800);
        Key(0x77, false);
        Assert.Equal(["down", "up"], await EventsAsync());
    }

    private sealed class FakeTimer(long due, Action action) : IDisposable
    {
        public long Due { get; } = due;

        public Action Action { get; } = action;

        public bool Disposed { get; set; }

        public void Dispose() => Disposed = true;
    }
}
