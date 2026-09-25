using System.Diagnostics;
using DictationApp.Core.Abstractions;
using DictationApp.Windows.Native;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Focus;

/// <summary>
/// Captures the foreground window, its process, title, elevation, editable state and (for browsers) the
/// address-bar URL, in that order, on chord-down before anything else moves.
/// </summary>
public sealed class ForegroundContextProvider : IForegroundContextProvider, IDisposable
{
    public static readonly TimeSpan UrlBudget = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, string[]> BrowserAddressBarNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ["Address and search bar"],
        ["msedge"] = ["Address and search bar"],
        ["brave"] = ["Address and search bar"],
        ["vivaldi"] = ["Address and search bar", "Search or enter an address"],
        ["opera"] = ["Address field"],
        ["firefox"] = ["Search with Google or enter address", "Search or enter address", "Enter address"],
    };

    private readonly FocusedEditableDetector _editable;
    private readonly ILogger<ForegroundContextProvider> _logger;
    private UIA3Automation? _automation;

    public ForegroundContextProvider(FocusedEditableDetector editable, ILogger<ForegroundContextProvider> logger)
    {
        _editable = editable;
        _logger = logger;
    }

    public ForegroundContext Capture()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return ForegroundContext.Unknown with { ProcessName = "explorer", IsEditable = false, EditableReason = "no-foreground" };
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var processName = ProcessName((int)pid);
        var title = NativeMethods.GetWindowText(hwnd);
        var elevated = ElevationProbe.IsTargetElevatedAboveUs((int)pid);
        var (editable, reason) = _editable.Detect(hwnd, threadId, processName);
        string? url = null;
        if (BrowserAddressBarNames.TryGetValue(processName, out var names))
        {
            url = ReadAddressBar(hwnd, names);
        }

        return new ForegroundContext(hwnd, (int)pid, processName, title, url, editable, elevated, reason);
    }

    public string? ReadTextBeforeCaret(ForegroundContext target)
    {
        // Only while the window the text will be pasted into still has focus, before and after the read.
        if (target.WindowHandle == 0 || NativeMethods.GetForegroundWindow() != target.WindowHandle)
        {
            return null;
        }

        var text = _editable.ReadTextBeforeCaret();
        return NativeMethods.GetForegroundWindow() == target.WindowHandle ? text : null;
    }

    public void Dispose()
    {
        _automation?.Dispose();
        _automation = null;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private string? ReadAddressBar(nint hwnd, string[] names)
    {
        var task = Task.Run(() =>
        {
            try
            {
                _automation ??= new UIA3Automation();
                var window = _automation.FromHandle(hwnd);
                var cf = new ConditionFactory(_automation.PropertyLibrary);
                foreach (var name in names)
                {
                    var edit = window.FindFirstDescendant(cf.ByControlType(ControlType.Edit).And(cf.ByName(name)));
                    if (edit is not null && edit.Patterns.Value.IsSupported)
                    {
                        var value = edit.Patterns.Value.Pattern.Value.ValueOrDefault;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value.Trim();
                        }
                    }
                }

                // Fallback: first Edit descendant whose value looks like a URL.
                foreach (var edit in window.FindAllDescendants(cf.ByControlType(ControlType.Edit)).Take(8))
                {
                    if (!edit.Patterns.Value.IsSupported)
                    {
                        continue;
                    }

                    var value = edit.Patterns.Value.Pattern.Value.ValueOrDefault;
                    if (!string.IsNullOrWhiteSpace(value) && (value.Contains("://", StringComparison.Ordinal) || value.Contains('.', StringComparison.Ordinal)) && !value.Contains(' ', StringComparison.Ordinal))
                    {
                        return value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Address bar read failed");
                _automation?.Dispose();
                _automation = null;
            }

            return null;
        });

        try
        {
            return task.Wait(UrlBudget) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }
}
