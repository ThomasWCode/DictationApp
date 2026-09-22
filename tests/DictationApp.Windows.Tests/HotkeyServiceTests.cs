using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Windows.Hotkey;
using Microsoft.Extensions.Logging.Abstractions;

namespace DictationApp.Windows.Tests;

/// <summary>Drives the hook filter directly; no real hook is installed.</summary>
public sealed class HotkeyServiceTests : IDisposable
{
    private readonly LowLevelKeyboardHook _hook = new(NullLogger<LowLevelKeyboardHook>.Instance);
    private readonly HotkeyService _service;
    private readonly List<string> _events = [];
    private int _winSuppressions;

    public HotkeyServiceTests()
    {
        _service = new HotkeyService(_hook, NullLogger<HotkeyService>.Instance) { SuppressWinKey = () => _winSuppressions++ };
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

    private async Task<string[]> EventsAsync()
    {
        await Task.Delay(60); // events are dispatched on the thread pool
        lock (_events)
        {
            return [.. _events];
        }
    }

    [Fact]
    public async Task Hold_mode_fires_down_on_full_chord_and_up_on_first_release()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"), HotkeyMode.Hold);
        Assert.False(Key(HotkeyChord.VkLControl, true));
        Assert.False(Key(HotkeyChord.VkLWin, true));
        Assert.False(Key(HotkeyChord.VkLWin, false));
        Assert.False(Key(HotkeyChord.VkLControl, false));

        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.Equal(1, _winSuppressions);
        Assert.False(_service.IsChordHeld);
    }

    [Fact]
    public async Task Win_plus_other_key_is_left_to_windows()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"), HotkeyMode.Hold);
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
        _service.Configure(HotkeyChord.Parse("Ctrl+Win"), HotkeyMode.Hold);
        Key(HotkeyChord.VkLWin, true);
        Key(HotkeyChord.VkLWin, false);
        Assert.Empty(await EventsAsync());
        Assert.Equal(0, _winSuppressions);
    }

    [Fact]
    public async Task Arrows_and_escape_are_swallowed_while_dictating_and_other_keys_blocked()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"), HotkeyMode.Hold);
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Assert.True(Key(HotkeyChord.VkLeft, true));
        Assert.True(Key(HotkeyChord.VkUp, true));
        Assert.True(Key('D', true));   // Win+Ctrl+D style shortcut blocked
        Assert.True(Key(HotkeyChord.VkEscape, true));
        Assert.False(Key(HotkeyChord.VkLControl, true)); // auto-repeat of a chord key passes
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
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"), HotkeyMode.Hold);
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
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"), HotkeyMode.Hold);
        _service.Enabled = false;
        Key(HotkeyChord.VkLControl, true);
        Key(HotkeyChord.VkLAlt, true);
        Key(HotkeyChord.VkLAlt, false);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Double_tap_toggle_starts_on_second_tap_and_stops_on_next()
    {
        _service.Configure(HotkeyChord.Parse("Ctrl+Alt"), HotkeyMode.DoubleTapToggle);
        void Tap()
        {
            Key(HotkeyChord.VkLControl, true);
            Key(HotkeyChord.VkLAlt, true);
            Key(HotkeyChord.VkLAlt, false);
            Key(HotkeyChord.VkLControl, false);
        }

        Tap();
        Assert.Empty(await EventsAsync());
        Tap();
        Assert.Equal(["down"], await EventsAsync());
        Assert.True(_service.IsChordHeld);
        Tap();
        Assert.Equal(["down", "up"], await EventsAsync());
        Assert.False(_service.IsChordHeld);
    }

    [Fact]
    public async Task Single_key_chord_works()
    {
        _service.Configure(HotkeyChord.Parse("F8"), HotkeyMode.Hold);
        Key(0x77, true);
        Key(0x77, false);
        Assert.Equal(["down", "up"], await EventsAsync());
    }
}
