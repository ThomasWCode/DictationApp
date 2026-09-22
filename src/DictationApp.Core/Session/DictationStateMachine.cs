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
    ChordUp,
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
/// Declarative state machine (Stateless) for one dictation. The only time-dependent decision, "was that a
/// short tap or a real hold?", lives here so it can be unit-tested with a fake clock. Everything else is
/// fired by the orchestrator in response to real events or timers.
/// </summary>
public sealed class DictationStateMachine
{
    private readonly StateMachine<DictationState, DictationTrigger> _machine;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private DateTimeOffset _armedAt;

    public DictationStateMachine(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _machine = new StateMachine<DictationState, DictationTrigger>(DictationState.Idle);

        _machine.Configure(DictationState.Idle)
            .Permit(DictationTrigger.ChordDown, DictationState.Arming)
            .Ignore(DictationTrigger.ChordUp)
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
                _armedAt = _time.GetUtcNow();
                LastReleaseWasShortTap = false;
                WasDiscarded = false;
            })
            .PermitDynamic(DictationTrigger.ChordUp, () =>
            {
                var held = _time.GetUtcNow() - _armedAt;
                LastReleaseWasShortTap = held < ShortPressThreshold;
                return LastReleaseWasShortTap ? DictationState.Idle : DictationState.Finalising;
            })
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
            .Permit(DictationTrigger.Escape, DictationState.Idle)
            .Ignore(DictationTrigger.ChordDown)
            .Ignore(DictationTrigger.ChordUp)
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

            Transitioned?.Invoke(new DictationTransition(t.Source, t.Destination, t.Trigger));
        });
    }

    public event Action<DictationTransition>? Transitioned;

    /// <summary>Raised when a ChordDown arrives while a dictation is already running (bar flash).</summary>
    public event Action? ChordDownIgnored;

    public TimeSpan ShortPressThreshold { get; init; } = TimeSpan.FromMilliseconds(300);

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

    /// <summary>True after a ChordUp in Arming that was shorter than <see cref="ShortPressThreshold"/>.</summary>
    public bool LastReleaseWasShortTap { get; private set; }

    /// <summary>True after an Escape transition; the current session's text must not be inserted or stored.</summary>
    public bool WasDiscarded { get; private set; }

    public TimeSpan HeldFor => State == DictationState.Idle ? TimeSpan.Zero : _time.GetUtcNow() - _armedAt;

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
