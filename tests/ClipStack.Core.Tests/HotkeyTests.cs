using ClipStack.Core.Interop;
using ClipStack.Core.Settings;

namespace ClipStack.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", 0x56, HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    [InlineData("ctrl+f", 0x46, HotkeyModifiers.Control)]
    [InlineData("Enter", 0x0D, HotkeyModifiers.None)]
    [InlineData("F2", 0x71, HotkeyModifiers.None)]
    [InlineData("F24", 0x87, HotkeyModifiers.None)]
    [InlineData("Ctrl+Shift+Delete", 0x2E, HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    [InlineData("Win+Alt+Space", 0x20, HotkeyModifiers.Win | HotkeyModifiers.Alt)]
    public void ParsesModifiersKeysAndNamedKeys(string text, int virtualKey, HotkeyModifiers modifiers)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(virtualKey, gesture.VirtualKey);
        Assert.Equal(modifiers, gesture.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]       // modifier only
    [InlineData("Ctrl+A+B")]         // two keys
    [InlineData("Ctrl+Nope")]        // unknown key
    [InlineData("F25")]              // out of range
    [InlineData(null)]
    public void RejectsMalformedText(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(gesture.IsEmpty);
    }

    [Theory]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("Enter")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Shift+Delete")]
    [InlineData("Alt+Win+Space")]
    public void FormatRoundTripsThroughParse(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(text, HotkeyGesture.Format(gesture));
        Assert.True(HotkeyGesture.TryParse(HotkeyGesture.Format(gesture), out var again));
        Assert.Equal(gesture, again);
    }

    [Fact]
    public void FormatOrdersModifiersCanonically()
    {
        var gesture = new HotkeyGesture(
            0x56,
            HotkeyModifiers.Shift | HotkeyModifiers.Win | HotkeyModifiers.Control | HotkeyModifiers.Alt);

        Assert.Equal("Ctrl+Shift+Alt+Win+V", HotkeyGesture.Format(gesture));
    }

    [Fact]
    public void EmptyGestureFormatsToNothing()
    {
        Assert.Equal(string.Empty, HotkeyGesture.Format(HotkeyGesture.None));
        Assert.True(HotkeyGesture.None.IsEmpty);
    }

    [Fact]
    public void ResolveFallsBackToDefaultsForMissingOrBrokenEntries()
    {
        var stored = new Dictionary<string, string>
        {
            ["showPanel"] = "Ctrl+Alt+Space",
            ["paste"] = "not a key",
        };

        var table = Hotkeys.Resolve(stored);

        Assert.Equal(9, table.Count);
        Assert.Equal(new HotkeyGesture(0x20, HotkeyModifiers.Control | HotkeyModifiers.Alt), table[HotkeyAction.ShowPanel]);
        Assert.Equal(Hotkeys.Default(HotkeyAction.Paste), table[HotkeyAction.Paste]);
        Assert.Equal(Hotkeys.Default(HotkeyAction.Edit), table[HotkeyAction.Edit]);
    }

    [Fact]
    public void DefaultsAreUniqueAndValidForTheirScope()
    {
        var table = Hotkeys.Resolve(null);

        Assert.Equal(table.Count, table.Values.Distinct().Count());

        foreach (var (action, gesture) in table)
        {
            Assert.True(Hotkeys.TryValidate(action, gesture, table, out var error), $"{action}: {error}");
        }

        Assert.True(Hotkeys.IsGlobal(HotkeyAction.ShowPanel));
        Assert.False(Hotkeys.IsGlobal(HotkeyAction.Paste));
    }

    [Fact]
    public void ToSettingsRoundTripsThroughResolve()
    {
        var table = Hotkeys.Resolve(null);
        table[HotkeyAction.Pin] = new HotkeyGesture(0x51, HotkeyModifiers.Control | HotkeyModifiers.Shift);

        var restored = Hotkeys.Resolve(Hotkeys.ToSettings(table));

        Assert.Equal(table, restored);
    }

    [Fact]
    public void ValidationRejectsDuplicatesBareGlobalKeysAndModifierOnly()
    {
        var table = Hotkeys.Resolve(null);

        Assert.False(Hotkeys.TryValidate(HotkeyAction.Pin, table[HotkeyAction.Paste], table, out var conflict));
        Assert.Contains("粘贴", conflict);

        Assert.False(Hotkeys.TryValidate(HotkeyAction.ShowPanel, new HotkeyGesture(0x56, HotkeyModifiers.None), table, out var bare));
        Assert.Contains("全局", bare);

        Assert.False(Hotkeys.TryValidate(HotkeyAction.Paste, new HotkeyGesture(0x11, HotkeyModifiers.Control), table, out var modifierOnly));
        Assert.Contains("非修饰键", modifierOnly);

        // A bare key is fine for panel-scoped actions.
        Assert.True(Hotkeys.TryValidate(HotkeyAction.Edit, new HotkeyGesture(0x72, HotkeyModifiers.None), table, out _));
    }

    [Fact]
    public void MatchesComparesModifiersExactly()
    {
        var gesture = new HotkeyGesture(0x50, HotkeyModifiers.Control);

        Assert.True(Hotkeys.Matches(gesture, 0x50, HotkeyModifiers.Control));
        Assert.False(Hotkeys.Matches(gesture, 0x50, HotkeyModifiers.Control | HotkeyModifiers.Shift));
        Assert.False(Hotkeys.Matches(gesture, 0x51, HotkeyModifiers.Control));
        Assert.False(Hotkeys.Matches(HotkeyGesture.None, 0x50, HotkeyModifiers.None));
    }
}
