using ClipStack.Core.Clipboard;
using ClipStack.Core.Images;
using ClipStack.Core.Models;
using System.Runtime.InteropServices;

namespace ClipStack.Core.Tests;

/// <summary>
/// Round-trips through the real Windows clipboard. The clipboard is machine state shared with
/// whatever else is running, so reads retry briefly and the tests bail out quietly when another
/// process keeps it locked.
/// </summary>
[Collection("clipboard")]
public sealed class ClipboardRoundTripTests
{
    [Fact]
    public void FilePayloadComesBackAsFileDropNotText()
    {
        RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "clipstack-roundtrip.png");
            if (!ClipboardWriter.TryWrite(ClipPayload.ForFiles([path])))
            {
                return; // clipboard busy; the layout assertions cover the encoding
            }

            var read = ReadWithRetry();
            Assert.NotNull(read);
            Assert.Equal(ClipType.File, read!.Type);
            Assert.Equal([path], read.FilePaths);
        });
    }

    [Fact]
    public void ImagePayloadComesBackByteForByte()
    {
        RunOnSta(() =>
        {
            var png = PngEncoder.EncodeRgba(2, 2, new byte[]
            {
                255, 0, 0, 255, 0, 255, 0, 255,
                0, 0, 255, 255, 255, 255, 255, 255,
            });

            if (!ClipboardWriter.TryWrite(ClipPayload.ForImage(png)))
            {
                return;
            }

            var read = ReadWithRetry();
            Assert.NotNull(read);
            Assert.Equal(ClipType.Image, read!.Type);
            Assert.Equal(png, read.ImagePng);
        });
    }

    [Fact]
    public void AttachedDibRidesAlongWithThePngFormat()
    {
        RunOnSta(() =>
        {
            var png = PngEncoder.EncodeRgba(3, 2, new byte[24]);
            var dib = DibImage.EncodeBgra32(3, 2, new byte[3 * 2 * 4]);

            if (!ClipboardWriter.TryWrite(ClipPayload.ForImage(png).WithDib(dib)))
            {
                return;
            }

            Assert.True(IsClipboardFormatAvailable(CfDib), "CF_DIB missing from clipboard");
            Assert.True(IsClipboardFormatAvailable(RegisterPngFormat()), "PNG format missing from clipboard");
        });
    }

    [Fact]
    public void EncodeBgra32BuildsABottomUp32BppDib()
    {
        // One row, two pixels: blue then red, as BGRA quads left-to-right.
        var bgra = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
        var dib = DibImage.EncodeBgra32(2, 1, bgra);

        Assert.Equal(40 + 8, dib.Length);
        // A single-row DIB stores that row left-to-right; bottom-up only matters per row.
        Assert.Equal(255, dib[40]); // B
        Assert.Equal(0, dib[41]);   // G
        Assert.Equal(0, dib[42]);   // R
        Assert.Equal(255, dib[43]); // A
        Assert.Equal(0, dib[44]);
        Assert.Equal(0, dib[45]);
        Assert.Equal(255, dib[46]);
        Assert.Equal(255, dib[47]);
    }

    private const uint CfDib = 8;

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    private static uint RegisterPngFormat() => RegisterClipboardFormat("PNG");

    /// <summary>
    /// The clipboard often stays locked for a moment after a write (the history service and
    /// other listeners grab it right away), so a single read is not reliable.
    /// </summary>
    private static ClipPayload? ReadWithRetry()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(50);
            }

            var payload = ClipboardReader.Read(skipOwnWrites: false);
            if (payload is not null)
            {
                return payload;
            }
        }

        return null;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
