using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
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
        ThumbnailPath = ResolveThumbnail(item);
        Preview = BuildPreview(item);
    }

    public ClipItem Item { get; }

    /// <summary>Row text, rendered as at most two wrapped lines.</summary>
    public string Preview { get; }

    public bool IsPinned => Item.Pinned;

    /// <summary>Image rows are the thumbnail alone; "图片" as text would say nothing.</summary>
    public Visibility PreviewVisibility =>
        Item.Type == ClipType.Image ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Local image file for the row thumbnail; bound directly by the row template.</summary>
    public string? ThumbnailPath { get; }

    /// <summary>
    /// Image items point at their stored PNG. File items can show a thumbnail too when they hold
    /// a single image file, so copying a screenshot file does not look like an opaque path.
    /// </summary>
    private static string? ResolveThumbnail(ClipItem item)
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
