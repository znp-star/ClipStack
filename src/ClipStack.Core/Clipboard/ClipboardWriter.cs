using System.Runtime.InteropServices;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;

namespace ClipStack.Core.Clipboard;

/// <summary>
/// Writes content back to the clipboard, tagging it with <see cref="ClipboardFormats.IgnoreMarkerName"/>
/// so our own monitor ignores the resulting update.
/// </summary>
public static class ClipboardWriter
{
    private const int OpenAttempts = 5;
    private const int OpenRetryDelayMs = 10;

    public static bool TryWrite(ClipPayload payload)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(payload.Text) && !SetUnicodeText(payload.Text))
                {
                    return false;
                }

                SetIgnoreMarker();
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        Log.Warn("Failed to write clipboard: it stayed locked by another process");
        return false;
    }

    private static bool SetUnicodeText(string text)
    {
        var byteCount = (text.Length + 1) * sizeof(char);
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return false;
        }

        try
        {
            var chars = new char[text.Length + 1];
            text.CopyTo(chars);
            Marshal.Copy(chars, 0, pointer, chars.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return false;
        }

        return true;
    }

    private static void SetIgnoreMarker()
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)1);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer != IntPtr.Zero)
        {
            Marshal.WriteByte(pointer, 0);
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(ClipboardFormats.IgnoreMarker, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
        }
    }
}
