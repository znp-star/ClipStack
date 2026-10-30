using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipStack.App.Interop;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;
using ClipStack.Core.Security;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;

namespace ClipStack.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ClipStack.SingleInstance";
    private const string ActivateEventName = @"Local\ClipStack.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private SettingsStore? _settings;
    private ItemStore? _store;
    private SensitiveQueue? _queue;
    private ClipboardMonitor? _monitor;
    private GlobalHotkey? _hotkey;
    private MainWindow? _panel;
    private TrayIcon? _tray;
    private Updater? _updater;
    private MenuItem? _updateItem;
    private DispatcherTimer? _cleanupTimer;
    private IntPtr _lastTarget;
    private volatile bool _shuttingDown;

    // One update flow at a time: a second one would only fight the first over Velopack's lock.
    private bool _updating;

    /// <summary>Effective bindings for the nine actions, resolved from settings at startup.</summary>
    private Dictionary<HotkeyAction, HotkeyGesture> _hotkeys = [];

    /// <summary>Collapses the burst of clipboard updates a single copy can produce.</summary>
    private string? _lastHash;
    private long _lastHashAt;
    private const long DuplicateWindowMs = 800;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0.1.2 moved the data out of Velopack's install folder; bring the old files along once.
        var migration = AppPaths.MigrateLegacyRoot();

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.Logs);
        Log.Info($"ClipStack starting ({Environment.OSVersion.Version})");

        if (migration is { } result)
        {
            if (result.Succeeded)
            {
                Log.Info(result.Message);
            }
            else
            {
                Log.Warn(result.Message);
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Log.Info("Another instance is running; activating it");
            SignalExistingInstance();
            Shutdown();
            return;
        }

        StartActivationListener();

        _settings = new SettingsStore(AppPaths.Settings);
        _settings.Load();
        ThemeManager.Apply(_settings.Current.Theme);

        _hotkeys = Hotkeys.Resolve(_settings.Current.Hotkeys);

        _store = new ItemStore(AppPaths.Database, AppPaths.Images);

        // Sensitive payloads live here only: nothing in this queue ever reaches the database.
        _queue = new SensitiveQueue(
            ttl: TimeSpan.FromMinutes(Math.Clamp(_settings.Current.SensitiveTtlMinutes, 1, 60)));

        PurgeExpired();
        ApplyAutostart();

        _panel = new MainWindow(_store, _settings, _queue);
        _panel.Closed += (_, _) => Shutdown();
        _panel.ApplyHotkeys(_hotkeys);
        _panel.ApplyPreviewTip(_settings.Current.ShowPreviewTip);
        _panel.ClearHistoryRequested += ClearHistory;

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardChanged += OnClipboardChanged;
        _monitor.Start();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += TogglePanel;

        var showPanel = _hotkeys[HotkeyAction.ShowPanel];
        var hotkeyText = HotkeyGesture.Format(showPanel);
        var hotkeyRegistered = !showPanel.IsEmpty
            && _hotkey.Register(showPanel.Modifiers, (uint)showPanel.VirtualKey);

        CreateTray();
        StartCleanupTimer();

        _updater = new Updater();
        StartUpdateCheck();

        // --silent keeps the panel hidden on launch (needed for autostart later, and for tests).
        var silent = e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (!silent)
        {
            // Show once on first launch so the panel is discoverable; afterwards the hotkey rules.
            _panel.ShowPanel(ResolvePasteTarget());
        }

        // --settings opens the dialog once startup is done. Opening a modal dialog from inside
        // OnStartup leaves it waiting for a render pass the outer message loop has not begun yet,
        // so it is queued for the idle point instead. It also needs a shown owner window, which is
        // why --silent ignores it.
        if (!silent && e.Args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings), DispatcherPriority.ApplicationIdle);
        }

        if (!hotkeyRegistered)
        {
            Log.Warn($"{hotkeyText} is already owned by another application");
            if (!silent)
            {
                _panel.SetStatus(
                    $"全局快捷键 {hotkeyText} 被其他程序占用，暂时只能通过再次启动本程序唤出；可在托盘菜单的「设置」里换一个");
            }
        }
    }

    /// <summary>
    /// Registers the panel hotkey, used both at startup and when the settings window changes it.
    /// On failure the previous binding is restored so the panel stays reachable.
    /// </summary>
    private string? TryRegisterGlobal(HotkeyGesture gesture)
    {
        if (_hotkey is null)
        {
            return "热键服务还没就绪";
        }

        if (!gesture.IsEmpty && _hotkey.Register(gesture.Modifiers, (uint)gesture.VirtualKey))
        {
            return null;
        }

        var previous = _hotkeys[HotkeyAction.ShowPanel];
        if (!previous.IsEmpty)
        {
            _hotkey.Register(previous.Modifiers, (uint)previous.VirtualKey);
        }

        return "被其他程序占用，请换一个";
    }

    /// <summary>Opens the settings dialog and applies what it returns.</summary>
    private void OpenSettings()
    {
        if (_panel is null || _settings is null)
        {
            return;
        }

        var dialog = new SettingsWindow(_settings.Current, TryRegisterGlobal);
        if (_panel.ShowDialogSuppressed(dialog) != true)
        {
            return;
        }

        _settings.Current.RetentionDays = dialog.RetentionDays;
        _settings.Current.SensitiveTtlMinutes = dialog.SensitiveTtlMinutes;
        _settings.Current.Autostart = dialog.Autostart;
        _settings.Current.ShowPreviewTip = dialog.ShowPreviewTip;
        _settings.Current.Theme = dialog.Theme;
        _settings.Current.Hotkeys = dialog.HotkeyMap;
        _settings.Save();

        _hotkeys = Hotkeys.Resolve(_settings.Current.Hotkeys);
        _panel.ApplyHotkeys(_hotkeys);
        _panel.ApplyPreviewTip(dialog.ShowPreviewTip);
        _panel.ApplySensitiveTtl(dialog.SensitiveTtlMinutes);
        ThemeManager.Apply(_settings.Current.Theme);
        ApplyAutostart();

        // A shorter retention window may make existing rows expire right away.
        PurgeExpired();

        _panel.SetStatus("设置已保存");
        Log.Info("Settings saved");
    }

    private void OnClipboardChanged(object? sender, ClipPayload payload)
    {
        if (_store is null || _queue is null)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // One copy can raise several WM_CLIPBOARDUPDATE messages; keep only the first.
            if (_lastHash == payload.ContentHash && now - _lastHashAt < DuplicateWindowMs)
            {
                return;
            }

            _lastHash = payload.ContentHash;
            _lastHashAt = now;

            // Sensitive payloads stop here: they are held in memory for a few minutes and are
            // never written to the database or to disk.
            var verdict = SensitivityFilter.Evaluate(payload);
            if (verdict.IsSensitive)
            {
                var held = _queue.Add(payload, now);
                Log.Info(
                    $"Held sensitive {payload.Type} item #{held.Id} in memory " +
                    $"({payload.ContentHash[..8]}, {verdict.Reason}: {verdict.Detail}) " +
                    $"from {payload.SourceApp ?? "unknown"}");

                if (_panel is { IsVisible: true })
                {
                    _panel.RefreshItems();
                }

                return;
            }

            var item = _store.AddOrTouch(payload, now);
            Log.Info(
                $"Captured {payload.Type} item #{item.Id} " +
                $"({payload.ContentHash[..8]}, {payload.Text?.Length ?? 0} chars, use_count={item.UseCount}) " +
                $"from {payload.SourceApp ?? "unknown"}");

            if (_panel is { IsVisible: true })
            {
                _panel.RefreshItems();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to store clipboard item", ex);
        }
    }

    private void TogglePanel()
    {
        if (_panel is null)
        {
            return;
        }

        if (_panel.IsVisible)
        {
            _panel.HidePanel();
            return;
        }

        _panel.ShowPanel(ResolvePasteTarget());
    }

    private void BringPanelUp()
    {
        if (_panel is null)
        {
            return;
        }

        if (_panel.IsVisible)
        {
            _panel.Activate();
            return;
        }

        _panel.ShowPanel(ResolvePasteTarget());
    }

    /// <summary>
    /// Remembers the window a paste should go to. Opening the panel from the tray leaves the
    /// taskbar (or desktop) in the foreground, which cannot take a paste, so the last real
    /// application window is reused instead.
    /// </summary>
    private IntPtr ResolvePasteTarget()
    {
        var foreground = WindowInfo.GetForeground();
        if (WindowInfo.IsShellSurface(foreground))
        {
            return WindowInfo.Describe(_lastTarget).App is not null ? _lastTarget : foreground;
        }

        _lastTarget = foreground;
        return foreground;
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);

        var thread = new Thread(() =>
        {
            while (!_shuttingDown)
            {
                if (!_activateEvent.WaitOne(500))
                {
                    continue;
                }

                try
                {
                    Dispatcher.BeginInvoke(new Action(BringPanelUp));
                }
                catch (TaskCanceledException)
                {
                    // Dispatcher is shutting down.
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClipStack.ActivationListener",
        };

        thread.Start();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is still starting up; nothing to do.
        }
    }

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon("ClipStack · 监听中");
            _tray.SetMenu(BuildTrayMenu());
            _tray.Activate += BringPanelUp;
            _tray.Show();
        }
        catch (Exception ex)
        {
            // The hotkey still works, so a missing icon degrades rather than blocks.
            Log.Error("Tray icon could not be created", ex);
        }
    }

    private ContextMenu BuildTrayMenu()
    {
        var open = new MenuItem { Header = "打开面板" };
        open.Click += (_, _) => BringPanelUp();

        var pause = new MenuItem { Header = "暂停监听", IsCheckable = true };
        pause.Click += (_, _) => SetPaused(pause.IsChecked);

        var clear = new MenuItem { Header = "清空历史…" };
        clear.Click += (_, _) => ClearHistory();

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => OpenSettings();

        var update = new MenuItem { Header = "检查更新…" };
        update.Click += OnCheckUpdateClicked;
        _updateItem = update;

        // A caption plus three short rows instead of "主题：" repeated three times: the same
        // grouping as a submenu, without a nested popup the flat template cannot host.
        var themeLabel = new MenuItem
        {
            Header = "主题",
            Style = (Style)FindResource("MenuSectionLabel"),
        };

        var childStyle = (Style)FindResource("MenuChildItem");
        var themeSystem = new MenuItem { Header = "跟随系统", IsCheckable = true, Style = childStyle };
        var themeLight = new MenuItem { Header = "浅色", IsCheckable = true, Style = childStyle };
        var themeDark = new MenuItem { Header = "深色", IsCheckable = true, Style = childStyle };
        themeSystem.Click += (_, _) => SetTheme("system");
        themeLight.Click += (_, _) => SetTheme("light");
        themeDark.Click += (_, _) => SetTheme("dark");

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => _panel?.Close();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(themeLabel);
        menu.Items.Add(themeSystem);
        menu.Items.Add(themeLight);
        menu.Items.Add(themeDark);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        menu.Items.Add(settings);
        menu.Items.Add(update);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        // The monitor and the theme can both be changed elsewhere (settings window, OS), so the
        // checked state is read when the menu opens rather than when it is built.
        menu.Opened += (_, _) =>
        {
            pause.IsChecked = _monitor?.IsPaused == true;
            themeSystem.IsChecked = ThemeManager.Preference == "system";
            themeLight.IsChecked = ThemeManager.Preference == "light";
            themeDark.IsChecked = ThemeManager.Preference == "dark";
        };

        return menu;
    }

    private void SetTheme(string preference)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Current.Theme = ThemeManager.Normalize(preference);
        _settings.Save();
        ThemeManager.Apply(_settings.Current.Theme);
        Log.Info($"Theme set to {_settings.Current.Theme}");
    }

    private static string CurrentVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Asks the feed once, shortly after startup, so a new release is noticed without the user
    /// having to look. Deliberately quiet: only the tray tooltip and the menu caption change.
    /// </summary>
    private void StartUpdateCheck()
    {
        if (_updater is not { IsInstalled: true })
        {
            return;
        }

        // Give the window and the clipboard monitor a moment before touching the network.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await CheckForUpdatesAsync(manual: false);
        };
        timer.Start();
    }

    private async void OnCheckUpdateClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync(manual: true);
        }
        catch (Exception ex)
        {
            Log.Error("Update flow failed", ex);
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updater is null)
        {
            return;
        }

        if (_updating)
        {
            if (manual)
            {
                MessageBox.Show(
                    "更新已经在进行中，进度在更新窗口里。",
                    "ClipStack",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        var check = await _updater.CheckAsync();

        switch (check.State)
        {
            case UpdateCheckState.NotInstalled:
                if (manual)
                {
                    MessageBox.Show(
                        "这一份不是通过安装包装的，没法自更新。\n先装一次 GitHub Releases 里的安装包，之后的版本就能自动升级。",
                        "ClipStack",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;

            case UpdateCheckState.Failed:
                // Saying "up to date" here would be a guess dressed up as an answer.
                if (manual)
                {
                    MessageBox.Show(
                        $"检查更新失败：{check.Error}\n\n可以稍后再试，或直接到 GitHub Releases 下载安装包覆盖安装，数据不受影响。",
                        "ClipStack",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;

            case UpdateCheckState.UpToDate:
                Log.Info($"Update check: up to date ({CurrentVersion})");
                if (manual)
                {
                    MessageBox.Show(
                        $"已经是最新版本（{CurrentVersion}）。",
                        "ClipStack",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
        }

        var version = check.Version!;

        if (_updateItem is not null)
        {
            _updateItem.Header = $"安装更新 v{version}…";
        }

        _tray?.SetTooltip($"ClipStack · 可更新到 {version}");

        if (!manual)
        {
            Log.Info($"Update {version} available");
            return;
        }

        var answer = MessageBox.Show(
            $"发现新版本 {version}（当前 {CurrentVersion}）。现在下载并重启完成安装吗？",
            "ClipStack",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var download = await DownloadWithProgressAsync(version);
        switch (download.State)
        {
            case UpdateDownloadState.Cancelled:
                _tray?.SetTooltip($"ClipStack · 可更新到 {version}");
                return;

            case UpdateDownloadState.Failed:
                MessageBox.Show(
                    $"下载失败：{download.Error}\n\n可以重试一次，或直接到 GitHub Releases 下载安装包覆盖安装，数据不受影响。",
                    "ClipStack",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
        }

        QuiesceForUpdate();

        if (!_updater.ApplyAndRestart())
        {
            MessageBox.Show("安装没能启动，请手动退出后重开再试。", "ClipStack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Downloads with the progress window on screen. The flow is marked busy for its whole length,
    /// so a second click cannot start a competing download that Velopack would refuse anyway.
    /// </summary>
    private async Task<UpdateDownload> DownloadWithProgressAsync(string version)
    {
        _updating = true;
        if (_updateItem is not null)
        {
            _updateItem.IsEnabled = false;
            _updateItem.Header = $"正在下载 v{version}…";
        }

        var window = new UpdateProgressWindow(version, _updater!.PendingBytes);
        window.Show();

        try
        {
            return await _updater.DownloadAsync(window.Report, window.Token);
        }
        finally
        {
            window.Close();
            _updating = false;

            if (_updateItem is not null)
            {
                _updateItem.IsEnabled = true;
                _updateItem.Header = $"安装更新 v{version}…";
            }
        }
    }

    /// <summary>
    /// Releases everything holding files the installer has to replace: the SQLite connection keeps
    /// its WAL files open, and the clipboard listener must stop before the process swaps builds.
    /// </summary>
    private void QuiesceForUpdate()
    {
        _hotkey?.Dispose();
        _monitor?.Stop();
        _queue?.Clear();
        _store?.Dispose();
        _tray?.Dispose();
        Log.Info("Released resources for the update");
    }

    private void SetPaused(bool paused)
    {
        if (_monitor is null)
        {
            return;
        }

        _monitor.IsPaused = paused;
        _tray?.SetTooltip(paused ? "ClipStack · 已暂停监听" : "ClipStack · 监听中");
        Log.Info(paused ? "Clipboard monitoring paused" : "Clipboard monitoring resumed");
    }

    private void ClearHistory()
    {
        if (_store is null)
        {
            return;
        }

        var answer = _panel is null
            ? MessageBox.Show(
                "清空全部历史记录？置顶条目也会被删除，图片文件一并清理，无法恢复。",
                "ClipStack",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : MessageBox.Show(
                _panel,
                "清空全部历史记录？置顶条目也会被删除，图片文件一并清理，无法恢复。",
                "ClipStack",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _store.Clear(includePinned: true);
        var dropped = _queue?.Count ?? 0;
        _queue?.Clear();

        _panel?.RefreshItems();
        Log.Info($"Cleared {removed} items and {dropped} sensitive entries");
    }

    /// <summary>Drops entries past the retention window. Pinned items are exempt inside the store.</summary>
    private void PurgeExpired()
    {
        if (_store is null || _settings is null)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cutoff = DateTimeOffset.UtcNow
                .AddDays(-_settings.Current.RetentionDays)
                .ToUnixTimeMilliseconds();

            var removed = _store.DeleteOlderThan(cutoff);

            // The queue purges on every add and query, but the panel may stay closed for days.
            var dropped = _queue?.Purge(now) ?? 0;

            if (removed == 0 && dropped == 0)
            {
                return;
            }

            if (removed > 0)
            {
                Log.Info($"Purged {removed} expired items");
            }

            if (dropped > 0)
            {
                Log.Info($"Dropped {dropped} expired sensitive items");
            }

            if (_panel is { IsVisible: true })
            {
                _panel.RefreshItems();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Purge failed", ex);
        }
    }

    private void StartCleanupTimer()
    {
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _cleanupTimer.Tick += (_, _) => PurgeExpired();
        _cleanupTimer.Start();
    }

    /// <summary>Keeps the Run key in sync with the setting, which also repairs a moved executable.</summary>
    private void ApplyAutostart()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            Autostart.Apply(_settings.Current.Autostart);
            Log.Info($"Autostart {(_settings.Current.Autostart ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Autostart could not be applied: {ex.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"ClipStack 遇到未处理的错误：{e.Exception.Message}\n\n日志目录：{AppPaths.Logs}",
            "ClipStack",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.ExceptionObject as Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;

        _cleanupTimer?.Stop();
        _cleanupTimer = null;

        // Last chance to drop expired entries: the panel may stay in the tray for days.
        PurgeExpired();

        _tray?.Dispose();
        _hotkey?.Dispose();
        _monitor?.Dispose();

        // The in-memory queue disappears with the process; drop it before the store closes.
        _queue?.Clear();
        _store?.Dispose();
        _activateEvent?.Dispose();

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // This instance never owned the mutex (second launch path).
        }

        _instanceMutex?.Dispose();
        Log.Info("ClipStack exited");

        base.OnExit(e);
    }
}
