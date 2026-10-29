using System.Runtime.InteropServices;

namespace ClipStack.Core.Interop;

public static class WindowInfo
{
    public static IntPtr GetForeground() => NativeMethods.GetForegroundWindow();

    /// <summary>Process name (without extension) and window title of a window handle.</summary>
    public static (string? App, string? Title) Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return (null, null);
        }

        return (GetProcessName(hwnd), GetTitle(hwnd));
    }

    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (!NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                return null;
            }

            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size));
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public static string? GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : null;
    }

    /// <summary>
    /// True for the taskbar, its notification area and the desktop. Those windows are what the
    /// foreground turns into when the panel is opened from the tray: they cannot receive a paste,
    /// so the caller should keep the previous real target instead.
    /// </summary>
    public static bool IsShellSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var buffer = new char[256];
        var copied = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Length);
        if (copied <= 0)
        {
            return false;
        }

        var className = new string(buffer, 0, copied);
        return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "TrayNotifyWnd" or "NotifyIconOverflowWindow"
            or "Progman" or "WorkerW";
    }

    /// <summary>
    /// False when the process cannot be opened at all, which on Windows normally means
    /// it runs at a higher integrity level (UIPI will also block our input).
    /// </summary>
    public static bool CanQueryProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseHandle(process);
        return true;
    }
}
