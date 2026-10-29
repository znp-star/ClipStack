using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;

namespace ClipStack.Core.Clipboard;

/// <summary>
/// Listens for clipboard updates through a message-only window and raises
/// <see cref="ClipboardChanged"/> on the thread that created the monitor.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private static readonly string OwnProcessName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ClipStack";

    private readonly NativeMessageWindow _window;
    private bool _started;
    private bool _disposed;

    public ClipboardMonitor()
    {
        _window = new NativeMessageWindow("ClipStack.ClipboardMonitor");
        _window.MessageReceived += OnMessage;
    }

    public event EventHandler<ClipPayload>? ClipboardChanged;

    public bool IsPaused { get; set; }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        if (!NativeMethods.AddClipboardFormatListener(_window.Handle))
        {
            throw new InvalidOperationException(
                $"AddClipboardFormatListener failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        _started = true;
        Log.Info("Clipboard monitor started");
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _started = false;
        Log.Info("Clipboard monitor stopped");
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return;
        }

        message.Handled = true;

        if (IsPaused)
        {
            return;
        }

        try
        {
            var payload = ClipboardReader.Read(skipOwnWrites: true);
            if (payload is null)
            {
                return;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            var (app, title) = WindowInfo.Describe(foreground);

            // When the panel itself owns the foreground window the real source is unknown;
            // recording "ClipStack" as the source would be misleading.
            if (string.Equals(app, OwnProcessName, StringComparison.OrdinalIgnoreCase))
            {
                app = null;
                title = null;
            }

            ClipboardChanged?.Invoke(this, payload.WithSource(app, title));
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard read failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _window.Dispose();
    }
}
