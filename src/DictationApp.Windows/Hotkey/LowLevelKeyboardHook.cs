using System.Runtime.InteropServices;
using DictationApp.Windows.Native;
using Microsoft.Extensions.Logging;

namespace DictationApp.Windows.Hotkey;

/// <summary>
/// WH_KEYBOARD_LL hook on a dedicated thread with its own message pump. The callback does the minimum:
/// decode the event, ask <see cref="Filter"/> whether to swallow it, and return. Windows silently removes a
/// hook whose callback is slow, so the hook is also re-installed periodically as a watchdog.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const uint WM_REINSTALL = NativeMethods.WM_APP + 1;
    private static readonly TimeSpan ReinstallInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<LowLevelKeyboardHook> _logger;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep the delegate alive for the hook's lifetime
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Timer? _watchdog;
    private volatile bool _running;

    public LowLevelKeyboardHook(ILogger<LowLevelKeyboardHook> logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool Injected, uint Time);

    /// <summary>Called synchronously on the hook thread. Return true to swallow the event. Must be fast.</summary>
    public Func<KeyEvent, bool>? Filter { get; set; }

    public bool IsInstalled => _hook != 0;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Pump(ready)) { IsBackground = true, Name = "KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        _watchdog = new Timer(_ => Post(WM_REINSTALL), null, ReinstallInterval, ReinstallInterval);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        Post(NativeMethods.WM_QUIT);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Post(uint message)
    {
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessageW(_threadId, message, 0, 0);
        }
    }

    private void Pump(ManualResetEventSlim ready)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        Install();
        ready.Set();
        try
        {
            while (_running && NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_REINSTALL)
                {
                    Uninstall();
                    Install();
                    continue;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            Uninstall();
            _threadId = 0;
        }
    }

    private void Install()
    {
        var module = NativeMethods.GetModuleHandleW(null);
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogError("SetWindowsHookEx failed with Win32 error {Error}", error);
        }
        else
        {
            _logger.LogDebug("Keyboard hook installed");
        }
    }

    private void Uninstall()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var msg = (int)wParam;
                var isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var injected = (data.flags & (NativeMethods.LLKHF_INJECTED | NativeMethods.LLKHF_LOWER_IL_INJECTED)) != 0;
                var ev = new KeyEvent((int)data.vkCode, isDown, injected, data.time);
                if (Filter?.Invoke(ev) == true)
                {
                    return 1;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the hook chain.
                _logger.LogError(ex, "Keyboard hook filter threw");
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
