using System.Security.Cryptography;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class FlashWorkflowTests
{
    private static readonly byte[] ValidElf =
    [
        0x7F, 0x45, 0x4C, 0x46,
        0x01, 0x01, 0x01, 0x00,
    ];

    [Fact]
    public async Task Missing_operator_is_blocked_before_firmware_or_gdb()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));

        var result = await workflow.ExecuteAsync(
            scope.Request(firmware, operatorName: "  "));

        Assert.Equal(FlashWorkflowStatus.Blocked, result.Status);
        Assert.Equal("E_OPERATOR_REQUIRED", result.Outcome.ErrorCode);
        Assert.False(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
        Assert.False(File.Exists(scope.DatabasePath));
    }

    [Fact]
    public async Task Valid_local_firmware_runs_two_phase_flash_and_logs_pass()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        var progress = new ProgressCollector();
        var request = scope.Request(firmware);
        var product = request.Catalog.Products.Single();
        request = request with
        {
            Catalog = request.Catalog with
            {
                Products =
                [
                    product with
                    {
                        Target = product.Target with
                        {
                            PowerMode = PowerMode.Probe,
                            FrequencyHz = 2_000_000,
                            ConnectReset = true,
                            TimeoutSeconds = 23,
                        },
                    },
                ],
            },
        };

        var result = await workflow.ExecuteAsync(
            request,
            progress);

        Assert.Equal(FlashWorkflowStatus.Passed, result.Status);
        Assert.True(result.IsPass);
        Assert.True(result.AttemptLogged);
        Assert.Equal(firmware, result.FirmwarePath);
        Assert.Equal(1, gdb.ScanCalls);
        Assert.Equal(1, gdb.FlashCalls);
        Assert.Equal(PowerMode.Probe, gdb.LastPower);
        Assert.Equal(2_000_000, gdb.LastFrequencyHz);
        Assert.True(gdb.LastConnectUnderReset);
        Assert.Equal(TimeSpan.FromSeconds(23), gdb.LastFlashTimeout);
        Assert.Contains(FlashWorkflowStage.ValidatingFirmware, progress.Stages);
        Assert.Contains(FlashWorkflowStage.Flashing, progress.Stages);

        using var store = new SqliteLogStore(scope.DatabasePath);
        var row = Assert.Single(store.QueryRecent());
        Assert.Equal("PASS", row.Result);
        Assert.Equal("PY32Fxxx M0+", row.TargetDetected);
        Assert.Equal("ci-clop", row.ProductId);
    }

    [Fact]
    public async Task Hash_mismatch_is_logged_and_never_starts_gdb()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        var request = scope.Request(firmware) with
        {
            Catalog = scope.CatalogFor(firmware, expectedHash: new string('0', 64)),
        };

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(FlashWorkflowStatus.Failed, result.Status);
        Assert.Equal("E_FW_HASH_MISMATCH", result.Outcome.ErrorCode);
        Assert.True(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
        Assert.Equal(0, gdb.FlashCalls);

        using var store = new SqliteLogStore(scope.DatabasePath);
        var row = Assert.Single(store.QueryRecent());
        Assert.Equal("FAIL", row.Result);
        Assert.Equal("E_FW_HASH_MISMATCH", row.ErrorCode);
    }

    [Fact]
    public async Task Conflicting_batch_is_refused_before_firmware_validation_or_gdb()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        var request = scope.Request(firmware, batchesEnabled: true, batch: "LOT-17");

        using (var store = new SqliteLogStore(scope.DatabasePath))
        {
            store.ReserveBatchLock("LOT-17", new BatchLockDescriptor(
                "other-product",
                "9.9.9",
                new string('a', 64),
                "STM32F4",
                512));
        }

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(FlashWorkflowStatus.Failed, result.Status);
        Assert.Equal("E_BATCH_LOCKED", result.Outcome.ErrorCode);
        Assert.True(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
        Assert.Equal(0, gdb.FlashCalls);

        using var log = new SqliteLogStore(scope.DatabasePath);
        var row = Assert.Single(log.QueryRecent());
        Assert.Equal("E_BATCH_LOCKED", row.ErrorCode);
    }

    [Fact]
    public async Task Revoked_release_is_logged_but_does_not_reserve_the_batch()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var request = scope.Request(firmware, batchesEnabled: true, batch: "LOT-REVOKED");
        request = request with
        {
            Catalog = request.Catalog with
            {
                Revoked = [new RevokedRelease("ci-clop", "1.0.0", "safety recall")],
            },
        };
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal("E_RELEASE_REVOKED", result.Outcome.ErrorCode);
        Assert.True(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
        using var store = new SqliteLogStore(scope.DatabasePath);
        Assert.Null(store.GetBatchLock("LOT-REVOKED"));
        Assert.Equal("E_RELEASE_REVOKED", Assert.Single(store.QueryRecent()).ErrorCode);
    }

    [Fact]
    public async Task Remote_auth_failure_is_normalized_and_logged_by_shared_workflow()
    {
        using var scope = new TempScope();
        var remoteRelease = scope.ReleaseFor("remote.elf", new string('a', 64)) with
        {
            ElfSource = new GitHubReleaseRef("owner/repo", "v1.0.0", "remote.elf"),
        };
        var catalog = scope.CatalogWith(remoteRelease);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(
            new ThrowingRemoteProvider(new NotSignedInException()),
            new FakeGdbFactory(gdb));
        var request = scope.Request(scope.WriteFirmware(ValidElf, "unused.elf")) with
        {
            Catalog = catalog,
        };

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal("E_NOT_SIGNED_IN", result.Outcome.ErrorCode);
        Assert.True(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"iskra-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "attempts.db");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }

        public string WriteFirmware(byte[] bytes, string filename = "firmware.elf")
        {
            var path = Path.Combine(DirectoryPath, filename);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public FlashWorkflowRequest Request(
            string firmwarePath,
            string operatorName = "operator-1",
            bool batchesEnabled = false,
            string? batch = null)
        {
            var settings = new AppSettings
            {
                DbPath = DatabasePath,
                StationId = "station-1",
                BatchesEnabled = batchesEnabled,
                TimeoutSeconds = 15,
            };
            return new FlashWorkflowRequest(
                CatalogFor(firmwarePath),
                DirectoryPath,
                "ci-clop",
                "1.0.0",
                settings,
                operatorName,
                batch,
                "fake-gdb",
                "COM30",
                "BMP-001");
        }

        public Catalog CatalogFor(string firmwarePath, string? expectedHash = null) =>
            CatalogWith(ReleaseFor(firmwarePath, expectedHash));

        public Catalog CatalogWith(FirmwareRelease release) => new(
            1,
            DateTime.UnixEpoch,
            [new Product(
                "ci-clop",
                "CI-CLOP",
                new TargetDescriptor("PY32Fxxx", "PY32F002Ax5", 32),
                [release],
                release.Version)]);

        public FirmwareRelease ReleaseFor(string firmwarePath, string? expectedHash = null) => new(
            "1.0.0",
            Path.GetFileName(firmwarePath),
            expectedHash ?? Sha256(firmwarePath),
            null,
            DateTime.UnixEpoch,
            null);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private sealed class FakeGdbFactory(FakeGdbProcess process) : IGdbProcessFactory
    {
        public GdbProcess Create(string gdbPath) => process;
    }

    private sealed class FakeGdbProcess : GdbProcess
    {
        public FakeGdbProcess() : base("fake-gdb") { }

        public int ScanCalls { get; private set; }
        public int FlashCalls { get; private set; }
        public PowerMode LastPower { get; private set; }
        public int LastFrequencyHz { get; private set; }
        public bool LastConnectUnderReset { get; private set; }
        public TimeSpan LastFlashTimeout { get; private set; }

        public override Task<GdbRunResult> RunScanAsync(
            string comPort,
            PowerMode power,
            int frequencyHz,
            bool connectUnderReset,
            TimeSpan timeout,
            Action<GdbLine>? onLine = null,
            CancellationToken ct = default)
        {
            ScanCalls++;
            LastPower = power;
            LastFrequencyHz = frequencyHz;
            LastConnectUnderReset = connectUnderReset;
            return Task.FromResult(Result(
                "Available Targets:",
                "No. Att Driver",
                " 1      PY32Fxxx M0+"));
        }

        public override Task<GdbRunResult> RunFlashAsync(
            string comPort,
            PowerMode power,
            int frequencyHz,
            bool connectUnderReset,
            string elfPath,
            TimeSpan timeout,
            Action<GdbLine>? onLine = null,
            CancellationToken ct = default)
        {
            FlashCalls++;
            LastPower = power;
            LastFrequencyHz = frequencyHz;
            LastConnectUnderReset = connectUnderReset;
            LastFlashTimeout = timeout;
            var result = Result(
                "Available Targets:",
                "No. Att Driver",
                " 1      PY32Fxxx M0+",
                "Loading section .text, size 0x8 lma 0x8000000",
                "Section .text, range 0x8000000 -- 0x8000008: matched.");
            foreach (var line in result.Output) onLine?.Invoke(line);
            return Task.FromResult(result);
        }

        private static GdbRunResult Result(params string[] lines) => new(
            0,
            false,
            TimeSpan.FromMilliseconds(20),
            lines.Select(text => new GdbLine(DateTime.UtcNow, GdbStream.Stdout, text)).ToArray());
    }

    private sealed class ProgressCollector : IProgress<FlashWorkflowProgress>
    {
        public List<FlashWorkflowStage> Stages { get; } = [];
        public void Report(FlashWorkflowProgress value) => Stages.Add(value.Stage);
    }

    private sealed class ThrowingRemoteProvider(Exception exception) : IRemoteFirmwareProvider
    {
        public Task<string> AcquireAsync(
            FirmwareRelease release,
            CancellationToken cancellationToken) => Task.FromException<string>(exception);
    }
}
