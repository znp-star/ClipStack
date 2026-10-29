using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;

namespace ClipStack.App.Interop;

/// <summary>
/// Notification-area icon. Shell_NotifyIcon is driven from a hidden top-level window (the same
/// kind the clipboard listener uses), and the context menu is a plain WPF <see cref="ContextMenu"/>
/// so it inherits the app's styling instead of the legacy Win32 menu look.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WmNull = 0x0000;
    private const int WmContextMenu = 0x007B;
    private const int WmTray = 0x8000 + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;

    private const int IdiApplication = 32512;

    private readonly NativeMessageWindow _window;
    private readonly IntPtr _icon;
    private readonly uint _id = 1;
    private readonly uint _taskbarCreated;

    private string _tooltip;
    private ContextMenu? _menu;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _window = new NativeMessageWindow("ClipStack.TrayIcon");
        _window.MessageReceived += OnMessage;

        _icon = RenderIcon(32);
        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIconW(IntPtr.Zero, (IntPtr)IdiApplication);
        }

        // Explorer restarts (crash, update) wipe every tray icon; it broadcasts this message so
        // well-behaved apps can add theirs back.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>Raised for a left click or a double click, both of which should bring up the panel.</summary>
    public event Action? Activate;

    public void SetMenu(ContextMenu menu) => _menu = menu;

    public void Show()
    {
        var data = CreateData();
        data.uFlags = NifMessage | NifIcon | NifTip;

        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (_added)
        {
            Log.Info("Tray icon added");
        }
        else
        {
            Log.Warn("Tray icon could not be added");
        }
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NifTip;
        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    private void OnMessage(WindowMessage message)
    {
        if (_taskbarCreated != 0 && message.Msg == _taskbarCreated)
        {
            // The notification area was rebuilt; re-add the icon.
            _added = false;
            Show();
            message.Handled = true;
            return;
        }

        if (message.Msg != WmTray)
        {
            return;
        }

        message.Handled = true;

        // The shell reports a click through several channels depending on where it happened
        // (taskbar corner, overflow flyout, keyboard): accept all of them.
        switch ((int)message.LParam)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
            case NinSelect:
            case NinKeySelect:
                Activate?.Invoke();
                break;

            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        if (_menu is null || _disposed)
        {
            return;
        }

        // The classic dance: own the foreground first so the menu closes when the user clicks
        // elsewhere, then post a no-op message so the popup gets a chance to see the click.
        _ = SetForegroundWindow(_window.Handle);
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        _ = PostMessageW(_window.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        Log.Info("Tray menu opened");
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = _id,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Activate = null;
        _menu = null;

        if (_added)
        {
            var data = CreateData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }

        _window.MessageReceived -= OnMessage;
        _window.Dispose();

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
        }
    }

    /// <summary>
    /// Draws the app mark (accent rounded square, two white bars) and turns it into an HICON.
    /// Rendering it here keeps the repo free of a binary icon asset.
    /// </summary>
    private static IntPtr RenderIcon(int size)
    {
        try
        {
            var accent = Application.Current?.TryFindResource("AccentBrush") as SolidColorBrush
                ?? new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0));
            var mark = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var corner = size * 0.22;
                dc.DrawRoundedRectangle(accent, null, new Rect(0, 0, size, size), corner, corner);

                var barHeight = size * 0.12;
                var barWidth = size * 0.46;
                var left = size * 0.27;
                dc.DrawRoundedRectangle(
                    mark, null, new Rect(left, size * 0.29, barWidth, barHeight), barHeight / 2, barHeight / 2);
                dc.DrawRoundedRectangle(
                    mark, null, new Rect(left, size * 0.55, barWidth, barHeight), barHeight / 2, barHeight / 2);
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var stride = size * 4;
            var pixels = new byte[stride * size];
            bitmap.CopyPixels(pixels, stride, 0);

            return CreateIcon(pixels, size, size);
        }
        catch (Exception)
        {
            // A missing tray icon is better than a crash on startup; the caller falls back.
            return IntPtr.Zero;
        }
    }

    private static IntPtr CreateIcon(byte[] bgra, int width, int height)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height, // negative height keeps the rows top-down, matching the WPF bitmap
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(bgra, 0, bits, bgra.Length);
        var mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
        if (mask == IntPtr.Zero)
        {
            _ = DeleteObject(color);
            return IntPtr.Zero;
        }

        var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);

        _ = DeleteObject(color);
        _ = DeleteObject(mask);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr handle);
}
