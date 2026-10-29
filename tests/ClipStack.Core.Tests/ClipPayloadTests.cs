using ClipStack.Core.Models;

namespace ClipStack.Core.Tests;

public sealed class ClipPayloadTests
{
    [Fact]
    public void SameTextProducesSameHash()
    {
        var first = ClipPayload.ForText("hello world");
        var second = ClipPayload.ForText("hello world");

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void DifferentTextProducesDifferentHash()
    {
        Assert.NotEqual(ClipPayload.ForText("a").ContentHash, ClipPayload.ForText("b").ContentHash);
    }

    [Fact]
    public void HtmlAndRtfDoNotAffectTheHash()
    {
        var plain = ClipPayload.ForText("same text");
        var rich = ClipPayload.ForText("same text", "<b>same text</b>", @"{\rtf1 same text}");

        Assert.Equal(plain.ContentHash, rich.ContentHash);
    }

    [Fact]
    public void SameTextAndFileEntryDoNotCollide()
    {
        var text = ClipPayload.ForText(@"C:\temp\a.txt");
        var file = ClipPayload.ForFiles([@"C:\temp\a.txt"]);

        Assert.NotEqual(text.ContentHash, file.ContentHash);
    }

    [Fact]
    public void FileHashIsStableAcrossReads()
    {
        var first = ClipPayload.ForFiles([@"C:\a.txt", @"C:\b.txt"]);
        var second = ClipPayload.ForFiles([@"C:\a.txt", @"C:\b.txt"]);

        Assert.Equal(first.ContentHash, second.ContentHash);
    }
}
