using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipStack.Core.Settings;

namespace ClipStack.App;

/// <summary>
/// Edits the handful of settings that exist so far. Values are only written back when the dialog
/// returns true: the caller (<see cref="App"/>) then saves the file and re-applies everything, so a
/// cancelled dialog leaves no trace.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int MinRetentionDays = 1;
    private const int MaxRetentionDays = 30;
    private const int MinSensitiveMinutes = 1;
    private const int MaxSensitiveMinutes = 60;

    private readonly ObservableCollection<HotkeyRow> _rows = [];
    private readonly Func<HotkeyGesture, string?> _tryRegisterGlobal;
    private readonly HotkeyGesture _registeredGlobal;

    public SettingsWindow(AppSettings current, Func<HotkeyGesture, string?> tryRegisterGlobal)
    {
        _tryRegisterGlobal = tryRegisterGlobal;
        InitializeComponent();
        ThemeManager.Attach(this);

        RetentionBox.Text = current.RetentionDays.ToString();
        SensitiveBox.Text = current.SensitiveTtlMinutes.ToString();
        AutostartCheck.IsChecked = current.Autostart;
        PreviewTipCheck.IsChecked = current.ShowPreviewTip;

        switch (ThemeManager.Normalize(current.Theme))
        {
            case "light":
                ThemeLight.IsChecked = true;
                break;
            case "dark":
                ThemeDark.IsChecked = true;
                break;
            default:
                ThemeSystem.IsChecked = true;
                break;
        }

        var table = Hotkeys.Resolve(current.Hotkeys);
        _registeredGlobal = table[HotkeyAction.ShowPanel];

        foreach (var action in Hotkeys.All)
        {
            _rows.Add(new HotkeyRow(action, table[action]));
        }

        HotkeyList.ItemsSource = _rows;
    }

    public int RetentionDays { get; private set; }

    public int SensitiveTtlMinutes { get; private set; }

    public bool Autostart { get; private set; }

    /// <summary>Whether the panel shows the row preview tip.</summary>
    public bool ShowPreviewTip { get; private set; } = true;

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; private set; } = "system";

    /// <summary>The action map in the shape <c>settings.json</c> stores it.</summary>
    public Dictionary<string, string> HotkeyMap { get; private set; } = [];

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        MessageText.Visibility = Visibility.Collapsed;

        if (!TryReadNumber(RetentionBox, MinRetentionDays, MaxRetentionDays, RetentionError, out var days)
            || !TryReadNumber(SensitiveBox, MinSensitiveMinutes, MaxSensitiveMinutes, SensitiveError, out var minutes))
        {
            return;
        }

        var table = _rows.ToDictionary(row => row.Action, row => row.Gesture);

        var valid = true;
        foreach (var row in _rows)
        {
            row.Error = Hotkeys.TryValidate(row.Action, row.Gesture, table, out var error) ? null : error;
            valid &= row.Error is null;
        }

        if (!valid)
        {
            ShowMessage("快捷键有冲突或不合格，请先修正标红的那几行。");
            return;
        }

        // The global hotkey is the only one the OS can refuse, so it is registered before the
        // dialog closes: accepting a gesture that cannot be registered would leave the panel
        // unreachable.
        var showPanel = table[HotkeyAction.ShowPanel];
        if (showPanel != _registeredGlobal)
        {
            var failure = _tryRegisterGlobal(showPanel);
            if (failure is not null)
            {
                _rows.First(row => row.Action == HotkeyAction.ShowPanel).Error = failure;
                ShowMessage("全局快捷键没能注册，换一个再试。");
                return;
            }
        }

        RetentionDays = days;
        SensitiveTtlMinutes = minutes;
        Autostart = AutostartCheck.IsChecked == true;
        ShowPreviewTip = PreviewTipCheck.IsChecked == true;
        Theme = ThemeDark.IsChecked == true ? "dark" : ThemeLight.IsChecked == true ? "light" : "system";
        HotkeyMap = Hotkeys.ToSettings(table);

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Gesture = Hotkeys.Default(row.Action);
            row.Error = null;
            row.IsRecording = false;
        }

        ShowMessage("已恢复默认快捷键，点「保存」后生效。");
    }

    /// <summary>Clicking a key cap starts capture; the next keystroke becomes the gesture.</summary>
    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyRow row } button)
        {
            return;
        }

        foreach (var other in _rows)
        {
            other.IsRecording = false;
        }

        row.IsRecording = true;
        button.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The dialog captures the keystroke, not just the focused key cap: the cap went deaf the
        // moment focus moved elsewhere (a system flyout such as Win+V takes it), which left the
        // box sitting on "请按键" while nothing could reach it.
        var row = _rows.FirstOrDefault(r => r.IsRecording);
        if (row is null)
        {
            return;
        }

        e.Handled = true;

        var key = HotkeyInput.KeyOf(e);

        if (key == Key.Escape)
        {
            row.IsRecording = false;
            return;
        }

        // Windows keeps the Win+key combinations for itself and never delivers them here, so
        // saying so beats waiting for a keystroke that cannot arrive.
        if (key is Key.LWin or Key.RWin)
        {
            row.Error = "Win 组合键被系统占用，请改用 Ctrl / Alt / Shift";
            return;
        }

        var gesture = HotkeyInput.Gesture(e, Keyboard.Modifiers);

        // Holding Ctrl on the way to Ctrl+Shift+V must not bind "Ctrl" itself.
        if (gesture.IsEmpty || Hotkeys.IsModifierKey(gesture.VirtualKey))
        {
            return;
        }

        // Backspace clears the box; Delete stays available because it is a default binding.
        if (gesture.VirtualKey == 0x08 && gesture.Modifiers == default)
        {
            row.Gesture = HotkeyGesture.None;
            row.Error = null;
            row.IsRecording = false;
            return;
        }

        row.Gesture = gesture;
        row.Error = null;
        row.IsRecording = false;
    }

    /// <summary>A key cap that lost the window must not stay on "请按键" forever.</summary>
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        foreach (var row in _rows)
        {
            row.IsRecording = false;
        }
    }

    private static bool TryReadNumber(TextBox box, int min, int max, TextBlock error, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max)
        {
            error.Visibility = Visibility.Collapsed;
            return true;
        }

        value = 0;
        error.Text = $"请填 {min}–{max} 之间的整数";
        error.Visibility = Visibility.Visible;
        return false;
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }
}
