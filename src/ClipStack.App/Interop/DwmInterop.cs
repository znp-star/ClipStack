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
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    /// <summary>Mica needs Windows 11 22H2 or newer.</summary>
    private const int MicaMinBuild = 22621;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

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

    /// <summary>
    /// Applies the Mica system backdrop. Returns false when the system is too old, in which
    /// case the caller must paint an opaque background instead of relying on DWM.
    /// </summary>
    public static bool TryApplyMica(IntPtr hwnd, bool dark)
    {
        if (Environment.OSVersion.Version.Build < MicaMinBuild)
        {
            return false;
        }

        ApplyDarkFrame(hwnd, dark);

        var backdrop = DwmsbtMainWindow;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }

    /// <summary>The user's accent colour from DWM (system highlight colour), as #AARRGGBB.</summary>
    public static uint GetAccentColor()
    {
        return DwmGetColorizationColor(out var color, out _) == 0 && color != 0
            ? color
            : 0xFF0067C0;
    }

    public static IntPtr GetHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();
}
