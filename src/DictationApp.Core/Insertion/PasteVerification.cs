namespace DictationApp.Core.Insertion;

/// <summary>Whether a paste can be confirmed from the characters the target reports before its caret, before and after it.</summary>
public static class PasteVerification
{
    /// <summary>
    /// True when the text before the caret changed from <paramref name="before"/> (read just before the paste) to
    /// <paramref name="after"/> (read once the paste settled) and now ends with <paramref name="pasted"/>'s last visible
    /// characters. Whitespace and invisible stand-ins (a rich editor's paragraph object, zero-width characters) are
    /// ignored, since editors turn line breaks into paragraphs. An unchanged reading is no confirmation however it ends:
    /// a target that ignored the paste may already end with the same characters. Nor is a null reading, before or after
    /// (the control does not say, or focus moved).
    /// </summary>
    public static bool LooksPasted(string pasted, string? before, string? after)
    {
        if (before is null || after is null)
        {
            return false;
        }

        var was = Visible(before);
        var now = Visible(after);
        var tail = Visible(pasted);
        if (now.Length == 0 || tail.Length == 0 || now == was)
        {
            return false;
        }

        var n = Math.Min(3, Math.Min(now.Length, tail.Length));
        return now.EndsWith(tail[^n..], StringComparison.Ordinal);
    }

    private static string Visible(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && c is not ('￼' or '​' or '‌' or '‍' or '⁠' or '﻿')).ToArray());
}
