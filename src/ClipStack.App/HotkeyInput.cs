using System.Windows.Input;
using ClipStack.Core.Interop;
using ClipStack.Core.Settings;

namespace ClipStack.App;

/// <summary>
/// The one place that translates between WPF input events and the framework-free
/// <see cref="HotkeyGesture"/> the settings file and the core assembly speak.
/// </summary>
internal static class HotkeyInput
{
    public static HotkeyModifiers Modifiers(ModifierKeys keys)
    {
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (keys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keys.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    public static ModifierKeys ToWpfModifiers(HotkeyModifiers modifiers)
    {
        var keys = ModifierKeys.None;
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            keys |= ModifierKeys.Control;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            keys |= ModifierKeys.Shift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            keys |= ModifierKeys.Alt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            keys |= ModifierKeys.Windows;
        }

        return keys;
    }

    /// <summary>Alt arrives as <see cref="Key.System"/>; the real key travels in SystemKey.</summary>
    public static Key KeyOf(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    public static HotkeyGesture Gesture(KeyEventArgs e, ModifierKeys modifiers) =>
        new(KeyInterop.VirtualKeyFromKey(KeyOf(e)), Modifiers(modifiers));

    /// <summary>Keys held down while waiting for the rest of the combination.</summary>
    public static bool IsModifierKey(int virtualKey) => Hotkeys.IsModifierKey(virtualKey);
}
