using ClipStack.Core.Interop;

namespace ClipStack.Core.Settings;

/// <summary>The nine configurable actions. Everything except <see cref="ShowPanel"/> only fires
/// while the panel has focus, so those may use bare keys such as Enter or F2.</summary>
public enum HotkeyAction
{
    ShowPanel,
    FocusSearch,
    Paste,
    PastePlain,
    Delete,
    Edit,
    Pin,
    CycleFilter,
    ClearHistory,
}

/// <summary>
/// Action metadata: the stable id used in <c>settings.json</c>, the label shown in the settings
/// window, the scope, and the factory default. Kept in one place so the settings UI, the panel
/// and the global registrar cannot drift apart.
/// </summary>
public static class Hotkeys
{
    public static readonly IReadOnlyList<HotkeyAction> All =
    [
        HotkeyAction.ShowPanel,
        HotkeyAction.FocusSearch,
        HotkeyAction.Paste,
        HotkeyAction.PastePlain,
        HotkeyAction.Delete,
        HotkeyAction.Edit,
        HotkeyAction.Pin,
        HotkeyAction.CycleFilter,
        HotkeyAction.ClearHistory,
    ];

    private static readonly Dictionary<HotkeyAction, string> Ids = new()
    {
        [HotkeyAction.ShowPanel] = "showPanel",
        [HotkeyAction.FocusSearch] = "focusSearch",
        [HotkeyAction.Paste] = "paste",
        [HotkeyAction.PastePlain] = "pastePlain",
        [HotkeyAction.Delete] = "delete",
        [HotkeyAction.Edit] = "edit",
        [HotkeyAction.Pin] = "pin",
        [HotkeyAction.CycleFilter] = "cycleFilter",
        [HotkeyAction.ClearHistory] = "clearHistory",
    };

    private static readonly Dictionary<HotkeyAction, string> Labels = new()
    {
        [HotkeyAction.ShowPanel] = "唤出面板",
        [HotkeyAction.FocusSearch] = "聚焦搜索",
        [HotkeyAction.Paste] = "粘贴",
        [HotkeyAction.PastePlain] = "粘贴为纯文本",
        [HotkeyAction.Delete] = "删除条目",
        [HotkeyAction.Edit] = "编辑条目",
        [HotkeyAction.Pin] = "置顶 / 取消置顶",
        [HotkeyAction.CycleFilter] = "切换过滤条",
        [HotkeyAction.ClearHistory] = "清空历史",
    };

    private static readonly Dictionary<HotkeyAction, string> DefaultText = new()
    {
        [HotkeyAction.ShowPanel] = "Ctrl+Shift+V",
        [HotkeyAction.FocusSearch] = "Ctrl+F",
        [HotkeyAction.Paste] = "Enter",
        [HotkeyAction.PastePlain] = "Ctrl+Enter",
        [HotkeyAction.Delete] = "Delete",
        [HotkeyAction.Edit] = "F2",
        [HotkeyAction.Pin] = "Ctrl+P",
        [HotkeyAction.CycleFilter] = "Ctrl+Tab",
        [HotkeyAction.ClearHistory] = "Ctrl+Shift+Delete",
    };

    public static string Id(HotkeyAction action) => Ids[action];

    public static string Label(HotkeyAction action) => Labels[action];

    /// <summary>System-wide actions go through RegisterHotKey; the rest only run in the panel.</summary>
    public static bool IsGlobal(HotkeyAction action) => action == HotkeyAction.ShowPanel;

    public static HotkeyGesture Default(HotkeyAction action) =>
        HotkeyGesture.TryParse(DefaultText[action], out var gesture) ? gesture : HotkeyGesture.None;

    /// <summary>
    /// Builds the effective table: a stored entry overrides the default only when it parses, so a
    /// hand-edited typo degrades to the default rather than to a dead key.
    /// </summary>
    public static Dictionary<HotkeyAction, HotkeyGesture> Resolve(IReadOnlyDictionary<string, string>? stored)
    {
        var resolved = new Dictionary<HotkeyAction, HotkeyGesture>(All.Count);

        foreach (var action in All)
        {
            var gesture = Default(action);
            if (stored is not null
                && stored.TryGetValue(Id(action), out var text)
                && HotkeyGesture.TryParse(text, out var parsed))
            {
                gesture = parsed;
            }

            resolved[action] = gesture;
        }

        return resolved;
    }

    public static Dictionary<string, string> ToSettings(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> table)
    {
        var stored = new Dictionary<string, string>(table.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (action, gesture) in table)
        {
            stored[Id(action)] = gesture.IsEmpty ? string.Empty : HotkeyGesture.Format(gesture);
        }

        return stored;
    }

    /// <summary>
    /// Rejects gestures that would break something: a modifier on its own, a global hotkey with
    /// no modifier (it would swallow that key system-wide), or a duplicate.
    /// </summary>
    public static bool TryValidate(
        HotkeyAction action,
        HotkeyGesture gesture,
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture> table,
        out string? error)
    {
        error = null;

        if (gesture.IsEmpty)
        {
            error = "请按下一个快捷键";
            return false;
        }

        if (IsModifierKey(gesture.VirtualKey))
        {
            error = "需要同时按一个非修饰键";
            return false;
        }

        if (IsGlobal(action) && gesture.IsBare)
        {
            error = "全局快捷键至少要带 Ctrl / Alt / Shift / Win 之一";
            return false;
        }

        foreach (var (other, otherGesture) in table)
        {
            if (other != action && otherGesture == gesture)
            {
                error = $"与「{Label(other)}」重复";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Registers a <see cref="HotkeyGesture"/> comparison for the panel: the same virtual key with
    /// an extra modifier held is a different gesture, and Caps Lock / Num Lock must not matter.
    /// </summary>
    public static bool Matches(HotkeyGesture gesture, int virtualKey, HotkeyModifiers modifiers) =>
        !gesture.IsEmpty && gesture.VirtualKey == virtualKey && gesture.Modifiers == modifiers;

    /// <summary>True for keys that only modify others; they can never be a gesture on their own.</summary>
    public static bool IsModifierKey(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or      // Shift, Ctrl, Alt
        0x5B or 0x5C or              // LWin, RWin
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or
        0x5D;                        // Apps key
}
