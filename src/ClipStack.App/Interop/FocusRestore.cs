using System.Windows.Automation;
using ClipStack.Core.Interop;
using ClipStack.Core.Logging;

namespace ClipStack.App.Interop;

/// <summary>
/// Remembers which control had keyboard focus before the panel took the foreground, so it can be
/// handed back afterwards. Re-activating the top-level window alone is not enough: Qt apps such as
/// DingTalk put the focus on the first focusable widget of the window (a title-bar button) when
/// they are activated again, so the synthesized Ctrl+V lands on that button and nothing is pasted.
/// </summary>
internal sealed class FocusRestore
{
    private readonly AutomationElement _element;
    private readonly string _description;

    private FocusRestore(AutomationElement element, string description)
    {
        _element = element;
        _description = description;
    }

    /// <summary>
    /// Snapshots the focused control, but only when it belongs to the target's process: when the
    /// panel is opened from the tray, the focus is still on one of our own windows.
    /// </summary>
    public static FocusRestore? Capture(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId != WindowInfo.GetProcessId(target))
            {
                return null;
            }

            var info = focused.Current;
            return new FocusRestore(focused, $"{info.ClassName} \"{info.Name}\"");
        }
        catch (Exception ex)
        {
            // UI Automation is best effort; without it the paste behaves as it did before.
            Log.Warn($"Could not read the focused control: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gives the target window the foreground and the control the caret back. Runs after the panel
    /// is hidden and before the paste keystroke: the app has already been activated, this only
    /// puts the focus back where the user left it.
    /// </summary>
    public void Restore(IntPtr target)
    {
        try
        {
            if (target != IntPtr.Zero && WindowInfo.GetForeground() != target)
            {
                WindowInfo.Activate(target);
            }

            _element.SetFocus();
            Log.Info($"Focus returned to {_description}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not return the focus: {ex.Message}");
        }
    }
}
