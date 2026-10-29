using System.Buffers.Binary;
using System.Text;

namespace ClipStack.Core.Clipboard;

/// <summary>
/// Builds the memory layout <c>CF_HDROP</c> expects: a DROPFILES header followed by a
/// double-null-terminated list of paths. Wide (UTF-16) paths keep non-ASCII file names intact.
/// </summary>
public static class DropFilesData
{
    /// <summary>DROPFILES is 20 bytes: pFiles, pt.x, pt.y, fNC, fWide.</summary>
    public const int HeaderSize = 20;

    public static byte[] Build(IReadOnlyList<string> paths)
    {
        var pathBytes = 0;
        foreach (var path in paths)
        {
            pathBytes += Encoding.Unicode.GetByteCount(path) + sizeof(char);
        }

        var buffer = new byte[HeaderSize + pathBytes + sizeof(char)];

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), HeaderSize); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 0);          // pt.x
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 0);          // pt.y
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), 0);         // fNC
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 1);         // fWide

        var offset = HeaderSize;
        foreach (var path in paths)
        {
            offset += Encoding.Unicode.GetBytes(path, buffer.AsSpan(offset));
            offset += sizeof(char); // the string's null terminator (already zero)
        }

        // The final extra null terminates the list; the loop above already left one zero pair.
        return buffer;
    }
}
