using System.Runtime.InteropServices;
using System.Text;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;

namespace ClipStack.Core.Clipboard;

/// <summary>
/// Reads the current clipboard. Must run on a thread that can own the clipboard;
/// retries briefly because other applications grab it constantly.
/// </summary>
public static class ClipboardReader
{
    private const int OpenAttempts = 5;
    private const int OpenRetryDelayMs = 10;

    /// <summary>
    /// Returns null when the clipboard holds nothing we handle, or when the content
    /// was written by ClipStack itself and <paramref name="skipOwnWrites"/> is true.
    /// </summary>
    public static ClipPayload? Read(bool skipOwnWrites = true)
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
                if (skipOwnWrites && NativeMethods.IsClipboardFormatAvailable(ClipboardFormats.IgnoreMarker))
                {
                    return null;
                }

                return ReadLocked();
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        Log.Warn("OpenClipboard failed after retries; clipboard update skipped");
        return null;
    }

    private static ClipPayload? ReadLocked()
    {
        if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
        {
            var files = ReadFileDrop();
            if (files.Count > 0)
            {
                return ClipPayload.ForFiles(files);
            }
        }

        var text = ReadUnicodeText();
        if (!string.IsNullOrEmpty(text))
        {
            return ClipPayload.ForText(
                text,
                ReadRegisteredString(ClipboardFormats.Html, Encoding.UTF8),
                ReadRegisteredString(ClipboardFormats.Rtf, Encoding.Latin1));
        }

        // Images are captured in a later milestone (DIB -> PNG needs an image stack).
        return null;
    }

    private static string? ReadUnicodeText()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var value = Marshal.PtrToStringUni(pointer);
            return string.IsNullOrEmpty(value) ? null : value.TrimEnd('\0');
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static string? ReadRegisteredString(uint format, Encoding encoding)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)NativeMethods.GlobalSize(handle);
            if (size <= 1)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            var text = encoding.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static List<string> ReadFileDrop()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (handle == IntPtr.Zero)
        {
            return [];
        }

        var count = NativeMethods.DragQueryFileW(handle, uint.MaxValue, null, 0);
        var files = new List<string>((int)count);
        for (uint index = 0; index < count; index++)
        {
            var length = NativeMethods.DragQueryFileW(handle, index, null, 0);
            if (length == 0)
            {
                continue;
            }

            var buffer = new char[length + 1];
            var copied = NativeMethods.DragQueryFileW(handle, index, buffer, (uint)buffer.Length);
            if (copied > 0)
            {
                files.Add(new string(buffer, 0, (int)copied));
            }
        }

        return files;
    }
}
