using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;

namespace DictationApp.Core.Rules;

/// <summary>
/// Per-application defaults. Either <see cref="ProcessGlob"/> (matched against the process name without
/// extension, case-insensitive, <c>*</c> and <c>?</c> wildcards) or <see cref="UrlHost"/> (matched against
/// the browser tab host, suffix match so <c>gmail.com</c> matches <c>mail.google.com</c> only when written
/// as <c>google.com</c>) must be set.
/// </summary>
public sealed class AppRule
{
    public string? ProcessGlob { get; set; }

    public string? UrlHost { get; set; }

    public Tone? Tone { get; set; }

    public CleanupLevel? Level { get; set; }

    public PasteMode? PasteMode { get; set; }

    /// <summary>Free-text hint passed to the LLM, e.g. "This is a chat message."</summary>
    public string? Hint { get; set; }

    public bool Enabled { get; set; } = true;

    public string DisplayTarget => !string.IsNullOrWhiteSpace(UrlHost) ? UrlHost! : ProcessGlob ?? string.Empty;

    public AppRule Clone() => (AppRule)MemberwiseClone();
}

/// <param name="MatchedRule">The rule that decided tone/level (url rule, else process rule), or null for defaults.</param>
public sealed record ResolvedRule(Tone Tone, CleanupLevel Level, PasteMode PasteMode, string? Hint, string MatchedBy, AppRule? MatchedRule = null);
