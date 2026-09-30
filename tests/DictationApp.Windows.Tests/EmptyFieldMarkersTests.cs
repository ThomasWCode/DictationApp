using DictationApp.Windows.Focus;

namespace DictationApp.Windows.Tests;

/// <summary>
/// Class lists as Chromium reports them for the editors that matter, measured against real Chrome: an empty box shows its
/// placeholder, which UI Automation reads as text before the caret, so the class lists are what says the box is empty.
/// </summary>
public class EmptyFieldMarkersTests
{
    [Theory]
    [InlineData("tiptap ProseMirror ProseMirror-focused", "is-empty is-editor-empty")] // the Claude apps' empty prompt box
    [InlineData("tiptap ProseMirror", "is-editor-empty")]
    [InlineData("tiptap ProseMirror", "IS-EDITOR-EMPTY")] // class names are not case sensitive here
    [InlineData("ql-editor ql-blank", "")] // Quill marks the editor itself
    [InlineData("ProseMirror", "placeholder")] // ChatGPT's placeholder paragraph
    public void An_empty_box_is_the_start_of_the_field(string editorClass, string blockClass)
    {
        Assert.Equal(string.Empty, EmptyFieldMarkers.TextBeforeCaret(editorClass, blockClass));
    }

    [Fact]
    public void An_empty_block_in_a_box_that_has_text_is_the_start_of_a_line()
    {
        Assert.Equal("\n", EmptyFieldMarkers.TextBeforeCaret("tiptap ProseMirror ProseMirror-focused", "is-empty"));
    }

    [Fact]
    public void The_box_being_empty_wins_over_the_block_being_empty()
    {
        Assert.Equal(string.Empty, EmptyFieldMarkers.TextBeforeCaret("tiptap", "is-empty is-editor-empty"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("tiptap ProseMirror ProseMirror-focused", "")] // a box with text: its block carries no marker
    [InlineData("tiptap ProseMirror", null)]
    [InlineData("Edit", null)] // a Win32 control's window class
    [InlineData("editor placeholder", "")] // a placeholder class on the editor proves nothing: sites leave it there
    [InlineData("tiptap", "has-placeholder")] // whole class names only
    [InlineData("tiptap", "is-empty-ish not-is-empty")]
    public void Anything_else_is_read_as_text(string? editorClass, string? blockClass)
    {
        Assert.Null(EmptyFieldMarkers.TextBeforeCaret(editorClass, blockClass));
    }
}
