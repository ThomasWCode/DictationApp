using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.Rules;
using DictationApp.Core.Settings;

namespace DictationApp.Core.Tests;

public class AppRulesResolverTests
{
    private static ForegroundContext Ctx(string process, string? url = null) =>
        new(1, 1, process, "title", url, true, false, "test");

    [Fact]
    public void New_settings_have_no_app_rules_so_every_app_uses_the_defaults()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.AppRules);
        var resolved = AppRulesResolver.Resolve(Ctx("OUTLOOK"), settings.AppRules, Tone.Casual, CleanupLevel.High, PasteMode.CtrlV);
        Assert.Equal(Tone.Casual, resolved.Tone);
        Assert.Equal(CleanupLevel.High, resolved.Level);
        Assert.Equal("default", resolved.MatchedBy);
    }

    [Fact]
    public void Legacy_seeded_targets_are_recognised_whatever_their_style()
    {
        Assert.True(LegacyAppRules.IsSeededTarget(new AppRule { ProcessGlob = "outlook", Tone = Tone.Casual }));
        Assert.True(LegacyAppRules.IsSeededTarget(new AppRule { UrlHost = "mail.google.com" }));
        Assert.False(LegacyAppRules.IsSeededTarget(new AppRule { ProcessGlob = "notepad" }));
        Assert.False(LegacyAppRules.IsSeededTarget(new AppRule { UrlHost = "example.com" }));
    }

    [Fact]
    public void Only_unmodified_seeds_are_treated_as_seeds()
    {
        var seed = LegacyAppRules.Seed().First(r => r.ProcessGlob == "OUTLOOK");
        Assert.True(LegacyAppRules.IsUnmodifiedSeed(seed));
        Assert.True(LegacyAppRules.IsUnmodifiedSeed(new AppRule { ProcessGlob = "outlook", Tone = Tone.Casual, Level = CleanupLevel.High, Hint = seed.Hint })); // remembered style
        Assert.False(LegacyAppRules.IsUnmodifiedSeed(new AppRule { ProcessGlob = "OUTLOOK", Tone = seed.Tone, Level = seed.Level, Hint = seed.Hint, PasteMode = PasteMode.CtrlShiftV }));
        Assert.False(LegacyAppRules.IsUnmodifiedSeed(new AppRule { ProcessGlob = "OUTLOOK", Tone = seed.Tone, Level = seed.Level, Hint = "Formal emails to my team." }));
        Assert.False(LegacyAppRules.IsUnmodifiedSeed(new AppRule { ProcessGlob = "OUTLOOK", Tone = seed.Tone, Level = seed.Level, Hint = seed.Hint, Enabled = false }));
        Assert.False(LegacyAppRules.IsUnmodifiedSeed(new AppRule { ProcessGlob = "notepad" }));
    }

    [Fact]
    public void Legacy_seed_rules_resolve_as_before()
    {
        var rules = LegacyAppRules.Seed();
        Assert.Equal(Tone.Formal, AppRulesResolver.Resolve(Ctx("OUTLOOK"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).Tone);
        Assert.Equal(Tone.Casual, AppRulesResolver.Resolve(Ctx("ms-teams"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).Tone);
        Assert.Equal(Tone.Formal, AppRulesResolver.Resolve(Ctx("chrome", "https://mail.google.com/mail/u/0/#inbox"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).Tone);
        Assert.Equal(CleanupLevel.None, AppRulesResolver.Resolve(Ctx("Code"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).Level);
        Assert.Equal("default", AppRulesResolver.Resolve(Ctx("notepad"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).MatchedBy);
    }

    [Fact]
    public void Url_rule_beats_process_rule_beats_default()
    {
        var rules = new List<AppRule>
        {
            new() { ProcessGlob = "chrome", Tone = Tone.Casual, Level = CleanupLevel.High },
            new() { UrlHost = "docs.google.com", Tone = Tone.Formal },
        };
        var resolved = AppRulesResolver.Resolve(Ctx("chrome", "https://docs.google.com/document/d/1"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlShiftV);

        Assert.Equal(Tone.Formal, resolved.Tone);          // from url rule
        Assert.Equal(CleanupLevel.High, resolved.Level);   // url rule unset -> process rule
        Assert.Equal(PasteMode.CtrlShiftV, resolved.PasteMode); // neither -> default
        Assert.Equal("url:docs.google.com", resolved.MatchedBy);
    }

    [Fact]
    public void Disabled_rules_are_skipped_and_first_match_wins()
    {
        var rules = new List<AppRule>
        {
            new() { ProcessGlob = "slack", Tone = Tone.Formal, Enabled = false },
            new() { ProcessGlob = "sl*", Tone = Tone.Casual },
            new() { ProcessGlob = "slack", Tone = Tone.Formal },
        };
        Assert.Equal(Tone.Casual, AppRulesResolver.Resolve(Ctx("slack"), rules, Tone.Neutral, CleanupLevel.Light, PasteMode.CtrlV).Tone);
    }

    [Theory]
    [InlineData("mail.google.com", "google.com", true)]
    [InlineData("mail.google.com", "mail.google.com", true)]
    [InlineData("notgoogle.com", "google.com", false)]
    [InlineData("google.com", "mail.google.com", false)]
    [InlineData("MAIL.GOOGLE.COM", ".google.com", true)]
    public void Host_matching_is_suffix_on_label_boundary(string host, string rule, bool expected)
    {
        Assert.Equal(expected, AppRulesResolver.HostMatches(host, rule));
    }

    [Theory]
    [InlineData("WINWORD", "winword", true)]
    [InlineData("WINWORD.exe", "WINWORD", true)]
    [InlineData("msedge", "ms*", true)]
    [InlineData("Code", "Cod?", true)]
    [InlineData("Codex", "Code", false)]
    [InlineData("", "Code", false)]
    public void Glob_matching(string process, string glob, bool expected)
    {
        Assert.Equal(expected, AppRulesResolver.GlobMatches(process, glob));
    }

    [Fact]
    public void Url_host_is_extracted_from_context()
    {
        Assert.Equal("mail.google.com", Ctx("chrome", "https://mail.google.com/mail").UrlHost);
        Assert.Equal("teams.microsoft.com", Ctx("chrome", "teams.microsoft.com/chat").UrlHost);
        Assert.Null(Ctx("chrome", "not a url at all").UrlHost);
        Assert.Null(Ctx("chrome").UrlHost);
    }
}

public class KeytermsSelectorTests
{
    [Fact]
    public void Orders_starred_then_usage_then_recency_and_caps()
    {
        var now = DateTimeOffset.UtcNow;
        var terms = new List<DictionaryTerm>();
        for (var i = 0; i < 150; i++)
        {
            terms.Add(new DictionaryTerm { Term = "term" + i, UseCount = i, AddedAt = now.AddDays(-i) });
        }

        terms.Add(new DictionaryTerm { Term = "starred", Starred = true, UseCount = 0, AddedAt = now.AddYears(-1) });
        terms.Add(new DictionaryTerm { Term = new string('x', 51), Starred = true });
        terms.Add(new DictionaryTerm { Term = "  " });
        terms.Add(new DictionaryTerm { Term = "TERM149" });

        var selected = KeytermsSelector.Select(terms);
        Assert.Equal(100, selected.Count);
        Assert.Equal("starred", selected[0]);
        Assert.Equal("term149", selected[1]);
        Assert.DoesNotContain(selected, t => t.Length > 50);
        Assert.Equal(selected.Count, selected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Recency_breaks_ties()
    {
        var now = DateTimeOffset.UtcNow;
        var terms = new[]
        {
            new DictionaryTerm { Term = "old", UseCount = 1, LastUsedAt = now.AddDays(-2) },
            new DictionaryTerm { Term = "new", UseCount = 1, LastUsedAt = now },
        };
        Assert.Equal(["new", "old"], KeytermsSelector.Select(terms));
    }
}

public class CorrectionDifferTests
{
    [Fact]
    public void Finds_replaced_words()
    {
        var diff = CorrectionDiffer.Diff("please start the ice on ice id today", "please start the isoniazid today");
        Assert.Single(diff);
        Assert.Equal("ice on ice id", diff[0].From);
        Assert.Equal("isoniazid", diff[0].To);
    }

    [Fact]
    public void Ignores_case_and_punctuation_only_changes()
    {
        Assert.Empty(CorrectionDiffer.Diff("hello world", "Hello, world."));
    }

    [Fact]
    public void Pure_insertions_and_deletions_are_not_corrections()
    {
        Assert.Empty(CorrectionDiffer.Diff("a b c", "a c"));
        Assert.Empty(CorrectionDiffer.Diff("a c", "a b c"));
    }

    [Fact]
    public void Suggest_terms_strips_punctuation_and_dedupes()
    {
        var terms = CorrectionDiffer.SuggestTerms(
        [
            new Correction("lsh tm", "LSHTM,"),
            new Correction("lsh tm", "LSHTM"),
            new Correction("x", "a"),
        ]);
        Assert.Equal(["LSHTM"], terms);
    }

    [Fact]
    public void Multiple_corrections_in_one_sentence()
    {
        var diff = CorrectionDiffer.Diff("the rifle pentane dose and eye so nice id", "the rifapentine dose and isoniazid");
        Assert.Equal(2, diff.Count);
        Assert.Equal("rifapentine", diff[0].To);
        Assert.Equal("isoniazid", diff[1].To);
    }
}
