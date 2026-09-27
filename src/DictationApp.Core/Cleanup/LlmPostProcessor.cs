using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.Cleanup;

/// <summary>
/// OpenAI-compatible chat call (Groq by default) with a per-model fallback chain. Budget: 4 s per attempt,
/// 8 s total. Rate limits (429), a model the key may not use (403), unknown models and other failures move to the
/// next model; only a rejected key (401) ends the chain. An answer the model did not finish (finish_reason other than
/// "stop", for example "length" when it ran out of tokens) is never used: the next model is tried. Any total failure
/// returns the normalised raw transcript so the user always gets their words. Reasoning models (gpt-oss) are asked for
/// low reasoning effort, otherwise they spend the whole completion budget thinking.
/// </summary>
public sealed class LlmPostProcessor : ITextPostProcessor
{
    public const string HttpClientName = "llm";
    public static readonly Uri DefaultBaseAddress = new(AppSettings.DefaultLlmBaseUrl);
    public static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApiKeyProvider _keys;
    private readonly ISettingsStore _settings;
    private readonly ILogger<LlmPostProcessor> _logger;
    private readonly TimeProvider _time;

    public LlmPostProcessor(
        IHttpClientFactory httpClientFactory,
        IApiKeyProvider keys,
        ISettingsStore settings,
        ILogger<LlmPostProcessor> logger,
        TimeProvider? time = null)
    {
        _httpClientFactory = httpClientFactory;
        _keys = keys;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Clamped completion budget: roughly 2× the input plus headroom.</summary>
    public static int MaxTokensFor(string input)
    {
        var approxTokens = Math.Max(1, input.Length / 4);
        return Math.Clamp(2 * approxTokens + 100, 200, 4000);
    }

    /// <summary>Groq's gpt-oss models accept reasoning_effort; "low" keeps latency and tokens down.</summary>
    public static string? ReasoningEffortFor(string model) =>
        model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? "low" : null;

    public IReadOnlyList<string> ModelChain(string? overrideModel)
    {
        var s = _settings.Current;
        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideModel))
        {
            chain.Add(overrideModel.Trim());
        }

        if (!string.IsNullOrWhiteSpace(s.LlmModel))
        {
            chain.Add(s.LlmModel.Trim());
        }

        chain.AddRange(s.LlmFallbackModels.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));
        return chain.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Uri BaseAddress
    {
        get
        {
            var url = _settings.Current.LlmBaseUrl;
            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var uri))
            {
                return uri;
            }

            return DefaultBaseAddress;
        }
    }

    public async Task<PostProcessResult> ProcessAsync(string rawTranscript, PostProcessRequest request, CancellationToken ct)
    {
        var fallback = SpokenCommandNormaliser.Normalise(rawTranscript);
        var key = _keys.GetLlmApiKey();
        if (string.IsNullOrEmpty(key))
        {
            return new PostProcessResult(fallback, false, null, "no-llm-key");
        }

        var models = ModelChain(request.ModelOverride);
        if (models.Count == 0)
        {
            return new PostProcessResult(fallback, false, null, "no-model");
        }

        // The model sees where the speaker paused, so it can rejoin a sentence the transcriber ended at a pause to think.
        // Not at None, which keeps the transcript's punctuation even when a tone runs the LLM.
        // Marked only when turns were actually joined (the texts differ), and never when the speaker's own words contain
        // the marker, which would otherwise be treated as a pause and removed.
        var input = request.Level != CleanupLevel.None && request.PauseMarkedTranscript is { } marked &&
            !string.Equals(marked, rawTranscript, StringComparison.Ordinal) &&
            !rawTranscript.Contains(TranscriptAssembler.PauseMarker, StringComparison.OrdinalIgnoreCase) ? marked : rawTranscript;
        var pauseMarkers = !ReferenceEquals(input, rawTranscript);
        var systemPrompt = PromptBuilder.BuildSystemPrompt(new PromptContext(request.Level, request.Tone, request.Keyterms, request.AppName, request.Url, request.AppHint, pauseMarkers));
        var body = new ChatRequest
        {
            Messages =
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", input),
            ],
            MaxTokens = MaxTokensFor(input),
            Temperature = 0.1,
        };

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var totalTimer = CancelAfter(total, TotalTimeout);
        var attempts = new List<string>(); // one reason per failed attempt: the trail kept when every model fails
        string? configurationProblem = null;
        var sw = Stopwatch.StartNew();
        var baseAddress = BaseAddress;

        foreach (var model in models)
        {
            if (total.IsCancellationRequested)
            {
                break;
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            using var attemptTimer = CancelAfter(attempt, PerAttemptTimeout);
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                client.BaseAddress = baseAddress;
                using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                req.Content = JsonContent.Create(body with { Model = model, ReasoningEffort = ReasoningEffortFor(model) }, options: Json);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                    attempts.Add($"{model}:http-{(int)resp.StatusCode}");
                    _logger.LogWarning("LLM {Model} returned {Status}: {Body}", model, (int)resp.StatusCode, Truncate(err, 300));
                    configurationProblem ??= ConfigurationProblemFor(model, resp.StatusCode, err);
                    if (resp.StatusCode is HttpStatusCode.Unauthorized)
                    {
                        break; // the key itself is rejected: every other model would refuse it too
                    }

                    continue; // 403 model not allowed for this key, 429 rate limit, 404 unknown model, 5xx: next model
                }

                var chat = await resp.Content.ReadFromJsonAsync<ChatResponse>(Json, attempt.Token).ConfigureAwait(false);
                var choice = chat?.Choices?.FirstOrDefault();
                var content = choice?.Message?.Content;
                var finish = choice?.FinishReason;
                if (!string.IsNullOrEmpty(finish) && !string.Equals(finish, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    // Cut off (usually "length": the reasoning and the answer ran out of tokens) or filtered: the text may
                    // be missing its end, which the length check cannot tell, so it is never inserted. Size only in the log.
                    attempts.Add($"{model}:incomplete-{finish}");
                    _logger.LogWarning("LLM output from {Model} not finished ({Finish}, {Completion} completion tokens, {Chars} chars); trying the next model", model, finish, chat?.Usage?.CompletionTokens, content?.Length ?? 0);
                    continue;
                }

                // Markers removed first, so the checks see exactly the text that would be inserted.
                var validation = OutputValidator.Validate(rawTranscript, content is not null && pauseMarkers ? TranscriptAssembler.RemovePauseMarkers(content) : content);
                if (!validation.IsValid)
                {
                    attempts.Add($"{model}:invalid-{validation.Reason}");
                    // Reason and size only: no dictated text in the log files.
                    _logger.LogWarning("LLM output from {Model} rejected ({Reason}, {Chars} chars)", model, validation.Reason, content?.Length ?? 0);
                    continue;
                }

                _logger.LogInformation("LLM cleanup via {Model} in {Elapsed} ms (level={Level}, tone={Tone}, pauses={Pauses}, finish={Finish}, tokens={Prompt}+{Completion})", model, sw.ElapsedMilliseconds, request.Level, request.Tone, pauseMarkers, finish ?? "none", chat?.Usage?.PromptTokens, chat?.Usage?.CompletionTokens);
                return new PostProcessResult(validation.Text, true, model, null, chat?.Usage?.PromptTokens, chat?.Usage?.CompletionTokens, configurationProblem);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                attempts.Add(total.IsCancellationRequested ? "total-timeout" : $"{model}:timeout");
                _logger.LogWarning("LLM {Model} timed out after {Elapsed} ms", model, sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
            {
                attempts.Add($"{model}:{ex.GetType().Name}");
                _logger.LogWarning(ex, "LLM {Model} failed", model);
            }
        }

        return new PostProcessResult(fallback, false, null, attempts.Count == 0 ? "unknown" : string.Join("; ", attempts), ConfigurationProblem: configurationProblem);
    }

    /// <summary>
    /// A failure that will repeat on every dictation until the settings change: the key is rejected (401), the key may
    /// not use this model (403), or the model does not exist or was retired (404, or 400 naming the model). Worth telling
    /// the user once even when a fallback model covered for it. Null for passing trouble (rate limits, timeouts, 5xx).
    /// </summary>
    public static string? ConfigurationProblemFor(string model, HttpStatusCode status, string? body) => (int)status switch
    {
        401 => "The Groq key was rejected. Check it in Settings › API.",
        403 => $"The Groq key may not use {model}. Allow the model in the Groq console or pick another in Settings › API.",
        404 => $"{model} is not available (unknown or retired). Pick another model in Settings › API.",
        400 when body is not null && body.Contains("model", StringComparison.OrdinalIgnoreCase) && (body.Contains("decommission", StringComparison.OrdinalIgnoreCase) || body.Contains("not_found", StringComparison.OrdinalIgnoreCase) || body.Contains("does not exist", StringComparison.OrdinalIgnoreCase)) =>
            $"{model} is not available (unknown or retired). Pick another model in Settings › API.",
        _ => null,
    };

    private static string Truncate(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[..max] + "…";

    /// <summary>TimeProvider-aware CancelAfter so tests can drive timeouts with a fake clock.</summary>
    private ITimer CancelAfter(CancellationTokenSource cts, TimeSpan after) =>
        _time.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The attempt finished before the timer fired; nothing to cancel.
                }
            },
            cts,
            after,
            Timeout.InfiniteTimeSpan);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatRequest
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; init; } = [];

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public Usage? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public ResponseMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ResponseMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
    }
}
