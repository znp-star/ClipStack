using ClipStack.Core.Models;
using ClipStack.Core.Storage;

namespace ClipStack.Core.Tests;

public sealed class SensitiveQueueTests
{
    private const long Minute = 60_000;

    [Fact]
    public void AddKeepsItemInMemoryWithNegativeId()
    {
        var queue = new SensitiveQueue();

        var item = queue.Add(ClipPayload.ForText("secret"), 1_000);

        Assert.True(item.IsSensitive);
        Assert.True(item.Id < 0);
        Assert.Equal(1, queue.Count);
        Assert.Same(item, queue.GetById(item.Id));
    }

    [Fact]
    public void SameContentRefreshesInsteadOfDuplicating()
    {
        var queue = new SensitiveQueue();

        var first = queue.Add(ClipPayload.ForText("secret"), 1_000);
        var second = queue.Add(ClipPayload.ForText("secret"), 2_000);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2_000, second.LastUsedAtMs);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void ImagePayloadsKeepTheirPixels()
    {
        var queue = new SensitiveQueue();

        var item = queue.Add(ClipPayload.ForImage([9, 9, 9]), 1_000);

        Assert.Equal(new byte[] { 9, 9, 9 }, item.ImageBytes);
        Assert.Null(item.ImagePath);
        Assert.Equal(3, queue.TotalBytes);
    }

    [Fact]
    public void QueryIsNewestFirstAndFilters()
    {
        var queue = new SensitiveQueue();
        queue.Add(ClipPayload.ForText("first secret"), 1_000);
        queue.Add(ClipPayload.ForText("https://example.com"), 2_000);
        queue.Add(ClipPayload.ForImage([1]), 3_000);

        var all = queue.Query(null, null, false, 3_000);
        Assert.Equal(3, all.Count);
        Assert.Equal(ClipType.Image, all[0].Type);

        var text = queue.Query(null, ClipType.Text, false, 3_000);
        Assert.Equal(2, text.Count);

        var links = queue.Query(null, ClipType.Text, true, 3_000);
        Assert.Single(links);
        Assert.True(links[0].IsLink);

        var searched = queue.Query("first", null, false, 3_000);
        Assert.Single(searched);
    }

    [Fact]
    public void EntriesExpireAfterTheirTtl()
    {
        var queue = new SensitiveQueue(ttl: TimeSpan.FromMinutes(10));
        queue.Add(ClipPayload.ForText("secret"), 0);

        Assert.Single(queue.Query(null, null, false, 9 * Minute));
        Assert.Equal(0, queue.Purge(9 * Minute));

        Assert.Empty(queue.Query(null, null, false, 11 * Minute));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void QueueDropsOldestEntriesBeyondItsCap()
    {
        var queue = new SensitiveQueue(maxItems: 2);

        queue.Add(ClipPayload.ForText("one"), 1_000);
        queue.Add(ClipPayload.ForText("two"), 2_000);
        queue.Add(ClipPayload.ForText("three"), 3_000);

        var remaining = queue.Query(null, null, false, 3_000);
        Assert.Equal(2, remaining.Count);
        Assert.Equal("three", remaining[0].Text);
        Assert.Equal("two", remaining[1].Text);
    }

    [Fact]
    public void QueueDropsOldestEntriesBeyondItsByteBudget()
    {
        var queue = new SensitiveQueue(maxBytes: 10);

        queue.Add(ClipPayload.ForText(new string('a', 8)), 1_000);
        queue.Add(ClipPayload.ForText(new string('b', 8)), 2_000);

        var remaining = queue.Query(null, null, false, 2_000);
        Assert.Single(remaining);
        Assert.Equal(2_000, remaining[0].LastUsedAtMs);
    }

    [Fact]
    public void DeleteAndClearRemoveEntries()
    {
        var queue = new SensitiveQueue();
        var item = queue.Add(ClipPayload.ForText("secret"), 1_000);

        Assert.True(queue.Delete(item.Id));
        Assert.False(queue.Delete(item.Id));
        Assert.Equal(0, queue.Count);

        queue.Add(ClipPayload.ForText("again"), 2_000);
        queue.Clear();
        Assert.Equal(0, queue.Count);
    }
}
