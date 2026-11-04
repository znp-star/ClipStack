using ClipStack.Core.Interop;

namespace ClipStack.Core.Tests;

/// <summary>
/// Locks the gesture that goes into the target window. Terminals paste on Shift+Insert and bind no
/// Ctrl variant, so holding Ctrl turned the keystroke into a key the program inside the terminal
/// saw: nvim typed the literal <c>&lt;C-S-Insert&gt;</c> into the buffer in insert mode.
/// </summary>
public class PasteInvokerTests
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkV = 0x56;
    private const ushort VkInsert = 0x2D;

    [Fact]
    public void TerminalPasteIsShiftInsertWithoutControl()
    {
        var keys = PasteInvoker.PasteKeystrokes(shiftInsert: true);

        Assert.Equal(
            new[] { (VkShift, false), (VkInsert, false), (VkInsert, true), (VkShift, true) },
            keys);
    }

    [Fact]
    public void OtherWindowsPasteIsCtrlV()
    {
        var keys = PasteInvoker.PasteKeystrokes(shiftInsert: false);

        Assert.Equal(
            new[] { (VkControl, false), (VkV, false), (VkV, true), (VkControl, true) },
            keys);
    }
}
