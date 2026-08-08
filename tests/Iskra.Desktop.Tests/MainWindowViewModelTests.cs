using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop.Tests;

/// <summary>
/// Covers the Avalonia view model's gating, banner state, selection, and
/// settings behavior on the headless platform. These are the rules an operator
/// depends on at the bench, so they are asserted here rather than left to a
/// manual pass over the UI.
/// </summary>
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ============================================================
    // Flash gating
    // ============================================================

    [AvaloniaFact]
    public void Station_with_everything_present_is_ready_to_flash()
    {
        var vm = Build();

        Assert.True(vm.CanFlash);
        Assert.Equal(vm.Text.FlashReady, vm.BannerTitle);
        Assert.Equal(vm.Text.FlashReadyBatchOff, vm.BannerDetail);
    }

    [AvaloniaFact]
    public void Missing_probe_blocks_flashing_with_a_warning_banner()
    {
        var vm = Build(probes: []);

        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyProbe, vm.BannerTitle);
        Assert.Equal(vm.Text.ReadyProbeDetail, vm.BannerDetail);
        AssertBannerIs("#FFF2C14E", vm.BannerBackground);
    }

    [AvaloniaFact]
    public void Several_probes_block_flashing()
    {
        // Two probes are as unsafe as none: the app cannot know which board the
        // operator meant.
        var vm = Build(probes: [Probe("COM30"), Probe("COM31")]);

        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyProbe, vm.BannerTitle);
    }

    [AvaloniaFact]
    public void Missing_gdb_blocks_flashing()
    {
        var vm = Build(gdbPath: null);

        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyGdb, vm.BannerTitle);
    }

    [AvaloniaFact]
    public void Unusable_catalog_blocks_flashing()
    {
        var vm = Build(useCatalog: false);

        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyCatalog, vm.BannerTitle);
        Assert.Empty(vm.ProductOptions);
    }

    [AvaloniaFact]
    public void Empty_operator_blocks_flashing_without_raising_a_warning()
    {
        var vm = Build(lastOperator: "  ");

        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyOperator, vm.BannerTitle);
        // Not a station fault, so the band stays neutral rather than amber.
        AssertBannerIs("#FFE8E8E8", vm.BannerBackground);
    }

    [AvaloniaFact]
    public void Batch_mode_requires_a_batch_id_before_flashing()
    {
        var vm = Build(batchesEnabled: true);

        Assert.True(vm.BatchesEnabled);
        Assert.False(vm.CanFlash);
        Assert.Equal(vm.Text.ReadyBatch, vm.BannerTitle);

        vm.BatchId = "LOT-7";

        Assert.True(vm.CanFlash);
        Assert.Equal(vm.Text.FlashReadyBatchOn, vm.BannerDetail);
    }

    [AvaloniaFact]
    public void Clearing_the_operator_revokes_readiness()
    {
        var vm = Build();
        Assert.True(vm.CanFlash);

        vm.OperatorName = string.Empty;

        Assert.False(vm.CanFlash);
    }

    // ============================================================
    // Catalog selection
    // ============================================================

    [AvaloniaFact]
    public void Product_and_release_default_to_the_catalog_default_release()
    {
        var vm = Build();

        Assert.Equal("ci-clop", Assert.Single(vm.ProductOptions).ProductId);
        Assert.Equal(2, vm.ReleaseOptions.Count);
        Assert.Equal("1.0.0", vm.SelectedRelease!.Version);
    }

    [AvaloniaFact]
    public void Remote_releases_are_marked_in_the_version_list()
    {
        var vm = Build();

        var remote = vm.ReleaseOptions.Single(r => r.Version == "2.0.0");
        Assert.True(remote.IsRemote);
        Assert.Equal("v2.0.0 (GitHub)", remote.Label);
        Assert.Equal("v1.0.0", vm.ReleaseOptions.Single(r => r.Version == "1.0.0").Label);
    }

    [AvaloniaFact]
    public void Refreshing_readiness_keeps_the_operators_current_selection()
    {
        var vm = Build();
        vm.SelectedRelease = vm.ReleaseOptions.Single(r => r.Version == "2.0.0");

        vm.RefreshReadinessCommand.Execute(null);

        Assert.Equal("2.0.0", vm.SelectedRelease!.Version);
    }

    // ============================================================
    // Flash execution
    // ============================================================

    [AvaloniaFact]
    public async Task Passing_flash_shows_the_green_band_and_streams_gdb_output()
    {
        var vm = Build();

        await vm.FlashCommand.ExecuteAsync();
        // gdb lines are posted to the dispatcher rather than invoked inline, so
        // the real reader thread never blocks on the UI thread. Drain the queue
        // before asserting on the console contents.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(vm.Text.FlashPass, vm.BannerTitle);
        AssertBannerIs("#FF1B8A1B", vm.BannerBackground);
        Assert.Contains("Loading section .text", vm.GdbLogText);
        Assert.True(vm.IsIdle);
    }

    [AvaloniaFact]
    public async Task Firmware_that_cannot_fit_the_target_fails_before_gdb_runs()
    {
        // 64 KB image against the fixture's 32 KB part.
        var vm = Build(firmwareLength: 64 * 1024);

        await vm.FlashCommand.ExecuteAsync();

        Assert.StartsWith("✗ E_FW_TOO_LARGE", vm.BannerTitle);
        AssertBannerIs("#FFC0392B", vm.BannerBackground);
    }

    // ============================================================
    // Settings
    // ============================================================

    [AvaloniaFact]
    public void Editing_a_setting_marks_the_tab_dirty()
    {
        var vm = Build();
        Assert.False(vm.IsSettingsDirty);

        vm.StationIdInput = "bench-2";

        Assert.True(vm.IsSettingsDirty);
        Assert.Equal(vm.Text.SettingsUnsaved, vm.SettingsStatusText);
    }

    [AvaloniaFact]
    public void Saving_valid_settings_persists_them_and_clears_dirty_state()
    {
        var persistence = new FakeSettingsPersistence(BaseSettings());
        var vm = Build(persistence: persistence);
        vm.StationIdInput = "bench-2";
        vm.TimeoutInput = "42";

        vm.SaveSettingsCommand.Execute(null);

        Assert.False(vm.IsSettingsDirty);
        Assert.Equal(vm.Text.SettingsSaved, vm.SettingsStatusText);
        Assert.Equal("bench-2", persistence.Saved!.StationId);
        Assert.Equal(42, persistence.Saved.TimeoutSeconds);
    }

    [AvaloniaFact]
    public void Invalid_numeric_settings_are_refused_and_never_persisted()
    {
        var persistence = new FakeSettingsPersistence(BaseSettings());
        var vm = Build(persistence: persistence);
        vm.BmpFrequencyInput = "not-a-number";

        vm.SaveSettingsCommand.Execute(null);

        Assert.True(vm.IsSettingsDirty);
        Assert.Contains(vm.Text.SettingsSwdFrequency, vm.SettingsStatusText);
        Assert.Null(persistence.Saved);
    }

    [AvaloniaFact]
    public void Auto_save_on_leaving_the_tab_only_writes_when_dirty()
    {
        var persistence = new FakeSettingsPersistence(BaseSettings());
        var vm = Build(persistence: persistence);

        Assert.True(vm.SaveSettingsIfDirty());
        Assert.Null(persistence.Saved);

        vm.StationIdInput = "bench-3";
        Assert.True(vm.SaveSettingsIfDirty());
        Assert.Equal("bench-3", persistence.Saved!.StationId);
    }

    [AvaloniaFact]
    public void Enabling_batches_through_settings_updates_the_flash_tab()
    {
        var vm = Build();
        Assert.False(vm.BatchesEnabled);

        vm.BatchesEnabledInput = true;
        vm.SaveSettingsCommand.Execute(null);

        Assert.True(vm.BatchesEnabled);
        Assert.False(vm.CanFlash); // now needs a batch ID
    }

    [AvaloniaFact]
    public void Hotkey_selection_drives_the_button_subtitle()
    {
        var vm = Build();

        vm.SelectedHotkey = vm.HotkeyOptions.Single(o => o.Value == FlashHotkey.F5);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(FlashHotkey.F5, vm.FlashHotkey);
        Assert.Equal("F5", vm.HotkeyLabel);
        Assert.Contains("F5", vm.HotkeyHintText);
    }

    [AvaloniaFact]
    public void Disabled_hotkey_shows_no_subtitle()
    {
        var vm = Build();

        vm.SelectedHotkey = vm.HotkeyOptions.Single(o => o.Value == FlashHotkey.None);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(string.Empty, vm.HotkeyHintText);
    }

    // ============================================================
    // Localization
    // ============================================================

    [AvaloniaFact]
    public void Switching_language_retranslates_the_readiness_banner()
    {
        var vm = Build(probes: []);
        var ukrainian = vm.BannerTitle;

        vm.SelectedLanguage = DesktopLocalization.Languages.Single(l => l.Code == IskraLanguages.German);

        Assert.NotEqual(ukrainian, vm.BannerTitle);
        Assert.Equal(vm.Text.ReadyProbe, vm.BannerTitle);
        Assert.Equal(IskraLanguages.German, vm.Text.LanguageCode);
    }

    // ============================================================
    // Fixture
    // ============================================================

    private MainWindowViewModel Build(
        IReadOnlyList<ProbeInfo>? probes = null,
        string? gdbPath = "gdb.exe",
        Catalog? catalog = null,
        bool useCatalog = true,
        string? lastOperator = "operator-1",
        bool batchesEnabled = false,
        uint firmwareLength = 1024,
        FakeSettingsPersistence? persistence = null)
    {
        var directory = NewTempDirectory();
        var firmwarePath = Path.Combine(directory, "firmware.elf");
        File.WriteAllBytes(firmwarePath, MinimalElf32(0x08000000, firmwareLength));

        var effectiveCatalog = catalog ?? (useCatalog ? CatalogFor(firmwarePath) : null);
        var settings = BaseSettings();
        settings.DbPath = Path.Combine(directory, "attempts.db");
        settings.LastOperator = lastOperator;
        settings.BatchesEnabled = batchesEnabled;

        persistence ??= new FakeSettingsPersistence(settings);
        persistence.Current = settings;

        var session = new FakeCatalogSession(effectiveCatalog, directory);
        var readiness = new StationReadinessService(
            session,
            discoverProbes: () => probes ?? [Probe("COM30")],
            discoverGdb: _ => gdbPath,
            fileExists: _ => gdbPath is not null);

        return new MainWindowViewModel(
            new SettingsWorkflow(persistence),
            new HistoryWorkflow(),
            readiness,
            new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory()));
    }

    private static AppSettings BaseSettings() => new()
    {
        StationId = "station-1",
        TimeoutSeconds = 15,
        BmpFrequencyHz = 1_000_000,
        LogShipIntervalMinutes = 5,
        LanguageCode = IskraLanguages.Ukrainian,
    };

    private static ProbeInfo Probe(string port) =>
        new(port, "Black Magic GDB Server", $"USB\\{port}", ProbeInterface.Gdb, "BMP-001");

    private static Catalog CatalogFor(string firmwarePath) => new(
        1,
        DateTime.UnixEpoch,
        [new Product(
            "ci-clop",
            "CI-CLOP",
            new TargetDescriptor("PY32Fxxx", "PY32F002Ax5", 32),
            [
                new FirmwareRelease(
                    "1.0.0",
                    Path.GetFileName(firmwarePath),
                    Sha256(firmwarePath),
                    null,
                    DateTime.UnixEpoch,
                    null),
                new FirmwareRelease(
                    "2.0.0",
                    Path.GetFileName(firmwarePath),
                    Sha256(firmwarePath),
                    null,
                    DateTime.UnixEpoch,
                    null,
                    ElfSource: new GitHubReleaseRef("owner/ci-clop-firmware", "v2.0.0", "ci-clop.elf")),
            ],
            "1.0.0")]);

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static byte[] MinimalElf32(uint loadAddress, uint length)
    {
        const int headerSize = 52;
        const int entrySize = 32;
        var bytes = new byte[headerSize + entrySize];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = 1; bytes[5] = 1; bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), entrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), 1);
        var entry = bytes.AsSpan(headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], loadAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], loadAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);
        return bytes;
    }

    private static void AssertBannerIs(string expectedHex, IBrush actual)
    {
        var brush = Assert.IsType<SolidColorBrush>(actual);
        Assert.Equal(Color.Parse(expectedHex), brush.Color);
    }

    private string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"iskra-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);
        return dir;
    }

    private sealed class FakeSettingsPersistence(AppSettings current) : ISettingsPersistence
    {
        public AppSettings Current { get; set; } = current;
        public AppSettings? Saved { get; private set; }

        public AppSettings Load() => Current.Clone();

        public void Save(AppSettings settings)
        {
            Saved = settings;
            Current = settings;
        }
    }

    private sealed class FakeCatalogSession(Catalog? catalog, string directory) : ICatalogSession
    {
        public CatalogSessionResult Current => Load(new AppSettings());

        public CatalogSessionResult Load(AppSettings settings) => catalog is null
            ? new CatalogSessionResult(
                CatalogSessionStatus.NotFound, null, null, null, null, false, "no catalog in fixture")
            : new CatalogSessionResult(
                CatalogSessionStatus.Ready,
                catalog,
                Path.Combine(directory, "catalog.json"),
                directory,
                CatalogTrustResult.Verified,
                false,
                null);
    }

    private sealed class FakeGdbFactory : IGdbProcessFactory
    {
        public GdbProcess Create(string gdbPath) => new FakeGdbProcess();
    }

    /// <summary>
    /// Replays a verbatim successful BMP session so the workflow's real parser
    /// decides PASS, rather than the test asserting against a stubbed verdict.
    /// </summary>
    private sealed class FakeGdbProcess() : GdbProcess("fake-gdb")
    {
        public override Task<GdbRunResult> RunScanAsync(
            string comPort,
            PowerMode power,
            int frequencyHz,
            bool connectUnderReset,
            TimeSpan timeout,
            Action<GdbLine>? onLine = null,
            CancellationToken ct = default) =>
            Task.FromResult(Replay(onLine,
                "Target voltage: 3.3V",
                "Available Targets:",
                "No. Att Driver",
                " 1      PY32Fxxx M0+"));

        public override Task<GdbRunResult> RunFlashAsync(
            string comPort,
            PowerMode power,
            int frequencyHz,
            bool connectUnderReset,
            string elfPath,
            TimeSpan timeout,
            Action<GdbLine>? onLine = null,
            CancellationToken ct = default) =>
            Task.FromResult(Replay(onLine,
                "Available Targets:",
                "No. Att Driver",
                " 1      PY32Fxxx M0+",
                "Loading section .text, size 0x400 lma 0x8000000",
                "Section .text, range 0x8000000 -- 0x8000400: matched."));

        private static GdbRunResult Replay(Action<GdbLine>? onLine, params string[] lines)
        {
            var captured = lines
                .Select(text => new GdbLine(DateTime.UtcNow, GdbStream.Stdout, text))
                .ToArray();
            foreach (var line in captured) onLine?.Invoke(line);
            return new GdbRunResult(0, false, TimeSpan.FromMilliseconds(20), captured);
        }
    }
}
