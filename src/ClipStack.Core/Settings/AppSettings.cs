using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipStack.Core.Settings;

/// <summary>Panel bounds as remembered per monitor, in device-independent pixels plus the DPI they were saved at.</summary>
public sealed class PanelGeometry
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Dpi { get; set; } = 96;
}

public sealed class AppSettings
{
    public int RetentionDays { get; set; } = 2;
    public int SensitiveTtlMinutes { get; set; } = 10;
    public long MaxItemBytes { get; set; } = 10 * 1024 * 1024;
    public string Theme { get; set; } = "system";
    public bool Autostart { get; set; } = true;

    /// <summary>Keyed by monitor device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public Dictionary<string, PanelGeometry> PanelGeometry { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Settings live in a single JSON file next to the database. Unknown keys are ignored so
/// older builds can read newer files, and a corrupt file falls back to defaults instead of
/// blocking startup.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            Current = new AppSettings();
        }

        Current.PanelGeometry = new Dictionary<string, PanelGeometry>(
            Current.PanelGeometry, StringComparer.OrdinalIgnoreCase);

        return Current;
    }

    /// <summary>Writes atomically (temp file + replace) so a crash cannot truncate the settings.</summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
