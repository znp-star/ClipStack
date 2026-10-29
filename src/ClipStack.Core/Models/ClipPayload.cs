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
    public string? SourceApp { get; private init; }
    public string? SourceTitle { get; private init; }

    public static ClipPayload ForText(string text, string? html = null, string? rtf = null) => new()
    {
        Type = ClipType.Text,
        Text = text,
        Html = html,
        Rtf = rtf,
        ContentHash = Hash(ClipType.Text, text),
    };

    public static ClipPayload ForFiles(IReadOnlyList<string> paths) => new()
    {
        Type = ClipType.File,
        FilePaths = paths,
        Text = string.Join("\r\n", paths),
        ContentHash = Hash(ClipType.File, string.Join("\n", paths)),
    };

    public ClipPayload WithSource(string? app, string? title) => new()
    {
        Type = Type,
        ContentHash = ContentHash,
        Text = Text,
        Html = Html,
        Rtf = Rtf,
        FilePaths = FilePaths,
        SourceApp = app,
        SourceTitle = title,
    };

    private static string Hash(ClipType type, string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes($"{(int)type}:{canonical}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
