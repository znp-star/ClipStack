using System.ComponentModel;
using ClipStack.Core.Settings;

namespace ClipStack.App;

/// <summary>One line of the hotkey list: the action, its current gesture, and any complaint.</summary>
public sealed class HotkeyRow : INotifyPropertyChanged
{
    private HotkeyGesture _gesture;
    private bool _isRecording;
    private string? _error;

    public HotkeyRow(HotkeyAction action, HotkeyGesture gesture)
    {
        Action = action;
        _gesture = gesture;
    }

    public HotkeyAction Action { get; }

    public string Label => Hotkeys.Label(Action);

    /// <summary>"全局" or "面板内", shown under the label so the scope is never a surprise.</summary>
    public string ScopeText => Hotkeys.IsGlobal(Action) ? "全局" : "面板内";

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set
        {
            if (_gesture == value)
            {
                return;
            }

            _gesture = value;
            Raise(nameof(Gesture));
            Raise(nameof(GestureText));
        }
    }

    /// <summary>True while the box is waiting for a keystroke.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (_isRecording == value)
            {
                return;
            }

            _isRecording = value;
            Raise(nameof(IsRecording));
            Raise(nameof(GestureText));
        }
    }

    public string GestureText => IsRecording
        ? "请按键…"
        : _gesture.IsEmpty ? "未设置" : HotkeyGesture.Format(_gesture);

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value)
            {
                return;
            }

            _error = value;
            Raise(nameof(Error));
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
