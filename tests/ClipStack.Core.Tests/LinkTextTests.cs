using ClipStack.Core.Models;

namespace ClipStack.Core.Tests;

public sealed class LinkTextTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:5080/path?q=1#frag")]
    [InlineData("www.github.com/dotnet")]
    [InlineData("HTTPS://EXAMPLE.COM/A")]
    [InlineData("  https://example.com/trimmed  ")]
    public void RecognisesSingleLinks(string text) => Assert.True(LinkText.Is(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("看看这个 https://example.com")]
    [InlineData("https://example.com 和 https://other.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("mailto:someone@example.com")]
    public void RejectsEverythingElse(string? text) => Assert.False(LinkText.Is(text));
}
