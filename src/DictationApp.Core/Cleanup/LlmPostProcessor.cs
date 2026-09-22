using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.Cleanup;

/// <summary>
/// OpenAI-compatible chat call (Groq by default) with a per-model fallback chain. Budget: 4 s per attempt,
/// 8 s total. Rate limits (429) and other failures move to the next model; any total failure returns the
/// normalised raw transcript so the user always gets their words. Reasoning models (gpt-oss) are asked for
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

        var systemPrompt = PromptBuilder.BuildSystemPrompt(new PromptContext(request.Level, request.Tone, request.Keyterms, request.AppName, request.Url, request.AppHint));
        var body = new ChatRequest
        {
            Messages =
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", rawTranscript),
            ],
            MaxTokens = MaxTokensFor(rawTranscript),
            Temperature = 0.1,
        };

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var totalTimer = CancelAfter(total, TotalTimeout);
        string? lastReason = null;
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
                    lastReason = $"{model}:http-{(int)resp.StatusCode}";
                    _logger.LogWarning("LLM {Model} returned {Status}: {Body}", model, (int)resp.StatusCode, Truncate(err, 300));
                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        break; // bad key: no point trying other models
                    }

                    continue; // 429 rate limit, 404 unknown model, 5xx: next model
                }

                var chat = await resp.Content.ReadFromJsonAsync<ChatResponse>(Json, attempt.Token).ConfigureAwait(false);
                var content = chat?.Choices?.FirstOrDefault()?.Message?.Content;
                var validation = OutputValidator.Validate(rawTranscript, content);
                if (!validation.IsValid)
                {
                    lastReason = $"{model}:invalid-{validation.Reason}";
                    _logger.LogWarning("LLM output from {Model} rejected ({Reason}): {Output}", model, validation.Reason, Truncate(content, 200));
                    continue;
                }

                _logger.LogInformation("LLM cleanup via {Model} in {Elapsed} ms (level={Level}, tone={Tone}, tokens={Prompt}+{Completion})", model, sw.ElapsedMilliseconds, request.Level, request.Tone, chat?.Usage?.PromptTokens, chat?.Usage?.CompletionTokens);
                return new PostProcessResult(validation.Text, true, model, null, chat?.Usage?.PromptTokens, chat?.Usage?.CompletionTokens);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastReason = total.IsCancellationRequested ? "total-timeout" : $"{model}:timeout";
                _logger.LogWarning("LLM {Model} timed out after {Elapsed} ms", model, sw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
            {
                lastReason = $"{model}:{ex.GetType().Name}";
                _logger.LogWarning(ex, "LLM {Model} failed", model);
            }
        }

        return new PostProcessResult(fallback, false, null, lastReason ?? "unknown");
    }

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
