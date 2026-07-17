using Iskra.Core;

namespace Iskra.Core.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _path;

    public AppSettingsTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var s = AppSettingsStore.Load(_path);
        Assert.Equal(1_000_000, s.BmpFrequencyHz);
        Assert.Equal(PowerMode.External, s.Power);
        Assert.True(s.RequireSignedCatalog);
        Assert.False(s.ConnectUnderReset);
        Assert.Equal(15, s.TimeoutSeconds);
        Assert.Equal(Environment.MachineName, s.StationId);
        Assert.False(s.BatchesEnabled);
        Assert.Equal(IskraLanguages.Ukrainian, s.LanguageCode);
        Assert.Null(s.CatalogPath);
        Assert.Null(s.GdbPath);
        Assert.Equal(FlashHotkey.Enter, s.FlashHotkey);
    }

    [Fact]
    public void Load_clamps_unsigned_setting_without_explicit_lab_environment()
    {
        var old = Environment.GetEnvironmentVariable(CatalogTrust.UnsignedLabModeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CatalogTrust.UnsignedLabModeEnvironmentVariable, null);
            AppSettingsStore.Save(new AppSettings { RequireSignedCatalog = false }, _path);

            Assert.True(AppSettingsStore.Load(_path).RequireSignedCatalog);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CatalogTrust.UnsignedLabModeEnvironmentVariable, old);
        }
    }

    [Fact]
    public void Round_trip_preserves_all_fields()
    {
        var original = new AppSettings
        {
            LanguageCode         = IskraLanguages.German,
            CatalogPath           = @"D:\fw\catalog.json",
            RequireSignedCatalog  = true,
            GdbPath               = @"C:\arm\bin\arm-none-eabi-gdb.exe",
            BmpFrequencyHz        = 2_000_000,
            Power                 = PowerMode.Probe,
            ConnectUnderReset     = true,
            TimeoutSeconds        = 25,
            DbPath                = @"D:\logs\flash.db",
            StationId             = "BENCH-7",
            BatchesEnabled        = true,
            LastOperator          = "Iryna",
            LastBatch             = "B-2026-099",
            FlashHotkey           = FlashHotkey.Space,
        };
        AppSettingsStore.Save(original, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal(original.CatalogPath,          loaded.CatalogPath);
        Assert.Equal(original.LanguageCode,         loaded.LanguageCode);
        Assert.Equal(original.RequireSignedCatalog, loaded.RequireSignedCatalog);
        Assert.Equal(original.GdbPath,              loaded.GdbPath);
        Assert.Equal(original.BmpFrequencyHz,       loaded.BmpFrequencyHz);
        Assert.Equal(original.Power,                loaded.Power);
        Assert.Equal(original.ConnectUnderReset,    loaded.ConnectUnderReset);
        Assert.Equal(original.TimeoutSeconds,       loaded.TimeoutSeconds);
        Assert.Equal(original.DbPath,               loaded.DbPath);
        Assert.Equal(original.StationId,            loaded.StationId);
        Assert.Equal(original.BatchesEnabled,       loaded.BatchesEnabled);
        Assert.Equal(original.LastOperator,         loaded.LastOperator);
        Assert.Equal(original.LastBatch,            loaded.LastBatch);
        Assert.Equal(original.FlashHotkey,          loaded.FlashHotkey);
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flsettings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "nested", "settings.json");
        try
        {
            Assert.False(Directory.Exists(dir));
            AppSettingsStore.Save(new AppSettings { StationId = "X" }, path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_is_atomic_via_tmp_rename()
    {
        // The implementation writes to <path>.tmp then moves. Confirm no .tmp lingers.
        AppSettingsStore.Save(new AppSettings { StationId = "first" }, _path);
        AppSettingsStore.Save(new AppSettings { StationId = "second" }, _path);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal("second", AppSettingsStore.Load(_path).StationId);
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        File.WriteAllText(_path, "{not valid json");
        var s = AppSettingsStore.Load(_path);
        Assert.Equal(1_000_000, s.BmpFrequencyHz);
        Assert.Equal(PowerMode.External, s.Power);
    }

    [Fact]
    public void Power_mode_serialises_as_snake_case_string()
    {
        AppSettingsStore.Save(new AppSettings { Power = PowerMode.Probe }, _path);
        var json = File.ReadAllText(_path);
        Assert.Contains("\"probe\"", json);
    }

    [Fact]
    public void Clone_returns_independent_copy()
    {
        var original = new AppSettings
        {
            CatalogPath = "a",
            BmpFrequencyHz = 500_000,
            BatchesEnabled = true,
        };
        var copy = original.Clone();
        copy.CatalogPath = "b";
        copy.BmpFrequencyHz = 9_000_000;
        copy.BatchesEnabled = false;
        Assert.Equal("a", original.CatalogPath);
        Assert.Equal(500_000, original.BmpFrequencyHz);
        Assert.True(original.BatchesEnabled);
        Assert.Equal(IskraLanguages.Ukrainian, original.LanguageCode);
    }

    [Theory]
    [InlineData("uk-UA", "uk")]
    [InlineData("EN_us", "en")]
    [InlineData("de-CH", "de")]
    [InlineData("unsupported", "uk")]
    public void Load_normalizes_language_without_discarding_other_settings(
        string persistedLanguage,
        string expectedLanguage)
    {
        AppSettingsStore.Save(new AppSettings
        {
            LanguageCode = persistedLanguage,
            StationId = "STATION-KEPT",
        }, _path);

        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal(expectedLanguage, loaded.LanguageCode);
        Assert.Equal("STATION-KEPT", loaded.StationId);
    }

    [Fact]
    public void Load_legacy_json_without_language_uses_ukrainian_and_keeps_other_fields()
    {
        File.WriteAllText(_path, """
            {
              "station_id": "LEGACY-STATION",
              "bmp_frequency_hz": 2000000
            }
            """);

        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal(IskraLanguages.Ukrainian, loaded.LanguageCode);
        Assert.Equal("LEGACY-STATION", loaded.StationId);
        Assert.Equal(2_000_000, loaded.BmpFrequencyHz);
    }

    // Sprint 5 fields ------------------------------------------------------

    [Fact]
    public void Log_shipping_defaults_are_enabled_with_5_minute_interval()
    {
        var s = AppSettingsStore.Load(_path);
        Assert.True(s.LogShippingEnabled);
        Assert.Equal(5, s.LogShipIntervalMinutes);
        Assert.Equal(AppSettings.DefaultLogShipperPrivateKeyPath, s.LogShipperPrivateKeyPath);
    }

    [Fact]
    public void Default_private_key_path_is_under_programdata_iskra()
    {
        var path = AppSettings.DefaultLogShipperPrivateKeyPath;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.StartsWith(programData, path);
        Assert.EndsWith("station-app.pem", path);
        Assert.Contains("Iskra", path);
    }

    [Fact]
    public void Round_trip_preserves_sprint5_fields()
    {
        var original = new AppSettings
        {
            LogShippingEnabled       = false,
            LogShipIntervalMinutes   = 30,
            LogShipperPrivateKeyPath = @"D:\keys\station.pem",
        };
        AppSettingsStore.Save(original, _path);
        var loaded = AppSettingsStore.Load(_path);
        Assert.False(loaded.LogShippingEnabled);
        Assert.Equal(30, loaded.LogShipIntervalMinutes);
        Assert.Equal(@"D:\keys\station.pem", loaded.LogShipperPrivateKeyPath);
    }

    [Fact]
    public void Clone_copies_sprint5_fields()
    {
        var original = new AppSettings
        {
            LogShippingEnabled       = false,
            LogShipIntervalMinutes   = 17,
            LogShipperPrivateKeyPath = @"X:\k.pem",
        };
        var copy = original.Clone();
        copy.LogShippingEnabled     = true;
        copy.LogShipIntervalMinutes = 1;
        Assert.False(original.LogShippingEnabled);
        Assert.Equal(17, original.LogShipIntervalMinutes);
        Assert.Equal(@"X:\k.pem", original.LogShipperPrivateKeyPath);
    }

    [Fact]
    public void Log_shipper_configuration_state_matches_ids_and_repo_is_locked()
    {
        // The App ID / installation ID may be blank in a fresh checkout or
        // populated after the owner provisions the GitHub App. The reported
        // configuration state must reflect the constants either way.
        var expectedConfigured =
            !string.IsNullOrWhiteSpace(GitHubAppConfig.LogShipperAppId) &&
            !string.IsNullOrWhiteSpace(GitHubAppConfig.LogShipperInstallationId);

        Assert.Equal(expectedConfigured, GitHubAppConfig.IsLogShipperConfigured);
        Assert.Equal("oleksandrmaslov", GitHubAppConfig.LogsRepoOwner);
        Assert.Equal("iskra-logs",       GitHubAppConfig.LogsRepoName);
    }
}
