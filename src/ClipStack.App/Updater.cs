using ClipStack.Core.Logging;
using Velopack;
using Velopack.Exceptions;
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

    /// <summary>
    /// Waits applied between lock retries: the whole download can take a minute on a slow line, and a
    /// competing operation holds the lock for at most that long.
    /// </summary>
    private static readonly TimeSpan[] LockRetries =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(10),
    ];

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

    /// <summary>Download size of that release, so the progress window can show megabytes.</summary>
    public long PendingBytes => _pending?.TargetFullRelease.Size ?? 0;

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

    /// <summary>
    /// Downloads whatever the last <see cref="CheckAsync"/> found. <paramref name="progress"/> gets
    /// the percentage from Velopack, on a background thread; <paramref name="cancelToken"/> stops it.
    /// </summary>
    /// <remarks>
    /// Two things go wrong on a slow or proxied connection, and both used to surface as "download
    /// failed": Velopack serialises update work with <c>.velopack_lock</c>, so a second operation
    /// makes the call fail before a byte is fetched; and a stalled transfer hangs forever without a
    /// read timeout, holding that lock. The first is retried after a short wait. The second is cut
    /// loose by a watchdog and started again from the beginning — Velopack does not send a Range
    /// header, so the <c>.partial</c> file cannot be resumed, only discarded.
    /// </remarks>
    public async Task<UpdateDownload> DownloadAsync(Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        if (_manager is null || _pending is null)
        {
            return UpdateDownload.Failed("没有待下载的更新");
        }

        var lockAttempt = 0;
        var stalls = 0;

        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            var lastActivity = Environment.TickCount64;

            void Report(int percent)
            {
                lastActivity = Environment.TickCount64;
                progress?.Invoke(percent);
            }

            // A transfer that stops moving never fails on its own; cancel it so it can be started
            // again on a fresh connection.
            using var watchdog = new Timer(
                _ =>
                {
                    if (Environment.TickCount64 - lastActivity < StallMs)
                    {
                        return;
                    }

                    try
                    {
                        attempt.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The attempt finished while the timer was firing.
                    }
                },
                state: null,
                dueTime: WatchdogInterval,
                period: WatchdogInterval);

            try
            {
                await _manager.DownloadUpdatesAsync(_pending, Report, attempt.Token);
                return UpdateDownload.Done;
            }
            catch (OperationCanceledException) when (!cancelToken.IsCancellationRequested)
            {
                stalls++;
                Log.Warn($"Update download made no progress for {StallMs / 1000}s (attempt {stalls})");
                if (stalls > StallRetries)
                {
                    return UpdateDownload.Failed(
                        $"下载连续 {StallRetries + 1} 次卡住不动，可能和网络或代理有关。稍后再试，或到 GitHub Releases 手动下载安装包。");
                }

                if (!await WaitAsync(TimeSpan.FromSeconds(2), cancelToken))
                {
                    Log.Info("Update download cancelled");
                    return UpdateDownload.Cancelled;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Update download cancelled");
                return UpdateDownload.Cancelled;
            }
            catch (AcquireLockFailedException) when (lockAttempt < LockRetries.Length)
            {
                var delay = LockRetries[lockAttempt++];
                Log.Info($"Update download waiting for the update lock ({delay.TotalSeconds:0}s)");

                // Waiting inside a catch block: a cancellation here is not caught by the clauses
                // below, so it has to be turned into an outcome right here.
                if (!await WaitAsync(delay, cancelToken))
                {
                    Log.Info("Update download cancelled");
                    return UpdateDownload.Cancelled;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Update download failed: {ex.Message}");
                return UpdateDownload.Failed(ex.Message);
            }
        }
    }

    /// <summary>No progress for this long means the connection is dead, not slow.</summary>
    private const int StallMs = 90_000;

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    /// <summary>How many times a stalled transfer is restarted before giving up.</summary>
    private const int StallRetries = 3;

    /// <summary>Waits, returning false when the user cancels while we wait.</summary>
    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancelToken)
    {
        try
        {
            await Task.Delay(delay, cancelToken);
            return true;
        }
        catch (OperationCanceledException)
        {
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

/// <summary>The outcome of <see cref="Updater.DownloadAsync"/>; the reason is kept for the dialog.</summary>
internal enum UpdateDownloadState
{
    /// <summary>The package is on disk and ready to install.</summary>
    Done,

    /// <summary>The user cancelled; nothing to report.</summary>
    Cancelled,

    /// <summary>Nothing was downloaded; <see cref="UpdateDownload.Error"/> says why.</summary>
    Failed,
}

internal sealed record UpdateDownload(UpdateDownloadState State, string? Error = null)
{
    public static readonly UpdateDownload Done = new(UpdateDownloadState.Done);

    public static readonly UpdateDownload Cancelled = new(UpdateDownloadState.Cancelled);

    public static UpdateDownload Failed(string error) => new(UpdateDownloadState.Failed, error);
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
