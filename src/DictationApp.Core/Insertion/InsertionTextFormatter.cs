namespace DictationApp.Core.Insertion;

/// <summary>
/// Pure. Decides the joining whitespace and capitalisation between consecutive insertions into the same
/// window. We never read the target's text (too slow and unreliable across apps); instead we remember what
/// we inserted last per window handle. The memory expires so a stale entry cannot mis-space tomorrow's text.
/// </summary>
public sealed class InsertionTextFormatter
{
    private readonly Dictionary<nint, (string Tail, DateTimeOffset At)> _lastByWindow = [];
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    public InsertionTextFormatter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long a previous insertion is remembered for spacing decisions.</summary>
    public TimeSpan Memory { get; init; } = TimeSpan.FromMinutes(10);

    public string Format(string text, nint windowHandle)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            string? tail = null;
            if (_lastByWindow.TryGetValue(windowHandle, out var last) && now - last.At <= Memory)
            {
                tail = last.Tail;
            }

            var result = Apply(text, tail);
            _lastByWindow[windowHandle] = (result.Length > 0 ? result[^1].ToString() : string.Empty, now);
            return result;
        }
    }

    /// <summary>Forget what was inserted into <paramref name="windowHandle"/> (e.g. the user typed in between).</summary>
    public void Reset(nint windowHandle)
    {
        lock (_lock)
        {
            _lastByWindow.Remove(windowHandle);
        }
    }

    public void ResetAll()
    {
        lock (_lock)
        {
            _lastByWindow.Clear();
        }
    }

    /// <summary>Pure core: <paramref name="previousTail"/> is the last character we inserted into this window, or null.</summary>
    public static string Apply(string text, string? previousTail)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var prev = string.IsNullOrEmpty(previousTail) ? '\0' : previousTail[^1];
        var s = text;
        var needsSpace = prev != '\0' && !char.IsWhiteSpace(prev) && char.IsLetterOrDigit(s[0]) && prev != '(' && prev != '[' && prev != '"' && prev != '\'';
        var afterSentenceEnd = prev is '.' or '!' or '?' or '\n';
        if (afterSentenceEnd && char.IsLetter(s[0]) && char.IsLower(s[0]))
        {
            s = char.ToUpperInvariant(s[0]) + s[1..];
        }

        return needsSpace ? " " + s : s;
    }
}
