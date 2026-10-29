namespace ClipStack.Core.Interop;

/// <summary>Hotkey modifier flags, mirroring the RegisterHotKey MOD_* values.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}
