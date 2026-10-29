namespace ClipStack.Core.Models;

/// <summary>A clipboard entry as stored in the database.</summary>
public sealed class ClipItem
{
    public long Id { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public ClipType Type { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string? ImagePath { get; init; }
    public string? SourceApp { get; init; }
    public string? SourceTitle { get; init; }
    public long CreatedAtMs { get; init; }
    public long LastUsedAtMs { get; init; }
    public int UseCount { get; init; }
    public bool Pinned { get; init; }
    public long? PinnedAtMs { get; init; }

    /// <summary>Text items whose whole content is one link; drives the 链接 filter and badge.</summary>
    public bool IsLink { get; init; }

    /// <summary>
    /// Held in memory only because its content matched the sensitivity rules: it has a negative
    /// id, never reaches the database, and disappears when the queue expires.
    /// </summary>
    public bool IsSensitive { get; init; }

    /// <summary>Pixels for a sensitive image, which must not be written to disk.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>
    /// The payload to put back on the clipboard. <paramref name="plainText"/> drops the HTML and
    /// RTF renditions, which is what "粘贴为纯文本" needs — some editors pick the rich formats
    /// even when the text looks plain.
    /// </summary>
    public ClipPayload ToPastePayload(bool plainText = false) => Type switch
    {
        ClipType.File => ClipPayload.ForFiles((Text ?? string.Empty)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)),
        ClipType.Image => ClipPayload.ForImage(ReadImage()),
        _ => plainText
            ? ClipPayload.ForText(Text ?? string.Empty)
            : ClipPayload.ForText(Text ?? string.Empty, Html, Rtf),
    };

    private byte[] ReadImage()
    {
        if (ImageBytes is { Length: > 0 })
        {
            return ImageBytes;
        }

        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
        {
            return [];
        }

        return File.ReadAllBytes(ImagePath);
    }
}
