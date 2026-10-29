namespace ClipStack.Core.Interop;

/// <summary>System-wide hotkey registered against a hidden message window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0xC51;

    private readonly NativeMessageWindow _window;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey()
    {
        _window = new NativeMessageWindow("ClipStack.GlobalHotkey");
        _window.MessageReceived += OnMessage;
    }

    public event Action? Pressed;

    /// <summary>Registers (or replaces) the hotkey. Returns false when another app owns it.</summary>
    public bool Register(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (_disposed)
        {
            return false;
        }

        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }

        _registered = NativeMethods.RegisterHotKey(
            _window.Handle, HotkeyId, (uint)modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
    }

    private void OnMessage(WindowMessage message)
    {
        if (message.Msg == NativeMethods.WM_HOTKEY && message.WParam == (IntPtr)HotkeyId)
        {
            message.Handled = true;
            Pressed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _window.Dispose();
    }
}
