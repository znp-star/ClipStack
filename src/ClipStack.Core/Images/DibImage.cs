using System.Buffers.Binary;

namespace ClipStack.Core.Images;

/// <summary>
/// Converts a packed DIB (the payload of <c>CF_DIB</c>, without a BITMAPFILEHEADER)
/// into a PNG file. Only the shapes Windows actually puts on the clipboard are
/// supported: 24- and 32-bit uncompressed or bitfield bitmaps, either row order.
/// </summary>
public static class DibImage
{
    private const int BitmapInfoHeaderSize = 40;
    private const int BiRgb = 0;
    private const int BiBitfields = 3;

    /// <summary>Refuse absurd sizes before allocating: 40 MP covers any multi-monitor screenshot.</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// Builds a packed CF_DIB (40-byte BITMAPINFOHEADER, 32bpp BI_RGB, bottom-up) from
    /// BGRA pixel rows, so image payloads can ride both the PNG format and the DIB format.
    /// WPF hands out Bgra32 rows top-down; the encoder flips them.
    /// </summary>
    public static byte[] EncodeBgra32(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "DIB 尺寸无效");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "图片过大");
        }

        var stride = width * 4;
        if (bgra.Length < (long)stride * height)
        {
            throw new ArgumentException("像素数据不足", nameof(bgra));
        }

        var dib = new byte[BitmapInfoHeaderSize + (long)stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height); // positive: bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);     // biPlanes
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);    // biBitCount
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), BiRgb);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), stride * height);

        var target = BitmapInfoHeaderSize;
        for (var y = height - 1; y >= 0; y--)
        {
            Array.Copy(bgra, (long)y * stride, dib, target, stride);
            target += stride;
        }

        return dib;
    }

    public static bool TryConvertToPng(ReadOnlySpan<byte> dib, out byte[] png, out string? reason)
    {
        png = [];
        reason = null;

        if (dib.Length < BitmapInfoHeaderSize)
        {
            reason = "DIB 太小";
            return false;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib[0..]);
        if (headerSize < BitmapInfoHeaderSize)
        {
            reason = $"不支持的 DIB 头（{headerSize} 字节）";
            return false;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);

        if (width <= 0 || height == 0)
        {
            reason = "DIB 尺寸无效";
            return false;
        }

        var topDown = height < 0;
        var pixelHeight = Math.Abs(height);

        if ((long)width * pixelHeight > MaxPixels)
        {
            reason = $"图片过大（{width}×{pixelHeight}）";
            return false;
        }

        if (bitCount is not (24 or 32))
        {
            reason = $"不支持的色深（{bitCount} 位）";
            return false;
        }

        if (compression is not (BiRgb or BiBitfields))
        {
            reason = $"不支持的 DIB 压缩方式（{compression}）";
            return false;
        }

        // BITMAPINFOHEADER (40) declares masks right after itself; V4/V5 headers carry them inside.
        var dataOffset = headerSize;
        if (compression == BiBitfields && headerSize == BitmapInfoHeaderSize)
        {
            dataOffset += 16; // three colour masks plus the optional alpha mask
        }

        var stride = ((width * bitCount + 31) / 32) * 4;
        var required = (long)dataOffset + (long)stride * pixelHeight;
        if (dib.Length < required)
        {
            reason = $"DIB 数据不完整（需要 {required} 字节，实际 {dib.Length}）";
            return false;
        }

        var bytesPerPixel = bitCount / 8;
        // A 32-bit BI_RGB bitmap has no defined alpha byte; treating it as opaque is what
        // Windows itself does when it hands one to other applications.
        var useAlpha = bitCount == 32 && compression == BiBitfields;

        var rgba = new byte[width * pixelHeight * 4];

        for (var y = 0; y < pixelHeight; y++)
        {
            var sourceRow = topDown ? y : pixelHeight - 1 - y;
            var source = dib.Slice(dataOffset + sourceRow * stride, width * bytesPerPixel);
            var target = rgba.AsSpan(y * width * 4, width * 4);

            for (var x = 0; x < width; x++)
            {
                var sourceIndex = x * bytesPerPixel;
                var targetIndex = x * 4;
                target[targetIndex] = source[sourceIndex + 2];     // R
                target[targetIndex + 1] = source[sourceIndex + 1]; // G
                target[targetIndex + 2] = source[sourceIndex];     // B
                target[targetIndex + 3] = useAlpha ? source[sourceIndex + 3] : (byte)255;
            }
        }

        png = PngEncoder.EncodeRgba(width, pixelHeight, rgba);
        return true;
    }
}
