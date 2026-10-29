using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;

namespace ClipStack.App;

/// <summary>
/// A list row: the stored item plus the one-line-per-item display data. Rows carry no
/// source, time, size or type label — the panel is a picker, not a log viewer.
/// </summary>
public sealed class ItemRow
{
    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Long side of the hover preview. The tip is capped at 520x390 DIP, so 800 still looks sharp
    /// at 150% scale while keeping a 4K screenshot from decoding at native size.
    /// </summary>
    private const int PreviewLongSide = 800;

    private ImageSource? _preview;
    private bool _previewResolved;

    public ItemRow(ClipItem item)
    {
        Item = item;
        Thumbnail = ResolveThumbnail(item);
        (Preview, IsPreviewTrimmed) = BuildPreview(item);
    }

    public ClipItem Item { get; }

    /// <summary>Row text, rendered as at most two wrapped lines.</summary>
    public string Preview { get; }

    /// <summary>
    /// True when <see cref="Preview"/> itself dropped content, so the row shows an ellipsis even if
    /// it happens to fit in two lines. The tip uses this to decide whether the full text is worth
    /// showing.
    /// </summary>
    public bool IsPreviewTrimmed { get; }

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
    /// Larger decode of the same pixels, used by the hover tip. Resolved on first use and kept for
    /// the row's lifetime: the row lives exactly as long as the item, so nothing outlives the list
    /// while a second hover on the same row costs nothing.
    /// </summary>
    public ImageSource? PreviewImage
    {
        get
        {
            if (_previewResolved)
            {
                return _preview;
            }

            _previewResolved = true;

            try
            {
                if (Item.ImageBytes is { Length: > 0 } bytes)
                {
                    _preview = PreviewFromBytes(bytes);
                    return _preview;
                }

                var path = ResolveThumbnailPath(Item);
                _preview = path is null ? null : PreviewFromFile(path);
            }
            catch (Exception ex)
            {
                // A broken or locked file must not take the panel down; the tip simply never opens.
                Log.Warn($"Preview could not be decoded: {ex.Message}");
                _preview = null;
            }

            return _preview;
        }
    }

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

    private static ImageSource PreviewFromFile(string path)
    {
        int width;
        int height;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            (width, height) = ReadPixelSize(stream);
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        ApplyLongSide(bitmap, width, height);
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageSource PreviewFromBytes(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var (width, height) = ReadPixelSize(stream);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        ApplyLongSide(bitmap, width, height);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Reads just the header: setting both DecodePixelWidth and DecodePixelHeight would stretch the
    /// image, so the long side is capped and the other axis follows the aspect ratio.
    /// </summary>
    private static (int Width, int Height) ReadPixelSize(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static void ApplyLongSide(BitmapImage bitmap, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (width >= height)
        {
            if (width > PreviewLongSide)
            {
                bitmap.DecodePixelWidth = PreviewLongSide;
            }
        }
        else if (height > PreviewLongSide)
        {
            bitmap.DecodePixelHeight = PreviewLongSide;
        }
    }

    private static (string Text, bool Trimmed) BuildPreview(ClipItem item)
    {
        if (item.Type == ClipType.File)
        {
            var paths = (item.Text ?? string.Empty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length == 0)
            {
                return ("（空文件列表）", false);
            }

            var names = paths.Take(2).Select(p => Path.GetFileName(p.TrimEnd('\\', '/')));
            var preview = string.Join("，", names);
            return paths.Length > 2
                ? ($"{preview} 等 {paths.Length} 个文件", true)
                : (preview, false);
        }

        if (item.Type == ClipType.Image)
        {
            return (string.Empty, false);
        }

        var text = (item.Text ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');
        text = WhitespaceRun.Replace(text, " ").Trim();

        if (text.Length == 0)
        {
            return ("（空白内容）", false);
        }

        return text.Length > 240 ? (text[..240] + "…", true) : (text, false);
    }
}
