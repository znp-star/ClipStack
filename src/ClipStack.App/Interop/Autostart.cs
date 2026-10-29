using Microsoft.Win32;

namespace ClipStack.App.Interop;

/// <summary>
/// Per-user "run at sign-in" entry, so no admin rights and no scheduled task are involved.
/// The stored command line carries <c>--silent</c>: auto-start must not pop the panel.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipStack";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return;
        }

        // Environment.ProcessPath is the apphost, not dotnet.exe, for a framework-dependent build.
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        key.SetValue(ValueName, $"\"{executable}\" --silent");
    }
}
