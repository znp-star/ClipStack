using System.Text;

namespace ClipStack.Core.Logging;

/// <summary>
/// Minimal rolling file log: one file per day, everything older than
/// <see cref="Retention"/> is removed on startup. No telemetry, no network.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static string? _directory;

    public static void Init(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
            PurgeOldFiles(directory);
        }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message}: {exception}");

    public static string CurrentLogPath =>
        _directory is null ? string.Empty : Path.Combine(_directory, $"clipstack-{DateTime.Now:yyyyMMdd}.log");

    private static void Write(string level, string message)
    {
        if (_directory is null)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    private static void PurgeOldFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.Now - Retention;
            foreach (var file in Directory.EnumerateFiles(directory, "clipstack-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Ignore: a locked log file is not worth failing startup over.
        }
    }
}
