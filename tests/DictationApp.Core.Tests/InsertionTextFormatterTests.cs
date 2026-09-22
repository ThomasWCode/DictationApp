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
    public void Reset_clears_memory()
    {
        var f = new InsertionTextFormatter();
        f.Format("abc", 5);
        f.Reset(5);
        Assert.Equal("def", f.Format("def", 5));
    }
}
