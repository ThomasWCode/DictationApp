using System.Text.RegularExpressions;

namespace DictationApp.Core.Dictionary;

/// <summary>A word (or short run of words) the user changed after insertion.</summary>
public sealed record Correction(string From, string To);

/// <summary>
/// Pure. Word-level longest-common-subsequence diff between the text we inserted and the text after the user
/// edited it. Replaced runs become candidate dictionary terms for the "Correct last dictation" dialog.
/// The words both texts start and end with are set aside first, so only the stretch between the first and the last
/// change is compared; that stretch is refused beyond <see cref="MaxCells"/> (<see cref="IsTooLong"/>) instead of
/// allocating a table of hundreds of megabytes for a long dictation.
/// </summary>
public static partial class CorrectionDiffer
{
    /// <summary>The most word pairs compared at once: 4,000 × 4,000, about 64 MB and well under a second.</summary>
    public const long MaxCells = 16_000_000;

    /// <summary>True when the changes span too many words to compare in one go (see <see cref="MaxCells"/>).</summary>
    public static bool IsTooLong(string inserted, string edited)
    {
        var (a, b) = ChangedStretch(Tokenise(inserted), Tokenise(edited));
        return (long)(a.Length + 1) * (b.Length + 1) > MaxCells;
    }

    /// <summary>The corrections, or none when <see cref="IsTooLong"/>.</summary>
    public static IReadOnlyList<Correction> Diff(string inserted, string edited)
    {
        var (a, b) = ChangedStretch(Tokenise(inserted), Tokenise(edited));
        if ((long)(a.Length + 1) * (b.Length + 1) > MaxCells)
        {
            return [];
        }

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

    /// <summary>The words between the common start and the common end of both texts.</summary>
    private static (string[] A, string[] B) ChangedStretch(string[] a, string[] b)
    {
        var start = 0;
        while (start < a.Length && start < b.Length && string.Equals(a[start], b[start], StringComparison.Ordinal))
        {
            start++;
        }

        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && string.Equals(a[endA - 1], b[endB - 1], StringComparison.Ordinal))
        {
            endA--;
            endB--;
        }

        return (a[start..endA], b[start..endB]);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{P}]+(?=\s|$)|(?<=^|\s)[\p{P}]+")]
    private static partial Regex TrailingPunctuation();
}
