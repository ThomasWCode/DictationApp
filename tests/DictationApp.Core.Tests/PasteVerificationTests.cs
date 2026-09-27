using DictationApp.Core.Insertion;

namespace DictationApp.Core.Tests;

public class PasteVerificationTests
{
    [Theory]
    [InlineData("See you on Tuesday.", "Hi. ", "day.", true)]
    [InlineData("See you on Tuesday.", "Hi. ", "ay.￼", true)]   // a rich editor's paragraph object after the text
    [InlineData("First item\n- second", "", "cond", true)]      // line breaks turned into paragraphs
    [InlineData("Ok", "", "Ok", true)]
    [InlineData("Ok", "Ok", "OkOk", true)]
    [InlineData("See you on Tuesday.", "Hi. ", "abc", false)]   // the caret is somewhere else
    [InlineData("See you on Tuesday.", "", "", false)]          // an empty field: nothing arrived
    [InlineData("See you on Tuesday.", "Hi. ", null, false)]    // the control does not say, or focus moved
    [InlineData("See you on Tuesday.", null, "day.", false)]    // nothing to compare with
    [InlineData("That was the day.", "A good day.", "A good day.", false)] // ignored, though it ends the same way
    [InlineData("Ok", "Ok", "Ok", false)]
    public void A_paste_is_confirmed_only_by_a_change_that_ends_with_the_pasted_text(string pasted, string? before, string? after, bool confirmed)
    {
        Assert.Equal(confirmed, PasteVerification.LooksPasted(pasted, before, after));
    }
}
