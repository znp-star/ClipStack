using ClipStack.Core.Models;
using ClipStack.Core.Storage;

namespace ClipStack.Core.Tests;

public sealed class ItemStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly ItemStore _store;

    public ItemStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clipstack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new ItemStore(Path.Combine(_directory, "test.db"));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // SQLite may keep the WAL file open a moment longer; the temp folder is disposable.
        }
    }

    [Fact]
    public void InsertCreatesOneItem()
    {
        var item = _store.AddOrTouch(ClipPayload.ForText("hello"), 1_000);

        Assert.True(item.Id > 0);
        Assert.Equal(ClipType.Text, item.Type);
        Assert.Equal("hello", item.Text);
        Assert.Equal(0, item.UseCount);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void SameContentIsDeduplicatedAndBumped()
    {
        var first = _store.AddOrTouch(ClipPayload.ForText("hello"), 1_000);
        var second = _store.AddOrTouch(ClipPayload.ForText("hello"), 2_000);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.UseCount);
        Assert.Equal(2_000, second.LastUsedAtMs);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void DifferentContentCreatesSecondItem()
    {
        _store.AddOrTouch(ClipPayload.ForText("one"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("two"), 2_000);

        Assert.Equal(2, _store.Count());
    }

    [Fact]
    public void QueryReturnsMostRecentlyUsedFirst()
    {
        _store.AddOrTouch(ClipPayload.ForText("old"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("new"), 2_000);

        var items = _store.Query(null, null, 10);

        Assert.Equal(["new", "old"], items.Select(i => i.Text));
    }

    [Fact]
    public void TouchingMovesAnItemToTheTop()
    {
        var old = _store.AddOrTouch(ClipPayload.ForText("old"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("new"), 2_000);

        _store.Touch(old.Id, 3_000);

        Assert.Equal("old", _store.Query(null, null, 10)[0].Text);
    }

    [Fact]
    public void SearchFiltersOnText()
    {
        _store.AddOrTouch(ClipPayload.ForText("docker compose up"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("git status"), 2_000);

        var items = _store.Query("dock", null, 10);

        Assert.Single(items);
        Assert.Equal("docker compose up", items[0].Text);
    }

    [Fact]
    public void SearchTreatsLikeWildcardsLiterally()
    {
        _store.AddOrTouch(ClipPayload.ForText("100% done"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("1000 done"), 2_000);

        var items = _store.Query("100%", null, 10);

        Assert.Single(items);
        Assert.Equal("100% done", items[0].Text);
    }

    [Fact]
    public void ExpiredItemsArePurgedButPinnedSurvive()
    {
        var expired = _store.AddOrTouch(ClipPayload.ForText("expired"), 1_000);
        var pinned = _store.AddOrTouch(ClipPayload.ForText("pinned"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("fresh"), 10_000);
        _store.SetPinned(pinned.Id, true, 1_000);

        var removed = _store.DeleteOlderThan(cutoffMs: 5_000);

        Assert.Equal(1, removed);
        var remaining = _store.Query(null, null, 10);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, i => i.Text == "pinned" && i.Pinned);
        Assert.Contains(remaining, i => i.Text == "fresh");
        Assert.DoesNotContain(remaining, i => i.Id == expired.Id);
    }

    [Fact]
    public void ClearKeepsPinnedItems()
    {
        var pinned = _store.AddOrTouch(ClipPayload.ForText("keep me"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("drop me"), 2_000);
        _store.SetPinned(pinned.Id, true, 1_000);

        var removed = _store.Clear();

        Assert.Equal(1, removed);
        Assert.Single(_store.Query(null, null, 10));
    }

    [Fact]
    public void PinnedItemsSortAboveRecentOnes()
    {
        var pinned = _store.AddOrTouch(ClipPayload.ForText("pinned"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("recent"), 5_000);
        _store.SetPinned(pinned.Id, true, 6_000);

        var items = _store.Query(null, null, 10);

        Assert.Equal("pinned", items[0].Text);
        Assert.True(items[0].Pinned);
    }
}
