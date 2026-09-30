namespace DictationApp.Core.Insertion;

/// <summary>Where the character before an insertion was learned from.</summary>
public enum SpacingBasis
{
    /// <summary>Neither the control nor the memory said anything: as far as we know nothing precedes the insertion.</summary>
    Unknown,

    /// <summary>Read from the control; an empty field reads as "".</summary>
    Caret,

    /// <summary>The control could not be read, so the last insertion into the window stood in.</summary>
    Memory,
}

/// <summary>What a key the user pressed leaves before the caret, as far as the key alone tells (see <see cref="InsertionTextFormatter.Typed"/>).</summary>
public enum TypedKey
{
    /// <summary>A character that shows: a letter, digit or punctuation mark.</summary>
    Character,

    /// <summary>Backspace: it removed a character, and one usually still shows before the caret.</summary>
    Erase,

    /// <summary>Space or Tab.</summary>
    Space,

    /// <summary>Enter, with or without a modifier: a new line, or a message sent and its box emptied.</summary>
    LineBreak,
}

/// <param name="Text">The text to insert, with its joining space and capital.</param>
/// <param name="Basis">Where the character before the insertion came from.</param>
/// <param name="Tail">That character, or null when there is none (the start of a field).</param>
public readonly record struct FormattedInsertion(string Text, SpacingBasis Basis, string? Tail);

/// <summary>
/// Decides the joining whitespace and capitalisation before an insertion. The text before the caret, read from
/// the control, decides when it is known: an empty chat box after a message was sent gets no leading space.
/// Otherwise what we inserted last per window handle stands in for it; that memory expires so a stale entry
/// cannot mis-space tomorrow's text.
/// </summary>
public sealed class InsertionTextFormatter
{
    /// <summary>Stands in for a character the user typed, or one Backspace left: it shows, so a dictation is joined with a space.</summary>
    private const string TypedCharacter = "a";

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
    public string Format(string text, nint windowHandle, string? textBeforeCaret = null) =>
        FormatDetailed(text, windowHandle, textBeforeCaret).Text;

    /// <summary><see cref="Format"/>, and where the character before the insertion came from, so the decision can be logged.</summary>
    public FormattedInsertion FormatDetailed(string text, nint windowHandle, string? textBeforeCaret = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new FormattedInsertion(string.Empty, SpacingBasis.Unknown, null);
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            string? tail = null;
            var basis = SpacingBasis.Unknown;
            if (textBeforeCaret is not null)
            {
                tail = VisibleTail(textBeforeCaret);
                basis = SpacingBasis.Caret;
            }
            else if (_lastByWindow.TryGetValue(windowHandle, out var last) && now - last.At <= Memory)
            {
                tail = last.Tail;
                basis = SpacingBasis.Memory;
            }

            var result = Apply(text, tail);
            _lastByWindow[windowHandle] = (result.Length > 0 ? result[^1].ToString() : string.Empty, now);
            return new FormattedInsertion(result, basis, tail);
        }
    }

    /// <summary>
    /// The user pressed <paramref name="key"/> in <paramref name="windowHandle"/> after something was inserted there: the
    /// memory now holds what that key left before the caret (a line break after Enter, a space, a character) instead of
    /// the end of the insertion, so a message sent with Enter is not followed by a space and a word typed in between
    /// still is. A window with nothing remembered is left alone: the memory only joins dictations in controls whose caret
    /// cannot be read.
    /// </summary>
    public void Typed(nint windowHandle, TypedKey key)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (!_lastByWindow.TryGetValue(windowHandle, out var last) || now - last.At > Memory)
            {
                return;
            }

            var tail = key switch
            {
                TypedKey.LineBreak => "\n",
                TypedKey.Space => " ",
                _ => TypedCharacter,
            };
            _lastByWindow[windowHandle] = (tail, now);
        }
    }

    /// <summary>Forget what was inserted into <paramref name="windowHandle"/> (the text did not arrive there).</summary>
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
