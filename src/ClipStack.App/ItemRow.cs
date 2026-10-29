using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Models;

namespace ClipStack.App;

/// <summary>
/// A list row: the stored item plus the one-line-per-item display data. Rows carry no
/// source, time, size or type label — the panel is a picker, not a log viewer.
/// </summary>
public sealed class ItemRow
{
    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    public ItemRow(ClipItem item)
    {
        Item = item;
        Thumbnail = ResolveThumbnail(item);
        Preview = BuildPreview(item);
    }

    public ClipItem Item { get; }

    /// <summary>Row text, rendered as at most two wrapped lines.</summary>
    public string Preview { get; }

    public bool IsPinned => Item.Pinned;

    /// <summary>In-memory entry that never reached the database; the row wears a badge for it.</summary>
    public bool IsSensitive => Item.IsSensitive;

    /// <summary>Image rows are the thumbnail alone; "图片" as text would say nothing.</summary>
    public Visibility PreviewVisibility =>
        Item.Type == ClipType.Image ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"未保存" chip: the row has no database record behind it, so say so.</summary>
    public Visibility SensitiveBadgeVisibility =>
        Item.IsSensitive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Row thumbnail, either decoded from a stored PNG file or from the pixels a sensitive image
    /// keeps in memory. Bound directly by the row template.
    /// </summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>
    /// Image items point at their stored PNG. File items can show a thumbnail too when they hold
    /// a single image file, so copying a screenshot file does not look like an opaque path.
    /// </summary>
    private static ImageSource? ResolveThumbnail(ClipItem item)
    {
        if (item.ImageBytes is { Length: > 0 } bytes)
        {
            return FromBytes(bytes);
        }

        var path = ResolveThumbnailPath(item);
        return path is null ? null : FromFile(path);
    }

    private static string? ResolveThumbnailPath(ClipItem item)
    {
        if (item.Type == ClipType.Image)
        {
            return string.IsNullOrEmpty(item.ImagePath) || !File.Exists(item.ImagePath) ? null : item.ImagePath;
        }

        if (item.Type != ClipType.File)
        {
            return null;
        }

        var paths = (item.Text ?? string.Empty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return paths.Length == 1 && ClipFileTypes.IsImageFile(paths[0]) && File.Exists(paths[0])
            ? paths[0]
            : null;
    }

    /// <summary>Decoding at roughly twice the display size keeps big screenshots cheap.</summary>
    private static ImageSource FromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnDemand;
        bitmap.DecodePixelWidth = 112;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        return bitmap;
    }

    private static ImageSource FromBytes(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 112;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string BuildPreview(ClipItem item)
    {
        if (item.Type == ClipType.File)
        {
            var paths = (item.Text ?? string.Empty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0)
            {
                return "（空文件列表）";
            }

            var names = paths.Take(2).Select(p => Path.GetFileName(p.TrimEnd('\\', '/')));
            var preview = string.Join("，", names);
            return paths.Length > 2 ? $"{preview} 等 {paths.Length} 个文件" : preview;
        }

        if (item.Type == ClipType.Image)
        {
            return string.Empty;
        }

        var text = (item.Text ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');
        text = WhitespaceRun.Replace(text, " ").Trim();

        return text.Length == 0
            ? "（空白内容）"
            : text.Length > 240 ? text[..240] + "…" : text;
    }
}
