namespace DictationApp.Core.Insertion;

/// <summary>
/// Decides the joining whitespace and capitalisation before an insertion. The text before the caret, read from
/// the control, decides when it is known: an empty chat box after a message was sent gets no leading space.
/// Otherwise what we inserted last per window handle stands in for it; that memory expires so a stale entry
/// cannot mis-space tomorrow's text.
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

    /// <param name="textBeforeCaret">The control's text just before the caret ("" at the start of a field), or null
    /// when it could not be read.</param>
    public string Format(string text, nint windowHandle, string? textBeforeCaret = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            string? tail = null;
            if (textBeforeCaret is not null)
            {
                tail = VisibleTail(textBeforeCaret);
            }
            else if (_lastByWindow.TryGetValue(windowHandle, out var last) && now - last.At <= Memory)
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

    /// <summary>
    /// The last character of <paramref name="textBeforeCaret"/> that is not an invisible stand-in, or null at the start
    /// of the field. Rich editors report an empty paragraph as an object character (U+FFFC); WhatsApp keeps a zero-width
    /// space in its empty fields.
    /// </summary>
    public static string? VisibleTail(string textBeforeCaret)
    {
        var trimmed = textBeforeCaret.TrimEnd('\uFFFC', '\u200B', '\u200C', '\u200D', '\u2060', '\uFEFF');
        return trimmed.Length == 0 ? null : trimmed[^1].ToString();
    }

    /// <summary>Pure core: <paramref name="previousTail"/> is the character before the insertion point, or null.</summary>
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
