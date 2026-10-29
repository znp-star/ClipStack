using System.Runtime.InteropServices;

namespace ClipStack.App.Interop;

/// <summary>
/// Monitor lookup for panel placement. Bounds are kept in device pixels in the settings file
/// and converted with the monitor's own DPI, so a per-monitor scale change never makes the
/// panel drift or grow.
/// </summary>
internal static class Monitors
{
    private const uint MonitorDefaultToNearest = 2;

    public readonly record struct MonitorInfo(string Device, int WorkX, int WorkY, int WorkWidth, int WorkHeight, uint Dpi)
    {
        public double Scale => Dpi / 96.0;
    }

    public static MonitorInfo ForWindow(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
        {
            return new MonitorInfo(string.Empty, 0, 0, 1920, 1080, 96);
        }

        var dpi = 96u;
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
        {
            dpi = dpiX;
        }

        var work = info.rcWork;
        return new MonitorInfo(
            info.szDevice,
            work.Left,
            work.Top,
            work.Right - work.Left,
            work.Bottom - work.Top,
            dpi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
