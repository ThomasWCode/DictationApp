using System.Text.RegularExpressions;

namespace DictationApp.Core.Cleanup;

public sealed record ValidationResult(bool IsValid, string Text, string? Reason);

/// <summary>
/// Pure. Guards against the classic small-model failures: wrapping output in quotes or fences, prefacing
/// with "Here is", answering instead of cleaning, or truncating. Rejected output falls back to raw text.
/// </summary>
public static partial class OutputValidator
{
    public const double MaxLengthRatio = 2.5;
    public const double MinLengthRatio = 0.4;

    private static readonly string[] BannedPrefixes =
    [
        "here is", "here's", "here are", "sure", "certainly", "of course", "okay, here", "ok, here",
        "the cleaned", "cleaned text:", "cleaned transcript", "output:", "as an ai", "i'm sorry", "i am sorry",
    ];

    public static ValidationResult Validate(string rawInput, string? modelOutput)
    {
        if (modelOutput is null)
        {
            return new ValidationResult(false, string.Empty, "empty");
        }

        var text = Strip(modelOutput);
        if (text.Length == 0)
        {
            return new ValidationResult(false, text, "empty");
        }

        var lower = text.ToLowerInvariant();
        foreach (var prefix in BannedPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new ValidationResult(false, text, "banned-prefix:" + prefix);
            }
        }

        var inputLen = Math.Max(1, rawInput.Trim().Length);
        var ratio = (double)text.Length / inputLen;
        if (inputLen >= 20 && ratio > MaxLengthRatio)
        {
            return new ValidationResult(false, text, $"too-long:{ratio:0.00}");
        }

        if (inputLen >= 20 && ratio < MinLengthRatio)
        {
            return new ValidationResult(false, text, $"too-short:{ratio:0.00}");
        }

        return new ValidationResult(true, text, null);
    }

    /// <summary>Removes code fences, surrounding quotes and leading labels.</summary>
    public static string Strip(string output)
    {
        var text = output.Trim();
        var fence = CodeFence().Match(text);
        if (fence.Success)
        {
            text = fence.Groups["body"].Value.Trim();
        }

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”') || (text[0] == '\'' && text[^1] == '\'')))
        {
            text = text[1..^1].Trim();
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^```[a-zA-Z]*\s*\n(?<body>[\s\S]*?)\n?```\s*$")]
    private static partial Regex CodeFence();
}
