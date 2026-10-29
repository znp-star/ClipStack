using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClipStack.App.Interop;
using Microsoft.Win32;

namespace ClipStack.App;

/// <summary>
/// Owns the three-state theme (follow system / light / dark). The switch itself is a dictionary
/// swap: the app has no hard-coded colours, so replacing the merged palette repaints everything.
/// Accent brushes are layered on top afterwards, and the DWM frame of every open window is told
/// which mode it is in.
/// </summary>
internal static class ThemeManager
{
    private const string ThemesPath = "Themes/";
    private const string LightPalette = ThemesPath + "Light.xaml";
    private const string DarkPalette = ThemesPath + "Dark.xaml";

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private static bool _watchingSystem;

    /// <summary>"system", "light" or "dark" — the value stored in settings.</summary>
    public static string Preference { get; private set; } = "system";

    /// <summary>Whether the palette currently in use is the dark one.</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(string? preference)
    {
        Preference = Normalize(preference);
        IsDark = ResolveIsDark(Preference);

        SwapPalette(IsDark);
        ApplyAccent(IsDark);
        ApplyWindowFrames(IsDark);
        WatchSystemChanges();
    }

    public static string Normalize(string? preference) => preference?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };

    /// <summary>Reads the Windows "app mode" setting; follows it only in system mode.</summary>
    public static bool ResolveIsDark(string preference) => preference switch
    {
        "dark" => true,
        "light" => false,
        _ => SystemPrefersDark(),
    };

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightTheme) is int light && light == 0;
        }
        catch (Exception)
        {
            // A locked-down registry is not a reason to fail startup; light is the safe default.
            return false;
        }
    }

    private static void SwapPalette(bool dark)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative),
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(palette);
        }
        else
        {
            // The palette is always the first merged dictionary, added by App.xaml.
            merged[0] = palette;
        }
    }

    /// <summary>
    /// Layers the system accent over the palette. Dark mode needs a lighter tint: the colour that
    /// reads well on white is invisible on #1F1F23.
    /// </summary>
    private static void ApplyAccent(bool dark)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var accent = DwmInterop.GetAccentColor();
        var red = (byte)(accent >> 16);
        var green = (byte)(accent >> 8);
        var blue = (byte)accent;

        if (dark)
        {
            red = Lighten(red);
            green = Lighten(green);
            blue = Lighten(blue);
        }

        app.Resources["AccentBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(red, green, blue)));
        app.Resources["SelectionBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(
            dark ? (byte)0x33 : (byte)0x2E, red, green, blue)));
        app.Resources["SelectionEdgeBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(
            dark ? (byte)0x66 : (byte)0x40, red, green, blue)));
        app.Resources["PinnedRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(
            dark ? (byte)0x1A : (byte)0x0F, red, green, blue)));
    }

    /// <summary>Blends a channel 40% towards white.</summary>
    private static byte Lighten(byte channel) => (byte)(channel + (255 - channel) * 0.4);

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static void ApplyWindowFrames(bool dark)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            if (window is MainWindow panel)
            {
                panel.ApplyTheme(dark);
                continue;
            }

            // Dialogs keep the system title bar; tell DWM to paint it dark as well.
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                DwmInterop.ApplyDarkFrame(hwnd, dark);
            }
        }
    }

    /// <summary>
    /// Applies the dark title bar to a dialog when it is created. Dialogs opened after the last
    /// theme change would otherwise come up with a light frame.
    /// </summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            DwmInterop.ApplyDarkFrame(hwnd, IsDark);
        }
    };

    /// <summary>In system mode, follow the OS when the user flips app mode while we run.</summary>
    private static void WatchSystemChanges()
    {
        if (_watchingSystem)
        {
            return;
        }

        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || Preference != "system")
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(Preference)));
        };
    }
}
