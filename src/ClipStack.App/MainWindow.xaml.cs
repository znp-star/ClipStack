using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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

    private static readonly TimeSpan PanelAnimation = TimeSpan.FromMilliseconds(120);

    private readonly ItemStore _store;
    private readonly SettingsStore _settings;
    private readonly SensitiveQueue _queue;
    private readonly ObservableCollection<ItemRow> _rows = [];
    private IntPtr _target;
    private Monitors.MonitorInfo _monitor;
    private ClipType? _filter;
    private bool _linksOnly;
    private bool _sensitiveOnly;
    private bool _suppressDeactivate;
    private bool _ready;

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

        _ready = true;
    }

    /// <summary>Shows the panel and remembers which window should receive the paste.</summary>
    public void ShowPanel(IntPtr target)
    {
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

        if (ItemList.SelectedIndex < 0 && _rows.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }

        AnimateIn();
    }

    public void HidePanel()
    {
        SaveGeometry();
        Hide();
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

        // Without Mica (older Windows) the translucent surface would show the raw glass,
        // so fall back to an opaque panel background.
        var mica = DwmInterop.TryApplyMica(hwnd, dark: false);
        Log.Info($"Panel backdrop: {(mica ? "Mica" : "opaque fallback")}");
        if (!mica)
        {
            RootBorder.Background = (Brush)FindResource("SurfaceBrush");
        }
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
        var monitor = _monitor.Device.Length > 0
            ? _monitor
            : Monitors.ForWindow(new WindowInteropHelper(this).Handle);
        if (monitor.Device.Length == 0)
        {
            return;
        }

        var scale = monitor.Scale;
        _settings.Current.PanelGeometry[monitor.Device] = new PanelGeometry
        {
            X = Math.Round(Left * scale),
            Y = Math.Round(Top * scale),
            Width = Math.Round(Width * scale),
            Height = Math.Round(Height * scale),
            Dpi = monitor.Dpi,
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

    private void PasteSelected()
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
        SaveGeometry();
        Hide();

        var payload = row.Item.ToPastePayload();
        if (payload.ImagePng is { Length: > 0 } png)
        {
            // Chromium-based apps ignore the raw PNG clipboard format; hand them DIB pixels too.
            payload = payload.WithDib(DibConverter.FromPng(png));
        }

        var result = PasteInvoker.Paste(payload, _target);

        if (result.Outcome != PasteOutcome.Sent)
        {
            Log.Warn($"Paste not delivered: {result.Reason}");
            // Nothing was typed into the target, so bring the panel back with the reason.
            ShowPanel(_target);
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
        _suppressDeactivate = true;
        try
        {
            var dialog = new EditWindow(row.Item.Text ?? string.Empty) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.EditedText == (row.Item.Text ?? string.Empty))
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
        finally
        {
            _suppressDeactivate = false;

            var hwnd = new WindowInteropHelper(this).Handle;
            WindowActivation.ForceForeground(hwnd);
            Activate();
            SearchBox.Focus();
        }
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
        var modifiers = Keyboard.Modifiers;

        switch (e.Key)
        {
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else
                {
                    HidePanel();
                }

                e.Handled = true;
                break;

            case Key.Enter:
                PasteSelected();
                e.Handled = true;
                break;

            case Key.Down when SearchBox.IsKeyboardFocusWithin:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up when SearchBox.IsKeyboardFocusWithin:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Delete when !SearchBox.IsKeyboardFocusWithin || SearchBox.Text.Length == 0:
                DeleteSelected();
                e.Handled = true;
                break;

            case Key.F2:
                EditSelected();
                e.Handled = true;
                break;

            case Key.P when modifiers == ModifierKeys.Control:
                TogglePinSelected();
                e.Handled = true;
                break;

            case Key.Tab when modifiers == ModifierKeys.Control:
                CycleFilter(1);
                e.Handled = true;
                break;

            case Key.Tab when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                CycleFilter(-1);
                e.Handled = true;
                break;
        }
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

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

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
        SaveGeometry();
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
