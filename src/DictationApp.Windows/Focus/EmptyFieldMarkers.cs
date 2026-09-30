namespace DictationApp.Windows.Focus;

/// <summary>
/// Recognises the empty box of a Chromium rich-text editor (Chrome, Edge, Electron apps such as the Claude desktop
/// app). Such a box draws its placeholder ("Reply to Claude") as CSS generated content, and UI Automation reports that
/// as ordinary text with the caret after it: read literally, an empty prompt box ends in a letter, so the dictation
/// that went into it got a leading space. Chromium hands out every element's HTML class list as its ClassName and the
/// editors mark an empty box there: TipTap (the Claude apps) puts <c>is-editor-empty</c> on the first block of an
/// empty editor and <c>is-empty</c> on every empty block, Quill puts <c>ql-blank</c> on the editor, and ChatGPT's box
/// marks its placeholder paragraph <c>placeholder</c>. Measured against real Chromium (see docs/decisions.md).
/// </summary>
internal static class EmptyFieldMarkers
{
    /// <summary>The <c>FrameworkId</c> UI Automation reports for Chromium content.</summary>
    public const string ChromiumFramework = "Chrome";

    /// <summary>Set on the editor or on its first block, and removed again as soon as there is any content.</summary>
    private static readonly HashSet<string> EmptyEditor = new(StringComparer.OrdinalIgnoreCase) { "is-editor-empty", "ql-blank" };

    /// <summary>
    /// Set on the block that shows the placeholder, and only while it does. On the editor itself it would prove nothing:
    /// a site may leave such a class there whatever the content.
    /// </summary>
    private static readonly HashSet<string> PlaceholderBlock = new(StringComparer.OrdinalIgnoreCase) { "placeholder" };

    /// <summary>Set on every empty block, the first or a later one.</summary>
    private static readonly HashSet<string> EmptyBlock = new(StringComparer.OrdinalIgnoreCase) { "is-empty" };

    /// <summary>
    /// What to report as the text before the caret instead of what UI Automation says: "" for the start of an empty
    /// field, a line break for the start of an empty line in a field that has text, or null when neither class list
    /// says the caret is in an empty block (the caller reads the text as usual).
    /// </summary>
    /// <param name="editorClass">ClassName of the focused element (the editor).</param>
    /// <param name="blockClass">ClassName of the element enclosing the caret (the paragraph).</param>
    public static string? TextBeforeCaret(string? editorClass, string? blockClass)
    {
        if (HasAny(editorClass, EmptyEditor) || HasAny(blockClass, EmptyEditor) || HasAny(blockClass, PlaceholderBlock))
        {
            return string.Empty;
        }

        return HasAny(blockClass, EmptyBlock) ? "\n" : null;
    }

    private static bool HasAny(string? classList, HashSet<string> wanted)
    {
        if (string.IsNullOrEmpty(classList))
        {
            return false;
        }

        foreach (var token in classList.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (wanted.Contains(token))
            {
                return true;
            }
        }

        return false;
    }
}
