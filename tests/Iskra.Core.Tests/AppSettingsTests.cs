using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

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
        Assert.False(s.RequireSignedCatalog);
        Assert.False(s.ConnectUnderReset);
        Assert.Equal(15, s.TimeoutSeconds);
        Assert.Equal(Environment.MachineName, s.StationId);
        Assert.Null(s.CatalogPath);
        Assert.Null(s.GdbPath);
    }

    [Fact]
    public void Round_trip_preserves_all_fields()
    {
        var original = new AppSettings
        {
            CatalogPath           = @"D:\fw\catalog.json",
            RequireSignedCatalog  = true,
            GdbPath               = @"C:\arm\bin\arm-none-eabi-gdb.exe",
            BmpFrequencyHz        = 2_000_000,
            Power                 = PowerMode.Probe,
            ConnectUnderReset     = true,
            TimeoutSeconds        = 25,
            DbPath                = @"D:\logs\flash.db",
            StationId             = "BENCH-7",
            LastOperator          = "Iryna",
            LastBatch             = "B-2026-099",
        };
        AppSettingsStore.Save(original, _path);
        var loaded = AppSettingsStore.Load(_path);

        Assert.Equal(original.CatalogPath,          loaded.CatalogPath);
        Assert.Equal(original.RequireSignedCatalog, loaded.RequireSignedCatalog);
        Assert.Equal(original.GdbPath,              loaded.GdbPath);
        Assert.Equal(original.BmpFrequencyHz,       loaded.BmpFrequencyHz);
        Assert.Equal(original.Power,                loaded.Power);
        Assert.Equal(original.ConnectUnderReset,    loaded.ConnectUnderReset);
        Assert.Equal(original.TimeoutSeconds,       loaded.TimeoutSeconds);
        Assert.Equal(original.DbPath,               loaded.DbPath);
        Assert.Equal(original.StationId,            loaded.StationId);
        Assert.Equal(original.LastOperator,         loaded.LastOperator);
        Assert.Equal(original.LastBatch,            loaded.LastBatch);
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
        var original = new AppSettings { CatalogPath = "a", BmpFrequencyHz = 500_000 };
        var copy = original.Clone();
        copy.CatalogPath = "b";
        copy.BmpFrequencyHz = 9_000_000;
        Assert.Equal("a", original.CatalogPath);
        Assert.Equal(500_000, original.BmpFrequencyHz);
    }
}
