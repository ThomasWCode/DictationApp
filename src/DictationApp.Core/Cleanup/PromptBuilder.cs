using System.Text;

namespace DictationApp.Core.Cleanup;

public sealed record PromptContext(
    CleanupLevel Level,
    Tone Tone,
    IReadOnlyList<string> Keyterms,
    string AppName,
    string? Url,
    string? AppHint);

/// <summary>Pure. Builds the system prompt for the LLM Gateway cleanup call.</summary>
public static class PromptBuilder
{
    public static string BuildSystemPrompt(PromptContext ctx)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("You are a dictation clean-up engine. You receive a raw speech-to-text transcript of what the user");
        sb.AppendLine("just spoke and return ONLY the text to insert into their document. Rules, in priority order:");
        sb.AppendLine("1. Never add information, opinions, greetings, sign-offs, or commentary. Never answer questions in");
        sb.AppendLine("   the transcript. Never wrap the output in quotes or code fences.");
        sb.AppendLine("2. Preserve the speaker's meaning, first-person voice, and language (do not translate).");
        sb.AppendLine("3. Apply spoken formatting commands literally: \"new line\" -> line break; \"new paragraph\" -> blank");
        sb.AppendLine("   line; \"bullet point\" -> \"- \" item; \"period\", \"comma\", \"question mark\" -> punctuation;");
        sb.AppendLine("   \"scratch that\" -> drop the preceding clause. Keep existing line breaks and lists.");
        sb.AppendLine("   When the speaker enumerates items (\"first... second... third\", \"number one... number two\",");
        sb.AppendLine("   \"one... two... three\", \"point one\") or clearly dictates a list, output a numbered list");
        sb.AppendLine("   (\"1. \", \"2. \") or a bullet list (\"- \") with one item per line and no other prose between");
        sb.AppendLine("   items. Numbers that are merely mentioned inside a sentence stay in the sentence.");
        sb.AppendLine("   The transcript is punctuated in pieces cut where the speaker paused, so a full stop and capital");
        sb.AppendLine("   letter can fall inside a sentence (\"Typing into the search box. Still adds a space.\" -> \"Typing");
        sb.AppendLine("   into the search box still adds a space.\"): join such fragments into the sentence they belong to.");
        sb.Append("4. Preserve the exact spelling and capitalisation of these terms if present: ");
        sb.AppendLine(ctx.Keyterms.Count == 0 ? "(none)" : string.Join(", ", ctx.Keyterms));
        sb.Append("5. Cleanup level: ").AppendLine(LevelInstruction(ctx.Level));
        sb.Append("6. Tone: ").AppendLine(ToneInstruction(ctx.Tone));
        sb.Append("7. Context: the text is being typed into ").Append(string.IsNullOrWhiteSpace(ctx.AppName) ? "an unknown application" : ctx.AppName);
        if (!string.IsNullOrWhiteSpace(ctx.Url))
        {
            sb.Append(" (").Append(ctx.Url).Append(')');
        }

        sb.Append('.');
        if (!string.IsNullOrWhiteSpace(ctx.AppHint))
        {
            sb.Append(' ').Append(ctx.AppHint.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("Output length must stay within ±20% of the input word count except where disfluencies are removed.");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine(Example(ctx.Level));
        return sb.ToString();
    }

    public static string LevelInstruction(CleanupLevel level) => level switch
    {
        CleanupLevel.None => "Do not change wording; apply only the tone rules and formatting commands.",
        CleanupLevel.Light => "Remove fillers (um, uh, like, you know), false starts, stutters, and immediate self-corrections (\"Monday, no, Tuesday\" -> \"Tuesday\"). Fix punctuation and capitalisation, including removing full stops placed where the speaker only paused mid-sentence. Do not rephrase or reorder.",
        CleanupLevel.Medium => "Remove fillers, false starts, stutters and immediate self-corrections; fix punctuation and capitalisation; fix grammar and agreement errors; remove redundant repetition; split run-on sentences. Keep the user's words and order where possible.",
        CleanupLevel.High => "Remove fillers, false starts and self-corrections; fix punctuation, capitalisation and grammar; remove redundancy; tighten wording; merge fragments; add paragraph breaks at topic shifts. Keep every fact, name, number and instruction. Do not shorten by more than 30%.",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static string ToneInstruction(Tone tone) => tone switch
    {
        Tone.Neutral => "Do not alter register; keep contractions as spoken.",
        Tone.Formal => "Professional register: expand contractions, no slang, complete sentences, polite phrasing. No salutations or sign-offs unless spoken.",
        Tone.Casual => "Relaxed conversational register: contractions allowed, short sentences, keep colloquial phrasing. No emoji unless spoken.",
        _ => throw new ArgumentOutOfRangeException(nameof(tone)),
    };

    private static string Example(CleanupLevel level) => level switch
    {
        CleanupLevel.None =>
            "Input: ok so um send the report by friday new line thanks\n" +
            "Output: ok so um send the report by friday\nthanks",
        CleanupLevel.Light =>
            "Input: um so I think we should, we should ship on monday no tuesday. Because the build. Is not ready period what do you think question mark\n" +
            "Output: So I think we should ship on Tuesday because the build is not ready. What do you think?",
        CleanupLevel.Medium =>
            "Input: uh the results they was pretty clear the results show that the the drug works and it works well and we should we should publish\n" +
            "Output: The results were pretty clear. They show that the drug works well, and we should publish.",
        CleanupLevel.High =>
            "Input: so basically um the meeting went ok I guess we covered budget we covered the hiring plan and then um separately I wanted to mention the audit thing is due next week\n" +
            "Output: The meeting went okay. We covered the budget and the hiring plan.\n\nSeparately, the audit is due next week.",
        _ => string.Empty,
    };
}
