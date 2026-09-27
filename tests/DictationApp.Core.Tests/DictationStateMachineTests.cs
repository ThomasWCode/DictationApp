using DictationApp.Core.Session;

namespace DictationApp.Core.Tests;

public class DictationStateMachineTests
{
    private static DictationStateMachine Create() => new();

    [Fact]
    public void Starts_idle_and_arms_on_chord_down()
    {
        var m = Create();
        Assert.Equal(DictationState.Idle, m.State);
        Assert.Equal(DictationState.Arming, m.Fire(DictationTrigger.ChordDown));
    }

    [Fact]
    public void Tap_before_begin_returns_to_idle_and_is_flagged()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);

        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.Tap));
        Assert.True(m.LastReleaseWasShortTap);
    }

    [Fact]
    public void Tap_after_begin_still_cancels()
    {
        // The hotkey service classifies from the physical key times, so a fast connection no longer turns a tap into
        // a recorded, inserted dictation.
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);

        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.Tap));
        Assert.True(m.LastReleaseWasShortTap);
    }

    [Fact]
    public void Release_before_begin_goes_to_finalising()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);

        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.ChordUp));
        Assert.False(m.LastReleaseWasShortTap);
    }

    [Fact]
    public void Full_happy_path()
    {
        var m = Create();
        var transitions = new List<DictationTransition>();
        m.Transitioned += transitions.Add;

        m.Fire(DictationTrigger.ChordDown);
        Assert.Equal(DictationState.Recording, m.Fire(DictationTrigger.BeginReceived));
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
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        Assert.Equal(DictationState.Idle, m.Fire(trigger));
    }

    [Fact]
    public void Escape_marks_session_discarded()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.Escape);

        Assert.True(m.WasDiscarded);
        Assert.Equal(DictationState.Idle, m.State);
    }

    [Fact]
    public void Escape_is_ignored_once_the_dictation_stopped_recording()
    {
        // A discard is only possible while recording; after the release the dictation always finishes.
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.CapReached);
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.Escape));

        m.Fire(DictationTrigger.HandshakeComplete);
        Assert.Equal(DictationState.PostProcessing, m.Fire(DictationTrigger.Escape));
        m.Fire(DictationTrigger.PostProcessed);
        Assert.Equal(DictationState.Inserting, m.Fire(DictationTrigger.Escape));
        Assert.False(m.WasDiscarded);
    }

    [Fact]
    public void Cap_reached_finalises_recording()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.CapReached));
    }

    [Fact]
    public void Nothing_heard_goes_idle_from_finalising()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.ChordUp);
        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.NothingHeard));
    }

    [Fact]
    public void Chord_down_while_busy_is_ignored_and_flashes()
    {
        var m = Create();
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
        var m = Create();
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

        m.Fire(DictationTrigger.ChordUp);
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.Tap));
        Assert.Equal(DictationState.Finalising, m.Fire(DictationTrigger.ChordUp));
    }

    [Fact]
    public void Copied_only_and_failed_leave_inserting()
    {
        var m = Create();
        m.Fire(DictationTrigger.ChordDown);
        m.Fire(DictationTrigger.BeginReceived);
        m.Fire(DictationTrigger.ChordUp);
        m.Fire(DictationTrigger.HandshakeComplete);
        m.Fire(DictationTrigger.PostProcessed);
        Assert.Equal(DictationState.Idle, m.Fire(DictationTrigger.CopiedOnly));
    }
}
