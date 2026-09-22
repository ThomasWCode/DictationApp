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

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
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
