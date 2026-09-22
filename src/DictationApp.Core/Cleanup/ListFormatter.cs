using System.Text;
using System.Text.RegularExpressions;

namespace DictationApp.Core.Cleanup;

/// <summary>
/// Pure. Turns spoken enumerations into a numbered list, one item per line. Recognised markers are
/// "1." / "1)" / "1:" at a word boundary and the phrases "number one", "point one", "item one" (words or
/// digits). A list needs at least two markers in ascending order starting at 1, so "version 1.2" and a lone
/// "number one priority" are left alone. Trailing "and" / "then" / "," before the next item is dropped.
/// </summary>
public static partial class ListFormatter
{
    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
    };

    public static string Format(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var markers = Marker().Matches(text)
            .Select(m => (m.Index, m.Length, Value: ValueOf(m)))
            .Where(m => m.Value > 0)
            .ToList();
        if (markers.Count < 2)
        {
            return text;
        }

        // Collect ascending runs 1, 2, 3, ... in text order; a run of at least two markers is a list.
        var runs = new List<List<(int Index, int Length, int Value)>>();
        List<(int Index, int Length, int Value)>? current = null;
        foreach (var marker in markers)
        {
            if (marker.Value == 1)
            {
                current = [marker];
                runs.Add(current);
            }
            else if (current is not null && marker.Value == current[^1].Value + 1)
            {
                current.Add(marker);
            }
            else
            {
                current = null;
            }
        }

        var listMarkers = runs.Where(r => r.Count >= 2).SelectMany(r => r).OrderBy(m => m.Index).ToList();
        if (listMarkers.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + listMarkers.Count * 4);
        var pos = 0;
        foreach (var marker in listMarkers)
        {
            var before = text[pos..marker.Index];
            before = TrailingJoiner().Replace(before, string.Empty);
            sb.Append(before);
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append('\n');
            }

            sb.Append(marker.Value).Append(". ");
            pos = marker.Index + marker.Length;
            // Skip whitespace and a stray punctuation mark right after the marker ("number one, buy milk").
            while (pos < text.Length && (char.IsWhiteSpace(text[pos]) || text[pos] is ',' or ':'))
            {
                pos++;
            }
        }

        sb.Append(text[pos..]);
        return sb.ToString().Trim();
    }

    private static int ValueOf(Match m)
    {
        if (m.Groups["d"].Success)
        {
            return int.Parse(m.Groups["d"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var w = m.Groups["w"].Value;
        if (int.TryParse(w, out var n))
        {
            return n;
        }

        return Words.TryGetValue(w, out var v) ? v : 0;
    }

    [GeneratedRegex(@"(?<![\w.,])(?:(?<d>\d{1,2})[.):](?=\s)|\b(?:number|point|item)\s+(?<w>one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|\d{1,2})\b(?![.,]?\d))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"[\s,]*\b(?:and then|and|then)\b[\s,]*$|[\s,]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingJoiner();
}
