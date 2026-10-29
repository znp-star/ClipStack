using ClipStack.Core.Models;
using ClipStack.Core.Security;

namespace ClipStack.Core.Tests;

public sealed class SensitivityFilterTests
{
    private static SensitivityVerdict Evaluate(string text, string? app = null, string? title = null) =>
        SensitivityFilter.Evaluate(ClipPayload.ForText(text).WithSource(app, title));

    [Fact]
    public void ProducerOptOutWins()
    {
        var verdict = SensitivityFilter.Evaluate(ClipPayload.ForText("plain").WithExcluded(true));

        Assert.True(verdict.IsSensitive);
        Assert.Equal(SensitivityReason.ProducerOptOut, verdict.Reason);
    }

    [Theory]
    [InlineData("KeePass")]
    [InlineData("1Password")]
    [InlineData("Bitwarden")]
    [InlineData("LastPass")]
    public void PasswordManagersAreSensitive(string app)
    {
        var verdict = Evaluate("any text", app, "vault");

        Assert.True(verdict.IsSensitive);
        Assert.Equal(SensitivityReason.Source, verdict.Reason);
    }

    [Theory]
    [InlineData("New tab - InPrivate")]
    [InlineData("Example - Incognito")]
    [InlineData("百度一下 - 无痕窗口")]
    public void PrivateBrowsingTitlesAreSensitive(string title)
    {
        var verdict = Evaluate("any text", "chrome", title);

        Assert.True(verdict.IsSensitive);
        Assert.Equal(SensitivityReason.Source, verdict.Reason);
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("card 4111 1111 1111 1111 on file")]
    [InlineData("4111111111111111")]
    public void SecretPatternsAreSensitive(string text)
    {
        var verdict = Evaluate(text);

        Assert.True(verdict.IsSensitive);
        Assert.Equal(SensitivityReason.Content, verdict.Reason);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("订单 2026-10-30 编号 12345")]
    [InlineData("4111 1111 1111 1112")] // same shape as a card, but the checksum fails
    [InlineData("1234567890123")]
    public void OrdinaryTextStaysClean(string text)
    {
        var verdict = Evaluate(text);

        Assert.False(verdict.IsSensitive);
        Assert.Equal(SensitivityReason.None, verdict.Reason);
    }

    [Fact]
    public void ImagesWithoutSourceOrPatternStayClean()
    {
        var verdict = SensitivityFilter.Evaluate(ClipPayload.ForImage([1, 2, 3]));

        Assert.False(verdict.IsSensitive);
    }
}
