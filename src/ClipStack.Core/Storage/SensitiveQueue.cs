using System.Text;
using ClipStack.Core.Models;

namespace ClipStack.Core.Storage;

/// <summary>
/// In-memory holding area for payloads that matched the sensitivity rules. Nothing here reaches
/// the database or the disk: entries expire on a timer of their own, the queue is capped in size,
/// and it disappears with the process.
/// </summary>
public sealed class SensitiveQueue
{
    private readonly List<ClipItem> _items = [];
    private readonly object _gate = new();
    private long _nextId = -1;

    public SensitiveQueue(int maxItems = 50, long maxBytes = 100L * 1024 * 1024, TimeSpan? ttl = null)
    {
        MaxItems = maxItems > 0 ? maxItems : 50;
        MaxBytes = maxBytes > 0 ? maxBytes : 100L * 1024 * 1024;
        Ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    public int MaxItems { get; }

    public long MaxBytes { get; }

    /// <summary>How long an entry stays available; reset from settings on startup.</summary>
    public TimeSpan Ttl { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _items.Sum(EstimateBytes);
            }
        }
    }

    /// <summary>
    /// Stores a copy of the payload in memory. The same content copied twice refreshes the
    /// existing entry instead of duplicating it, mirroring how the database de-duplicates.
    /// </summary>
    public ClipItem Add(ClipPayload payload, long nowMs)
    {
        lock (_gate)
        {
            PurgeLocked(nowMs);

            var existingIndex = _items.FindIndex(item => item.ContentHash == payload.ContentHash);

            // Reuse the id of the replaced entry so the panel keeps its selection.
            var id = existingIndex >= 0 ? _items[existingIndex].Id : _nextId--;
            if (existingIndex >= 0)
            {
                _items.RemoveAt(existingIndex);
            }

            var item = new ClipItem
            {
                Id = id,
                ContentHash = payload.ContentHash,
                Type = payload.Type,
                Text = payload.Text,
                Html = payload.Html,
                Rtf = payload.Rtf,
                ImageBytes = payload.Type == ClipType.Image ? payload.ImagePng : null,
                SourceApp = payload.SourceApp,
                SourceTitle = payload.SourceTitle,
                CreatedAtMs = nowMs,
                LastUsedAtMs = nowMs,
                UseCount = 1,
                Pinned = false,
                IsLink = payload.Type == ClipType.Text && LinkText.Is(payload.Text),
                IsSensitive = true,
            };

            _items.Add(item);
            TrimLocked();
            return item;
        }
    }

    /// <summary>Newest first, matching the time flow of the stored history.</summary>
    public IReadOnlyList<ClipItem> Query(string? search, ClipType? type, bool linksOnly, long nowMs)
    {
        lock (_gate)
        {
            PurgeLocked(nowMs);

            var needle = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

            return _items
                .Where(item => type is null || item.Type == type.Value)
                .Where(item => !linksOnly || item.IsLink)
                .Where(item => needle is null
                    || (item.Text ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastUsedAtMs)
                .ThenByDescending(item => item.Id)
                .ToList();
        }
    }

    public ClipItem? GetById(long id)
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(item => item.Id == id);
        }
    }

    public bool Delete(long id)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            _items.RemoveAt(index);
            return true;
        }
    }

    /// <summary>Drops everything past the TTL. Called on every add, query and timer tick.</summary>
    public int Purge(long nowMs)
    {
        lock (_gate)
        {
            return PurgeLocked(nowMs);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    private int PurgeLocked(long nowMs)
    {
        var cutoff = nowMs - (long)Ttl.TotalMilliseconds;
        return _items.RemoveAll(item => item.CreatedAtMs < cutoff);
    }

    private void TrimLocked()
    {
        while (_items.Count > MaxItems)
        {
            _items.RemoveAt(0);
        }

        while (_items.Count > 1 && _items.Sum(EstimateBytes) > MaxBytes)
        {
            _items.RemoveAt(0);
        }
    }

    private static long EstimateBytes(ClipItem item)
    {
        if (item.ImageBytes is { Length: > 0 } bytes)
        {
            return bytes.LongLength;
        }

        var total = 0L;
        total += item.Text is null ? 0 : Encoding.UTF8.GetByteCount(item.Text);
        total += item.Html is null ? 0 : Encoding.UTF8.GetByteCount(item.Html);
        total += item.Rtf is null ? 0 : Encoding.UTF8.GetByteCount(item.Rtf);
        return total;
    }
}
