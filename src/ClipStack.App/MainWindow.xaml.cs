using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipStack.App.Interop;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Models;
using ClipStack.Core.Settings;
using ClipStack.Core.Storage;

namespace ClipStack.App;

public partial class MainWindow : Window
{
    private const int MaxRows = 200;
    private const double MinPanelWidth = 320;
    private const double MinPanelHeight = 240;

    /// <summary>Characters the text tip shows before it stops. Pasted logs can be megabytes.</summary>
    private const int TipTextLimit = 4000;

    private static readonly TimeSpan PanelAnimation = TimeSpan.FromMilliseconds(120);

    private readonly ItemStore _store;
    private readonly SettingsStore _settings;
    private readonly SensitiveQueue _queue;
    private readonly ObservableCollection<ItemRow> _rows = [];
    private readonly DispatcherTimer _tipDelay = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private Dictionary<HotkeyAction, HotkeyGesture> _hotkeys = Hotkeys.Resolve(null);
    private IntPtr _target;

    // Which control inside the target had the caret when the panel opened; null when the app does
    // not expose UI Automation and the callers fall back to plain window activation.
    private FocusRestore? _targetFocus;
    private Monitors.MonitorInfo _monitor;
    private ClipType? _filter;
    private bool _linksOnly;
    private bool _sensitiveOnly;
    private bool _suppressDeactivate;
    private bool _ready;
    private bool _previewTip = true;

    // The row the tip is anchored to, and the one it is about to be. Keeping both lets a pointer
    // move cancel a pending tip and close a visible one on the row it was opened for.
    private FrameworkElement? _tipAnchor;
    private ItemRow? _pendingRow;
    private FrameworkElement? _pendingAnchor;

    public MainWindow(ItemStore store, SettingsStore settings, SensitiveQueue queue)
    {
        _store = store;
        _settings = settings;
        _queue = queue;
        InitializeComponent();

        ItemList.ItemsSource = _rows;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
        SourceInitialized += OnSourceInitialized;
        _tipDelay.Tick += OnTipDelayElapsed;

        _ready = true;
        UpdateHints();
    }

    /// <summary>Raised when the panel asks for the history to be cleared (a configurable hotkey).</summary>
    public event Action? ClearHistoryRequested;

    /// <summary>Rebinds the panel hotkeys and refreshes the footer hint line.</summary>
    public void ApplyHotkeys(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> table)
    {
        _hotkeys = new Dictionary<HotkeyAction, HotkeyGesture>(table);
        UpdateHints();
    }

    /// <summary>Retunes the sensitive queue without a restart.</summary>
    public void ApplySensitiveTtl(int minutes) =>
        _queue.Ttl = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60));

    /// <summary>Turns the row preview tip on or off, closing one that is already up.</summary>
    public void ApplyPreviewTip(bool enabled)
    {
        _previewTip = enabled;

        if (!enabled)
        {
            HideRowTip();
        }
    }

    /// <summary>
    /// Shows a dialog owned by the panel without the panel hiding itself behind it — losing
    /// activation to our own child window is not a reason to disappear. Focus returns to the
    /// search box when the dialog closes.
    /// </summary>
    public bool? ShowDialogSuppressed(Window dialog)
    {
        dialog.Owner = this;
        _suppressDeactivate = true;

        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            _suppressDeactivate = false;

            // Only reclaim focus if the panel is still on screen. The settings dialog can be
            // opened from the tray, where the panel is hidden — ForceForeground calls
            // ShowWindow(SW_RESTORE), which would un-hide the native window behind WPF's back and
            // leave an unpainted rectangle on the desktop (WPF still thinks it is hidden, so it
            // never lays the content out).
            if (IsVisible)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                WindowActivation.ForceForeground(hwnd);
                Activate();
                SearchBox.Focus();
            }
        }
    }

    private void UpdateHints()
    {
        string Key(HotkeyAction action) => HotkeyGesture.Format(_hotkeys[action]);

        HintsList.ItemsSource = new[]
        {
            new HotkeyHint("↑↓", "选择"),
            new HotkeyHint(Key(HotkeyAction.Paste), "粘贴"),
            new HotkeyHint(Key(HotkeyAction.PastePlain), "纯文本"),
            new HotkeyHint(Key(HotkeyAction.Pin), "置顶"),
            new HotkeyHint(Key(HotkeyAction.Edit), "编辑"),
            new HotkeyHint(Key(HotkeyAction.Delete), "删除"),
            new HotkeyHint(Key(HotkeyAction.CycleFilter), "换过滤"),
            new HotkeyHint("Esc", "关闭"),
        };
    }

    /// <summary>Shows the panel and remembers which window should receive the paste.</summary>
    public void ShowPanel(IntPtr target, bool captureFocus = true)
    {
        // Snapshot the caret before the panel takes the foreground. A re-show after a failed paste
        // keeps the snapshot it was opened with: by then the focus is already our own window.
        if (captureFocus || _target != target)
        {
            _targetFocus = FocusRestore.Capture(target);
        }

        _target = target;
        StatusText.Visibility = Visibility.Collapsed;

        var (targetApp, _) = WindowInfo.Describe(target);
        Log.Info($"Panel shown, target={targetApp ?? "none"}");

        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
        }

        // Every invocation starts from the full history, like Win+V.
        if (FilterAll.IsChecked != true)
        {
            FilterAll.IsChecked = true;
        }

        RefreshItems();

        // Create the handle first so placement happens while the panel is still invisible.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        PlaceOnMonitor(hwnd);

        Show();

        // Windows may refuse the activation; force it so the panel can take key input.
        WindowActivation.ForceForeground(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);

        // Every invocation starts at the newest entry, not whatever was selected last time.
        if (_rows.Count > 0)
        {
            ItemList.SelectedIndex = 0;
            ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
        else
        {
            ItemList.SelectedIndex = -1;
        }

        AnimateIn();
    }

    /// <summary>
    /// Hides the panel. <paramref name="returnFocus"/> hands the foreground and the caret back to
    /// the window the panel was opened over, which is what dismissing it with Esc should do.
    /// </summary>
    public void HidePanel(bool returnFocus = false)
    {
        HideRowTip();
        SaveGeometry();
        Hide();

        if (returnFocus && _target != IntPtr.Zero)
        {
            _targetFocus?.Restore(_target);
        }
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Reloads the list from the store, keeping the current selection when possible.</summary>
    public void RefreshItems()
    {
        if (!_ready)
        {
            return;
        }

        var previousId = (ItemList.SelectedItem as ItemRow)?.Item.Id;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var stored = _store.Query(SearchBox.Text, _filter, MaxRows, _linksOnly);
        var sensitive = _queue.Query(SearchBox.Text, _filter, _linksOnly, now);

        // Pinned entries stay on top; everything else, sensitive or not, is one time flow.
        IEnumerable<ClipItem> visible = _sensitiveOnly
            ? sensitive
            : stored.Where(item => item.Pinned)
                .Concat(stored.Where(item => !item.Pinned)
                    .Concat(sensitive)
                    .OrderByDescending(item => item.LastUsedAtMs))
                .Take(MaxRows);

        // Rebuilding the rows invalidates the container the tip is anchored to.
        HideRowTip();

        _rows.Clear();
        foreach (var item in visible)
        {
            _rows.Add(new ItemRow(item));
        }

        EmptyHint.Text = !string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "没有匹配的记录"
            : _sensitiveOnly
                ? "暂时没有敏感内容"
                : _filter is not null
                    ? "这个分类还没有记录"
                    : "还没有记录，复制点什么试试";
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_rows.Count == 0)
        {
            return;
        }

        var restoredIndex = 0;
        if (previousId is { } id)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Item.Id == id)
                {
                    restoredIndex = i;
                    break;
                }
            }
        }

        ItemList.SelectedIndex = restoredIndex;
        ItemList.ScrollIntoView(ItemList.SelectedItem);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        DwmInterop.ApplyRoundedCorners(hwnd);
        ApplyTheme(ThemeManager.IsDark);
    }

    /// <summary>
    /// Re-applies the DWM backdrop for the current theme. Without Mica (older Windows) the
    /// translucent surface would show raw glass, so the panel paints itself opaquely instead.
    /// The brushes stay dynamic references, so a later palette swap still repaints them.
    /// </summary>
    public void ApplyTheme(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var mica = DwmInterop.TryApplyMica(hwnd, dark);
        RootBorder.SetResourceReference(
            BackgroundProperty, mica ? "PanelSurfaceBrush" : "SurfaceBrush");

        Log.Info($"Panel backdrop: {(mica ? "Mica" : "opaque fallback")} ({(dark ? "dark" : "light")})");
    }

    /// <summary>Restores the remembered bounds for this monitor, or centres the panel on it.</summary>
    private void PlaceOnMonitor(IntPtr hwnd)
    {
        _monitor = Monitors.ForWindow(hwnd);
        var scale = _monitor.Scale;

        MaxWidth = _monitor.WorkWidth * 0.7 / scale;
        MaxHeight = _monitor.WorkHeight * 0.7 / scale;
        MinWidth = MinPanelWidth;
        MinHeight = MinPanelHeight;

        var hasSaved = _monitor.Device.Length > 0
            && _settings.Current.PanelGeometry.TryGetValue(_monitor.Device, out var saved)
            && saved.Width >= MinPanelWidth && saved.Height >= MinPanelHeight;

        if (hasSaved)
        {
            var geometry = _settings.Current.PanelGeometry[_monitor.Device];
            Width = Math.Min(geometry.Width / scale, MaxWidth);
            Height = Math.Min(geometry.Height / scale, MaxHeight);
            Left = Clamp(geometry.X / scale, _monitor.WorkX / scale, _monitor.WorkX / scale + MaxWidth - Width);
            Top = Clamp(geometry.Y / scale, _monitor.WorkY / scale, _monitor.WorkY / scale + MaxHeight - Height);
            return;
        }

        Width = Math.Min(DefaultWidth, MaxWidth);
        Height = Math.Min(DefaultHeight, MaxHeight);
        Left = (_monitor.WorkX + (_monitor.WorkWidth - Width * scale) / 2) / scale;
        Top = (_monitor.WorkY + (_monitor.WorkHeight - Height * scale) / 2) / scale;
    }

    private const double DefaultWidth = 420;
    private const double DefaultHeight = 520;

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);

    private void SaveGeometry()
    {
        // Started with --silent and quit from the tray: the panel never opened, so there is
        // no monitor and no real bounds to remember. _monitor is a struct, so an unshown
        // panel leaves it at default with a null Device.
        if (string.IsNullOrEmpty(_monitor.Device))
        {
            return;
        }

        var scale = _monitor.Scale;
        _settings.Current.PanelGeometry[_monitor.Device] = new PanelGeometry
        {
            X = Math.Round(Left * scale),
            Y = Math.Round(Top * scale),
            Width = Math.Round(Width * scale),
            Height = Math.Round(Height * scale),
            Dpi = _monitor.Dpi,
        };

        _settings.Save();
    }

    private void AnimateIn()
    {
        RootBorder.BeginAnimation(OpacityProperty, null);
        PanelOffset.BeginAnimation(TranslateTransform.YProperty, null);

        RootBorder.Opacity = 0;
        PanelOffset.Y = 8;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootBorder.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, PanelAnimation) { EasingFunction = ease });
        PanelOffset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, PanelAnimation) { EasingFunction = ease });
    }

    private void PasteSelected(bool plainText = false)
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            return;
        }

        // Sensitive entries never reach the store, so there is no usage counter to bump.
        if (!row.Item.IsSensitive)
        {
            _store.Touch(row.Item.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        _suppressDeactivate = true;
        HideRowTip();
        SaveGeometry();
        Hide();

        var payload = row.Item.ToPastePayload(plainText);
        if (payload.ImagePng is { Length: > 0 } png)
        {
            // Chromium-based apps ignore the raw PNG clipboard format; hand them DIB pixels too.
            payload = payload.WithDib(DibConverter.FromPng(png));
        }

        var result = PasteInvoker.Paste(payload, _target, _targetFocus is null ? null : () => _targetFocus.Restore(_target));

        if (result.Outcome != PasteOutcome.Sent)
        {
            Log.Warn($"Paste not delivered: {result.Reason}");
            // Nothing was typed into the target, so bring the panel back with the reason.
            ShowPanel(_target, captureFocus: false);
            SetStatus(result.Reason ?? "内容已复制到剪贴板");
        }

        _suppressDeactivate = false;
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(ItemList.SelectedIndex + delta, 0, _rows.Count - 1);
        ItemList.SelectedIndex = index;
        ItemList.ScrollIntoView(ItemList.SelectedItem);
        UpdateTipForSelection();
    }

    private void TogglePinSelected()
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            return;
        }

        if (row.Item.IsSensitive)
        {
            SetStatus("敏感内容只留在内存里，不能置顶");
            return;
        }

        var unpinning = row.Item.Pinned;

        _store.SetPinned(row.Item.Id, !row.Item.Pinned, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        RefreshItems();

        if (unpinning)
        {
            SelectPinnedOrFirst();
        }
    }

    /// <summary>
    /// After unpinning, the item drops out of the pinned block and lands somewhere in the time
    /// flow; following it would throw the selection far down the list. Stay in the pinned block
    /// instead, and once it is empty fall back to the first row below it.
    /// </summary>
    private void SelectPinnedOrFirst()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var target = _rows.FirstOrDefault(candidate => candidate.IsPinned);
        if (target is not null)
        {
            ItemList.SelectedItem = target;
        }
        else
        {
            ItemList.SelectedIndex = 0;
        }

        ItemList.ScrollIntoView(ItemList.SelectedItem);
    }

    private void DeleteSelected()
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            return;
        }

        var index = ItemList.SelectedIndex;
        if (row.Item.IsSensitive)
        {
            _queue.Delete(row.Item.Id);
            Log.Info($"Deleted sensitive item #{row.Item.Id}");
        }
        else
        {
            _store.Delete(row.Item.Id);
            Log.Info($"Deleted item #{row.Item.Id}");
        }

        RefreshItems();

        if (_rows.Count > 0)
        {
            ItemList.SelectedIndex = Math.Clamp(index, 0, _rows.Count - 1);
            ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
    }

    /// <summary>
    /// Edits the text of the selected item. Images and files have no editor in this version,
    /// matching the plan's "no image or rich text editing" scope.
    /// </summary>
    private void EditSelected()
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            return;
        }

        if (row.Item.IsSensitive)
        {
            SetStatus("敏感内容只留在内存里，不能编辑");
            return;
        }

        if (row.Item.Type != ClipType.Text)
        {
            SetStatus("只有文本条目可以编辑");
            return;
        }

        // The dialog deactivates this window; without the flag the panel would hide behind it.
        var dialog = new EditWindow(row.Item.Text ?? string.Empty);
        if (ShowDialogSuppressed(dialog) != true || dialog.EditedText == (row.Item.Text ?? string.Empty))
        {
            return;
        }

        var hash = ClipPayload.ForText(dialog.EditedText).ContentHash;
        if (!_store.UpdateText(
                row.Item.Id,
                dialog.EditedText,
                hash,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            SetStatus("相同内容已经存在，这次修改没有保存");
            return;
        }

        RefreshItems();
    }

    private void CycleFilter(int delta)
    {
        var chips = new[] { FilterAll, FilterText, FilterLink, FilterImage, FilterFile, FilterSensitive };
        var current = Array.FindIndex(chips, chip => chip.IsChecked == true);
        var next = ((current < 0 ? 0 : current) + delta + chips.Length) % chips.Length;
        chips[next].IsChecked = true;
    }

    private void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        _filter = tag switch
        {
            "text" or "link" => ClipType.Text,
            "image" => ClipType.Image,
            "file" => ClipType.File,
            _ => null,
        };
        _linksOnly = tag == "link";
        _sensitiveOnly = tag == "sensitive";

        RefreshItems();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt turns the real key into SystemKey, which is what carries the key code.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = ToHotkeyModifiers(Keyboard.Modifiers);

        switch (key)
        {
            // Navigation and dismissal are not configurable: they are how the panel works rather
            // than what it does, and rebinding them would only strand the user.
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else
                {
                    HidePanel(returnFocus: true);
                }

                e.Handled = true;
                return;

            case Key.Down when SearchBox.IsKeyboardFocusWithin:
                MoveSelection(1);
                e.Handled = true;
                return;

            case Key.Up when SearchBox.IsKeyboardFocusWithin:
                MoveSelection(-1);
                e.Handled = true;
                return;
        }

        var action = MatchAction(key, modifiers);
        if (action is null)
        {
            return;
        }

        e.Handled = true;

        switch (action.Value)
        {
            case HotkeyAction.FocusSearch:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;

            case HotkeyAction.Paste:
                PasteSelected();
                break;

            case HotkeyAction.PastePlain:
                PasteSelected(plainText: true);
                break;

            case HotkeyAction.Delete:
                // While the search box has text, Delete belongs to the text field.
                if (!SearchBox.IsKeyboardFocusWithin || SearchBox.Text.Length == 0)
                {
                    DeleteSelected();
                }
                else
                {
                    e.Handled = false;
                }

                break;

            case HotkeyAction.Edit:
                EditSelected();
                break;

            case HotkeyAction.Pin:
                TogglePinSelected();
                break;

            case HotkeyAction.CycleFilter:
                // Ctrl+Shift+Tab walks the filter bar backwards; keep that even when the forward
                // gesture carries a Shift of its own.
                var backwards = !_hotkeys[HotkeyAction.CycleFilter].Modifiers.HasFlag(HotkeyModifiers.Shift)
                    && modifiers.HasFlag(HotkeyModifiers.Shift);
                CycleFilter(backwards ? -1 : 1);
                break;

            case HotkeyAction.ClearHistory:
                ClearHistoryRequested?.Invoke();
                break;
        }
    }

    /// <summary>Finds the action bound to a keystroke, if any.</summary>
    private HotkeyAction? MatchAction(Key key, HotkeyModifiers modifiers)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        foreach (var (action, gesture) in _hotkeys)
        {
            if (Hotkeys.Matches(gesture, virtualKey, modifiers))
            {
                return action;
            }
        }

        // The reverse filter gesture is derived rather than configurable.
        if (_hotkeys[HotkeyAction.CycleFilter] is var filter
            && !filter.Modifiers.HasFlag(HotkeyModifiers.Shift)
            && Hotkeys.Matches(filter, virtualKey, modifiers & ~HotkeyModifiers.Shift)
            && modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            return HotkeyAction.CycleFilter;
        }

        return null;
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys keys)
    {
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (keys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keys.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate || !IsVisible)
        {
            return;
        }

        // A freshly shown window can raise Deactivated before it gains activation;
        // re-check on the dispatcher instead of hiding immediately.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_suppressDeactivate && IsVisible && !IsActive)
            {
                HidePanel();
            }
        }), DispatcherPriority.Background);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems();

    /// <summary>
    /// Pointer over the list: show the preview for the row under the cursor, after a short delay so
    /// sweeping across rows does not strobe. Moving onto a row with nothing to preview closes the
    /// tip at once instead of leaving the previous row's preview on screen.
    /// </summary>
    private void ItemList_MouseMove(object sender, MouseEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemList, e.OriginalSource as DependencyObject) is FrameworkElement container &&
            container.DataContext is ItemRow row)
        {
            if (ReferenceEquals(container, _tipAnchor) || ReferenceEquals(container, _pendingAnchor))
            {
                return;
            }

            if (NeedsTip(row, container))
            {
                _pendingRow = row;
                _pendingAnchor = container;
                _tipDelay.Stop();
                _tipDelay.Start();
            }
            else
            {
                HideRowTip();
            }

            return;
        }

        HideRowTip();
    }

    private void ItemList_MouseLeave(object sender, MouseEventArgs e) => HideRowTip();

    /// <summary>Row containers move under the tip while scrolling, so the tip would drift: drop it.</summary>
    private void ItemList_ScrollChanged(object sender, ScrollChangedEventArgs e) => HideRowTip();

    private void OnTipDelayElapsed(object? sender, EventArgs e)
    {
        _tipDelay.Stop();

        if (_pendingRow is { } row && _pendingAnchor is { } anchor)
        {
            ShowRowTip(row, anchor);
        }
    }

    /// <summary>Keyboard navigation jumps the selection, so the tip follows it without the delay.</summary>
    private void UpdateTipForSelection()
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            HideRowTip();
            return;
        }

        // A row that was only just scrolled into view has no container until layout runs.
        ItemList.UpdateLayout();

        if (ItemList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container &&
            NeedsTip(row, container))
        {
            ShowRowTip(row, container);
        }
        else
        {
            HideRowTip();
        }
    }

    /// <summary>
    /// A row earns a tip for one of two reasons: it holds an image worth seeing larger, or its text
    /// does not fit in the two lines the row allows.
    /// </summary>
    private bool NeedsTip(ItemRow row, FrameworkElement container)
    {
        if (!_previewTip)
        {
            return false;
        }

        if (row.Thumbnail is not null)
        {
            return true;
        }

        return row.IsPreviewTrimmed || IsTextClipped(FindPreviewText(container));
    }

    private void ShowRowTip(ItemRow row, FrameworkElement anchor)
    {
        _tipDelay.Stop();
        _pendingRow = null;
        _pendingAnchor = null;

        if (!_previewTip)
        {
            return;
        }

        ImageSource? image = row.Thumbnail is null ? null : row.PreviewImage;
        string? text = null;

        if (image is null)
        {
            text = ClipTipText(row.Item.Text);
            if (string.IsNullOrEmpty(text))
            {
                HideRowTip();
                return;
            }
        }

        TipImage.Source = image;
        TipImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        TipText.Text = text;
        TipText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;

        FitTipToScreen(anchor);

        _tipAnchor = anchor;
        RowTip.IsOpen = true;
    }

    private void HideRowTip()
    {
        _tipDelay.Stop();
        _pendingRow = null;
        _pendingAnchor = null;

        if (RowTip.IsOpen)
        {
            RowTip.IsOpen = false;
        }

        if (_tipAnchor is null)
        {
            return;
        }

        _tipAnchor = null;

        // Drop the decoded pixels with the tip; the row keeps its own cache for the next visit.
        TipImage.Source = null;
        TipText.Text = null;
    }

    /// <summary>
    /// Sizes the tip to the room beside the panel and puts it on the side that has more of that
    /// room. Anchoring to the panel instead of the row keeps the tip outside the panel: anchored to
    /// a row, "place on the left" means "over the list".
    /// </summary>
    private void FitTipToScreen(FrameworkElement anchor)
    {
        var work = SystemParameters.WorkArea;
        var gap = RowTip.HorizontalOffset;
        var right = work.Right - (Left + ActualWidth) - gap;
        var left = Left - work.Left - gap;

        // Right first: it is the side the list reads towards. The left side only wins when the
        // right one is too narrow to be worth it, because flipping sides on a near-tie looks random.
        var useRight = right >= 300 || right >= left;
        var width = Math.Clamp(useRight ? right : left, 240, 520);

        TipImage.MaxWidth = width;
        TipText.MaxWidth = width;

        RowTip.PlacementTarget = this;
        RowTip.Placement = useRight ? PlacementMode.Right : PlacementMode.Left;
        RowTip.VerticalOffset = Math.Max(0, anchor.TranslatePoint(new Point(0, 0), this).Y - 8);
    }

    /// <summary>The row's preview block, found by name because it lives inside the row template.</summary>
    private static TextBlock? FindPreviewText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Name: "PreviewText" } block)
            {
                return block;
            }

            if (FindPreviewText(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the two-line block is actually cutting the entry off. Measured with FormattedText so
    /// the block's own layout is never disturbed; MaxHeight is two lines of LineHeight.
    /// </summary>
    private static bool IsTextClipped(TextBlock? block)
    {
        if (block is null || string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0)
        {
            return false;
        }

        if (double.IsNaN(block.MaxHeight))
        {
            return false;
        }

        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var formatted = new FormattedText(
            block.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            block.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip)
        {
            MaxTextWidth = block.ActualWidth,
        };

        return formatted.Height > block.MaxHeight;
    }

    /// <summary>
    /// Full text for the tip, capped: a pasted log can be megabytes, and a tip nobody can reach the
    /// end of is worse than one that says it stopped.
    /// </summary>
    private static string? ClipTipText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        return text.Length <= TipTextLimit ? text : text[..TipTextLimit] + "\n…（内容过长，已截断）";
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemRow row)
        {
            // Same rule as the Ctrl+P path: sensitive entries live in memory only, so there is
            // nothing in the store to pin.
            if (row.Item.IsSensitive)
            {
                SetStatus("敏感内容只留在内存里，不能置顶");
            }
            else
            {
                _store.SetPinned(row.Item.Id, !row.Item.Pinned, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                RefreshItems();
            }
        }

        e.Handled = true;
    }

    /// <summary>Buttons inside a row (the pin control) must not trigger the row's paste.</summary>
    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void ItemList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInsideButton(source))
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(ItemList, source) is not ListBoxItem container)
        {
            return;
        }

        if (container.DataContext is not ItemRow row)
        {
            return;
        }

        ItemList.SelectedItem = row;
        PasteSelected();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.OriginalSource is DependencyObject source &&
            (IsDescendantOf(source, SearchBox) || IsDescendantOf(source, ItemList)))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag started; harmless.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Nothing about quitting may hinge on geometry: base.OnClosed raises Closed, and that
        // is what shuts the application down.
        try
        {
            SaveGeometry();
        }
        catch (Exception ex)
        {
            Log.Error("Saving panel geometry failed", ex);
        }

        base.OnClosed(e);
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
