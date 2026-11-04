using System.Runtime.InteropServices;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;

namespace ClipStack.Core.Interop;

public enum PasteOutcome
{
    /// <summary>Content was copied and the paste keystroke was delivered.</summary>
    Sent,

    /// <summary>Content was copied but no keystroke was delivered; see the reason.</summary>
    CopiedOnly,

    /// <summary>Nothing was copied; the clipboard stayed locked.</summary>
    Failed,
}

public readonly record struct PasteResult(PasteOutcome Outcome, string? Reason);

/// <summary>
/// Copies an item to the clipboard and pastes it into the window that was foreground
/// before the panel opened. Never elevates, never sends input to a window it could not
/// verify as foreground.
/// </summary>
public static class PasteInvoker
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_V = 0x56;
    private const ushort VK_INSERT = 0x2D;

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal", "conhost", "openconsole", "mintty", "alacritty",
        "wezterm-gui", "wezterm", "tabby", "hyper", "terminus",
    };

    private static readonly HashSet<string> CopyOnlyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc",
    };

    public static PasteResult Paste(ClipPayload payload, IntPtr target, Action? onActivated = null)
    {
        if (!ClipboardWriter.TryWrite(payload))
        {
            return new PasteResult(PasteOutcome.Failed, "剪贴板被其他程序占用");
        }

        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return new PasteResult(PasteOutcome.CopiedOnly, "目标窗口已关闭，内容已复制");
        }

        var app = WindowInfo.GetProcessName(target);

        if (app is not null && CopyOnlyProcesses.Contains(app))
        {
            return new PasteResult(PasteOutcome.CopiedOnly, "远程桌面窗口，内容已复制");
        }

        if (!WindowInfo.CanQueryProcess(target))
        {
            return new PasteResult(PasteOutcome.CopiedOnly, "目标窗口权限高于本程序，内容已复制");
        }

        if (!NativeMethods.SetForegroundWindow(target))
        {
            return new PasteResult(PasteOutcome.CopiedOnly, "无法切换到目标窗口，内容已复制");
        }

        if (!WaitForForeground(target, 200))
        {
            return new PasteResult(PasteOutcome.CopiedOnly, "目标窗口未能激活，内容已复制");
        }

        var shiftInsert = app is not null && TerminalProcesses.Contains(app);

        // The window is foreground now, but the control that had the caret may not be: Qt apps hand
        // the focus to their first focusable widget when activated again. Give it back, then let it
        // settle before typing, otherwise the keystroke lands somewhere that ignores paste.
        onActivated?.Invoke();
        Thread.Sleep(60);
        SendPasteKeystroke(shiftInsert);
        Log.Info($"Pasted item into {app ?? "unknown"} ({(shiftInsert ? "Shift+Insert" : "Ctrl+V")})");
        return new PasteResult(PasteOutcome.Sent, null);
    }

    private static bool WaitForForeground(IntPtr target, int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            if (NativeMethods.GetForegroundWindow() == target)
            {
                return true;
            }

            Thread.Sleep(10);
            waited += 10;
        }

        return NativeMethods.GetForegroundWindow() == target;
    }

    /// <summary>
    /// The virtual keys of a paste gesture, in the order they must go down and up.
    ///
    /// A terminal pastes on <c>Shift+Insert</c> (Windows Terminal, conhost, WezTerm, Alacritty and
    /// mintty all bind it) and binds no Ctrl variant, so Ctrl must stay out of this gesture: a
    /// Ctrl+Shift+Insert would fall through the terminal to the program running in it, where it is
    /// just an unrecognised key — nvim prints it as <c>&lt;C-S-Insert&gt;</c> into the buffer while
    /// in insert mode, and drops it in normal mode.
    /// </summary>
    public static IReadOnlyList<(ushort VirtualKey, bool Up)> PasteKeystrokes(bool shiftInsert) =>
        shiftInsert
            ? [(VK_SHIFT, false), (VK_INSERT, false), (VK_INSERT, true), (VK_SHIFT, true)]
            : [(VK_CONTROL, false), (VK_V, false), (VK_V, true), (VK_CONTROL, true)];

    private static void SendPasteKeystroke(bool shiftInsert)
    {
        var inputs = PasteKeystrokes(shiftInsert)
            .Select(step => new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = step.VirtualKey,
                        dwFlags = step.Up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                    },
                },
            })
            .ToArray();

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            Log.Warn($"SendInput delivered {sent}/{inputs.Length} events");
        }
    }
}
