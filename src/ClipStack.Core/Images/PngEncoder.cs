using System.Buffers.Binary;
using System.IO.Compression;

namespace ClipStack.Core.Images;

/// <summary>
/// A minimal PNG writer: 8-bit RGBA, filter type 0 on every row, zlib deflate.
/// ClipStack only needs to persist screenshots and pasted bitmaps, so a full
/// imaging stack (and its native dependency) is not worth pulling in.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public const byte ColorTypeRgba = 6;

    public static bool LooksLikePng(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Encodes tightly packed 8-bit RGBA rows (stride = width * 4) as a PNG file.</summary>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive");
        }

        var stride = checked(width * 4);
        if (rgba.Length < (long)stride * height)
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4", nameof(rgba));
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;                    // bit depth
        header[9] = ColorTypeRgba;        // colour type
        header[10] = 0;                   // compression
        header[11] = 0;                   // filter
        header[12] = 0;                   // interlace

        using var output = new MemoryStream(capacity: stride * height / 4 + 1024);
        output.Write(Signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", Deflate(width, height, stride, rgba));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] Deflate(int width, int height, int stride, ReadOnlySpan<byte> rgba)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            row[0] = 0; // filter: none

            for (var y = 0; y < height; y++)
            {
                rgba.Slice(y * stride, stride).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, first);
        crc = Update(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
