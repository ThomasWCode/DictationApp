using System.Runtime.InteropServices;
using DictationApp.Windows.Native;

namespace DictationApp.Windows.Focus;

/// <summary>
/// Detects whether a target process runs elevated relative to us. UIPI silently drops SendInput into
/// higher-integrity windows, so an elevated target gets the clipboard-only path instead.
/// </summary>
public static class ElevationProbe
{
    private static readonly Lazy<bool> SelfElevated = new(() => IsProcessElevated(NativeMethods.GetCurrentProcess(), out _) == true);

    public static bool IsSelfElevated => SelfElevated.Value;

    /// <summary>True when the target is elevated and we are not.</summary>
    public static bool IsTargetElevatedAboveUs(int processId)
    {
        if (processId <= 0 || IsSelfElevated)
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == 0)
        {
            // Cannot even open it with limited rights: protected or very elevated. Treat as elevated.
            return Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;
        }

        try
        {
            var elevated = IsProcessElevated(handle, out var accessDenied);
            // Opening the token of a same-integrity process succeeds; a denial is itself the signal.
            return elevated ?? accessDenied;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool? IsProcessElevated(nint processHandle, out bool accessDenied)
    {
        accessDenied = false;
        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out var token))
        {
            accessDenied = Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;
            return null;
        }

        try
        {
            var elevation = new NativeMethods.TOKEN_ELEVATION();
            var size = (uint)Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, ref elevation, size, out _))
            {
                return null;
            }

            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
