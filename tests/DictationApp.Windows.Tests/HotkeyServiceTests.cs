using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;

namespace DictationApp.Windows.Tests;

/// <summary>Drives the hook filter directly with a fake clock; no real hook is installed.</summary>
public sealed class HotkeyServiceTests : IDisposable
{
    private readonly LowLevelKeyboardHook _hook = new(NullLogger<LowLevelKeyboardHook>.Instance);
    private readonly HotkeyService _service;
    private readonly List<string> _events = [];
    private long _now = 100_000;
    private int _winSuppressions;

    public HotkeyServiceTests()
    {
        _service = new HotkeyService(_hook, NullLogger<HotkeyService>.Instance)
        {
            SuppressWinKey = () => _winSuppressions++,
            Clock = () => _now,
        };
        _service.ChordDown += () => Add("down");
        _service.ChordUp += () => Add("up");
        _service.EscapePressed += () => Add("esc");
        _service.ArrowPressed += d => Add("arrow:" + d);
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

    private void Advance(int ms) => _now += ms;

    /// <summary>Press and release Ctrl+Alt, holding for <paramref name="holdMs"/>.</summary>
    private void Press(int holdMs)
    {
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Advance(holdMs);
        Key(HotkeyChord.VkLAlt, false);
        Key(HotkeyChord.VkLControl, false);
    }

    private async Task<string[]> EventsAsync()
    {
        await Task.Delay(60); // events are dispatched on a background consumer
        lock (_events)
        {
            return [.. _events];
        }
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
    public async Task Lone_tap_raises_down_then_up_so_the_orchestrator_cancels_it()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Double_tap_starts_hands_free_and_next_press_stops_it()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);          // tap 1: down + up (cancelled by the orchestrator as a short press)
        Advance(200);
        Press(120);          // tap 2 within 400 ms: down, no up -> hands-free
        Assert.Equal(["down", "up", "down"], await EventsAsync());
        Assert.True(_service.IsHandsFree);
        Assert.True(_service.IsChordHeld);

        Advance(5000);
        Press(80);           // any press stops; its release is not a new tap
        Assert.Equal(["down", "up", "down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
        Assert.False(_service.IsChordHeld);

        Advance(2000);
        Press(120);          // a lone tap afterwards is just a tap again
        Assert.Equal(["down", "up", "down", "up", "down", "up"], await EventsAsync());
    }

    [Fact]
    public async Task Two_taps_too_far_apart_are_two_lone_taps()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Advance(900);
        Press(120);
        Assert.Equal(["down", "up", "down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task A_long_hold_after_a_tap_is_a_normal_hold()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Advance(200);
        Press(2000);
        Assert.Equal(["down", "up", "down", "up"], await EventsAsync());
        Assert.False(_service.IsHandsFree);
    }

    [Fact]
    public async Task Hands_free_swallows_only_escape_and_lets_other_keys_through()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"));
        Press(120);
        Advance(200);
        Press(120);
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
}
