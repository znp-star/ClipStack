using System.IO;
using System.Text.RegularExpressions;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Models;

namespace ClipStack.App;

/// <summary>A list row: the stored item plus its pre-rendered display strings.</summary>
public sealed class ItemRow
{
    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    public ItemRow(ClipItem item)
    {
        Item = item;
        ThumbnailPath = ResolveThumbnail(item);
        Badge = item.Type == ClipType.File && ThumbnailPath is not null
            ? "图片"
            : item.Type switch
            {
                ClipType.Image => "图片",
                ClipType.File => "文件",
                _ => "文本",
            };
        Preview = BuildPreview(item);
        Meta = BuildMeta(item, ThumbnailPath);
    }

    public ClipItem Item { get; }
    public string Badge { get; }
    public string Preview { get; }
    public string Meta { get; }

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
            return "图片";
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

    private static string BuildMeta(ClipItem item, string? thumbnailPath)
    {
        var app = string.IsNullOrWhiteSpace(item.SourceApp) ? "未知来源" : item.SourceApp;
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(item.LastUsedAtMs);

        var time = age.TotalSeconds switch
        {
            < 60 => "刚刚",
            < 3600 => $"{(int)age.TotalMinutes} 分钟前",
            < 86400 => $"{(int)age.TotalHours} 小时前",
            _ => $"{(int)age.TotalDays} 天前",
        };

        var sizeSource = thumbnailPath ?? (item.Type == ClipType.Image ? item.ImagePath : null);
        var size = sizeSource is null ? null : FormatSize(sizeSource);
        var middle = size is null ? time : $"{time} · {size}";

        return item.Pinned ? $"已置顶 · {app}" : $"{app} · {middle}";
    }

    private static string? FormatSize(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            var bytes = new FileInfo(path).Length;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024} KB",
                _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
            };
        }
        catch (IOException)
        {
            return null;
        }
    }
}
