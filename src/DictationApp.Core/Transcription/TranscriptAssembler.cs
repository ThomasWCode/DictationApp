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

    /// <summary>Written between turns in <see cref="PauseMarkedText"/>.</summary>
    public const string PauseMarker = "[pause]";

    /// <summary>
    /// <see cref="FinalText"/> prepared by <see cref="JoinAtPauses"/>, for the cleanup LLM only. Every turn ends at a
    /// pause of at least min_turn_silence and is punctuated as a whole sentence, so a full stop at a boundary may only
    /// be the speaker stopping to think.
    /// </summary>
    public string PauseMarkedText => JoinAtPauses(Parts(includeOpenTurns: true));

    /// <summary>
    /// Joins turn texts with <see cref="PauseMarker"/> and removes the transcriber's guess at each pause: the full
    /// stop ending a turn is dropped and the next turn's first word lower-cased (not "I", acronyms or mixed-case names
    /// such as "WhatsApp"), so the LLM decides afresh whether a sentence ends there. Measured against gpt-oss-120b at
    /// Light: kept, the full stop anchored the model and "chat box. [pause] Still appends" stayed split; removed, all
    /// such breaks joined while real sentence ends and names were restored. Question and exclamation marks stay.
    /// </summary>
    public static string JoinAtPauses(IEnumerable<string> turns)
    {
        var parts = turns.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i];
            if (i > 0)
            {
                sb.Append(' ').Append(PauseMarker).Append(' ');
                text = LowerFirstWord(text);
            }

            if (i < parts.Count - 1 && text.EndsWith('.') && !text.EndsWith("..", StringComparison.Ordinal))
            {
                text = text[..^1];
            }

            sb.Append(text);
        }

        return sb.ToString();
    }

    private static string LowerFirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetter(text[end]) || text[end] == '\''))
        {
            end++;
        }

        var word = text[..end];
        if (word.Length == 0 || !char.IsUpper(word[0]) || word == "I" || word.StartsWith("I'", StringComparison.Ordinal) || word.Skip(1).Any(char.IsUpper))
        {
            return text;
        }

        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>Removes any <see cref="PauseMarker"/> a model left in its output.</summary>
    public static string RemovePauseMarkers(string text)
    {
        if (!text.Contains(PauseMarker, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var result = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]*\[pause\][ \t]*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        result = System.Text.RegularExpressions.Regex.Replace(result, @" {2,}", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @" (?=[.,;:!?\n])|(?<=\n) ", string.Empty);
        return result.Trim();
    }

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

    private string Join(bool includeOpenTurns) => string.Join(' ', Parts(includeOpenTurns));

    private List<string> Parts(bool includeOpenTurns)
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

            return parts;
        }
    }
}
