using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Focus;

/// <summary>
/// Decides whether the focused control can take pasted text. Order: UI Automation focused element →
/// caret presence via GetGUIThreadInfo → per-process allowlist → default editable. "Unknown" is treated
/// as editable because a paste into a non-editable control is a harmless no-op, and a paste the inserter cannot
/// confirm afterwards (by reading the text before the caret) leaves the text on the clipboard as well.
/// </summary>
public sealed class FocusedEditableDetector : IDisposable
{
    public static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(350);

    /// <summary>Time allowed for reading the text before the caret; about 10 ms in Chromium and Win32 edits.</summary>
    public static readonly TimeSpan CaretBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>Characters read before the caret: enough to see past an invisible stand-in or two.</summary>
    private const int CaretContextLength = 4;

    /// <summary>Characters of the caret's line read to decide whether anything is on it.</summary>
    private const int LineProbeLength = 256;

    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "Code - Insiders", "Teams", "ms-teams", "slack", "chrome", "msedge", "firefox",
        "brave", "opera", "vivaldi", "notepad", "notepad++", "WINWORD", "OUTLOOK", "olk", "WhatsApp", "Discord",
        "powershell", "pwsh", "cmd", "conhost", "devenv", "rider64", "idea64", "obsidian", "Notion", "Signal",
        "Telegram", "wordpad", "EXCEL", "POWERPNT", "ONENOTE", "sublime_text", "Cursor", "Windsurf",
    };

    private static readonly HashSet<ControlType> EditableTypes = [ControlType.Edit, ControlType.Document, ControlType.ComboBox];

    private static readonly HashSet<ControlType> NonEditableTypes =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.MenuItem, ControlType.Menu,
        ControlType.MenuBar, ControlType.Hyperlink, ControlType.Image, ControlType.TabItem, ControlType.Slider,
        ControlType.ListItem, ControlType.TreeItem, ControlType.ScrollBar, ControlType.ProgressBar, ControlType.Separator,
        ControlType.SplitButton, ControlType.ToolBar, ControlType.Tab, ControlType.Tree, ControlType.List,
        ControlType.DataGrid, ControlType.Header, ControlType.HeaderItem, ControlType.Spinner, ControlType.StatusBar,
        ControlType.ToolTip, ControlType.TitleBar, ControlType.Thumb,
    ];

    private readonly ILogger<FocusedEditableDetector> _logger;
    private UIA3Automation? _automation;

    public FocusedEditableDetector(ILogger<FocusedEditableDetector> logger)
    {
        _logger = logger;
    }

    public (bool Editable, string Reason) Detect(uint threadId, string processName)
    {
        // 1. UI Automation, time-boxed: some apps stall UIA calls for seconds.
        var uia = RunWithBudget(() => ProbeUia(), UiaBudget);
        if (uia is { } verdict)
        {
            return verdict;
        }

        // 2. A caret in the foreground thread is a strong signal for classic Win32 edit controls.
        var info = new NativeMethods.GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.hwndCaret != 0)
        {
            return (true, "caret");
        }

        // 3. Apps we know host editable surfaces but expose little to UIA.
        if (Allowlist.Contains(processName))
        {
            return (true, "allowlist");
        }

        return (true, "default");
    }

    /// <summary>
    /// The last few characters before the caret of the focused control, from the Text pattern (the caret or
    /// selection start), never from before the start of the control. The caller checks that the target window has
    /// focus: the control itself may belong to another process (WebView2 content runs in msedgewebview2). "" at the
    /// start of a field, or when a control without a caret position (Value pattern only, a Chromium contenteditable
    /// reporting no caret) is empty. In a Chromium editor, "" or a line break when the caret is in an empty box or on an
    /// empty line, whatever placeholder it shows (a placeholder is not text before the caret; see
    /// <see cref="EmptyFieldMarkers"/>). Null otherwise: password fields, text whose caret position is unknown, controls
    /// with neither pattern, and when UI Automation does not answer in time.
    /// </summary>
    public string? ReadTextBeforeCaret()
    {
        var task = Task.Run(() => ProbeTextBeforeCaret());
        try
        {
            if (task.Wait(CaretBudget))
            {
                return task.Result;
            }

            _logger.LogDebug("Reading the text before the caret took longer than {Budget} ms", CaretBudget.TotalMilliseconds);
            return null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
    }

    private string? ProbeTextBeforeCaret()
    {
        try
        {
            var focused = Automation().FocusedElement();
            return focused is null ? null : TextBeforeCaretOf(focused);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the text before the caret failed");
            return null;
        }
    }

    /// <summary>The read itself, on any element: the focused one in use, a chosen one in tests and probes.</summary>
    internal static string? TextBeforeCaretOf(AutomationElement focused)
    {
        if (focused.Properties.IsPassword.ValueOrDefault)
        {
            return null;
        }

        // Chromium (Chrome, Edge, Electron apps such as the Claude app) needs checks of its own; see EmptyFieldMarkers.
        var chromium = focused.Properties.FrameworkId.ValueOrDefault == EmptyFieldMarkers.ChromiumFramework;

        if (focused.Patterns.Text.IsSupported)
        {
            var pattern = focused.Patterns.Text.Pattern;
            var selection = pattern.GetSelection();
            if (selection.Length > 0)
            {
                // A Chromium editor's placeholder reads as text before the caret, but the line it sits on is empty.
                if (chromium && EmptyLineText(focused, pattern, selection[0]) is { } empty)
                {
                    return empty;
                }

                var before = selection[0].Clone();
                before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -CaretContextLength);
                before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);

                // Never past the start of the control: Chromium lets the range run back into the page before the field,
                // so a caret at the start of a field would read the end of whatever text comes before it on the page.
                var document = pattern.DocumentRange;
                if (before.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) < 0)
                {
                    before.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start);
                }

                return before.GetText(CaretContextLength) ?? string.Empty;
            }

            // No caret reported (an empty contenteditable in Chromium): an empty document is the start of the field, and so
            // is an editor that marks itself empty although its placeholder fills the document.
            if (IsBlank(pattern.DocumentRange.GetText(CaretContextLength)) || (chromium && EditorMarkedEmpty(focused)))
            {
                return string.Empty;
            }
        }

        // The Value pattern gives no caret position: only an empty field says what comes before the insertion.
        if (focused.Patterns.Value.IsSupported && IsBlank(focused.Patterns.Value.Pattern.Value.ValueOrDefault))
        {
            return string.Empty;
        }

        return null;
    }

    /// <summary>
    /// In a Chromium editor: "" (the start of the field) or a line break (the start of a line) when the caret is on a line
    /// with nothing on it, else null (read the text as usual). Two signs, in order: the classes an editor puts on an empty
    /// box or block (see <see cref="EmptyFieldMarkers"/>), and a caret line with no text on it, which covers editors
    /// without such classes (a placeholder drawn by CSS is not part of the line). A placeholder that is an element on the
    /// line, in an editor without such classes, cannot be told from real content there (a mention at the end of a
    /// paragraph looks the same), so it reads as text. A failed check leaves the ordinary read alone rather than losing it.
    /// </summary>
    private static string? EmptyLineText(AutomationElement focused, ITextPattern pattern, ITextRange caret)
    {
        try
        {
            // The element enclosing the caret can be the field itself (a textarea, text directly in an editor): its classes
            // are the editor's, and block-only markers such as "placeholder" prove nothing there.
            var block = caret.GetEnclosingElement();
            var blockClass = block is not null && !block.Equals(focused) ? block.Properties.ClassName.ValueOrDefault : null;
            var marked = EmptyFieldMarkers.TextBeforeCaret(focused.Properties.ClassName.ValueOrDefault, blockClass);
            if (marked is not null)
            {
                return marked;
            }

            var line = caret.Clone();
            line.ExpandToEnclosingUnit(TextUnit.Line);
            if (IsBlank(line.GetText(LineProbeLength)))
            {
                return line.CompareEndpoints(TextPatternRangeEndpoint.Start, pattern.DocumentRange, TextPatternRangeEndpoint.Start) <= 0 ? string.Empty : "\n";
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A Chromium editor that reports no caret but marks itself empty: Quill on the editor, TipTap on its first block
    /// (<c>is-editor-empty</c>, set only while the whole editor is empty). The first block is looked up in the raw view:
    /// Chromium leaves paragraphs out of the ordinary (control) view, whose first child is the placeholder's text.
    /// </summary>
    private static bool EditorMarkedEmpty(AutomationElement focused)
    {
        try
        {
            if (EmptyFieldMarkers.TextBeforeCaret(focused.Properties.ClassName.ValueOrDefault, null) == string.Empty)
            {
                return true;
            }

            var first = focused.Automation.TreeWalkerFactory.GetRawViewWalker().GetFirstChild(focused);
            return first is not null && EmptyFieldMarkers.TextBeforeCaret(null, first.Properties.ClassName.ValueOrDefault) == string.Empty;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private (bool, string)? ProbeUia()
    {
        try
        {
            var focused = Automation().FocusedElement();
            if (focused is null)
            {
                return null;
            }

            var type = focused.Properties.ControlType.ValueOrDefault;
            if (EditableTypes.Contains(type))
            {
                return (true, "uia:" + type);
            }

            if (focused.Patterns.Value.IsSupported)
            {
                var pattern = focused.Patterns.Value.Pattern;
                if (!pattern.IsReadOnly.ValueOrDefault)
                {
                    return (true, "uia:value");
                }
            }

            if (focused.Patterns.Text.IsSupported)
            {
                return (true, "uia:text");
            }

            if (NonEditableTypes.Contains(type))
            {
                return (false, "uia:" + type);
            }

            // Pane/Custom/Window/Group: undecided; fall through to the other probes.
            return null;
        }
        catch (Exception ex)
        {
            // Not disposed here: a probe that timed out earlier may still be using the shared automation object.
            _logger.LogDebug(ex, "UIA focus probe failed");
            return null;
        }
    }

    /// <summary>
    /// The shared automation object, created on first use. Its UI Automation timeouts end a call into an app that hangs
    /// after 2 s, so a probe that the caller stopped waiting for does not keep running (and piling up) for long.
    /// </summary>
    private UIA3Automation Automation()
    {
        if (_automation is { } existing)
        {
            return existing;
        }

        var automation = new UIA3Automation();
        try
        {
            automation.ConnectionTimeout = TimeSpan.FromSeconds(2);
            automation.TransactionTimeout = TimeSpan.FromSeconds(2);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UI Automation timeouts not available");
        }

        return _automation = automation;
    }

    /// <summary>Empty apart from whitespace and invisible stand-ins such as a rich editor's empty paragraph.</summary>
    private static bool IsBlank(string? text) => (text ?? string.Empty).Trim().Trim('\uFFFC', '\u200B', '\uFEFF').Length == 0;

    private static T? RunWithBudget<T>(Func<T?> probe, TimeSpan budget) where T : struct
    {
        var task = Task.Run(probe);
        try
        {
            return task.Wait(budget) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }
}
