using System.Runtime.InteropServices;

namespace ClipStack.App.Interop;

/// <summary>
/// Brings the panel to the foreground. Windows normally refuses this for a process
/// that is not already foreground, but a hotkey press grants the receiving process
/// the right to steal focus; the thread-input fallback covers the remaining cases.
/// </summary>
internal static class WindowActivation
{
    private const uint SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd))
        {
            return;
        }

        // Fallback: temporarily share the input queue with the current foreground thread.
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        if (!AttachThreadInput(currentThread, foregroundThread, true))
        {
            return;
        }

        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
