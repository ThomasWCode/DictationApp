using DictationApp.Core.Cleanup;
using DictationApp.Core.Dictionary;
using DictationApp.Core.Rules;
using DictationApp.Core.Settings;

namespace DictationApp.Core.Tests;

/// <summary>
/// The Settings window merges its own changes into the latest settings on Save, so changes made elsewhere while it was
/// open (a correction, a remembered style change, use counts) survive.
/// </summary>
public class SettingsEditTrackerTests
{
    private sealed class Window
    {
        public Window(AppSettings loaded)
        {
            Tone = loaded.DefaultTone;
            Level = loaded.DefaultCleanupLevel;
            Hotkey = loaded.Hotkey;
            Terms = loaded.Dictionary.Select(t => t.Clone()).ToList();
            Rules = loaded.AppRules.Select(r => r.Clone()).ToList();
            Tracker.Reset(loaded, Terms, Rules);
        }

        public SettingsEditTracker Tracker { get; } = new();

        public Tone Tone { get; set; }

        public CleanupLevel Level { get; set; }

        public string Hotkey { get; set; }

        public List<DictionaryTerm> Terms { get; }

        public List<AppRule> Rules { get; }

        public void SaveInto(AppSettings latest) => Tracker.Diff(Tone, Level, Hotkey, Terms, Rules).ApplyTo(latest);
    }

    private static AppSettings Settings() => new()
    {
        DefaultTone = Tone.Neutral,
        DefaultCleanupLevel = CleanupLevel.Light,
        Dictionary = [new DictionaryTerm { Term = "LSHTM", UseCount = 3 }],
        AppRules = [new AppRule { ProcessGlob = "OUTLOOK", Tone = Tone.Formal, Level = CleanupLevel.Medium }],
    };

    [Fact]
    public void A_correction_made_while_settings_are_open_survives_an_unrelated_save()
    {
        var latest = Settings();
        var window = new Window(latest);
        latest.Dictionary.Add(new DictionaryTerm { Term = "Tuberculosis", Starred = true }); // Correct last dictation
        window.Rules[0].Hint = null; // untouched
        window.SaveInto(latest);

        Assert.Contains(latest.Dictionary, t => t.Term == "Tuberculosis" && t.Starred);
        Assert.Contains(latest.Dictionary, t => t.Term == "LSHTM");
    }

    [Fact]
    public void A_remembered_style_change_and_use_counts_survive_but_an_explicit_style_change_wins()
    {
        var latest = Settings();
        var window = new Window(latest);
        latest.DefaultTone = Tone.Casual;                 // remembered from a dictation
        latest.Dictionary[0].UseCount = 9;                // a dictation used the term
        latest.AppRules[0].Tone = Tone.Neutral;           // remembered against the rule
        window.SaveInto(latest);

        Assert.Equal(Tone.Casual, latest.DefaultTone);
        Assert.Equal(9, latest.Dictionary[0].UseCount);
        Assert.Equal(Tone.Neutral, latest.AppRules[0].Tone);

        var again = new Window(latest);
        again.Level = CleanupLevel.High;                  // changed on the Style tab
        latest.DefaultCleanupLevel = CleanupLevel.None;   // and elsewhere meanwhile
        again.SaveInto(latest);
        Assert.Equal(CleanupLevel.High, latest.DefaultCleanupLevel);
    }

    [Fact]
    public void Terms_removed_starred_renamed_or_added_here_are_merged_by_name()
    {
        var latest = Settings();
        latest.Dictionary.Add(new DictionaryTerm { Term = "Plasmodium" });
        latest.Dictionary.Add(new DictionaryTerm { Term = "Colour" });
        var window = new Window(latest);
        window.Terms.RemoveAll(t => t.Term == "Plasmodium");
        window.Terms.Single(t => t.Term == "LSHTM").Starred = true;
        window.Terms.Single(t => t.Term == "Colour").Term = "Color";
        window.Terms.Add(new DictionaryTerm { Term = "Anopheles", Starred = true });
        latest.Dictionary.Add(new DictionaryTerm { Term = "anopheles", UseCount = 2 }); // the same term, added elsewhere
        window.SaveInto(latest);

        Assert.DoesNotContain(latest.Dictionary, t => t.Term == "Plasmodium");
        Assert.True(latest.Dictionary.Single(t => t.Term == "LSHTM").Starred);
        Assert.Equal(3, latest.Dictionary.Single(t => t.Term == "LSHTM").UseCount);
        Assert.Contains(latest.Dictionary, t => t.Term == "Color");
        var anopheles = Assert.Single(latest.Dictionary, t => string.Equals(t.Term, "Anopheles", StringComparison.OrdinalIgnoreCase));
        Assert.True(anopheles.Starred);
        Assert.Equal(2, anopheles.UseCount);
    }

    [Fact]
    public void App_rules_are_replaced_only_when_edited_here()
    {
        var latest = Settings();
        var window = new Window(latest);
        window.Rules[0].PasteMode = Abstractions.PasteMode.CtrlShiftV;
        latest.AppRules[0].Tone = Tone.Casual; // remembered meanwhile; the edited list wins as a whole
        window.SaveInto(latest);

        Assert.Equal(Abstractions.PasteMode.CtrlShiftV, latest.AppRules[0].PasteMode);
        Assert.Equal(Tone.Formal, latest.AppRules[0].Tone);
    }

    [Fact]
    public void A_term_removed_here_is_not_brought_back_by_a_refresh()
    {
        var latest = Settings();
        var window = new Window(latest);
        window.Terms.Clear();

        Assert.True(window.Tracker.RemovedHere("lshtm", window.Terms));
        Assert.False(window.Tracker.RemovedHere("Tuberculosis", window.Terms));
    }
}
