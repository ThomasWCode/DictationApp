using System.Text.Json;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.Rules;

namespace DictationApp.Core.Settings;

/// <summary>
/// Remembers what the Settings window loaded, so that Save writes only what the user changed there into the latest
/// settings instead of the window's whole copy. Other writers change settings while the window is open: "Correct last
/// dictation" adds dictionary terms, a remembered tone or level updates the defaults or an app rule, and every
/// dictation updates term use counts. Writing the window's snapshot back undid all of them.
/// <para>
/// Tracked: the default tone and level, the hotkey (written only when changed), dictionary terms (added, removed,
/// renamed or starred here, matched case-insensitively; use counts always come from the latest settings) and the app
/// rules (replaced as a whole list, and only when edited here). Every other setting is a plain value the window owns.
/// </para>
/// </summary>
public sealed class SettingsEditTracker
{
    private readonly Dictionary<DictionaryTerm, (string Term, bool Starred)> _terms = new(ReferenceEqualityComparer.Instance);
    private string _rules = "[]";

    public Tone Tone { get; private set; }

    public CleanupLevel Level { get; private set; }

    public string Hotkey { get; private set; } = string.Empty;

    /// <summary>Starts tracking. <paramref name="shownTerms"/> are the window's own copies of the loaded terms.</summary>
    public void Reset(AppSettings loaded, IEnumerable<DictionaryTerm> shownTerms, IEnumerable<AppRule> shownRules)
    {
        Tone = loaded.DefaultTone;
        Level = loaded.DefaultCleanupLevel;
        Hotkey = loaded.Hotkey;
        _terms.Clear();
        foreach (var term in shownTerms)
        {
            _terms[term] = (term.Term, term.Starred);
        }

        _rules = Snapshot(shownRules);
    }

    /// <summary>A term added elsewhere while the window is open, now shown in it: part of the baseline, not an edit.</summary>
    public void Track(DictionaryTerm shownTerm) => _terms[shownTerm] = (shownTerm.Term, shownTerm.Starred);

    /// <summary>The defaults changed elsewhere and the window now shows the new values: they are the baseline.</summary>
    public void TrackStyle(Tone tone, CleanupLevel level)
    {
        Tone = tone;
        Level = level;
    }

    /// <summary>True when the user removed <paramref name="term"/> in this window (so a refresh must not bring it back).</summary>
    public bool RemovedHere(string term, IEnumerable<DictionaryTerm> shownTerms)
    {
        var shown = new HashSet<DictionaryTerm>(shownTerms, ReferenceEqualityComparer.Instance);
        return _terms.Any(kv => !shown.Contains(kv.Key) && SameTerm(kv.Value.Term, term));
    }

    public SettingsEdits Diff(Tone tone, CleanupLevel level, string hotkey, IReadOnlyCollection<DictionaryTerm> shownTerms, IReadOnlyCollection<AppRule> shownRules)
    {
        var shown = new HashSet<DictionaryTerm>(shownTerms, ReferenceEqualityComparer.Instance);
        var removed = _terms.Where(kv => !shown.Contains(kv.Key)).Select(kv => kv.Value.Term).ToList();
        var edited = new List<TermEdit>();
        var added = new List<DictionaryTerm>();
        foreach (var term in shownTerms)
        {
            var known = _terms.TryGetValue(term, out var original);
            if (string.IsNullOrWhiteSpace(term.Term))
            {
                if (known)
                {
                    removed.Add(original.Term); // blanked in the grid
                }

                continue;
            }

            if (!known)
            {
                var copy = term.Clone();
                copy.Term = copy.Term.Trim();
                added.Add(copy);
            }
            else if (!string.Equals(original.Term, term.Term, StringComparison.Ordinal) || original.Starred != term.Starred)
            {
                edited.Add(new TermEdit(original.Term, term.Term.Trim(), term.Starred));
            }
        }

        var rules = Snapshot(shownRules) == _rules
            ? null
            : shownRules.Where(r => !string.IsNullOrWhiteSpace(r.ProcessGlob) || !string.IsNullOrWhiteSpace(r.UrlHost)).Select(r => r.Clone()).ToList();
        return new SettingsEdits(
            tone == Tone ? null : tone,
            level == Level ? null : level,
            string.Equals(hotkey, Hotkey, StringComparison.Ordinal) ? null : hotkey,
            removed,
            added,
            edited,
            rules);
    }

    internal static bool SameTerm(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Snapshot(IEnumerable<AppRule> rules) =>
        JsonSerializer.Serialize(rules.Select(r => new { r.ProcessGlob, r.UrlHost, r.Tone, r.Level, r.PasteMode, r.Hint, r.Enabled }));
}

/// <summary>A dictionary term renamed or (un)starred in the Settings window, identified by its text when loaded.</summary>
public sealed record TermEdit(string OriginalTerm, string Term, bool Starred);

/// <summary>What the Settings window changed; <see cref="ApplyTo"/> merges it into the latest settings.</summary>
public sealed record SettingsEdits(
    Tone? Tone,
    CleanupLevel? Level,
    string? Hotkey,
    IReadOnlyList<string> RemovedTerms,
    IReadOnlyList<DictionaryTerm> AddedTerms,
    IReadOnlyList<TermEdit> EditedTerms,
    IReadOnlyList<AppRule>? Rules)
{
    public void ApplyTo(AppSettings settings)
    {
        if (Tone is { } tone)
        {
            settings.DefaultTone = tone;
        }

        if (Level is { } level)
        {
            settings.DefaultCleanupLevel = level;
        }

        if (Hotkey is { } hotkey)
        {
            settings.Hotkey = hotkey;
        }

        foreach (var term in RemovedTerms)
        {
            settings.Dictionary.RemoveAll(t => SettingsEditTracker.SameTerm(t.Term, term));
        }

        foreach (var edit in EditedTerms)
        {
            // A term removed elsewhere meanwhile stays removed.
            if (settings.Dictionary.FirstOrDefault(t => SettingsEditTracker.SameTerm(t.Term, edit.OriginalTerm)) is { } existing)
            {
                existing.Term = edit.Term;
                existing.Starred = edit.Starred;
            }
        }

        foreach (var term in AddedTerms)
        {
            // The same term may have been added elsewhere meanwhile (say by "Correct last dictation"): keep one.
            if (settings.Dictionary.FirstOrDefault(t => SettingsEditTracker.SameTerm(t.Term, term.Term)) is { } existing)
            {
                existing.Starred |= term.Starred;
            }
            else
            {
                settings.Dictionary.Add(term.Clone());
            }
        }

        // A rename can collide with another term: keep one entry, starred if either was, with the higher use count.
        settings.Dictionary = settings.Dictionary
            .GroupBy(t => t.Term.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Aggregate((a, b) =>
            {
                a.Starred |= b.Starred;
                a.UseCount = Math.Max(a.UseCount, b.UseCount);
                a.LastUsedAt = b.LastUsedAt is { } later && (a.LastUsedAt is null || later > a.LastUsedAt) ? later : a.LastUsedAt;
                return a;
            }))
            .ToList();

        if (Rules is not null)
        {
            settings.AppRules = Rules.Select(r => r.Clone()).ToList();
        }
    }
}
