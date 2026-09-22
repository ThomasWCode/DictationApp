using System.Text;
using System.Text.Json;

namespace DictationApp.Core.Transcription;

/// <summary>Query-string parameters for one streaming session. Immutable; built per dictation.</summary>
public sealed record SessionOptions
{
    public const int MaxPromptLength = 1750;

    public string SpeechModel { get; init; } = "universal-3-5-pro";

    public int SampleRate { get; init; } = 16_000;

    public string Encoding { get; init; } = "pcm_s16le";

    /// <summary>Free-text context (Pro only), at most 1750 characters.</summary>
    public string? Prompt { get; init; }

    public IReadOnlyList<string> Keyterms { get; init; } = [];

    public string? LanguageCodes { get; init; }

    public int? MinTurnSilenceMs { get; init; }

    public int? MaxTurnSilenceMs { get; init; }

    public double? VadThreshold { get; init; }

    public int? InactivityTimeoutSeconds { get; init; }

    public bool FormatTurns { get; init; } = true;

    public bool IsPro => SpeechModel.Contains("pro", StringComparison.OrdinalIgnoreCase);

    public string BuildQueryString()
    {
        var sb = new StringBuilder();
        void Add(string key, string value)
        {
            sb.Append(sb.Length == 0 ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        Add("sample_rate", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("speech_model", SpeechModel);
        Add("encoding", Encoding);
        if (FormatTurns)
        {
            Add("format_turns", "true");
        }

        if (!string.IsNullOrWhiteSpace(Prompt) && IsPro)
        {
            var prompt = Prompt.Length > MaxPromptLength ? Prompt[..MaxPromptLength] : Prompt;
            Add("prompt", prompt);
        }

        if (Keyterms.Count > 0)
        {
            Add("keyterms_prompt", JsonSerializer.Serialize(Keyterms));
        }

        if (!string.IsNullOrWhiteSpace(LanguageCodes))
        {
            Add("language_codes", LanguageCodes);
        }

        if (MinTurnSilenceMs is { } min)
        {
            Add("min_turn_silence", min.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (MaxTurnSilenceMs is { } max)
        {
            Add("max_turn_silence", max.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (VadThreshold is { } vad)
        {
            Add("vad_threshold", vad.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (InactivityTimeoutSeconds is { } inactivity)
        {
            Add("inactivity_timeout", inactivity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
