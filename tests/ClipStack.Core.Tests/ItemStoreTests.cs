using ClipStack.Core.Models;
using ClipStack.Core.Storage;
using Microsoft.Data.Sqlite;

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

    [Fact]
    public void QueryFiltersByType()
    {
        _store.AddOrTouch(ClipPayload.ForText("a snippet"), 1_000);
        _store.AddOrTouch(ClipPayload.ForImage([1, 2, 3, 4]), 2_000);

        var texts = _store.Query(null, ClipType.Text, 10);
        var images = _store.Query(null, ClipType.Image, 10);

        Assert.Equal(["a snippet"], texts.Select(i => i.Text));
        Assert.Single(images);
        Assert.Equal(ClipType.Image, images[0].Type);
    }

    [Fact]
    public void UpdateTextRewritesContentAndDropsRichFormats()
    {
        var item = _store.AddOrTouch(ClipPayload.ForText("before", html: "<b>before</b>"), 1_000);
        _store.SetPinned(item.Id, true, 1_000);

        var saved = _store.UpdateText(item.Id, "after", ClipPayload.ForText("after").ContentHash, 2_000);

        Assert.True(saved);
        var updated = _store.GetById(item.Id);
        Assert.NotNull(updated);
        Assert.Equal("after", updated.Text);
        Assert.Null(updated.Html);
        Assert.Equal(ClipPayload.ForText("after").ContentHash, updated.ContentHash);
        Assert.True(updated.Pinned);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void UpdateTextRefusesContentOwnedByAnotherItem()
    {
        _store.AddOrTouch(ClipPayload.ForText("first"), 1_000);
        var second = _store.AddOrTouch(ClipPayload.ForText("second"), 2_000);

        var saved = _store.UpdateText(second.Id, "first", ClipPayload.ForText("first").ContentHash, 3_000);

        Assert.False(saved);
        Assert.Equal("second", _store.GetById(second.Id)!.Text);
        Assert.Equal(2, _store.Count());
    }

    [Fact]
    public void DeleteRemovesTheImageFile()
    {
        var item = _store.AddOrTouch(ClipPayload.ForImage([9, 8, 7]), 1_000);
        Assert.NotNull(item.ImagePath);
        Assert.True(File.Exists(item.ImagePath));

        _store.Delete(item.Id);

        Assert.Null(_store.GetById(item.Id));
        Assert.False(File.Exists(item.ImagePath));
        Assert.Equal(0, _store.Count());
    }

    [Fact]
    public void LinkItemsAreFlaggedAndFilterable()
    {
        _store.AddOrTouch(ClipPayload.ForText("https://example.com/docs"), 1_000);
        _store.AddOrTouch(ClipPayload.ForText("see https://example.com/docs for details"), 2_000);

        var links = _store.Query(null, ClipType.Text, 10, linksOnly: true);
        var texts = _store.Query(null, ClipType.Text, 10);

        Assert.Single(links);
        Assert.True(links[0].IsLink);
        Assert.Equal("https://example.com/docs", links[0].Text);
        Assert.Equal(2, texts.Count);
        Assert.False(texts[0].IsLink);
    }

    [Fact]
    public void UpdateTextReclassifiesLinks()
    {
        var item = _store.AddOrTouch(ClipPayload.ForText("plain words"), 1_000);

        _store.UpdateText(item.Id, "https://example.com", ClipPayload.ForText("https://example.com").ContentHash, 2_000);
        Assert.True(_store.GetById(item.Id)!.IsLink);

        _store.UpdateText(item.Id, "plain again", ClipPayload.ForText("plain again").ContentHash, 3_000);
        Assert.False(_store.GetById(item.Id)!.IsLink);
    }

    [Fact]
    public void ExistingDatabaseGainsLinkColumnAndClassification()
    {
        var path = Path.Combine(_directory, "legacy.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE items (
                    id           INTEGER PRIMARY KEY,
                    content_hash TEXT    NOT NULL,
                    type         TEXT    NOT NULL,
                    text         TEXT,
                    html         TEXT,
                    rtf          TEXT,
                    image_path   TEXT,
                    source_app   TEXT,
                    source_title TEXT,
                    created_at   INTEGER NOT NULL,
                    last_used_at INTEGER NOT NULL,
                    use_count    INTEGER NOT NULL DEFAULT 0,
                    pinned       INTEGER NOT NULL DEFAULT 0,
                    pinned_at    INTEGER
                );

                INSERT INTO items (content_hash, type, text, created_at, last_used_at)
                VALUES ('h1', 'text', 'https://example.com/a', 1, 1),
                       ('h2', 'text', 'ordinary note', 1, 1);
                """;
            create.ExecuteNonQuery();
        }

        using var store = new ItemStore(path);

        var links = store.Query(null, ClipType.Text, 10, linksOnly: true);
        Assert.Single(links);
        Assert.Equal("https://example.com/a", links[0].Text);
        Assert.Equal(2, store.Query(null, null, 10).Count);
    }
}
