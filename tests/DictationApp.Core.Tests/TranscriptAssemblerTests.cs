using DictationApp.Core.Transcription;

namespace DictationApp.Core.Tests;

public class TranscriptAssemblerTests
{
    private static TurnMessage Turn(int order, string text, bool end, bool formatted = false, string? utterance = null) =>
        new() { Type = "Turn", TurnOrder = order, Transcript = text, Utterance = utterance ?? text, EndOfTurn = end, TurnIsFormatted = formatted };

    [Fact]
    public void Joins_final_turns_in_turn_order_regardless_of_arrival()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(2, "third.", true, true));
        a.Ingest(Turn(0, "First.", true, true));
        a.Ingest(Turn(1, "second.", true, true));

        Assert.Equal("First. second. third.", a.FinalText);
        Assert.Equal(3, a.TurnCount);
    }

    [Fact]
    public void Formatted_final_turn_wins_over_unformatted_duplicate()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(0, "hello world", true, formatted: false));
        a.Ingest(Turn(0, "Hello, world.", true, formatted: true));
        a.Ingest(Turn(0, "hello world", true, formatted: false)); // late duplicate

        Assert.Equal("Hello, world.", a.FinalText);
    }

    [Fact]
    public void Partial_never_overwrites_closed_turn()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(0, "Done.", true, true));
        var changed = a.Ingest(Turn(0, "Do", false));

        Assert.False(changed);
        Assert.Equal("Done.", a.FinalText);
    }

    [Fact]
    public void Live_text_includes_open_partial_and_final_text_includes_trailing_partial()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(0, "First sentence.", true, true));
        a.Ingest(Turn(1, "and then", false));

        Assert.True(a.HasOpenTurn);
        Assert.Equal("First sentence. and then", a.LiveText);
        Assert.Equal("First sentence. and then", a.FinalText);
        Assert.Equal("First sentence.", a.ClosedText);
    }

    [Fact]
    public void Utterance_preferred_over_transcript_then_words()
    {
        var withWords = new TurnMessage
        {
            TurnOrder = 0,
            Words = [new WordInfo { Text = "from" }, new WordInfo { Text = "words" }],
        };
        Assert.Equal("from words", withWords.BestText);

        var withTranscript = new TurnMessage { TurnOrder = 0, Transcript = "from transcript", Words = [new WordInfo { Text = "x" }] };
        Assert.Equal("from transcript", withTranscript.BestText);

        var withUtterance = new TurnMessage { TurnOrder = 0, Transcript = "t", Utterance = "from utterance" };
        Assert.Equal("from utterance", withUtterance.BestText);
    }

    [Fact]
    public void Empty_turns_are_skipped_in_join()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(0, string.Empty, true, true, utterance: string.Empty));
        a.Ingest(Turn(1, "Only.", true, true));

        Assert.Equal("Only.", a.FinalText);
    }

    [Fact]
    public void Partial_updates_replace_previous_partial_for_same_turn()
    {
        var a = new TranscriptAssembler();
        a.Ingest(Turn(0, "Please", false));
        a.Ingest(Turn(0, "Please send", false));
        a.Ingest(Turn(0, "Please send the report.", true, true));

        Assert.Equal("Please send the report.", a.FinalText);
        Assert.False(a.HasOpenTurn);
    }
}
