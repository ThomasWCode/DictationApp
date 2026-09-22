using DictationApp.Core.Session;
using Microsoft.Extensions.Time.Testing;

namespace DictationApp.Core.Tests;

public class DictationStateMachineTests
{
    private static (DictationStateMachine Machine, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        return (new DictationStateMachine(clock), clock);
    }

    [Fact]
    public void Starts_idle_and_arms_on_chord_down()
    {
        var (m, _) = Create();
        Assert.Equal(DictationState.Idle, m.State);
        Assert.Equal(DictationState.Arming, m.Fire(DictationTrigger.ChordDown));
    }

    [Fact]
    public void Short_tap_returns_to_idle_and_is_flagged()
    {
        var (m, clock) = Create();
        m.Fire(DictationTrigger.ChordDown);
        clock.Advance(TimeSpan.FromMilliseconds(120));

        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.ChordUp));
        Assert.True(m.LastReleaseWasShortTap);
    }

    [Fact]
    public void Release_after_threshold_before_begin_goes_to_finalising()
    {
        var (m, clock) = Create();
        m.Fire(DictationTrigger.ChordDown);
        clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.ChordUp));
        Assert.False(m.LastReleaseWasShortTap);
    }

    [Fact]
    public void Full_happy_path()
    {
        var (m, clock) = Create();
        var transitions = new List<DictationTransition>();
        m.Transitioned += transitions.Add;

        m.Fire(DictationTrigger.ChordDown);
        Assert.Equal(DictationState.Recording, m.Fire(DictationTrigger.BeginReceived));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.ChordUp));
        Assert.Equal(DictationState.PostProcessing, m.Fire(DictationTrigger.HandshakeComplete));
        Assert.Equal(DictationState.Inserting, m.Fire(DictationTrigger.PostProcessed));
        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.Inserted));
        Assert.Equal(6, transitions.Count);
    }

    [Theory]
    [InlineData(DictationTrigger.ConnectTimeout)]
    [InlineData(DictationTrigger.SocketFault)]
    [InlineData(DictationTrigger.Escape)]
    [InlineData(DictationTrigger.Failed)]
    public void Arming_failure_paths_return_to_idle(DictationTrigger trigger)
    {
        var (m, _) = Create();
        m.Fire(DictationTrigger.ChordDown);
        Assert.Equal(DictationState.Idle, m.Fire(trigger));
    }

    [Fact]
    public void Escape_marks_session_discarded()
    {
        var (m, _) = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.Escape);

        Assert.True(m.WasDiscarded);
        Assert.Equal(DictationState.Idle, m.State);
    }

    [Fact]
    public void Cap_reached_finalises_recording()
    {
        var (m, _) = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.CapReached));
    }

    [Fact]
    public void Nothing_heard_goes_idle_from_finalising()
    {
        var (m, _) = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.ChordUp);
        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.NothingHeard));
    }

    [Fact]
    public void Chord_down_while_busy_is_ignored_and_flashes()
    {
        var (m, _) = Create();
        var flashes = 0;
        m.ChordDownIgnored += () => flashes++;
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);

        Assert.Equal(DictationState.Recording, m.Fire(DictationTrigger.ChordDown));
        Assert.Equal(1, flashes);
    }

    [Fact]
    public void Irrelevant_triggers_are_ignored_in_every_state()
    {
        var (m, _) = Create();
        foreach (var trigger in Enum.GetValues<DictationTrigger>())
        {
            if (trigger != DictationTrigger.ChordDown)
            {
                Assert.Equal(DictationState.Idle, m.Fire(trigger));
            }
        }

        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        Assert.Equal(DictationState.Recording, m.Fire(DictationTrigger.PostProcessed));
        Assert.Equal(DictationState.Recording, m.Fire(DictationTrigger.HandshakeComplete));
    }

    [Fact]
    public void Copied_only_and_failed_leave_inserting()
    {
        var (m, _) = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.ChordUp);
        m.Fire(DictationTrigger.HandshakeComplete);
        m.Fire(DictationTrigger.PostProcessed);
        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.CopiedOnly));
    }
}
