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

    public static PasteResult Paste(ClipPayload payload, IntPtr target)
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
        Thread.Sleep(20);
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

    private static void SendPasteKeystroke(bool shiftInsert)
    {
        var inputs = new List<NativeMethods.INPUT>(5);

        void Key(ushort virtualKey, bool up) => inputs.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                },
            },
        });

        var key = shiftInsert ? VK_INSERT : VK_V;

        Key(VK_CONTROL, up: false);
        if (shiftInsert)
        {
            Key(VK_SHIFT, up: false);
        }

        Key(key, up: false);
        Key(key, up: true);

        if (shiftInsert)
        {
            Key(VK_SHIFT, up: true);
        }

        Key(VK_CONTROL, up: true);

        var array = inputs.ToArray();
        var sent = NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != array.Length)
        {
            Log.Warn($"SendInput delivered {sent}/{array.Length} events");
        }
    }
}
