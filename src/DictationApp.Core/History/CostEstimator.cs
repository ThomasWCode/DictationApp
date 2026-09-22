namespace DictationApp.Core.History;

/// <summary>
/// Best-effort USD estimate shown in History. Streaming is billed per audio hour; the LLM Gateway passes
/// provider prices through at 0% markup (USD per million tokens). Unknown models estimate as zero.
/// </summary>
public static class CostEstimator
{
    private static readonly Dictionary<string, decimal> SttPerHour = new(StringComparer.OrdinalIgnoreCase)
    {
        ["universal-3-5-pro"] = 0.45m,
        ["universal-streaming"] = 0.15m,
    };

    // (input, output) USD per 1M tokens. Cleanup runs on Groq's free tier, so every Groq model costs nothing;
    // the table exists for anyone pointing LlmBaseUrl at a paid OpenAI-compatible endpoint.
    private static readonly Dictionary<string, (decimal In, decimal Out)> LlmPerMillion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen/qwen3.8-27b"] = (0m, 0m),
        ["openai/gpt-oss-120b"] = (0m, 0m),
        ["openai/gpt-oss-20b"] = (0m, 0m),
    };

    public static decimal SttCost(string speechModel, TimeSpan audioDuration)
    {
        var rate = SttPerHour.TryGetValue(speechModel, out var r) ? r : 0.45m;
        return Math.Round(rate * (decimal)audioDuration.TotalHours, 6);
    }

    public static decimal LlmCost(string? model, int? promptTokens, int? completionTokens)
    {
        if (model is null || !LlmPerMillion.TryGetValue(model, out var price))
        {
            return 0m;
        }

        var cost = ((promptTokens ?? 0) * price.In + (completionTokens ?? 0) * price.Out) / 1_000_000m;
        return Math.Round(cost, 6);
    }
}
