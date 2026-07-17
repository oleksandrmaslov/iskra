using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class SettingsWorkflowTests
{
    [Fact]
    public void BuildCandidate_normalizes_and_maps_all_shared_policy_fields()
    {
        var current = new AppSettings();
        var draft = SettingsDraft.FromSettings(current) with
        {
            LanguageCode = "de-DE",
            CatalogPath = "  catalog.json  ",
            GdbPath = "  gdb.exe  ",
            BmpFrequencyHz = "2000000",
            Power = PowerMode.Probe,
            ConnectUnderReset = true,
            TimeoutSeconds = "23",
            DbPath = "  attempts.db  ",
            StationId = "  STATION-7  ",
            BatchesEnabled = true,
            LastOperator = "  Iryna  ",
            LastBatch = "  LOT-9  ",
            FlashHotkey = FlashHotkey.F5,
            LogShippingEnabled = false,
            LogShipIntervalMinutes = "17",
            LogShipperPrivateKeyPath = "  station.pem  ",
        };

        var result = new SettingsWorkflow(new MemoryPersistence(current))
            .BuildCandidate(current, draft);

        Assert.True(result.IsSaved);
        var saved = result.Settings!;
        Assert.Equal(IskraLanguages.German, saved.LanguageCode);
        Assert.Equal("catalog.json", saved.CatalogPath);
        Assert.Equal(CatalogTrust.OfficialCatalogSource.Owner, saved.CatalogOwner);
        Assert.Equal(CatalogTrust.OfficialCatalogSource.Repo, saved.CatalogRepo);
        Assert.Equal("gdb.exe", saved.GdbPath);
        Assert.Equal(2_000_000, saved.BmpFrequencyHz);
        Assert.Equal(PowerMode.Probe, saved.Power);
        Assert.True(saved.ConnectUnderReset);
        Assert.Equal(23, saved.TimeoutSeconds);
        Assert.Equal("attempts.db", saved.DbPath);
        Assert.Equal("STATION-7", saved.StationId);
        Assert.Equal("Iryna", saved.LastOperator);
        Assert.Equal("LOT-9", saved.LastBatch);
        Assert.Equal(FlashHotkey.F5, saved.FlashHotkey);
        Assert.False(saved.LogShippingEnabled);
        Assert.Equal(17, saved.LogShipIntervalMinutes);
        Assert.Equal("station.pem", saved.LogShipperPrivateKeyPath);
    }

    [Theory]
    [InlineData(SettingsField.BmpFrequencyHz)]
    [InlineData(SettingsField.TimeoutSeconds)]
    [InlineData(SettingsField.LogShipIntervalMinutes)]
    public void Positive_integer_fields_return_specific_validation_issue(SettingsField field)
    {
        var current = new AppSettings();
        var draft = SettingsDraft.FromSettings(current) with
        {
            BmpFrequencyHz = field == SettingsField.BmpFrequencyHz ? "0" : "1000000",
            TimeoutSeconds = field == SettingsField.TimeoutSeconds ? "bad" : "15",
            LogShipIntervalMinutes = field == SettingsField.LogShipIntervalMinutes ? "-1" : "5",
        };

        var result = new SettingsWorkflow().BuildCandidate(current, draft);

        Assert.Equal(SettingsSaveStatus.ValidationFailed, result.Status);
        Assert.Equal(field, result.InvalidField);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void Disabled_batches_clear_stale_last_batch_and_blank_values_use_defaults()
    {
        var current = new AppSettings { LastBatch = "OLD" };
        var draft = SettingsDraft.FromSettings(current) with
        {
            BatchesEnabled = false,
            LastBatch = "STALE",
            StationId = " ",
            LogShipperPrivateKeyPath = " ",
        };

        var result = new SettingsWorkflow().BuildCandidate(current, draft);

        Assert.Null(result.Settings!.LastBatch);
        Assert.Equal(Environment.MachineName, result.Settings.StationId);
        Assert.Equal(AppSettings.DefaultLogShipperPrivateKeyPath, result.Settings.LogShipperPrivateKeyPath);
    }

    [Fact]
    public void Persistence_failure_does_not_report_or_replace_candidate_as_saved()
    {
        var current = new AppSettings { StationId = "ORIGINAL" };
        var persistence = new ThrowingPersistence(current);
        var draft = SettingsDraft.FromSettings(current) with { StationId = "NEW" };

        var result = new SettingsWorkflow(persistence).Save(current, draft);

        Assert.Equal(SettingsSaveStatus.WriteFailed, result.Status);
        Assert.Null(result.Settings);
        Assert.Equal("ORIGINAL", persistence.Current.StationId);
        Assert.Contains("disk full", result.Diagnostic);
    }

    [Fact]
    public void Narrow_language_update_reloads_latest_settings_before_saving()
    {
        var persistence = new MemoryPersistence(new AppSettings
        {
            LanguageCode = IskraLanguages.Ukrainian,
            StationId = "NEW-WPF-STATION",
            BmpFrequencyHz = 2_000_000,
            LastOperator = "newer-wpf-value",
        });

        var result = new SettingsWorkflow(persistence).UpdateLanguage("en-US");

        Assert.True(result.IsSaved);
        Assert.Equal(IskraLanguages.English, persistence.Current.LanguageCode);
        Assert.Equal("NEW-WPF-STATION", persistence.Current.StationId);
        Assert.Equal(2_000_000, persistence.Current.BmpFrequencyHz);
        Assert.Equal("newer-wpf-value", persistence.Current.LastOperator);
    }

    [Theory]
    [InlineData(true, "LOT-42")]
    [InlineData(false, null)]
    public void Remember_operator_selection_respects_optional_batch_policy(
        bool batchesEnabled,
        string? expectedBatch)
    {
        var persistence = new MemoryPersistence(new AppSettings
        {
            BatchesEnabled = batchesEnabled,
            LastBatch = "OLD",
        });
        var current = persistence.Load();

        var result = new SettingsWorkflow(persistence)
            .RememberOperatorSelection(current, "  Oleksandr  ", "  LOT-42  ");

        Assert.True(result.IsSaved);
        Assert.Equal("Oleksandr", persistence.Current.LastOperator);
        Assert.Equal(expectedBatch, persistence.Current.LastBatch);
    }

    private sealed class MemoryPersistence(AppSettings current) : ISettingsPersistence
    {
        public AppSettings Current { get; private set; } = current.Clone();
        public AppSettings Load() => Current.Clone();
        public void Save(AppSettings settings) => Current = settings.Clone();
    }

    private sealed class ThrowingPersistence(AppSettings current) : ISettingsPersistence
    {
        public AppSettings Current { get; } = current.Clone();
        public AppSettings Load() => Current.Clone();
        public void Save(AppSettings settings) => throw new IOException("disk full");
    }
}
