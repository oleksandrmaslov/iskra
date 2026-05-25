using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashlightApp.Core;

/// <summary>
/// User-facing app settings: catalog source, debugger/BMP knobs, log location.
/// Persisted as JSON in the per-user appdata directory. Used by the WPF UI;
/// the CLI continues to take everything via flags, but can read defaults from
/// the same file in the future.
/// </summary>
public sealed class AppSettings
{
    // Catalog
    public string? CatalogPath { get; set; }
    public bool RequireSignedCatalog { get; set; }

    // Debugger / Black Magic Probe
    public string? GdbPath { get; set; }
    public int BmpFrequencyHz { get; set; } = 1_000_000;
    public PowerMode Power { get; set; } = PowerMode.External;
    public bool ConnectUnderReset { get; set; }
    public int TimeoutSeconds { get; set; } = 15;

    // Logging
    public string? DbPath { get; set; }
    public string StationId { get; set; } = Environment.MachineName;

    // Operator quality-of-life: remember last picks
    public string? LastOperator { get; set; }
    public string? LastBatch { get; set; }

    public AppSettings Clone() => new()
    {
        CatalogPath          = CatalogPath,
        RequireSignedCatalog = RequireSignedCatalog,
        GdbPath              = GdbPath,
        BmpFrequencyHz       = BmpFrequencyHz,
        Power                = Power,
        ConnectUnderReset    = ConnectUnderReset,
        TimeoutSeconds       = TimeoutSeconds,
        DbPath               = DbPath,
        StationId            = StationId,
        LastOperator         = LastOperator,
        LastBatch            = LastBatch,
    };
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Default location: <c>%LOCALAPPDATA%\FlashlightApp\settings.json</c>.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FlashlightApp",
        "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings file shouldn't break the app — fall back to defaults.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
