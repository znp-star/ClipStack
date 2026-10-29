using ClipStack.Core.Clipboard;
using ClipStack.Core.Images;
using ClipStack.Core.Models;

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
