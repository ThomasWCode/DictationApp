using DictationApp.Core.Abstractions;
using DictationApp.Core.Insertion;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Insertion;

/// <summary>
/// Inserts text by pasting: snapshot clipboard → set our text → release held modifiers → bring the target to the
/// front and check it is there → Ctrl+V (or Ctrl+Shift+V), checking Windows accepted every keystroke → wait for the
/// target to read the clipboard → read back the characters before the caret → restore the snapshot. We never type
/// character by character: paste is atomic, respects the target's own undo, and handles thousands of characters in
/// one step.
/// <para>
/// Nothing is claimed that was not checked. When the target cannot be brought to the front or the keystrokes are
/// refused, no paste is sent and the text stays on the clipboard (CopiedOnly). When the paste was sent but the
/// characters before the caret cannot be read back or do not match (an app that exposes no text, a browser page with
/// no field focused), the text is left on the clipboard as well instead of restoring the previous contents
/// (<see cref="InsertionResult.LeftOnClipboard"/>): unsure whether it landed, the user can still paste it. A restore
/// that fails after a delivered paste does not turn it into a failure.
/// </para>
/// </summary>
public sealed class ClipboardPasteInserter : ITextInserter
{
    public static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SequenceStable = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan SequenceMaxWait = TimeSpan.FromMilliseconds(600);

    private static readonly int[] ModifierKeys =
    [
        HotkeyChord.VkLControl, HotkeyChord.VkRControl, HotkeyChord.VkLShift, HotkeyChord.VkRShift,
        HotkeyChord.VkLAlt, HotkeyChord.VkRAlt, HotkeyChord.VkLWin, HotkeyChord.VkRWin,
    ];

    private readonly ClipboardService _clipboard;
    private readonly IForegroundContextProvider _foreground;
    private readonly ILogger<ClipboardPasteInserter> _logger;

    public ClipboardPasteInserter(ClipboardService clipboard, IForegroundContextProvider foreground, ILogger<ClipboardPasteInserter> logger)
    {
        _clipboard = clipboard;
        _foreground = foreground;
        _logger = logger;
    }

    public async Task<InsertionResult> InsertAsync(string text, ForegroundContext target, PasteMode pasteMode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new InsertionResult(InsertionOutcome.Inserted);
        }

        ClipboardSnapshot? snapshot;
        uint seqAfterSet;
        try
        {
            snapshot = await _clipboard.SnapshotAsync(ct).ConfigureAwait(false);
            await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
            seqAfterSet = NativeMethods.GetClipboardSequenceNumber();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paste insertion failed");
            return new InsertionResult(InsertionOutcome.Failed, ex.Message);
        }

        // From here the text is on the clipboard: every early return leaves it there for the user to paste.
        var app = string.IsNullOrEmpty(target.ProcessName) ? "the app" : target.ProcessName;
        var expected = target.WindowHandle != 0 ? target.WindowHandle : NativeMethods.GetForegroundWindow();
        if (expected == 0 || !NativeMethods.IsWindow(expected))
        {
            _logger.LogInformation("Paste target window is gone; text left on the clipboard");
            return new InsertionResult(InsertionOutcome.CopiedOnly, $"{app} was closed, text copied");
        }

        ReleaseHeldModifiers();
        if (!EnsureForeground(expected, app))
        {
            return new InsertionResult(InsertionOutcome.CopiedOnly, $"Could not bring {app} to the front, text copied");
        }

        await Task.Delay(30, ct).ConfigureAwait(false);
        if (NativeMethods.GetForegroundWindow() != expected)
        {
            _logger.LogInformation("Focus left {Process} just before the paste; text left on the clipboard", app);
            return new InsertionResult(InsertionOutcome.CopiedOnly, $"Focus left {app} before the paste, text copied");
        }

        if (!SendPaste(pasteMode))
        {
            ReleaseHeldModifiers(); // a partial Ctrl+V may have left Ctrl or Shift down
            _logger.LogInformation("Windows blocked the paste keystrokes for {Process}; text left on the clipboard", app);
            return new InsertionResult(InsertionOutcome.CopiedOnly, $"Windows blocked the paste into {app}, text copied");
        }

        try
        {
            await Task.Delay(PasteSettle, ct).ConfigureAwait(false);
            var stableSeq = await WaitForStableSequenceAsync(ct).ConfigureAwait(false);
            if (stableSeq != seqAfterSet)
            {
                _logger.LogInformation("Clipboard changed by another app after paste; leaving it alone");
                return new InsertionResult(InsertionOutcome.Inserted);
            }

            var readBack = _foreground.ReadTextBeforeCaret(target with { WindowHandle = expected });
            if (!PasteVerification.LooksPasted(text, readBack))
            {
                _logger.LogInformation("Paste into {Process} could not be confirmed ({ReadBack}); text left on the clipboard as well", app, readBack is null ? "not readable" : "text differs");
                return new InsertionResult(InsertionOutcome.Inserted, $"Pasted into {app}; also on the clipboard, as the paste could not be confirmed", LeftOnClipboard: true);
            }

            await _clipboard.RestoreAsync(snapshot, ct).ConfigureAwait(false);
            return new InsertionResult(InsertionOutcome.Inserted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The paste was delivered; only putting the previous clipboard back failed.
            _logger.LogWarning(ex, "Pasted, but the previous clipboard could not be restored");
            return new InsertionResult(InsertionOutcome.Inserted, "The previous clipboard contents could not be restored", LeftOnClipboard: true);
        }
    }

    /// <summary>The user is still lifting fingers off the chord; a held Ctrl or Win would corrupt Ctrl+V.</summary>
    public static void ReleaseHeldModifiers()
    {
        var ups = new List<NativeMethods.INPUT>();
        foreach (var vk in ModifierKeys)
        {
            if (NativeMethods.IsKeyDown(vk))
            {
                ups.Add(NativeMethods.KeyInput((ushort)vk, up: true));
            }
        }

        if (ups.Count > 0)
        {
            NativeMethods.SendKeys([.. ups]);
            Thread.Sleep(15);
        }
    }

    /// <summary>Sends the paste chord; false when Windows injected fewer keystrokes than asked (UIPI, secure desktop).</summary>
    public static bool SendPaste(PasteMode mode)
    {
        var inputs = mode == PasteMode.CtrlShiftV
            ?
            [
                NativeMethods.KeyInput(HotkeyChord.VkLControl, false),
                NativeMethods.KeyInput(HotkeyChord.VkLShift, false),
                NativeMethods.KeyInput((ushort)'V', false),
                NativeMethods.KeyInput((ushort)'V', true),
                NativeMethods.KeyInput(HotkeyChord.VkLShift, true),
                NativeMethods.KeyInput(HotkeyChord.VkLControl, true),
            ]
            : new[]
            {
                NativeMethods.KeyInput(HotkeyChord.VkLControl, false),
                NativeMethods.KeyInput((ushort)'V', false),
                NativeMethods.KeyInput((ushort)'V', true),
                NativeMethods.KeyInput(HotkeyChord.VkLControl, true),
            };
        return NativeMethods.SendKeys(inputs) == inputs.Length;
    }

    /// <summary>True when <paramref name="hwnd"/> is the foreground window afterwards (Windows may refuse to switch).</summary>
    private bool EnsureForeground(nint hwnd, string app)
    {
        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // The Flow bar or a toast may have been clicked; bring the original window back before pasting.
        var foreground = NativeMethods.GetForegroundWindow();
        var ourThread = NativeMethods.GetCurrentThreadId();
        var fgThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = fgThread != 0 && fgThread != ourThread && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }

        // SetForegroundWindow's own result is not trusted: what counts is whether the window is in front now.
        var ok = NativeMethods.GetForegroundWindow() == hwnd;
        if (!ok)
        {
            _logger.LogInformation("Windows refused to bring {Process} to the front; text left on the clipboard", app);
        }

        return ok;
    }

    private static async Task<uint> WaitForStableSequenceAsync(CancellationToken ct)
    {
        var last = NativeMethods.GetClipboardSequenceNumber();
        var stableSince = Environment.TickCount64;
        var deadline = stableSince + (long)SequenceMaxWait.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
            var now = NativeMethods.GetClipboardSequenceNumber();
            if (now != last)
            {
                last = now;
                stableSince = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - stableSince >= SequenceStable.TotalMilliseconds)
            {
                break;
            }
        }

        return last;
    }
}
