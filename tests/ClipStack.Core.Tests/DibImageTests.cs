using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Images;

namespace ClipStack.Core.Tests;

public sealed class DibImageTests
{
    [Fact]
    public void TwentyFourBitBottomUpBitmapConvertsWithCorrectPixelOrder()
    {
        // 2x2, bottom-up: the first row in memory is the bottom row of the image.
        var dib = BuildDib(2, 2, 24, compression: 0, topDown: false, rows:
        [
            [255, 0, 0, 255, 255, 255], // top row: blue, white
            [0, 0, 255, 0, 255, 0],     // bottom row: red, green
        ]);

        Assert.True(DibImage.TryConvertToPng(dib, out var png, out var reason), reason);

        var bitmap = Decode(png);
        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(2, bitmap.PixelHeight);

        // Top-left is blue, bottom-left is red.
        AssertPixel(bitmap, 0, 0, 0x0000FF);
        AssertPixel(bitmap, 1, 0, 0xFFFFFF);
        AssertPixel(bitmap, 0, 1, 0xFF0000);
        AssertPixel(bitmap, 1, 1, 0x00FF00);
    }

    [Fact]
    public void TopDownBitmapIsNotFlipped()
    {
        var dib = BuildDib(1, 2, 24, compression: 0, topDown: true, rows:
        [
            [0, 0, 255],     // red
            [0, 255, 0],     // green
        ]);

        Assert.True(DibImage.TryConvertToPng(dib, out var png, out var reason), reason);

        var bitmap = Decode(png);
        AssertPixel(bitmap, 0, 0, 0xFF0000);
        AssertPixel(bitmap, 0, 1, 0x00FF00);
    }

    [Fact]
    public void ThirtyTwoBitRgbBitmapIsTreatedAsOpaque()
    {
        // Alpha byte is zero here; a BI_RGB 32-bit bitmap must still come out opaque.
        // DIB bytes are B, G, R, A.
        var dib = BuildDib(1, 1, 32, compression: 0, topDown: false, rows: [[10, 20, 30, 0]]);

        Assert.True(DibImage.TryConvertToPng(dib, out var png, out var reason), reason);

        var converted = Decode(png);
        var pixel = ReadPixel(converted, 0, 0);

        Assert.Equal(0x1E140A, (pixel.R << 16) | (pixel.G << 8) | pixel.B); // R=30, G=20, B=10
        Assert.Equal(255, pixel.A);
    }

    [Fact]
    public void UnsupportedColourDepthIsRejected()
    {
        var dib = BuildDib(1, 1, 8, compression: 0, topDown: false, rows: [[0, 0, 0, 0]]);

        Assert.False(DibImage.TryConvertToPng(dib, out _, out var reason));
        Assert.Contains("色深", reason);
    }

    [Fact]
    public void TruncatedBitmapIsRejected()
    {
        var dib = BuildDib(4, 4, 24, compression: 0, topDown: false, rows: new byte[4][]);
        var truncated = dib[..(dib.Length - 8)];

        Assert.False(DibImage.TryConvertToPng(truncated, out _, out var reason));
        Assert.Contains("不完整", reason);
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, int expectedRgb)
    {
        var pixel = ReadPixel(bitmap, x, y);
        Assert.Equal(expectedRgb, (pixel.R << 16) | (pixel.G << 8) | pixel.B);
    }

    /// <summary>Reads one pixel normalised to Bgra32, whatever the decoder handed back.</summary>
    private static (byte B, byte G, byte R, byte A) ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var normalised = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var pixel = new byte[4];
        normalised.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    /// <summary>Builds a BITMAPINFOHEADER DIB. Row data is given top-down for readability.</summary>
    private static byte[] BuildDib(int width, int height, short bitCount, int compression, bool topDown, byte[][] rows)
    {
        var stride = ((width * bitCount + 31) / 32) * 4;
        var offset = 40 + (compression == 3 ? 16 : 0);
        var data = new byte[offset + stride * height];

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), topDown ? -height : height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(14), bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), compression);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), stride * height);

        for (var y = 0; y < height && y < rows.Length; y++)
        {
            var targetRow = topDown ? y : height - 1 - y;
            rows[y].CopyTo(data.AsSpan(offset + targetRow * stride));
        }

        return data;
    }
}
