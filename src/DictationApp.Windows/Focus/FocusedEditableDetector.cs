using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Focus;

/// <summary>
/// Decides whether the focused control can take pasted text. Order: UI Automation focused element →
/// caret presence via GetGUIThreadInfo → per-process allowlist → default editable. "Unknown" is treated
/// as editable because a paste into a non-editable control is a harmless no-op and the text stays on the
/// clipboard anyway.
/// </summary>
public sealed class FocusedEditableDetector : IDisposable
{
    public static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(350);

    /// <summary>Time allowed for reading the text before the caret; about 10 ms in Chromium and Win32 edits.</summary>
    public static readonly TimeSpan CaretBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>Characters read before the caret: enough to see past an invisible stand-in or two.</summary>
    private const int CaretContextLength = 4;

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

    public (bool Editable, string Reason) Detect(nint foregroundHwnd, uint threadId, string processName)
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

        // 4. The desktop / Explorer shell with nothing focused is the classic "no text box" case.
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) && foregroundHwnd == 0)
        {
            return (false, "desktop");
        }

        return (true, "default");
    }

    /// <summary>
    /// The last few characters before the caret of the focused control, from the Text pattern (the caret or
    /// selection start). The caller checks that the target window has focus: the control itself may belong to another
    /// process (WebView2 content runs in msedgewebview2). "" at the start of a field,
    /// or when a control without a caret position (Value pattern only, a Chromium contenteditable reporting no caret)
    /// is empty. Null otherwise: password fields, text whose caret position is unknown, controls with neither pattern,
    /// and when UI Automation does not answer in time.
    /// </summary>
    public string? ReadTextBeforeCaret() => RunWithBudget(() => ProbeTextBeforeCaret() is { } text ? new CaretText(text) : (CaretText?)null, CaretBudget)?.Text;

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
    }

    private string? ProbeTextBeforeCaret()
    {
        try
        {
            _automation ??= new UIA3Automation();
            var focused = _automation.FocusedElement();
            if (focused is null || focused.Properties.IsPassword.ValueOrDefault)
            {
                return null;
            }

            if (focused.Patterns.Text.IsSupported)
            {
                var pattern = focused.Patterns.Text.Pattern;
                var selection = pattern.GetSelection();
                if (selection.Length > 0)
                {
                    var before = selection[0].Clone();
                    before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -CaretContextLength);
                    before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
                    return before.GetText(CaretContextLength) ?? string.Empty;
                }

                // No caret reported (an empty contenteditable in Chromium): an empty document is the start of the field.
                if (IsBlank(pattern.DocumentRange.GetText(CaretContextLength)))
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the text before the caret failed");
            return null;
        }
    }

    private (bool, string)? ProbeUia()
    {
        try
        {
            _automation ??= new UIA3Automation();
            var focused = _automation.FocusedElement();
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
            _logger.LogDebug(ex, "UIA focus probe failed");
            try
            {
                _automation?.Dispose();
            }
            catch (Exception)
            {
            }

            _automation = null;
            return null;
        }
    }

    /// <summary>Empty apart from whitespace and invisible stand-ins such as a rich editor's empty paragraph.</summary>
    private static bool IsBlank(string? text) => (text ?? string.Empty).Trim().Trim('\uFFFC', '\u200B', '\uFEFF').Length == 0;

    private readonly record struct CaretText(string Text);

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
