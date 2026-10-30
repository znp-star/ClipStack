using ClipStack.Core.Storage;

namespace ClipStack.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "clipstack-tests", Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // The temp folder is disposable.
        }
    }

    [Fact]
    public void MigrateMovesDatabaseSettingsImagesAndWal()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(legacy, "images"));
        File.WriteAllText(Path.Combine(legacy, "clipstack.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "clipstack.db-wal"), "wal");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "images", "a.png"), "png");

        var result = AppPaths.Migrate(legacy, target);

        Assert.NotNull(result);
        Assert.True(result!.Value.Succeeded);
        Assert.Equal("db", File.ReadAllText(Path.Combine(target, "clipstack.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(target, "clipstack.db-wal")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(target, "images", "a.png")));
    }

    [Fact]
    public void MigrateRemovesAFolderThatHeldOnlyOurData()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "clipstack.db"), "db");

        AppPaths.Migrate(legacy, target);

        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void MigrateKeepsAnInstalledAppInPlace()
    {
        // Up to 0.1.1 the install folder and the data folder could be the same directory.
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(legacy, "current"));
        File.WriteAllText(Path.Combine(legacy, "clipstack.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "Update.exe"), "stub");

        var result = AppPaths.Migrate(legacy, target);

        Assert.True(result!.Value.Succeeded);
        Assert.True(File.Exists(Path.Combine(target, "clipstack.db")));
        Assert.True(Directory.Exists(Path.Combine(legacy, "current")));
        Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
    }

    [Fact]
    public void MigrateDoesNothingWhenTheTargetAlreadyHasADatabase()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "data");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "clipstack.db"), "old");
        File.WriteAllText(Path.Combine(target, "clipstack.db"), "new");

        Assert.Null(AppPaths.Migrate(legacy, target));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "clipstack.db")));
        Assert.True(File.Exists(Path.Combine(legacy, "clipstack.db")));
    }

    [Fact]
    public void MigrateDoesNothingWithoutALegacyDatabase()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        Assert.Null(AppPaths.Migrate(legacy, target));
        Assert.False(Directory.Exists(target));
    }
}
