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

    public ClipPayload ToPastePayload() => Type switch
    {
        ClipType.File => ClipPayload.ForFiles((Text ?? string.Empty)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)),
        ClipType.Image => ClipPayload.ForImage(ReadImage()),
        _ => ClipPayload.ForText(Text ?? string.Empty, Html, Rtf),
    };

    private byte[] ReadImage()
    {
        if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
        {
            return [];
        }

        return File.ReadAllBytes(ImagePath);
    }
}
