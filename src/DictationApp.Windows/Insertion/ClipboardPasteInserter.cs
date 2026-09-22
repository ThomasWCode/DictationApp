using DictationApp.Core.Abstractions;
using DictationApp.Core.Settings;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Insertion;

/// <summary>
/// Inserts text by pasting: snapshot clipboard → set our text → release held modifiers → Ctrl+V (or
/// Ctrl+Shift+V) → wait for the target to read the clipboard → restore the snapshot unless somebody else
/// changed the clipboard meanwhile. We never type character by character: paste is atomic, respects the
/// target's own undo, and handles thousands of characters in one step.
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
    private readonly ILogger<ClipboardPasteInserter> _logger;

    public ClipboardPasteInserter(ClipboardService clipboard, ILogger<ClipboardPasteInserter> logger)
    {
        _clipboard = clipboard;
        _logger = logger;
    }

    public async Task<InsertionResult> InsertAsync(string text, ForegroundContext target, PasteMode pasteMode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new InsertionResult(InsertionOutcome.Inserted);
        }

        ClipboardSnapshot? snapshot = null;
        try
        {
            snapshot = await _clipboard.SnapshotAsync(ct).ConfigureAwait(false);
            await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
            var seqAfterSet = NativeMethods.GetClipboardSequenceNumber();

            ReleaseHeldModifiers();
            EnsureForeground(target);
            await Task.Delay(30, ct).ConfigureAwait(false);
            SendPaste(pasteMode);

            await Task.Delay(PasteSettle, ct).ConfigureAwait(false);
            var stableSeq = await WaitForStableSequenceAsync(ct).ConfigureAwait(false);
            if (stableSeq != seqAfterSet)
            {
                _logger.LogInformation("Clipboard changed by another app after paste; leaving it alone");
                return new InsertionResult(InsertionOutcome.Inserted);
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
            _logger.LogError(ex, "Paste insertion failed");
            return new InsertionResult(InsertionOutcome.Failed, ex.Message);
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

    public static void SendPaste(PasteMode mode)
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
        NativeMethods.SendKeys(inputs);
    }

    private void EnsureForeground(ForegroundContext target)
    {
        if (target.WindowHandle == 0 || !NativeMethods.IsWindow(target.WindowHandle))
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() == target.WindowHandle)
        {
            return;
        }

        // The Flow bar or a toast may have been clicked; bring the original window back before pasting.
        var foreground = NativeMethods.GetForegroundWindow();
        var ourThread = NativeMethods.GetCurrentThreadId();
        var fgThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = fgThread != 0 && fgThread != ourThread && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
        try
        {
            if (NativeMethods.IsIconic(target.WindowHandle))
            {
                NativeMethods.ShowWindow(target.WindowHandle, NativeMethods.SW_RESTORE);
            }

            if (!NativeMethods.SetForegroundWindow(target.WindowHandle))
            {
                _logger.LogDebug("SetForegroundWindow refused for {Process}", target.ProcessName);
            }
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }
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
