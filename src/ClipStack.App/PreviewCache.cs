using System.Windows.Media;

namespace ClipStack.App;

/// <summary>
/// A tiny least-recently-used cache for decoded hover previews. Previewing row after row would
/// otherwise keep every 800px decode alive for as long as the list lives: a page of screenshot
/// rows costs a couple of megabytes each, so sweeping the list once could add hundreds.
/// </summary>
internal sealed class PreviewCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);

    // Most recently used first; the tail is what gets dropped.
    private readonly LinkedList<Entry> _order = new();

    public PreviewCache(int capacity) => _capacity = Math.Max(1, capacity);

    public int Count => _entries.Count;

    /// <summary>
    /// Returns the cached preview for <paramref name="key"/>, decoding it with
    /// <paramref name="decode"/> on a miss. A failed decode is cached as null too, so a broken
    /// file is not reopened on every hover.
    /// </summary>
    public ImageSource? Get(string key, Func<ImageSource?> decode)
    {
        if (_entries.TryGetValue(key, out var hit))
        {
            _order.Remove(hit);
            _order.AddFirst(hit);
            return hit.Value.Image;
        }

        var image = decode();
        var node = _order.AddFirst(new Entry(key, image));
        _entries[key] = node;

        while (_entries.Count > _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }

        return image;
    }

    /// <summary>Drops every decode; the panel calls this when it hides.</summary>
    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
    }

    private readonly record struct Entry(string Key, ImageSource? Image);
}
