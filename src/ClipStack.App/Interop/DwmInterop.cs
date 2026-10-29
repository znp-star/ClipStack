using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace ClipStack.App.Interop;

/// <summary>DWM window attributes used by the panel chrome.</summary>
internal static class DwmInterop
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 rounded corners; no-op on older builds.</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        var preference = DwmcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    public static void ApplyDarkFrame(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }

    public static IntPtr GetHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();
}
