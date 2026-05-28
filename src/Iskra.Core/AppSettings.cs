using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

/// <summary>
/// Operator-configurable keyboard shortcut that triggers the FLASH button
/// on the WPF main window. <see cref="None"/> disables the shortcut entirely.
/// Choices are intentionally limited to keys that don't clash with common
/// text-entry: Enter and the F-keys are always safe; Space is suppressed
/// while focus is on a text input.
/// </summary>
public enum FlashHotkey
{
    None,
    Space,
    Enter,
    F2,
    F5,
}

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

    // Remote catalog auto-update (Sprint 3.5)
    public bool CatalogAutoUpdate { get; set; } = true;
    public string CatalogOwner { get; set; } = "oleksandrmaslov";
    public string CatalogRepo  { get; set; } = "iskra-catalog";

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

    // Operator-configurable hotkey that triggers the FLASH button on the
    // Flash tab. Default Enter — barcode scanners emit it as the line
    // terminator, so the typical operator flow "scan batch → press flash"
    // collapses to a single barcode swipe.
    public FlashHotkey FlashHotkey { get; set; } = FlashHotkey.Enter;

    public AppSettings Clone() => new()
    {
        CatalogPath          = CatalogPath,
        RequireSignedCatalog = RequireSignedCatalog,
        CatalogAutoUpdate    = CatalogAutoUpdate,
        CatalogOwner         = CatalogOwner,
        CatalogRepo          = CatalogRepo,
        GdbPath              = GdbPath,
        BmpFrequencyHz       = BmpFrequencyHz,
        Power                = Power,
        ConnectUnderReset    = ConnectUnderReset,
        TimeoutSeconds       = TimeoutSeconds,
        DbPath               = DbPath,
        StationId            = StationId,
        LastOperator         = LastOperator,
        LastBatch            = LastBatch,
        FlashHotkey          = FlashHotkey,
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
    /// Default location: <c>%LOCALAPPDATA%\Iskra\settings.json</c>.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Iskra",
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
