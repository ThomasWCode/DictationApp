using DictationApp.Core.Insertion;

namespace DictationApp.Core.Tests;

public class PasteVerificationTests
{
    [Theory]
    [InlineData("See you on Tuesday.", "day.", true)]
    [InlineData("See you on Tuesday.", "ay.￼", true)]   // a rich editor's paragraph object after the text
    [InlineData("First item\n- second", "cond", true)]      // line breaks turned into paragraphs
    [InlineData("Ok", "Ok", true)]
    [InlineData("See you on Tuesday.", "abc", false)]      // the caret is somewhere else
    [InlineData("See you on Tuesday.", "", false)]         // an empty field: nothing arrived
    [InlineData("See you on Tuesday.", null, false)]       // the control does not say, or focus moved
    public void A_paste_is_confirmed_only_by_the_text_before_the_caret(string pasted, string? textBeforeCaret, bool confirmed)
    {
        Assert.Equal(confirmed, PasteVerification.LooksPasted(pasted, textBeforeCaret));
    }
}
