using System.Net;
using System.Text;
using System.Text.Json;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace DictationApp.Core.Tests;

public class LlmPostProcessorTests
{
    private static readonly PostProcessRequest Request = new(CleanupLevel.Light, Tone.Neutral, ["LSHTM"], "notepad", null, null);

    private delegate Task<HttpResponseMessage> Script(HttpRequestMessage request, int callNumber, CancellationToken ct);

    private sealed class ScriptedHandler(Script script) : HttpMessageHandler
    {
        public List<(string Model, string Body, string? Auth, Uri? Url)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            Calls.Add((model, body, request.Headers.Authorization?.ToString(), request.RequestUri));
            return await script(request, Calls.Count, cancellationToken);
        }
    }

    private static HttpResponseMessage Ok(string content, int prompt = 100, int completion = 20) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content, reasoning = "thinking…" } } },
                usage = new { prompt_tokens = prompt, completion_tokens = completion },
            }), Encoding.UTF8, "application/json"),
        };

    private static (LlmPostProcessor Processor, ScriptedHandler Handler, FakeTimeProvider Clock) Create(
        Script script,
        string? apiKey = "gsk_test",
        string model = "qwen/qwen3.8-27b",
        string? baseUrl = null,
        params string[] fallbacks)
    {
        var handler = new ScriptedHandler(script);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var keys = Substitute.For<IApiKeyProvider>();
        keys.GetLlmApiKey().Returns(apiKey);
        keys.GetApiKey().Returns("assemblyai-key-unused-here");
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { LlmModel = model, LlmFallbackModels = [.. fallbacks], LlmBaseUrl = baseUrl ?? AppSettings.DefaultLlmBaseUrl });
        var clock = new FakeTimeProvider();
        return (new LlmPostProcessor(factory, keys, settings, NullLogger<LlmPostProcessor>.Instance, clock), handler, clock);
    }

    [Fact]
    public async Task Calls_groq_with_bearer_key_and_returns_cleaned_text()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("I think we should ship on Tuesday.")));
        var result = await p.ProcessAsync("um I think we should ship on tuesday", Request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("I think we should ship on Tuesday.", result.Text);
        Assert.Equal("qwen/qwen3.8-27b", result.Model);
        Assert.Equal(100, result.PromptTokens);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("Bearer gsk_test", call.Auth);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", call.Url!.ToString());
        Assert.Contains("\"temperature\":0.1", call.Body);
        Assert.Contains("LSHTM", call.Body);
        Assert.DoesNotContain("reasoning_effort", call.Body); // qwen: no reasoning parameter
    }

    [Fact]
    public async Task The_model_sees_the_pauses_and_no_marker_reaches_the_text()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("Typing into the box [pause] still adds a space.")));
        var request = Request with { PauseMarkedTranscript = "Typing into the box [pause] still adds a space." };

        var result = await p.ProcessAsync("Typing into the box. Still adds a space.", request, CancellationToken.None);

        Assert.Contains("Typing into the box [pause] still adds a space.", handler.Calls[0].Body);
        Assert.Contains("marks where the speaker stopped", handler.Calls[0].Body);
        Assert.Equal("Typing into the box still adds a space.", result.Text);
    }

    [Fact]
    public async Task Level_none_keeps_the_transcript_punctuation()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("Typing into the box. Still adds a space.")));
        var request = Request with { Level = CleanupLevel.None, Tone = Tone.Formal, PauseMarkedTranscript = "Typing into the box [pause] still adds a space." };

        await p.ProcessAsync("Typing into the box. Still adds a space.", request, CancellationToken.None);

        Assert.DoesNotContain("[pause]", handler.Calls[0].Body);
    }

    [Fact]
    public async Task Reasoning_models_are_asked_for_low_effort()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("Clean text here now.")), model: "openai/gpt-oss-120b");
        await p.ProcessAsync("some raw text here now", Request, CancellationToken.None);
        Assert.Contains("\"reasoning_effort\":\"low\"", handler.Calls[0].Body);
        Assert.Equal("low", LlmPostProcessor.ReasoningEffortFor("openai/gpt-oss-20b"));
        Assert.Null(LlmPostProcessor.ReasoningEffortFor("qwen/qwen3.8-27b"));
    }

    [Fact]
    public async Task Rate_limit_moves_to_the_next_free_model()
    {
        var (p, handler, _) = Create(
            (_, n, _) => Task.FromResult(n < 3 ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"message\":\"Rate limit reached\"}}") } : Ok("Clean text here now.")),
            model: "a",
            fallbacks: ["b", "c"]);
        var result = await p.ProcessAsync("some raw text here now", Request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("c", result.Model);
        Assert.Equal(["a", "b", "c"], handler.Calls.Select(c => c.Model));
    }

    [Fact]
    public async Task Unauthorized_stops_the_chain_and_returns_raw()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") }), model: "a", fallbacks: ["b"]);
        var result = await p.ProcessAsync("raw period", Request, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("Raw.", result.Text);
        Assert.Single(handler.Calls);
        Assert.Contains("http-401", result.FailureReason);
    }

    [Fact]
    public async Task Invalid_output_is_rejected_and_next_model_tried()
    {
        var (p, handler, _) = Create((_, n, _) => Task.FromResult(n == 1 ? Ok("Sure! Here you go: the text.") : Ok("The actual cleaned text goes here.")), model: "a", fallbacks: ["b"]);
        var result = await p.ProcessAsync("the actual cleaned text goes here", Request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("b", result.Model);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task All_models_failing_returns_normalised_raw_with_reason()
    {
        var (p, _, _) = Create((_, _, _) => throw new HttpRequestException("offline"), model: "a", fallbacks: ["b"]);
        var result = await p.ProcessAsync("hello new line world", Request, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("Hello\nWorld", result.Text);
        Assert.Contains("HttpRequestException", result.FailureReason);
    }

    [Fact]
    public async Task Missing_groq_key_short_circuits()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("x")), apiKey: null);
        var result = await p.ProcessAsync("text", Request, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("no-llm-key", result.FailureReason);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Custom_base_url_is_honoured()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("Clean text here now.")), baseUrl: "https://example.test/v1");
        await p.ProcessAsync("some raw text here now", Request, CancellationToken.None);
        Assert.Equal("https://example.test/v1/chat/completions", handler.Calls[0].Url!.ToString());
    }

    [Fact]
    public async Task Per_attempt_timeout_moves_to_next_model()
    {
        var (p, handler, clock) = Create(
            async (_, n, ct) =>
            {
                if (n == 1)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return Ok("Second model answered with text.");
            },
            model: "slow",
            fallbacks: ["fast"]);

        var task = p.ProcessAsync("second model answered with text", Request, CancellationToken.None);
        await Task.Delay(100);
        clock.Advance(LlmPostProcessor.PerAttemptTimeout + TimeSpan.FromMilliseconds(10));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Applied);
        Assert.Equal("fast", result.Model);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Total_timeout_gives_up_with_raw_text()
    {
        var (p, _, clock) = Create(
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok("never");
            },
            model: "slow1",
            fallbacks: ["slow2", "slow3"]);

        var task = p.ProcessAsync("some words to clean up", Request, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100);
            clock.Advance(LlmPostProcessor.PerAttemptTimeout + TimeSpan.FromMilliseconds(10));
        }

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Applied);
        Assert.Equal("Some words to clean up", result.Text);
    }

    [Fact]
    public void Max_tokens_is_clamped()
    {
        Assert.Equal(200, LlmPostProcessor.MaxTokensFor("hi"));
        Assert.Equal(4000, LlmPostProcessor.MaxTokensFor(new string('x', 40_000)));
        Assert.Equal(2 * 100 + 100, LlmPostProcessor.MaxTokensFor(new string('x', 400)));
    }

    [Fact]
    public void Model_chain_dedupes_and_honours_override()
    {
        var (p, _, _) = Create((_, _, _) => Task.FromResult(Ok("x")), model: "a", fallbacks: ["b", "a", " c "]);
        Assert.Equal(["z", "a", "b", "c"], p.ModelChain("z"));
        Assert.Equal(["a", "b", "c"], p.ModelChain(null));
    }
}
