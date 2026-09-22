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

public class LlmGatewayPostProcessorTests
{
    private static readonly PostProcessRequest Request = new(CleanupLevel.Light, Tone.Neutral, ["LSHTM"], "notepad", null, null);

    private delegate Task<HttpResponseMessage> Script(HttpRequestMessage request, int callNumber, CancellationToken ct);

    private sealed class ScriptedHandler(Script script) : HttpMessageHandler
    {
        public List<(string Model, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
            Calls.Add((model, body));
            return await script(request, Calls.Count, cancellationToken);
        }
    }

    private static HttpResponseMessage Ok(string content, int prompt = 100, int completion = 20) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content } } },
                usage = new { prompt_tokens = prompt, completion_tokens = completion },
            }), Encoding.UTF8, "application/json"),
        };

    private static (LlmGatewayPostProcessor Processor, ScriptedHandler Handler, FakeTimeProvider Clock) Create(
        Script script,
        string? apiKey = "key",
        string model = "gemini-2.5-flash-lite",
        params string[] fallbacks)
    {
        var handler = new ScriptedHandler(script);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = LlmGatewayPostProcessor.DefaultBaseAddress });
        var keys = Substitute.For<IApiKeyProvider>();
        keys.GetApiKey().Returns(apiKey);
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { LlmModel = model, LlmFallbackModels = [.. fallbacks] });
        var clock = new FakeTimeProvider();
        return (new LlmGatewayPostProcessor(factory, keys, settings, NullLogger<LlmGatewayPostProcessor>.Instance, clock), handler, clock);
    }

    [Fact]
    public async Task Returns_cleaned_text_from_first_model()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("I think we should ship on Tuesday.")));
        var result = await p.ProcessAsync("um I think we should ship on tuesday", Request, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("I think we should ship on Tuesday.", result.Text);
        Assert.Equal("gemini-2.5-flash-lite", result.Model);
        Assert.Equal(100, result.PromptTokens);
        Assert.Single(handler.Calls);
        Assert.Contains("\"temperature\":0.1", handler.Calls[0].Body);
        Assert.Contains("LSHTM", handler.Calls[0].Body);
    }

    [Fact]
    public async Task Falls_back_through_the_chain_on_http_errors()
    {
        var (p, handler, _) = Create(
            (_, n, _) => Task.FromResult(n < 3 ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"no access\"}") } : Ok("Clean text here now.")),
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
    public async Task Missing_api_key_short_circuits()
    {
        var (p, handler, _) = Create((_, _, _) => Task.FromResult(Ok("x")), apiKey: null);
        var result = await p.ProcessAsync("text", Request, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("no-api-key", result.FailureReason);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Per_attempt_timeout_moves_to_next_model()
    {
        var (p, handler, clock) = Create(
            async (_, n, ct) =>
            {
                if (n == 1)
                {
                    // Hang until the per-attempt timer (driven by the fake clock) cancels the request.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return Ok("Second model answered with text.");
            },
            model: "slow",
            fallbacks: ["fast"]);

        var task = p.ProcessAsync("second model answered with text", Request, CancellationToken.None);
        await Task.Delay(100);
        clock.Advance(LlmGatewayPostProcessor.PerAttemptTimeout + TimeSpan.FromMilliseconds(10));
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
            clock.Advance(LlmGatewayPostProcessor.PerAttemptTimeout + TimeSpan.FromMilliseconds(10));
        }

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Applied);
        Assert.Equal("Some words to clean up", result.Text);
    }

    [Fact]
    public void Max_tokens_is_clamped()
    {
        Assert.Equal(200, LlmGatewayPostProcessor.MaxTokensFor("hi"));
        Assert.Equal(4000, LlmGatewayPostProcessor.MaxTokensFor(new string('x', 40_000)));
        Assert.Equal(2 * 100 + 100, LlmGatewayPostProcessor.MaxTokensFor(new string('x', 400)));
    }

    [Fact]
    public void Model_chain_dedupes_and_honours_override()
    {
        var (p, _, _) = Create((_, _, _) => Task.FromResult(Ok("x")), model: "a", fallbacks: ["b", "a", " c "]);
        Assert.Equal(["z", "a", "b", "c"], p.ModelChain("z"));
        Assert.Equal(["a", "b", "c"], p.ModelChain(null));
    }
}
