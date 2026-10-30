namespace ClipStack.Core.Storage;

public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Overrides the data root, for tests and portable runs.</summary>
    public static void UseRoot(string root) => _rootOverride = root;

    public static string Root => _rootOverride ?? DefaultRoot;

    /// <summary>
    /// Local application data, deliberately beside Velopack's install folder rather than inside it.
    /// Velopack owns <c>%LOCALAPPDATA%\ClipStack</c>: it treats a non-empty folder there as an
    /// existing installation (so Setup offers to "repair" and empties it), and uninstalling deletes
    /// the whole folder. Local rather than roaming, because clipboard history must not travel with
    /// a roaming profile.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack-data");

    /// <summary>Where 0.1.1 and earlier kept their data, inside Velopack's install folder.</summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack");

    public static string Database => Path.Combine(Root, "clipstack.db");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Images => Path.Combine(Root, "images");
    public static string Logs => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>
    /// Moves data out of <see cref="LegacyRoot"/>, once, on the first launch of a version that no
    /// longer writes there. Returns null when there was nothing to move (or a root override is set).
    /// </summary>
    public static (bool Succeeded, string Message)? MigrateLegacyRoot() =>
        _rootOverride is null ? Migrate(LegacyRoot, DefaultRoot) : null;

    /// <summary>Takes the directories as arguments so the move can be tested against temp folders.</summary>
    public static (bool Succeeded, string Message)? Migrate(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var database = Path.Combine(target, "clipstack.db");
        if (File.Exists(database) || !Directory.Exists(source))
        {
            return null;
        }

        // -wal / -shm travel with the database; a clean shutdown leaves them absent or empty.
        var databaseFiles = Directory.GetFiles(source, "clipstack.db*");
        if (databaseFiles.Length == 0)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(target);

            foreach (var file in databaseFiles)
            {
                File.Move(file, Path.Combine(target, Path.GetFileName(file)));
            }

            var settings = Path.Combine(source, "settings.json");
            if (File.Exists(settings))
            {
                File.Move(settings, Path.Combine(target, "settings.json"));
            }

            foreach (var name in new[] { "images", "logs" })
            {
                var from = Path.Combine(source, name);
                if (Directory.Exists(from))
                {
                    Directory.Move(from, Path.Combine(target, name));
                }
            }

            // Succeeds only when nothing else holds the folder: true when it stored nothing but our
            // own data, false for an installed app that keeps its binaries there. Either is fine.
            if (!Directory.EnumerateFileSystemEntries(source).Any())
            {
                Directory.Delete(source);
            }

            return (true, $"Moved data from {source} to {target}");
        }
        catch (Exception ex)
        {
            return (false, $"Moving data from {source} to {target} failed: {ex.Message}");
        }
    }
}
