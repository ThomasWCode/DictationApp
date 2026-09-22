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

    // (input, output) USD per 1M tokens, from GET /v1/models on 2026-09-22.
    private static readonly Dictionary<string, (decimal In, decimal Out)> LlmPerMillion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-2.5-flash-lite"] = (0.10m, 0.40m),
        ["gemini-2.5-flash"] = (0.30m, 2.50m),
        ["gemini-2.5-pro"] = (1.25m, 10m),
        ["claude-haiku-4-5-20251001"] = (1m, 5m),
        ["claude-sonnet-4-5-20250929"] = (3m, 15m),
        ["gpt-5-nano"] = (0.05m, 0.40m),
        ["gpt-5-mini"] = (0.25m, 2m),
        ["gpt-oss-20b"] = (0.07m, 0.30m),
        ["gpt-oss-120b"] = (0.15m, 0.60m),
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
