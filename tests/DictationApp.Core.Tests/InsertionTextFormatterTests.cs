using DictationApp.Core.Insertion;
using Microsoft.Extensions.Time.Testing;

namespace DictationApp.Core.Tests;

public class InsertionTextFormatterTests
{
    [Theory]
    [InlineData("hello", null, "hello")]
    [InlineData("hello", "d", " hello")]
    [InlineData("hello", " ", "hello")]
    [InlineData("hello", ".", " Hello")]
    [InlineData("hello", "\n", "Hello")]
    [InlineData("hello", "?", " Hello")]
    [InlineData(", and more", "d", ", and more")]
    [InlineData("hello", "(", "hello")]
    [InlineData("Hello", ".", " Hello")]
    [InlineData("", "x", "")]
    public void Pure_apply_rules(string text, string? previousTail, string expected)
    {
        Assert.Equal(expected, InsertionTextFormatter.Apply(text, previousTail));
    }

    [Fact]
    public void Tracks_tail_per_window_and_forgets_after_memory_window()
    {
        var clock = new FakeTimeProvider();
        var f = new InsertionTextFormatter(clock) { Memory = TimeSpan.FromMinutes(10) };

        Assert.Equal("first sentence.", f.Format("first sentence.", 1));
        Assert.Equal(" Second one", f.Format("second one", 1));
        Assert.Equal("other window", f.Format("other window", 2));
        Assert.Equal(" continues", f.Format("continues", 1));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("fresh start", f.Format("fresh start", 1));
    }

    [Fact]
    public void The_text_before_the_caret_wins_over_memory()
    {
        var f = new InsertionTextFormatter();
        f.Format("First message.", 1);

        // The message was sent, so the chat box is empty: no leading space, although we inserted there before.
        Assert.Equal("Second message.", f.Format("Second message.", 1, textBeforeCaret: ""));
        Assert.Equal(" Third", f.Format("third", 1, textBeforeCaret: "one."));
        Assert.Equal(" more", f.Format("more", 1, textBeforeCaret: "word"));
        Assert.Equal("next", f.Format("next", 1, textBeforeCaret: "end "));
    }

    [Theory]
    [InlineData("\uFFFC", "hello")] // an empty paragraph in a rich editor (the Claude app's prompt box)
    [InlineData("\u200B", "hello")] // WhatsApp's empty fields
    [InlineData("\n\uFFFC", "Hello")] // an empty line after earlier text
    public void Invisible_stand_ins_before_the_caret_are_skipped(string textBeforeCaret, string expected)
    {
        var f = new InsertionTextFormatter();
        f.Format("Earlier text", 1);

        Assert.Equal(expected, f.Format("hello", 1, textBeforeCaret));
    }

    [Fact]
    public void Unreadable_controls_fall_back_to_memory()
    {
        var f = new InsertionTextFormatter();
        f.Format("First sentence.", 1);

        Assert.Equal(" Second", f.Format("second", 1, textBeforeCaret: null));
    }

    [Fact]
    public void Reset_clears_memory()
    {
        var f = new InsertionTextFormatter();
        f.Format("abc", 5);
        f.Reset(5);
        Assert.Equal("def", f.Format("def", 5));
    }
}
