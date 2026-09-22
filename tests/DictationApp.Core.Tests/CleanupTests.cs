using DictationApp.Core.Cleanup;

namespace DictationApp.Core.Tests;

public class SpokenCommandNormaliserTests
{
    [Theory]
    [InlineData("send it by friday period thanks", "Send it by friday. Thanks")]
    [InlineData("first item new line second item", "First item\nSecond item")]
    [InlineData("intro new paragraph body", "Intro\n\nBody")]
    [InlineData("shopping bullet point milk bullet point eggs", "Shopping\n- Milk\n- Eggs")]
    [InlineData("really question mark", "Really?")]
    [InlineData("wait comma no", "Wait, no")]
    [InlineData("one exclamation mark two", "One! Two")]
    public void Applies_spoken_commands(string input, string expected)
    {
        Assert.Equal(expected, SpokenCommandNormaliser.Normalise(input));
    }

    [Fact]
    public void Scratch_that_drops_preceding_clause()
    {
        Assert.Equal("Meet on monday. Actually tuesday", SpokenCommandNormaliser.Normalise("meet on monday. we said wednesday scratch that actually tuesday"));
        Assert.Equal("Tuesday", SpokenCommandNormaliser.Normalise("monday scratch that tuesday"));
    }

    [Fact]
    public void Commands_are_case_insensitive_and_whole_words()
    {
        Assert.Equal("A periodic table", SpokenCommandNormaliser.Normalise("a periodic table"));
        Assert.Equal("Done.", SpokenCommandNormaliser.Normalise("done PERIOD"));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Equal(string.Empty, SpokenCommandNormaliser.Normalise("   "));
    }

    [Fact]
    public void Capitalises_sentence_starts()
    {
        Assert.Equal("Hello. How are you?", SpokenCommandNormaliser.Normalise("hello. how are you?"));
    }
}

public class PromptBuilderTests
{
    private static PromptContext Ctx(CleanupLevel level = CleanupLevel.Light, Tone tone = Tone.Neutral, params string[] terms) =>
        new(level, tone, terms, "OUTLOOK", "https://mail.google.com/mail/u/0/", "This is an email.");

    [Fact]
    public void Prompt_contains_level_tone_keyterms_and_context()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Ctx(CleanupLevel.Medium, Tone.Formal, "LSHTM", "isoniazid"));

        Assert.Contains(PromptBuilder.LevelInstruction(CleanupLevel.Medium), prompt);
        Assert.Contains(PromptBuilder.ToneInstruction(Tone.Formal), prompt);
        Assert.Contains("LSHTM, isoniazid", prompt);
        Assert.Contains("OUTLOOK", prompt);
        Assert.Contains("mail.google.com", prompt);
        Assert.Contains("This is an email.", prompt);
        Assert.Contains("Never add information", prompt);
        Assert.Contains("Example:", prompt);
    }

    [Fact]
    public void No_keyterms_says_none()
    {
        Assert.Contains("(none)", PromptBuilder.BuildSystemPrompt(Ctx()));
    }

    [Theory]
    [InlineData(CleanupLevel.None)]
    [InlineData(CleanupLevel.Light)]
    [InlineData(CleanupLevel.Medium)]
    [InlineData(CleanupLevel.High)]
    public void Every_level_has_an_instruction_and_example(CleanupLevel level)
    {
        var prompt = PromptBuilder.BuildSystemPrompt(Ctx(level));
        Assert.Contains("Cleanup level: ", prompt);
        Assert.Contains("Input:", prompt);
        Assert.Contains("Output:", prompt);
    }
}

public class PostProcessorRouterTests
{
    [Fact]
    public void None_neutral_needs_no_llm()
    {
        Assert.False(PostProcessorRouter.NeedsLlm(CleanupLevel.None, Tone.Neutral));
        Assert.True(PostProcessorRouter.NeedsLlm(CleanupLevel.None, Tone.Formal));
        Assert.True(PostProcessorRouter.NeedsLlm(CleanupLevel.Light, Tone.Neutral));
    }

    [Fact]
    public async Task Passthrough_normalises_commands_without_network()
    {
        var p = new PassthroughPostProcessor();
        var result = await p.ProcessAsync("hello period new line bye", new PostProcessRequest(CleanupLevel.None, Tone.Neutral, [], "app", null, null), CancellationToken.None);

        Assert.Equal("Hello.\nBye", result.Text);
        Assert.False(result.Applied);
        Assert.Null(result.Model);
    }
}

public class OutputValidatorTests
{
    private const string Input = "so um I think we should ship on tuesday what do you think";

    [Fact]
    public void Accepts_clean_output()
    {
        var v = OutputValidator.Validate(Input, "I think we should ship on Tuesday. What do you think?");
        Assert.True(v.IsValid);
    }

    [Theory]
    [InlineData("Here is the cleaned text: I think we should ship.")]
    [InlineData("Sure! I think we should ship on Tuesday.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_banned_prefixes_and_empty(string output)
    {
        Assert.False(OutputValidator.Validate(Input, output).IsValid);
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(OutputValidator.Validate(Input, null).IsValid);
    }

    [Fact]
    public void Strips_fences_and_quotes()
    {
        Assert.Equal("Ship on Tuesday.", OutputValidator.Strip("```text\nShip on Tuesday.\n```"));
        Assert.Equal("Ship on Tuesday.", OutputValidator.Strip("\"Ship on Tuesday.\""));
        Assert.Equal("Ship on Tuesday.", OutputValidator.Strip("“Ship on Tuesday.”"));
    }

    [Fact]
    public void Rejects_runaway_length_and_truncation()
    {
        var tooLong = string.Join(' ', Enumerable.Repeat("word", 80));
        Assert.False(OutputValidator.Validate(Input, tooLong).IsValid);
        Assert.False(OutputValidator.Validate(Input, "Ship.").IsValid);
    }

    [Fact]
    public void Short_inputs_skip_ratio_checks()
    {
        Assert.True(OutputValidator.Validate("hi", "Hello there, how are you doing today?").IsValid);
    }
}
