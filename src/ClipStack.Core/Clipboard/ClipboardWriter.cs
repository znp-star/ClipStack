using System.Runtime.InteropServices;
using ClipStack.Core.Images;
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
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 15;

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

                if (payload.ImagePng is { Length: > 0 } image)
                {
                    if (!SetBytes(ClipboardFormats.Png, image))
                    {
                        return false;
                    }

                    // Chromium-based apps build paste files from the DIB and ignore the raw
                    // PNG format, so the pixels ride along in both shapes.
                    if (payload.ImageDib is { Length: > 0 } dib
                        && !SetBytes(NativeMethods.CF_DIB, dib))
                    {
                        Log.Warn("Failed to set CF_DIB; keeping PNG only");
                    }
                }
                else if (payload.FilePaths is { Count: > 0 } files)
                {
                    // Paste a real file drop so Explorer and chat apps receive the file,
                    // not its path as text.
                    if (!SetBytes(NativeMethods.CF_HDROP, DropFilesData.Build(files)))
                    {
                        return false;
                    }

                    // PNG pixels ride along, so targets that only accept images get one.
                    if (files.Count == 1 && files[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        TrySetFileBytes(files[0]);
                    }
                }
                else if (!string.IsNullOrEmpty(payload.Text))
                {
                    if (!SetUnicodeText(payload.Text))
                    {
                        return false;
                    }
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
        var chars = new char[text.Length + 1];
        text.CopyTo(chars);
        return SetBytes(NativeMethods.CF_UNICODETEXT, MemoryMarshal.AsBytes(chars.AsSpan()).ToArray());
    }

    /// <summary>Adds the PNG pixels of a dropped image file when it can be read cheaply.</summary>
    private static void TrySetFileBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > ClipboardReader.MaxImageBytes)
            {
                return;
            }

            var bytes = File.ReadAllBytes(path);
            if (PngEncoder.LooksLikePng(bytes))
            {
                _ = SetBytes(ClipboardFormats.Png, bytes);
            }
        }
        catch (IOException)
        {
            // The file vanished or is locked; the file drop itself is still valid.
        }
    }

    private static bool SetBytes(uint format, byte[] bytes)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
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
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
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
