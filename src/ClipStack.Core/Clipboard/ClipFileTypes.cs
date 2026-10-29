namespace ClipStack.Core.Clipboard;

/// <summary>File-name helpers for deciding what a file drop really contains.</summary>
public static class ClipFileTypes
{
    /// <summary>Extensions WPF can render straight from disk, so a file row can show a thumbnail.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
    };

    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && ImageExtensions.Contains(extension);
    }
}
