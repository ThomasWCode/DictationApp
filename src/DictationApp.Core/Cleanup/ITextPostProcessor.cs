namespace DictationApp.Core.Cleanup;

public sealed record PostProcessRequest(
    CleanupLevel Level,
    Tone Tone,
    IReadOnlyList<string> Keyterms,
    string AppName,
    string? Url,
    string? AppHint,
    string? ModelOverride = null);

public sealed record PostProcessResult(
    string Text,
    bool Applied,
    string? Model,
    string? FailureReason,
    int? PromptTokens = null,
    int? CompletionTokens = null)
{
    public static PostProcessResult Passthrough(string text) => new(text, false, null, null);
}

public interface ITextPostProcessor
{
    Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct);
}

/// <summary>Spoken-command normalisation only; no network.</summary>
public sealed class PassthroughPostProcessor : ITextPostProcessor
{
    public Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct) =>
        Task.FromResult(PostProcessResult.Passthrough(SpokenCommandNormaliser.Normalise(rawTranscript)));
}

/// <summary>Sends None+Neutral to the passthrough (no network call) and everything else to the LLM.</summary>
public sealed class PostProcessorRouter(PassthroughPostProcessor passthrough, LlmPostProcessor llm) : ITextPostProcessor
{
    public static bool NeedsLlm(CleanupLevel level, Tone tone) => level != CleanupLevel.None || tone != Tone.Neutral;

    public Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct) =>
        NeedsLlm(request.Level, request.Tone)
            ? llm.ProcessAsync(rawTranscript, request, ct)
            : passthrough.ProcessAsync(rawTranscript, request, ct);
}
