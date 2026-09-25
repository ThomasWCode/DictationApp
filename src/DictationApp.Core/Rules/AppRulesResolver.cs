using System.Text.RegularExpressions;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;

namespace DictationApp.Core.Rules;

/// <summary>Pure. URL rule beats process rule beats defaults; within a kind, the first matching rule wins.</summary>
public static class AppRulesResolver
{
    public static ResolvedRule Resolve(
        ForegroundContext context,
        IReadOnlyList<AppRule> rules,
        Tone defaultTone,
        CleanupLevel defaultLevel,
        PasteMode defaultPaste)
    {
        AppRule? urlRule = null;
        AppRule? processRule = null;
        var host = context.UrlHost;

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (urlRule is null && host is not null && !string.IsNullOrWhiteSpace(rule.UrlHost) && HostMatches(host, rule.UrlHost))
            {
                urlRule = rule;
            }

            if (processRule is null && !string.IsNullOrWhiteSpace(rule.ProcessGlob) && GlobMatches(context.ProcessName, rule.ProcessGlob))
            {
                processRule = rule;
            }
        }

        // Layer: defaults <- process rule <- url rule. Unset fields fall through.
        var tone = urlRule?.Tone ?? processRule?.Tone ?? defaultTone;
        var level = urlRule?.Level ?? processRule?.Level ?? defaultLevel;
        var paste = urlRule?.PasteMode ?? processRule?.PasteMode ?? defaultPaste;
        var hint = urlRule?.Hint ?? processRule?.Hint;
        var matchedBy = urlRule is not null ? $"url:{urlRule.UrlHost}" : processRule is not null ? $"process:{processRule.ProcessGlob}" : "default";
        return new ResolvedRule(tone, level, paste, hint, matchedBy, urlRule ?? processRule);
    }

    /// <summary><c>mail.google.com</c> matches rule host <c>mail.google.com</c> or <c>google.com</c> (suffix on a label boundary).</summary>
    public static bool HostMatches(string host, string ruleHost)
    {
        host = host.Trim().ToLowerInvariant();
        ruleHost = ruleHost.Trim().ToLowerInvariant().TrimStart('.');
        if (host.Length == 0 || ruleHost.Length == 0)
        {
            return false;
        }

        return host == ruleHost || host.EndsWith("." + ruleHost, StringComparison.Ordinal);
    }

    public static bool GlobMatches(string processName, string glob)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        var g = glob.Trim();
        if (g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            g = g[..^4];
        }

        var pattern = "^" + Regex.Escape(g).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
}

/// <summary>
/// The rules versions 0.1 and 0.2 seeded into every new settings file. Since 0.3 the list starts empty and every
/// app follows the Style defaults; these are kept only so the settings migration can recognise and remove them.
/// </summary>
public static class LegacyAppRules
{
    /// <summary>True when <paramref name="rule"/> targets an app or host that was seeded, whatever its tone or level now.</summary>
    public static bool IsSeededTarget(AppRule rule) => Seed().Any(seed =>
        string.Equals(seed.ProcessGlob?.Trim(), rule.ProcessGlob?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(seed.UrlHost?.Trim(), rule.UrlHost?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static List<AppRule> Seed() =>
    [
        new() { ProcessGlob = "OUTLOOK", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is an email." },
        new() { ProcessGlob = "olk", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is an email." },
        new() { UrlHost = "mail.google.com", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is an email." },
        new() { UrlHost = "outlook.live.com", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is an email." },
        new() { UrlHost = "outlook.office.com", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is an email." },
        new() { ProcessGlob = "ms-teams", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { ProcessGlob = "Teams", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { ProcessGlob = "slack", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { ProcessGlob = "WhatsApp", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { UrlHost = "web.whatsapp.com", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { UrlHost = "teams.microsoft.com", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { UrlHost = "slack.com", Tone = Tone.Casual, Level = CleanupLevel.Light, Hint = "This is a chat message." },
        new() { ProcessGlob = "WINWORD", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is a document." },
        new() { UrlHost = "docs.google.com", Tone = Tone.Formal, Level = CleanupLevel.Medium, Hint = "This is a document." },
        new() { ProcessGlob = "Code", Tone = Tone.Neutral, Level = CleanupLevel.None, Hint = "This is a code editor." },
        new() { ProcessGlob = "WindowsTerminal", Tone = Tone.Neutral, Level = CleanupLevel.None, Hint = "This is a terminal." },
        new() { ProcessGlob = "powershell", Tone = Tone.Neutral, Level = CleanupLevel.None, Hint = "This is a terminal." },
        new() { ProcessGlob = "pwsh", Tone = Tone.Neutral, Level = CleanupLevel.None, Hint = "This is a terminal." },
        new() { ProcessGlob = "cmd", Tone = Tone.Neutral, Level = CleanupLevel.None, Hint = "This is a terminal." },
    ];
}
