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

public class ListFormatterTests
{
    [Fact]
    public void Numeric_markers_become_a_numbered_list()
    {
        var input = "Three things to do. 1. Buy milk. 2. Call mum. 3. Write the report.";
        Assert.Equal("Three things to do.\n1. Buy milk.\n2. Call mum.\n3. Write the report.", ListFormatter.Format(input));
    }

    [Fact]
    public void Spoken_number_words_become_a_numbered_list_and_joiners_are_dropped()
    {
        var input = "shopping list number one buy milk and number two call mum, then number three write the report";
        Assert.Equal("shopping list\n1. buy milk\n2. call mum\n3. write the report", ListFormatter.Format(input));
    }

    [Theory]
    [InlineData("version 1.2 is out and 3.4 follows")]
    [InlineData("my number one priority is sleep")]
    [InlineData("I have 2. They have 3.")]
    [InlineData("point one five percent")]
    public void Prose_with_numbers_is_left_alone(string input)
    {
        Assert.Equal(input, ListFormatter.Format(input));
    }

    [Fact]
    public void Requires_a_run_starting_at_one()
    {
        Assert.Equal("see 2. and 3. below", ListFormatter.Format("see 2. and 3. below"));
    }

    [Fact]
    public void Works_through_the_normaliser_with_capitalisation()
    {
        var input = "to do period number one buy milk number two call mum";
        Assert.Equal("To do.\n1. Buy milk\n2. Call mum", SpokenCommandNormaliser.Normalise(input));
    }

    [Fact]
    public void Parenthesis_and_colon_markers_are_accepted()
    {
        Assert.Equal("Steps\n1. open\n2. close", ListFormatter.Format("Steps 1) open 2) close"));
        Assert.Equal("1. first\n2. second", ListFormatter.Format("1: first 2: second"));
    }

    [Fact]
    public void Prompt_mentions_list_formatting()
    {
        var prompt = PromptBuilder.BuildSystemPrompt(new PromptContext(CleanupLevel.Light, Tone.Neutral, [], "app", null, null));
        Assert.Contains("numbered list", prompt);
        Assert.Contains("one item per line", prompt);
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
    public void Prompt_asks_to_rejoin_sentences_split_at_pauses()
    {
        Assert.Contains("cut where the speaker paused", PromptBuilder.BuildSystemPrompt(Ctx()));

        // None keeps the transcript's punctuation even when a tone sends it to the LLM.
        Assert.DoesNotContain("cut where the speaker paused", PromptBuilder.BuildSystemPrompt(Ctx(CleanupLevel.None, Tone.Formal)));
    }

    [Fact]
    public void Prompt_explains_pause_markers_when_the_transcript_has_them()
    {
        var marked = PromptBuilder.BuildSystemPrompt(Ctx() with { PauseMarkers = true });

        Assert.Contains("\"[pause]\" marks where the speaker stopped", marked);
        Assert.Contains("Never output [pause]", marked);
        Assert.DoesNotContain("\"[pause]\" marks", PromptBuilder.BuildSystemPrompt(Ctx()));
        Assert.DoesNotContain("\"[pause]\" marks", PromptBuilder.BuildSystemPrompt(Ctx(CleanupLevel.None, Tone.Formal) with { PauseMarkers = true }));
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
