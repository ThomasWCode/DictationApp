using Stateless;

namespace DictationApp.Core.Session;

public enum DictationState
{
    Idle,
    Arming,
    Recording,
    Finalising,
    PostProcessing,
    Inserting,
}

public enum DictationTrigger
{
    ChordDown,

    /// <summary>The chord was released after a hold, or a hands-free dictation was stopped: finish and insert.</summary>
    ChordUp,

    /// <summary>A lone press shorter than the tap threshold, as classified by the hotkey service: cancel.</summary>
    Tap,
    BeginReceived,
    ConnectTimeout,
    SocketFault,
    Escape,
    CapReached,
    HandshakeComplete,
    NothingHeard,
    PostProcessed,
    Inserted,
    CopiedOnly,
    Failed,
}

/// <summary>
/// Declarative state machine (Stateless) for one dictation, fired by the orchestrator in response to real events and
/// timers.
/// <para>
/// Whether a release was a short tap or a hold is decided by the hotkey service from the physical key times
/// (<see cref="DictationTrigger.Tap"/> versus <see cref="DictationTrigger.ChordUp"/>), never from when the orchestrator
/// got round to handling the release: start-up work (microphone, window lookup) can take longer than the 300 ms
/// threshold, which turned taps into billed holds. A tap cancels whether or not the streaming session has begun.
/// </para>
/// A discard (<see cref="DictationTrigger.Escape"/>) is only possible while recording (Arming or Recording). Once the
/// chord is released the dictation always finishes, into the text field or onto the clipboard, and is kept in History.
/// </summary>
public sealed class DictationStateMachine
{
    private readonly StateMachine<DictationState, DictationTrigger> _machine;
    private readonly object _lock = new();

    public DictationStateMachine()
    {
        _machine = new StateMachine<DictationState, DictationTrigger>(DictationState.Idle);

        _machine.Configure(DictationState.Idle)
            .Permit(DictationTrigger.ChordDown, DictationState.Arming)
            .Ignore(DictationTrigger.ChordUp)
            .Ignore(DictationTrigger.Tap)
            .Ignore(DictationTrigger.Escape)
            .Ignore(DictationTrigger.BeginReceived)
            .Ignore(DictationTrigger.SocketFault)
            .Ignore(DictationTrigger.ConnectTimeout)
            .Ignore(DictationTrigger.CapReached)
            .Ignore(DictationTrigger.HandshakeComplete)
            .Ignore(DictationTrigger.NothingHeard)
            .Ignore(DictationTrigger.PostProcessed)
            .Ignore(DictationTrigger.Inserted)
            .Ignore(DictationTrigger.CopiedOnly)
            .Ignore(DictationTrigger.Failed);

        _machine.Configure(DictationState.Arming)
            .OnEntry(() =>
            {
                LastReleaseWasShortTap = false;
                WasDiscarded = false;
            })
            .Permit(DictationTrigger.ChordUp, DictationState.Finalising)
            .Permit(DictationTrigger.Tap, DictationState.Idle)
            .Permit(DictationTrigger.BeginReceived, DictationState.Recording)
            .Permit(DictationTrigger.ConnectTimeout, DictationState.Idle)
            .Permit(DictationTrigger.SocketFault, DictationState.Idle)
            .Permit(DictationTrigger.Escape, DictationState.Idle)
            .Permit(DictationTrigger.Failed, DictationState.Idle)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.CapReached)
            .Ignore(DictationTrigger.HandshakeComplete)
            .Ignore(DictationTrigger.NothingHeard)
            .Ignore(DictationTrigger.PostProcessed)
            .Ignore(DictationTrigger.Inserted)
            .Ignore(DictationTrigger.CopiedOnly);

        _machine.Configure(DictationState.Recording)
            .Permit(DictationTrigger.ChordUp, DictationState.Finalising)
            .Permit(DictationTrigger.Tap, DictationState.Idle) // Begin arrived before the tap was released
            .Permit(DictationTrigger.CapReached, DictationState.Finalising)
            .Permit(DictationTrigger.Escape, DictationState.Idle)
            .Permit(DictationTrigger.SocketFault, DictationState.Idle)
            .Permit(DictationTrigger.Failed, DictationState.Idle)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.BeginReceived)
            .Ignore(DictationTrigger.ConnectTimeout)
            .Ignore(DictationTrigger.HandshakeComplete)
            .Ignore(DictationTrigger.NothingHeard)
            .Ignore(DictationTrigger.PostProcessed)
            .Ignore(DictationTrigger.Inserted)
            .Ignore(DictationTrigger.CopiedOnly);

        _machine.Configure(DictationState.Finalising)
            .Permit(DictationTrigger.HandshakeComplete, DictationState.PostProcessing)
            .Permit(DictationTrigger.NothingHeard, DictationState.Idle)
            .Permit(DictationTrigger.Failed, DictationState.Idle)
            .Ignore(DictationTrigger.Escape) // released: the dictation finishes (a discard is only possible while recording)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.ChordUp)
            .Ignore(DictationTrigger.Tap)
            .Ignore(DictationTrigger.BeginReceived)
            .Ignore(DictationTrigger.ConnectTimeout)
            .Ignore(DictationTrigger.SocketFault)
            .Ignore(DictationTrigger.CapReached)
            .Ignore(DictationTrigger.PostProcessed)
            .Ignore(DictationTrigger.Inserted)
            .Ignore(DictationTrigger.CopiedOnly);

        _machine.Configure(DictationState.PostProcessing)
            .Permit(DictationTrigger.PostProcessed, DictationState.Inserting)
            .Permit(DictationTrigger.Failed, DictationState.Idle)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.ChordUp)
            .Ignore(DictationTrigger.Tap)
            .Ignore(DictationTrigger.Escape)
            .Ignore(DictationTrigger.BeginReceived)
            .Ignore(DictationTrigger.ConnectTimeout)
            .Ignore(DictationTrigger.SocketFault)
            .Ignore(DictationTrigger.CapReached)
            .Ignore(DictationTrigger.HandshakeComplete)
            .Ignore(DictationTrigger.NothingHeard)
            .Ignore(DictationTrigger.Inserted)
            .Ignore(DictationTrigger.CopiedOnly);

        _machine.Configure(DictationState.Inserting)
            .Permit(DictationTrigger.Inserted, DictationState.Idle)
            .Permit(DictationTrigger.CopiedOnly, DictationState.Idle)
            .Permit(DictationTrigger.Failed, DictationState.Idle)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.ChordUp)
            .Ignore(DictationTrigger.Tap)
            .Ignore(DictationTrigger.Escape)
            .Ignore(DictationTrigger.BeginReceived)
            .Ignore(DictationTrigger.ConnectTimeout)
            .Ignore(DictationTrigger.SocketFault)
            .Ignore(DictationTrigger.CapReached)
            .Ignore(DictationTrigger.HandshakeComplete)
            .Ignore(DictationTrigger.NothingHeard)
            .Ignore(DictationTrigger.PostProcessed);

        _machine.OnTransitioned(t =>
        {
            if (t.Trigger == DictationTrigger.Escape)
            {
                WasDiscarded = true;
            }

            if (t.Trigger == DictationTrigger.Tap)
            {
                LastReleaseWasShortTap = true;
            }

            Transitioned?.Invoke(new DictationTransition(t.Source, t.Destination, t.Trigger));
        });
    }

    public event Action<DictationTransition>? Transitioned;

    /// <summary>Raised when a ChordDown arrives while a dictation is already running (bar flash).</summary>
    public event Action? ChordDownIgnored;

    public DictationState State
    {
        get
        {
            lock (_lock)
            {
                return _machine.State;
            }
        }
    }

    public bool IsIdle => State == DictationState.Idle;

    /// <summary>True after a <see cref="DictationTrigger.Tap"/> cancelled the dictation.</summary>
    public bool LastReleaseWasShortTap { get; private set; }

    /// <summary>True after an Escape transition; the current session's text must not be inserted or stored.</summary>
    public bool WasDiscarded { get; private set; }

    /// <summary>Fires the trigger; returns the resulting state. Unknown triggers for the state are ignored.</summary>
    public DictationState Fire(DictationTrigger trigger)
    {
        lock (_lock)
        {
            if (trigger == DictationTrigger.ChordDown && _machine.State != DictationState.Idle)
            {
                ChordDownIgnored?.Invoke();
                return _machine.State;
            }

            _machine.Fire(trigger);
            return _machine.State;
        }
    }

    public bool CanFire(DictationTrigger trigger)
    {
        lock (_lock)
        {
            return _machine.CanFire(trigger);
        }
    }
}

public readonly record struct DictationTransition(DictationState From, DictationState To, DictationTrigger Trigger);
