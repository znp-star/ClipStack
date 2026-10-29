using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private DispatcherTimer? _cleanupTimer;
    private IntPtr _lastTarget;
    private volatile bool _shuttingDown;

    /// <summary>Collapses the burst of clipboard updates a single copy can produce.</summary>
    private string? _lastHash;
    private long _lastHashAt;
    private const long DuplicateWindowMs = 800;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.Logs);
        Log.Info($"ClipStack starting ({Environment.OSVersion.Version})");

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
        ApplyAccent();

        _store = new ItemStore(AppPaths.Database, AppPaths.Images);

        // Sensitive payloads live here only: nothing in this queue ever reaches the database.
        _queue = new SensitiveQueue(
            ttl: TimeSpan.FromMinutes(Math.Clamp(_settings.Current.SensitiveTtlMinutes, 1, 60)));

        PurgeExpired();
        ApplyAutostart();

        _panel = new MainWindow(_store, _settings, _queue);
        _panel.Closed += (_, _) => Shutdown();

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardChanged += OnClipboardChanged;
        _monitor.Start();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += TogglePanel;

        var hotkeyRegistered = _hotkey.Register(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            (uint)KeyInterop.VirtualKeyFromKey(Key.V));

        CreateTray();
        StartCleanupTimer();

        // --silent keeps the panel hidden on launch (needed for autostart later, and for tests).
        var silent = e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (!silent)
        {
            // Show once on first launch so the panel is discoverable; afterwards the hotkey rules.
            _panel.ShowPanel(ResolvePasteTarget());
        }

        if (!hotkeyRegistered)
        {
            Log.Warn("Ctrl+Shift+V is already owned by another application");
            if (!silent)
            {
                _panel.SetStatus("全局快捷键 Ctrl+Shift+V 被其他程序占用，暂时只能通过再次启动本程序唤出");
            }
        }
    }

    /// <summary>Replaces the accent brushes with the user's DWM accent colour.</summary>
    private void ApplyAccent()
    {
        var accent = DwmInterop.GetAccentColor();
        var red = (byte)(accent >> 16);
        var green = (byte)(accent >> 8);
        var blue = (byte)accent;

        Resources["AccentBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(red, green, blue)));
        Resources["SelectionBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, red, green, blue)));
        Resources["SelectionEdgeBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x66, red, green, blue)));
        Resources["PinnedRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x0F, red, green, blue)));
        Resources["PinBadgeBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x24, red, green, blue)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
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

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => _panel?.Close();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        // The monitor can be paused from elsewhere later (settings), so read the state on open.
        menu.Opened += (_, _) => pause.IsChecked = _monitor?.IsPaused == true;
        return menu;
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
