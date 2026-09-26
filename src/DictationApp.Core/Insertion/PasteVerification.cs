namespace DictationApp.Core.Insertion;

/// <summary>Whether a paste can be confirmed from the characters the target reports before its caret afterwards.</summary>
public static class PasteVerification
{
    /// <summary>
    /// True when <paramref name="textBeforeCaret"/>, read after pasting <paramref name="pasted"/>, ends with the pasted
    /// text's last visible characters. Whitespace and invisible stand-ins (a rich editor's paragraph object, zero-width
    /// characters) are ignored, since editors turn line breaks into paragraphs. Null (the control does not say, or focus
    /// moved) is never a confirmation.
    /// </summary>
    public static bool LooksPasted(string pasted, string? textBeforeCaret)
    {
        if (textBeforeCaret is null)
        {
            return false;
        }

        var after = Visible(textBeforeCaret);
        var tail = Visible(pasted);
        if (after.Length == 0 || tail.Length == 0)
        {
            return false;
        }

        var n = Math.Min(3, Math.Min(after.Length, tail.Length));
        return after.EndsWith(tail[^n..], StringComparison.Ordinal);
    }

    private static string Visible(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && c is not ('￼' or '​' or '‌' or '‍' or '⁠' or '﻿')).ToArray());
}
