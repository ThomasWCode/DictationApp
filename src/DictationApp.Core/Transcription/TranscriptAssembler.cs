namespace DictationApp.Core.Transcription;

/// <summary>
/// Pure. Collects Turn messages keyed by <c>turn_order</c>. Turns are immutable once <c>end_of_turn</c> is true,
/// but the server may send an unformatted final turn followed by a formatted one; the formatted one wins.
/// Out-of-order and duplicate deliveries are tolerated.
/// </summary>
public sealed class TranscriptAssembler
{
    private readonly SortedDictionary<int, TurnMessage> _turns = [];
    private readonly object _lock = new();

    public int TurnCount
    {
        get
        {
            lock (_lock)
            {
                return _turns.Count;
            }
        }
    }

    /// <summary>True when the most recent turn has not yet reached end_of_turn.</summary>
    public bool HasOpenTurn
    {
        get
        {
            lock (_lock)
            {
                return _turns.Count > 0 && !_turns.Values.Last().EndOfTurn;
            }
        }
    }

    public int? LastTurnOrder
    {
        get
        {
            lock (_lock)
            {
                return _turns.Count > 0 ? _turns.Keys.Last() : null;
            }
        }
    }

    /// <summary>Finals plus the current partial, for the overlay.</summary>
    public string LiveText => Join(includeOpenTurns: true);

    /// <summary>Joined text of end_of_turn turns, plus a trailing partial if the server never closed it.</summary>
    public string FinalText => Join(includeOpenTurns: true);

    /// <summary>Only turns the server explicitly closed.</summary>
    public string ClosedText => Join(includeOpenTurns: false);

    /// <summary>Returns true when the turn changed the assembled text.</summary>
    public bool Ingest(TurnMessage turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (_lock)
        {
            if (_turns.TryGetValue(turn.TurnOrder, out var existing))
            {
                // Never let an unformatted or partial turn overwrite a formatted, closed one.
                if (existing.EndOfTurn && !turn.EndOfTurn)
                {
                    return false;
                }

                if (existing.EndOfTurn && existing.TurnIsFormatted && !turn.TurnIsFormatted)
                {
                    return false;
                }

                if (turn.BestText.Length == 0 && existing.BestText.Length > 0 && !turn.EndOfTurn)
                {
                    return false;
                }
            }

            _turns[turn.TurnOrder] = turn;
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _turns.Clear();
        }
    }

    private string Join(bool includeOpenTurns)
    {
        lock (_lock)
        {
            var parts = new List<string>(_turns.Count);
            foreach (var turn in _turns.Values)
            {
                if (!turn.EndOfTurn && !includeOpenTurns)
                {
                    continue;
                }

                var text = turn.BestText;
                if (text.Length > 0)
                {
                    parts.Add(text);
                }
            }

            return string.Join(' ', parts);
        }
    }
}
