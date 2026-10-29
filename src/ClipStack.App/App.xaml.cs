using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClipStack.Core.Clipboard;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;
using ClipStack.Core.Storage;

namespace ClipStack.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ClipStack.SingleInstance";
    private const string ActivateEventName = @"Local\ClipStack.Activate";

    /// <summary>Retention until the settings window lands in M5.</summary>
    private const int DefaultRetentionDays = 2;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private ItemStore? _store;
    private ClipboardMonitor? _monitor;
    private GlobalHotkey? _hotkey;
    private MainWindow? _panel;
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

        _store = new ItemStore(AppPaths.Database);
        var purged = _store.DeleteOlderThan(
            DateTimeOffset.UtcNow.AddDays(-DefaultRetentionDays).ToUnixTimeMilliseconds());
        if (purged > 0)
        {
            Log.Info($"Purged {purged} expired items");
        }

        _panel = new MainWindow(_store);
        _panel.Closed += (_, _) => Shutdown();

        _monitor = new ClipboardMonitor();
        _monitor.ClipboardChanged += OnClipboardChanged;
        _monitor.Start();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += TogglePanel;

        var hotkeyRegistered = _hotkey.Register(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            (uint)KeyInterop.VirtualKeyFromKey(Key.V));

        // --silent keeps the panel hidden on launch (needed for autostart later, and for tests).
        var silent = e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (!silent)
        {
            // Show once on first launch so the panel is discoverable; afterwards the hotkey rules.
            _panel.ShowPanel(WindowInfo.GetForeground());
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

    private void OnClipboardChanged(object? sender, ClipPayload payload)
    {
        if (_store is null)
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

        _panel.ShowPanel(WindowInfo.GetForeground());
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

        _panel.ShowPanel(WindowInfo.GetForeground());
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

        _hotkey?.Dispose();
        _monitor?.Dispose();
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
