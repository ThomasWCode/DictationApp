namespace DictationApp.Core.Dictionary;

/// <summary>
/// Pure. AssemblyAI accepts at most 100 keyterms of at most 50 characters. When the dictionary is larger,
/// starred terms win, then most-used, then most recently used, then most recently added.
/// </summary>
public static class KeytermsSelector
{
    public const int MaxTerms = 100;

    public static IReadOnlyList<string> Select(IEnumerable<DictionaryTerm> terms, int maxTerms = MaxTerms, int maxLength = DictionaryTerm.MaxLength)
    {
        return terms
            .Where(t => !string.IsNullOrWhiteSpace(t.Term))
            .Select(t => (Term: t, Text: t.Term.Trim()))
            .Where(x => x.Text.Length <= maxLength)
            .GroupBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.Term.Starred)
            .ThenByDescending(x => x.Term.UseCount)
            .ThenByDescending(x => x.Term.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.Term.AddedAt)
            .Take(maxTerms)
            .Select(x => x.Text)
            .ToArray();
    }
}
