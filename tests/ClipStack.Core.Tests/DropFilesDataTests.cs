using System.Buffers.Binary;
using System.Text;
using ClipStack.Core.Clipboard;

namespace ClipStack.Core.Tests;

public sealed class DropFilesDataTests
{
    [Fact]
    public void HeaderDescribesWideFilePathList()
    {
        var data = DropFilesData.Build([@"C:\temp\shot.png"]);

        Assert.Equal(DropFilesData.HeaderSize, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)));
    }

    [Fact]
    public void PathsAreDoubleNullTerminatedUtf16()
    {
        var paths = new[] { @"C:\a\图片一.png", @"C:\b\two.png" };
        var data = DropFilesData.Build(paths);

        var payload = Encoding.Unicode.GetString(data, DropFilesData.HeaderSize, data.Length - DropFilesData.HeaderSize);
        var parts = payload.Split('\0');

        Assert.Equal(paths[0], parts[0]);
        Assert.Equal(paths[1], parts[1]);
        Assert.Equal(string.Empty, parts[2]);
        // Two terminators end the list: the last two entries are both empty.
        Assert.Equal(string.Empty, parts[3]);
    }

    [Fact]
    public void SinglePathStillEndsWithDoubleNull()
    {
        var data = DropFilesData.Build([@"C:\a.png"]);

        Assert.Equal(0, data[^1]);
        Assert.Equal(0, data[^2]);
        Assert.Equal(0, data[^3]);
        Assert.Equal(0, data[^4]);
    }

    [Theory]
    [InlineData(@"C:\shot.png", true)]
    [InlineData(@"C:\shot.PNG", true)]
    [InlineData(@"C:\photo.JPEG", true)]
    [InlineData(@"C:\anim.gif", true)]
    [InlineData(@"C:\doc.pdf", false)]
    [InlineData(@"C:\no-extension", false)]
    [InlineData("", false)]
    public void ImageFileDetectionIsCaseInsensitiveAndExtensionBased(string path, bool expected)
    {
        Assert.Equal(expected, ClipFileTypes.IsImageFile(path));
    }
}
