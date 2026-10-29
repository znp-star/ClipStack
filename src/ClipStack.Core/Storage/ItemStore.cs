using ClipStack.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipStack.Core.Storage;

/// <summary>
/// SQLite-backed item store. A single connection guarded by a lock; SQLite is
/// used in WAL mode so reads never block the clipboard ingest path.
/// </summary>
public sealed class ItemStore : IDisposable
{
    private const string SelectColumns =
        "id, content_hash, type, text, html, rtf, image_path, source_app, source_title, " +
        "created_at, last_used_at, use_count, pinned, pinned_at";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS items (
            id           INTEGER PRIMARY KEY,
            content_hash TEXT    NOT NULL,
            type         TEXT    NOT NULL CHECK (type IN ('text', 'image', 'file')),
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

        CREATE UNIQUE INDEX IF NOT EXISTS ux_items_hash   ON items(content_hash);
        CREATE INDEX        IF NOT EXISTS ix_items_recent ON items(last_used_at DESC);
        CREATE INDEX        IF NOT EXISTS ix_items_pinned ON items(pinned, pinned_at DESC);

        PRAGMA user_version = 1;
        """;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public ItemStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA synchronous = NORMAL;");
        Execute(Schema);
    }

    /// <summary>
    /// Inserts a new item, or refreshes an existing one with the same content hash
    /// (bumping last_used_at / use_count instead of creating a duplicate).
    /// </summary>
    public ClipItem AddOrTouch(ClipPayload payload, long nowMs)
    {
        lock (_gate)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO items
                        (content_hash, type, text, html, rtf, image_path, source_app, source_title,
                         created_at, last_used_at, use_count, pinned)
                    VALUES
                        ($hash, $type, $text, $html, $rtf, $image, $app, $title, $now, $now, 0, 0)
                    ON CONFLICT(content_hash) DO UPDATE SET
                        last_used_at = $now,
                        use_count    = use_count + 1;
                    """;
                command.Parameters.AddWithValue("$hash", payload.ContentHash);
                command.Parameters.AddWithValue("$type", payload.Type.ToDbValue());
                command.Parameters.AddWithValue("$text", (object?)payload.Text ?? DBNull.Value);
                command.Parameters.AddWithValue("$html", (object?)payload.Html ?? DBNull.Value);
                command.Parameters.AddWithValue("$rtf", (object?)payload.Rtf ?? DBNull.Value);
                command.Parameters.AddWithValue("$image", DBNull.Value);
                command.Parameters.AddWithValue("$app", (object?)payload.SourceApp ?? DBNull.Value);
                command.Parameters.AddWithValue("$title", (object?)payload.SourceTitle ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", nowMs);
                command.ExecuteNonQuery();
            }

            return GetByHashLocked(payload.ContentHash)
                ?? throw new InvalidOperationException("Item disappeared right after insert.");
        }
    }

    /// <summary>Marks an item as used (paste, copy back) without changing its content.</summary>
    public void Touch(long id, long nowMs)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE items SET last_used_at = $now, use_count = use_count + 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Pins or unpins an item. Pinned items never expire and sort into their own block.</summary>
    public void SetPinned(long id, bool pinned, long nowMs)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = pinned
                ? "UPDATE items SET pinned = 1, pinned_at = $now WHERE id = $id;"
                : "UPDATE items SET pinned = 0, pinned_at = NULL WHERE id = $id;";
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public ClipItem? GetById(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public IReadOnlyList<ClipItem> Query(string? search, ClipType? type, int limit)
    {
        var like = string.IsNullOrWhiteSpace(search) ? null : $"%{EscapeLike(search.Trim())}%";

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SelectColumns}
                FROM items
                WHERE ($type IS NULL OR type = $type)
                  AND ($like IS NULL OR text LIKE $like ESCAPE '\')
                ORDER BY pinned DESC,
                         COALESCE(CASE WHEN pinned = 1 THEN pinned_at END, last_used_at) DESC,
                         id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$type", type is null ? DBNull.Value : type.Value.ToDbValue());
            command.Parameters.AddWithValue("$like", (object?)like ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            var items = new List<ClipItem>();
            while (reader.Read())
            {
                items.Add(Map(reader));
            }

            return items;
        }
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM items WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes unpinned items last used before <paramref name="cutoffMs"/>.</summary>
    public int DeleteOlderThan(long cutoffMs)
    {
        lock (_gate)
        {
            var imagePaths = new List<string>();

            using (var select = _connection.CreateCommand())
            {
                select.CommandText = "SELECT image_path FROM items WHERE pinned = 0 AND last_used_at < $cutoff AND image_path IS NOT NULL;";
                select.Parameters.AddWithValue("$cutoff", cutoffMs);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    imagePaths.Add(reader.GetString(0));
                }
            }

            using var delete = _connection.CreateCommand();
            delete.CommandText = "DELETE FROM items WHERE pinned = 0 AND last_used_at < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", cutoffMs);
            var removed = delete.ExecuteNonQuery();

            foreach (var path in imagePaths)
            {
                TryDeleteFile(path);
            }

            return removed;
        }
    }

    /// <summary>Deletes history; pinned items survive unless <paramref name="includePinned"/>.</summary>
    public int Clear(bool includePinned = false)
    {
        lock (_gate)
        {
            var imagePaths = new List<string>();

            using (var select = _connection.CreateCommand())
            {
                select.CommandText = includePinned
                    ? "SELECT image_path FROM items WHERE image_path IS NOT NULL;"
                    : "SELECT image_path FROM items WHERE pinned = 0 AND image_path IS NOT NULL;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    imagePaths.Add(reader.GetString(0));
                }
            }

            using var delete = _connection.CreateCommand();
            delete.CommandText = includePinned ? "DELETE FROM items;" : "DELETE FROM items WHERE pinned = 0;";
            var removed = delete.ExecuteNonQuery();

            foreach (var path in imagePaths)
            {
                TryDeleteFile(path);
            }

            return removed;
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM items;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    private ClipItem? GetByHashLocked(string hash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM items WHERE content_hash = $hash;";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static ClipItem Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ContentHash = reader.GetString(1),
        Type = ClipTypeNames.FromDbValue(reader.GetString(2)),
        Text = reader.IsDBNull(3) ? null : reader.GetString(3),
        Html = reader.IsDBNull(4) ? null : reader.GetString(4),
        Rtf = reader.IsDBNull(5) ? null : reader.GetString(5),
        ImagePath = reader.IsDBNull(6) ? null : reader.GetString(6),
        SourceApp = reader.IsDBNull(7) ? null : reader.GetString(7),
        SourceTitle = reader.IsDBNull(8) ? null : reader.GetString(8),
        CreatedAtMs = reader.GetInt64(9),
        LastUsedAtMs = reader.GetInt64(10),
        UseCount = reader.GetInt32(11),
        Pinned = reader.GetInt64(12) != 0,
        PinnedAtMs = reader.IsDBNull(13) ? null : reader.GetInt64(13),
    };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked image file is harmless; it is retried on the next purge.
        }
    }
}
