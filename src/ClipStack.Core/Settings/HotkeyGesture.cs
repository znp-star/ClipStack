using ClipStack.Core.Interop;

namespace ClipStack.Core.Settings;

/// <summary>
/// A key combination such as <c>Ctrl+Shift+V</c>, stored as a virtual-key code plus modifier
/// flags so this assembly stays free of any UI framework. The App layer converts to and from
/// WPF keys (<c>KeyInterop</c>) when it needs to match or capture them.
/// </summary>
public readonly record struct HotkeyGesture(int VirtualKey, HotkeyModifiers Modifiers)
{
    /// <summary>The "unset" gesture: an empty box in the settings window.</summary>
    public static readonly HotkeyGesture None = default;

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>True when the gesture has no modifier at all, which is only sane for panel keys.</summary>
    public bool IsBare => Modifiers == HotkeyModifiers.None;

    public override string ToString() => Format(this);

    public static string Format(HotkeyGesture gesture)
    {
        if (gesture.IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(gesture.VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>
    /// Parses notation as written in the settings file. Unknown key names and a missing key are
    /// both failures, so a typo falls back to the default instead of silently disabling an action.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var virtualKey = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            // A second key token means the string is malformed ("Ctrl+A+B").
            if (virtualKey != 0 || !TryParseKeyName(token, out virtualKey))
            {
                return false;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(virtualKey, modifiers);
        return true;
    }

    public static bool TryParseKeyName(string token, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var name = token.Trim();

        foreach (var (vk, label) in NamedKeys)
        {
            if (string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
            {
                virtualKey = vk;
                return true;
            }
        }

        if (name.Length == 1)
        {
            var ch = char.ToUpperInvariant(name[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (name.Length is >= 2 and <= 3
            && (name[0] is 'F' or 'f')
            && int.TryParse(name.AsSpan(1), out var index)
            && index is >= 1 and <= 24)
        {
            virtualKey = 0x70 + index - 1;
            return true;
        }

        return false;
    }

    public static string KeyName(int virtualKey)
    {
        foreach (var (vk, label) in NamedKeys)
        {
            if (vk == virtualKey)
            {
                return label;
            }
        }

        if (virtualKey is >= 'A' and <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        return "0x" + virtualKey.ToString("X2");
    }

    private static readonly (int VirtualKey, string Name)[] NamedKeys =
    [
        (0x08, "Backspace"),
        (0x09, "Tab"),
        (0x0D, "Enter"),
        (0x1B, "Esc"),
        (0x20, "Space"),
        (0x21, "PageUp"),
        (0x22, "PageDown"),
        (0x23, "End"),
        (0x24, "Home"),
        (0x25, "Left"),
        (0x26, "Up"),
        (0x27, "Right"),
        (0x28, "Down"),
        (0x2D, "Insert"),
        (0x2E, "Delete"),
        (0x5B, "LWin"),
        (0x5C, "RWin"),
        (0x6A, "NumMultiply"),
        (0x6B, "NumAdd"),
        (0x6D, "NumSubtract"),
        (0x6E, "NumDecimal"),
        (0x6F, "NumDivide"),
        (0xBA, "Semicolon"),
        (0xBB, "Plus"),
        (0xBC, "Comma"),
        (0xBD, "Minus"),
        (0xBE, "Period"),
        (0xBF, "Slash"),
        (0xC0, "Tilde"),
        (0xDB, "LBracket"),
        (0xDC, "Backslash"),
        (0xDD, "RBracket"),
        (0xDE, "Quote"),
    ];
}
