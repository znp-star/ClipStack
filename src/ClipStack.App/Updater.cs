using ClipStack.Core.Logging;
using Velopack;
using Velopack.Sources;

namespace ClipStack.App;

/// <summary>
/// Self-update through Velopack. Only a copy produced by the Velopack installer can replace
/// itself, so a build run straight out of <c>bin\</c> reports "not installed" instead of failing.
/// </summary>
internal sealed class Updater
{
    private const string RepositoryUrl = "https://github.com/znp-star/ClipStack";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public Updater()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false));
        }
        catch (Exception ex)
        {
            // A broken update feed must never block startup; the app simply cannot update itself.
            Log.Warn($"Updater could not be created: {ex.Message}");
        }
    }

    /// <summary>True only for an installed copy.</summary>
    public bool IsInstalled => _manager is { IsInstalled: true };

    /// <summary>Version of the release found by the last successful check, if any.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Asks the feed for a newer release and remembers it. Returns the version, or null when the
    /// app is up to date, not installed, or the check failed.
    /// </summary>
    public async Task<string?> CheckAsync()
    {
        if (_manager is not { IsInstalled: true })
        {
            return null;
        }

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            return PendingVersion;
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads whatever the last <see cref="CheckAsync"/> found.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (_manager is null || _pending is null)
        {
            return false;
        }

        try
        {
            await _manager.DownloadUpdatesAsync(_pending);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Update download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts the downloaded build. The caller must have released the database and stopped the
    /// clipboard listener first: the installer replaces the files this process has open.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
        {
            return false;
        }

        try
        {
            _manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Applying the update failed", ex);
            return false;
        }
    }
}
