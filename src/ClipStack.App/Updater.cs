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
    /// <summary>
    /// The release feed sits next to the packages, so reading it is one plain download of
    /// <c>releases.win.json</c>. Asking <see cref="GithubSource"/> instead goes through the GitHub
    /// REST API, which answers anonymous callers with 403 once the hourly per-IP quota is used up —
    /// on a shared or VPN address that happens constantly, and the app then looks up to date when
    /// it is not.
    /// </summary>
    private const string FeedUrl = "https://github.com/znp-star/ClipStack/releases/latest/download";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public Updater()
    {
        try
        {
            _manager = new UpdateManager(new SimpleWebSource(FeedUrl));
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
    /// Asks the feed for a newer release and remembers it. The three outcomes are kept apart on
    /// purpose: telling the user "you are up to date" when the request failed hides the reason
    /// they are stuck on an old build.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync()
    {
        if (_manager is not { IsInstalled: true })
        {
            return UpdateCheck.NotInstalled;
        }

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return UpdateCheck.Failed(ex.Message);
        }

        return _pending is null ? UpdateCheck.UpToDate : UpdateCheck.Available(PendingVersion!);
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

/// <summary>What one look at the release feed produced.</summary>
internal enum UpdateCheckState
{
    /// <summary>A newer release is waiting for download.</summary>
    Available,

    /// <summary>The feed answered, and it holds nothing newer than this build.</summary>
    UpToDate,

    /// <summary>The feed could not be read; the build may well be old, but nobody knows.</summary>
    Failed,

    /// <summary>This copy did not come from the installer, so it cannot replace itself.</summary>
    NotInstalled,
}

/// <summary>The outcome of <see cref="Updater.CheckAsync"/>, with the version when there is one.</summary>
internal sealed record UpdateCheck(UpdateCheckState State, string? Version, string? Error)
{
    public static UpdateCheck Available(string version) => new(UpdateCheckState.Available, version, null);

    public static readonly UpdateCheck UpToDate = new(UpdateCheckState.UpToDate, null, null);

    public static readonly UpdateCheck NotInstalled = new(UpdateCheckState.NotInstalled, null, null);

    public static UpdateCheck Failed(string error) => new(UpdateCheckState.Failed, null, error);
}
