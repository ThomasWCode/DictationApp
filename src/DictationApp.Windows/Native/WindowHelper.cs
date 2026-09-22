using System.Runtime.InteropServices;

namespace DictationApp.Windows.Native;

/// <summary>Public helpers for the WPF layer: no-activate overlay styling and monitor work areas.</summary>
public static class WindowHelper
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>Makes a window never take keyboard focus and hides it from Alt+Tab.</summary>
    public static void MakeNoActivateToolWindow(nint hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)style);
    }

    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static bool IsWindow(nint hwnd) => NativeMethods.IsWindow(hwnd);

    public static bool SetForegroundWindow(nint hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    /// <summary>Work area (excluding taskbar) of the monitor nearest to <paramref name="hwnd"/>, in physical pixels.</summary>
    public static (int Left, int Top, int Right, int Bottom) GetWorkArea(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor != 0 && GetMonitorInfoW(monitor, ref info))
        {
            return (info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        return (0, 0, 1920, 1040);
    }

    /// <summary>DPI scale of the monitor nearest to <paramref name="hwnd"/> (1.0 = 96 dpi).</summary>
    public static double GetDpiScale(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
