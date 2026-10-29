using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipStack.App.Interop;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;
using ClipStack.Core.Storage;

namespace ClipStack.App;

public partial class MainWindow : Window
{
    private const int MaxRows = 200;

    private readonly ItemStore _store;
    private readonly ObservableCollection<ItemRow> _rows = [];
    private IntPtr _target;
    private bool _suppressDeactivate;
    private bool _ready;

    public MainWindow(ItemStore store)
    {
        _store = store;
        InitializeComponent();

        ItemList.ItemsSource = _rows;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
        SourceInitialized += (_, _) => DwmInterop.ApplyRoundedCorners(new WindowInteropHelper(this).Handle);

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

        RefreshItems();

        Show();

        // Windows may refuse the activation; force it so the panel can take key input.
        WindowActivation.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);

        if (ItemList.SelectedIndex < 0 && _rows.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }
    }

    public void HidePanel() => Hide();

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

        var items = _store.Query(SearchBox.Text, null, MaxRows);

        _rows.Clear();
        foreach (var item in items)
        {
            _rows.Add(new ItemRow(item));
        }

        EmptyHint.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? "还没有记录，复制点什么试试"
            : "没有匹配的记录";
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

    private void PasteSelected()
    {
        if (ItemList.SelectedItem is not ItemRow row)
        {
            return;
        }

        _store.Touch(row.Item.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _suppressDeactivate = true;
        Hide();

        var result = PasteInvoker.Paste(row.Item.ToPastePayload(), _target);

        if (result.Outcome != PasteOutcome.Sent)
        {
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else
                {
                    Hide();
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
                Hide();
            }
        }), DispatcherPriority.Background);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshItems();

    private void ItemList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
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
