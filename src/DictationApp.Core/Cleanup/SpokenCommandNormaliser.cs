using System.Text;
using System.Text.RegularExpressions;

namespace DictationApp.Core.Cleanup;

/// <summary>
/// Pure regex pass that applies spoken formatting commands. Runs alone when cleanup is None; otherwise the
/// same commands are described to the LLM. Commands are matched as whole words, case-insensitively.
/// </summary>
public static partial class SpokenCommandNormaliser
{
    public static string Normalise(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var s = text.Trim();
        s = ScratchThat().Replace(s, static m => RemovePrecedingClause(m));
        s = NewParagraph().Replace(s, "\n\n");
        s = NewLine().Replace(s, "\n");
        s = BulletPoint().Replace(s, "\n- ");
        s = Punctuation().Replace(s, static m => PunctuationFor(m.Groups[1].Value));
        s = CollapseSpacesBeforePunctuation().Replace(s, "$1");
        s = SpaceAfterNewline().Replace(s, "\n");
        s = MultiSpace().Replace(s, " ");
        s = CapitaliseAfterSentenceEnd(s);
        return s.Trim();
    }

    private static string RemovePrecedingClause(Match m)
    {
        // "scratch that" is handled in a second pass because we need the surrounding text.
        return "SCRATCH";
    }

    private static string PunctuationFor(string command) => command.ToLowerInvariant() switch
    {
        "period" or "full stop" => ".",
        "comma" => ",",
        "question mark" => "?",
        "exclamation mark" or "exclamation point" => "!",
        "colon" => ":",
        "semicolon" or "semi colon" => ";",
        "open paren" or "open parenthesis" or "open bracket" => " (",
        "close paren" or "close parenthesis" or "close bracket" => ")",
        "hyphen" or "dash" => "-",
        _ => command,
    };

    private static string CapitaliseAfterSentenceEnd(string s)
    {
        // Resolve scratch markers: drop text back to the previous sentence boundary or line start.
        const string marker = "SCRATCH";
        while (s.Contains(marker, StringComparison.Ordinal))
        {
            var idx = s.IndexOf(marker, StringComparison.Ordinal);
            var before = s[..idx];
            var after = s[(idx + marker.Length)..];
            var cut = Math.Max(Math.Max(before.LastIndexOf('.'), before.LastIndexOf('?')), Math.Max(before.LastIndexOf('!'), before.LastIndexOf('\n')));
            before = cut >= 0 ? before[..(cut + 1)] : string.Empty;
            s = before.TrimEnd() + (after.Length > 0 ? " " + after.TrimStart() : string.Empty);
        }

        var sb = new StringBuilder(s.Length);
        var capitalise = true;
        foreach (var ch in s)
        {
            if (capitalise && char.IsLetter(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
                capitalise = false;
            }
            else
            {
                sb.Append(ch);
                if (ch is '.' or '?' or '!' or '\n')
                {
                    capitalise = true;
                }
                else if (!char.IsWhiteSpace(ch) && ch != '-' && ch != '"')
                {
                    capitalise = false;
                }
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\bscratch that\b[.,]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScratchThat();

    [GeneratedRegex(@"\s*[.,]?\s*\bnew paragraph\b[.,]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewParagraph();

    [GeneratedRegex(@"\s*[.,]?\s*\b(?:new line|newline)\b[.,]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewLine();

    [GeneratedRegex(@"\s*[.,]?\s*\bbullet point\b[.,]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BulletPoint();

    [GeneratedRegex(@"\s*\b(period|full stop|comma|question mark|exclamation mark|exclamation point|colon|semicolon|semi colon|open paren|open parenthesis|open bracket|close paren|close parenthesis|close bracket)\b[.,]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+([.,?!:;)])")]
    private static partial Regex CollapseSpacesBeforePunctuation();

    [GeneratedRegex(@"\n[ \t]+")]
    private static partial Regex SpaceAfterNewline();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();
}
