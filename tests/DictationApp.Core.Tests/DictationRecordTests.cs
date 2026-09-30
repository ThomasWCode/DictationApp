using DictationApp.Core.History;

namespace DictationApp.Core.Tests;

/// <summary>What History copies: never the joining space a dictation got after existing text (as on Android).</summary>
public class DictationRecordTests
{
    [Fact]
    public void Copy_leaves_out_the_joining_space_the_text_was_inserted_with()
    {
        var appended = new DictationRecord { RawTranscript = "second part", InsertedText = " Second part." };

        Assert.Equal("Second part.", appended.CopyText);
    }

    [Fact]
    public void Copy_falls_back_to_the_raw_transcript_when_nothing_was_inserted()
    {
        Assert.Equal("heard words", new DictationRecord { RawTranscript = "heard words" }.CopyText);
        Assert.Equal(string.Empty, new DictationRecord().CopyText);
    }
}
