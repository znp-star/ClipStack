using System.Security.Cryptography;
using System.Text;

namespace ClipStack.Core.Models;

/// <summary>
/// A clipboard snapshot as read from the OS, before it is persisted.
/// </summary>
public sealed class ClipPayload
{
    public ClipType Type { get; private init; }
    public string ContentHash { get; private init; } = string.Empty;
    public string? Text { get; private init; }
    public string? Html { get; private init; }
    public string? Rtf { get; private init; }
    public IReadOnlyList<string>? FilePaths { get; private init; }

    /// <summary>PNG bytes for image items; persisted as a file rather than a database blob.</summary>
    public byte[]? ImagePng { get; private init; }

    public string? SourceApp { get; private init; }
    public string? SourceTitle { get; private init; }

    /// <summary>
    /// The producer asked the clipboard history to skip this content
    /// (<c>ExcludeClipboardContentFromMonitorProcessing</c>), which password managers use.
    /// </summary>
    public bool Excluded { get; private init; }

    /// <summary>
    /// Packed CF_DIB pixels for image payloads. The PNG format alone is invisible to
    /// Chromium-based apps, which build paste files from the DIB; callers that can decode
    /// fill this in before the payload reaches the clipboard writer.
    /// </summary>
    public byte[]? ImageDib { get; private init; }

    public static ClipPayload ForText(string text, string? html = null, string? rtf = null) => new()
    {
        Type = ClipType.Text,
        Text = text,
        Html = html,
        Rtf = rtf,
        ContentHash = HashString(ClipType.Text, text),
    };

    public static ClipPayload ForFiles(IReadOnlyList<string> paths) => new()
    {
        Type = ClipType.File,
        FilePaths = paths,
        Text = string.Join("\r\n", paths),
        ContentHash = HashString(ClipType.File, string.Join("\n", paths)),
    };

    public static ClipPayload ForImage(byte[] png) => new()
    {
        Type = ClipType.Image,
        ImagePng = png,
        ContentHash = HashBytes(ClipType.Image, png),
    };

    public ClipPayload WithSource(string? app, string? title) => new()
    {
        Type = Type,
        ContentHash = ContentHash,
        Text = Text,
        Html = Html,
        Rtf = Rtf,
        FilePaths = FilePaths,
        ImagePng = ImagePng,
        ImageDib = ImageDib,
        SourceApp = app,
        SourceTitle = title,
        Excluded = Excluded,
    };

    /// <summary>Marks the payload as opted out of clipboard history by its producer.</summary>
    public ClipPayload WithExcluded(bool excluded) => new()
    {
        Type = Type,
        ContentHash = ContentHash,
        Text = Text,
        Html = Html,
        Rtf = Rtf,
        FilePaths = FilePaths,
        ImagePng = ImagePng,
        ImageDib = ImageDib,
        SourceApp = SourceApp,
        SourceTitle = SourceTitle,
        Excluded = excluded,
    };

    /// <summary>Attaches CF_DIB pixels to an image payload; other payload kinds ignore it.</summary>
    public ClipPayload WithDib(byte[]? dib) => new()
    {
        Type = Type,
        ContentHash = ContentHash,
        Text = Text,
        Html = Html,
        Rtf = Rtf,
        FilePaths = FilePaths,
        ImagePng = ImagePng,
        ImageDib = Type == ClipType.Image ? dib : null,
        SourceApp = SourceApp,
        SourceTitle = SourceTitle,
        Excluded = Excluded,
    };

    private static string HashString(ClipType type, string canonical) =>
        HashBytes(type, Encoding.UTF8.GetBytes(canonical));

    private static string HashBytes(ClipType type, ReadOnlySpan<byte> content)
    {
        var prefix = Encoding.UTF8.GetBytes($"{(int)type}:");
        var buffer = new byte[prefix.Length + content.Length];
        prefix.CopyTo(buffer, 0);
        content.CopyTo(buffer.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }
}
