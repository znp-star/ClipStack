using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipStack.Core.Interop;

/// <summary>
/// A hidden top-level window used to receive clipboard and hotkey notifications
/// without bringing a visible window (or a WPF dependency) into ClipStack.Core.
/// </summary>
/// <remarks>
/// It must be a real top-level window, not a message-only (HWND_MESSAGE) one:
/// the clipboard listener only reports WM_CLIPBOARDUPDATE to top-level windows.
/// WS_EX_TOOLWINDOW plus never calling ShowWindow keeps it invisible and out of Alt+Tab.
/// </remarks>
public sealed class NativeMessageWindow : IDisposable
{
    private readonly NativeMethods.WndProc _wndProc;
    private readonly string _className;
    private readonly IntPtr _instance;
    private IntPtr _hwnd;
    private bool _disposed;

    public IntPtr Handle => _hwnd;

    /// <summary>Raised for every message sent to the window, on the creating thread.</summary>
    public event Action<WindowMessage>? MessageReceived;

    public NativeMessageWindow(string classNamePrefix = "ClipStack.MessageWindow")
    {
        _instance = NativeMethods.GetModuleHandleW(null);
        _className = $"{classNamePrefix}.{Guid.NewGuid():N}";
        _wndProc = WndProc;

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = _instance,
            lpszClassName = _className,
        };

        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
        }

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW,
            _className,
            string.Empty,
            NativeMethods.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnregisterClassW(_className, _instance);
            throw new Win32Exception(error, "CreateWindowEx failed");
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var handler = MessageReceived;
        if (handler is not null)
        {
            var message = new WindowMessage(hWnd, msg, wParam, lParam);
            handler(message);
            if (message.Handled)
            {
                return message.Result;
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MessageReceived = null;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        NativeMethods.UnregisterClassW(_className, _instance);
        GC.KeepAlive(_wndProc);
    }
}

public sealed class WindowMessage
{
    public WindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        HWnd = hWnd;
        Msg = msg;
        WParam = wParam;
        LParam = lParam;
    }

    public IntPtr HWnd { get; }
    public uint Msg { get; }
    public IntPtr WParam { get; }
    public IntPtr LParam { get; }

    /// <summary>Set to true to stop the default window procedure.</summary>
    public bool Handled { get; set; }

    public IntPtr Result { get; set; }
}
