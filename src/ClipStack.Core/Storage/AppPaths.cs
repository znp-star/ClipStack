namespace ClipStack.Core.Storage;

public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Overrides the data root, for tests and portable runs.</summary>
    public static void UseRoot(string root) => _rootOverride = root;

    public static string Root => _rootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack");

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
}
