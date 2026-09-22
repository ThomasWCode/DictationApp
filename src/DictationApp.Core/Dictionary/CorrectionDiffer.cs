using System.Text.RegularExpressions;

namespace DictationApp.Core.Dictionary;

/// <summary>A word (or short run of words) the user changed after insertion.</summary>
public sealed record Correction(string From, string To);

/// <summary>
/// Pure. Word-level longest-common-subsequence diff between the text we inserted and the text after the user
/// edited it. Replaced runs become candidate dictionary terms for the "Correct last dictation" dialog.
/// </summary>
public static partial class CorrectionDiffer
{
    public static IReadOnlyList<Correction> Diff(string inserted, string edited)
    {
        var a = Tokenise(inserted);
        var b = Tokenise(edited);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<Correction>();
        var removed = new List<string>();
        var added = new List<string>();
        int x = 0, y = 0;

        void Flush()
        {
            if (removed.Count > 0 && added.Count > 0)
            {
                result.Add(new Correction(string.Join(' ', removed), string.Join(' ', added)));
            }

            removed.Clear();
            added.Clear();
        }

        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                Flush();
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                removed.Add(a[x++]);
            }
            else
            {
                added.Add(b[y++]);
            }
        }

        while (x < a.Length)
        {
            removed.Add(a[x++]);
        }

        while (y < b.Length)
        {
            added.Add(b[y++]);
        }

        Flush();
        return result.Where(IsInteresting).ToArray();
    }

    /// <summary>Candidate dictionary terms: the replacement side of each correction, without punctuation noise.</summary>
    public static IReadOnlyList<string> SuggestTerms(IEnumerable<Correction> corrections) =>
        corrections
            .Select(c => StripPunctuation(c.To))
            .Where(t => t.Length is >= 2 and <= DictionaryTerm.MaxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsInteresting(Correction c)
    {
        // Ignore pure punctuation/case-only edits; those are not dictionary material.
        var from = StripPunctuation(c.From);
        var to = StripPunctuation(c.To);
        return to.Length > 0 && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPunctuation(string s) => TrailingPunctuation().Replace(s, string.Empty).Trim();

    private static string[] Tokenise(string s) => string.IsNullOrWhiteSpace(s)
        ? []
        : Whitespace().Split(s.Trim());

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{P}]+(?=\s|$)|(?<=^|\s)[\p{P}]+")]
    private static partial Regex TrailingPunctuation();
}
