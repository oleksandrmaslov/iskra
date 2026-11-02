using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Iskra.Application;
using Iskra.Core;
using Microsoft.Data.Sqlite;

namespace Iskra.Application.Tests;

public sealed class FlashWorkflowTests
{
    [Fact]
    public void Production_workflow_exposes_no_gdb_factory_injection_constructor()
    {
        var constructor = Assert.Single(typeof(FlashWorkflow).GetConstructors());
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("GdbProcessFactory", StringComparison.Ordinal));
    }

    // A real minimal ELF32 with one PT_LOAD segment. The workflow now validates
    // the image's load map against the catalog target, so a magic-bytes-only
    // stub is no longer accepted as valid firmware.
    private static readonly byte[] ValidElf = MinimalElf32(loadAddress: 0x08000000, length: 256);

    private static byte[] MinimalElf32(uint loadAddress, uint length)
    {
        const int headerSize = 52;
        const int programEntrySize = 32;
        const int sectionEntrySize = 40;
        var names = Encoding.ASCII.GetBytes("\0.shstrtab\0.text\0");
        const uint shstrtabName = 1;
        const uint textName = 11;
        var namesOffset = headerSize + programEntrySize;
        var dataOffset = (namesOffset + names.Length + 3) & ~3;
        var sectionOffset = (checked(dataOffset + (int)length) + 3) & ~3;
        var bytes = new byte[checked(sectionOffset + sectionEntrySize * 3)];

        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = 1; // ELF32
        bytes[5] = 1; // little endian
        bytes[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 2);           // e_type = ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 40);          // e_machine = ARM
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), headerSize);  // e_phoff
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), (uint)sectionOffset); // e_shoff
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), programEntrySize); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), 1);           // e_phnum
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(46), sectionEntrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(50), 1);

        var entry = bytes.AsSpan(headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 1);                      // p_type = PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)dataOffset);  // p_offset
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], loadAddress);       // p_vaddr
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], loadAddress);      // p_paddr
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], length);           // p_filesz
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);           // p_memsz
        names.CopyTo(bytes, namesOffset);

        var namesSection = bytes.AsSpan(sectionOffset + sectionEntrySize, sectionEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(namesSection, shstrtabName);
        BinaryPrimitives.WriteUInt32LittleEndian(namesSection[4..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(namesSection[16..], (uint)namesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(namesSection[20..], (uint)names.Length);
        var textSection = bytes.AsSpan(sectionOffset + sectionEntrySize * 2, sectionEntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection, textName);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[8..], 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[12..], loadAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[16..], (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[20..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(textSection[32..], 4);
        return bytes;
    }

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
    public async Task Invalid_mutable_settings_are_blocked_at_workflow_boundary()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        var request = scope.Request(firmware);
        request.Settings.BmpFrequencyHz = FlashOptions.MaxBmpFrequencyHz + 1;

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(FlashWorkflowStatus.Blocked, result.Status);
        Assert.Equal("E_SETTINGS_INVALID", result.Outcome.ErrorCode);
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
            CatalogPermit = CatalogActivationPermit.ForTests(request.Catalog with
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
            }),
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
        Assert.NotEqual(firmware, gdb.LastFirmwarePath);
        Assert.Equal(ValidElf, gdb.LastFirmwareBytes);
        Assert.False(File.Exists(gdb.LastFirmwarePath));
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
            CatalogPermit = CatalogActivationPermit.ForTests(
                scope.CatalogFor(firmware, expectedHash: new string('0', 64))),
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
    public async Task Oversized_firmware_is_logged_and_never_starts_gdb()
    {
        using var scope = new TempScope();
        // 64 KB aimed at the fixture's 32 KB part. The hash is correct, so only
        // the range check stands between this and a wrong-part flash.
        var firmware = scope.WriteFirmware(MinimalElf32(0x08000000, 64 * 1024));
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));

        var result = await workflow.ExecuteAsync(scope.Request(firmware));

        Assert.Equal(FlashWorkflowStatus.Failed, result.Status);
        Assert.Equal("E_FW_TOO_LARGE", result.Outcome.ErrorCode);
        Assert.True(result.AttemptLogged);
        Assert.Equal(0, gdb.ScanCalls);
        Assert.Equal(0, gdb.FlashCalls);

        using var store = new SqliteLogStore(scope.DatabasePath);
        Assert.Equal("E_FW_TOO_LARGE", Assert.Single(store.QueryRecent()).ErrorCode);
    }

    [Fact]
    public async Task Firmware_outside_the_declared_memory_map_is_logged_and_never_starts_gdb()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(MinimalElf32(0x08000000, 1024));
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));

        // Same family per bmp_match, but this part's flash lives at 0x00000000.
        var request = scope.Request(firmware);
        var product = request.Catalog.Products.Single();
        request = request with
        {
            CatalogPermit = CatalogActivationPermit.ForTests(request.Catalog with
            {
                Products = [product with { Target = product.Target with { FlashOrigin = 0x00000000 } }],
            }),
        };

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(FlashWorkflowStatus.Failed, result.Status);
        Assert.Equal("E_FW_ADDRESS_RANGE", result.Outcome.ErrorCode);
        Assert.Equal(0, gdb.ScanCalls);
        Assert.Equal(0, gdb.FlashCalls);
    }

    [Fact]
    public async Task Declared_memory_map_that_matches_the_image_still_flashes()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(MinimalElf32(0x08000000, 1024));
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));

        var request = scope.Request(firmware);
        var product = request.Catalog.Products.Single();
        request = request with
        {
            CatalogPermit = CatalogActivationPermit.ForTests(request.Catalog with
            {
                Products = [product with { Target = product.Target with { FlashOrigin = 0x08000000 } }],
            }),
        };

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(FlashWorkflowStatus.Passed, result.Status);
        Assert.Equal(1, gdb.FlashCalls);
    }

    [Fact]
    public async Task Verified_flash_is_reported_failed_when_durable_audit_write_fails()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        var request = scope.Request(firmware);
        request.Settings.DbPath = Path.Combine(scope.DirectoryPath, "audit.db");
        gdb.BeforeReturn = () =>
        {
            SqliteConnection.ClearAllPools();
            File.Delete(request.Settings.DbPath);
            Directory.CreateDirectory(request.Settings.DbPath);
        };

        var result = await workflow.ExecuteAsync(request);

        Assert.Equal(1, gdb.FlashCalls);
        Assert.Equal(FlashWorkflowStatus.Failed, result.Status);
        Assert.False(result.IsPass);
        Assert.False(result.AttemptLogged);
        Assert.Equal("E_AUDIT_WRITE_FAILED", result.Outcome.ErrorCode);
        Assert.Contains("attempt ended with PASS", result.Outcome.ErrorMessage);
    }

    [Fact]
    public async Task Started_row_exists_before_gdb_and_same_row_becomes_terminal()
    {
        using var scope = new TempScope();
        var firmware = scope.WriteFirmware(ValidElf);
        var gdb = new FakeGdbProcess();
        var workflow = new FlashWorkflow(gdbProcessFactory: new FakeGdbFactory(gdb));
        long startedId = 0;
        gdb.BeforeScan = () =>
        {
            using var store = new SqliteLogStore(scope.DatabasePath);
            var row = Assert.Single(store.QueryStartedAttempts());
            startedId = row.Id;
            Assert.Equal(FlashAttemptStates.Started, row.AttemptState);
            Assert.Empty(store.GetUnsynced());
        };

        var result = await workflow.ExecuteAsync(scope.Request(firmware));

        Assert.True(result.IsPass);
        using var reopened = new SqliteLogStore(scope.DatabasePath);
        var terminal = Assert.Single(reopened.QueryRecent());
        Assert.Equal(startedId, terminal.Id);
        Assert.Equal(FlashAttemptStates.Terminal, terminal.AttemptState);
        Assert.Empty(reopened.QueryStartedAttempts());
    }

    [Fact]
    public async Task Cancellation_after_start_is_persisted_before_it_is_rethrown()
    {
        using var scope = new TempScope();
        var remote = scope.ReleaseFor("remote.elf", new string('a', 64)) with
        {
            ElfSource = new GitHubReleaseRef("owner/repo", "v1.0.0", "remote.elf"),
        };
        var request = scope.Request(scope.WriteFirmware(ValidElf, "unused.elf")) with
        {
            CatalogPermit = CatalogActivationPermit.ForTests(scope.CatalogWith(remote)),
        };
        using var cancellation = new CancellationTokenSource();
        var workflow = new FlashWorkflow(
            new WaitingRemoteProvider(),
            new FakeGdbFactory(new FakeGdbProcess()));

        var running = workflow.ExecuteAsync(request, cancellationToken: cancellation.Token);
        await WaitingRemoteProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        using var store = new SqliteLogStore(scope.DatabasePath);
        var row = Assert.Single(store.QueryRecent());
        Assert.Equal(FlashAttemptStates.Terminal, row.AttemptState);
        Assert.Equal("FAIL", row.Result);
        Assert.Equal("E_CANCELLED", row.ErrorCode);
        Assert.Empty(store.QueryStartedAttempts());
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
            CatalogPermit = CatalogActivationPermit.ForTests(request.Catalog with
            {
                Revoked = [new RevokedRelease("ci-clop", "1.0.0", "safety recall")],
            }),
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
            CatalogPermit = CatalogActivationPermit.ForTests(catalog),
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
            var catalog = CatalogFor(firmwarePath);
            return new FlashWorkflowRequest(
                CatalogActivationPermit.ForTests(catalog),
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
        public string? LastFirmwarePath { get; private set; }
        public byte[]? LastFirmwareBytes { get; private set; }
        public Action? BeforeReturn { get; set; }
        public Action? BeforeScan { get; set; }

        public override Task<GdbGuardedRunResult> RunGuardedAsync(
            string endpoint,
            PowerMode power,
            int frequencyHz,
            bool connectUnderReset,
            string firmwarePath,
            TimeSpan scanTimeout,
            TimeSpan flashTimeout,
            Func<GdbRunResult, bool> scanGate,
            Action<GdbLine>? onLine = null,
            CancellationToken ct = default,
            string? probeLockIdentity = null)
        {
            BeforeScan?.Invoke();
            ScanCalls++;
            LastPower = power;
            LastFrequencyHz = frequencyHz;
            LastConnectUnderReset = connectUnderReset;
            var scan = Result(
                "Available Targets:",
                "No. Att Driver",
                " 1      PY32Fxxx M0+");
            foreach (var line in scan.Output) onLine?.Invoke(line);
            if (!scanGate(scan))
                return Task.FromResult(new GdbGuardedRunResult(scan, null));

            FlashCalls++;
            LastFlashTimeout = flashTimeout;
            LastFirmwarePath = firmwarePath;
            LastFirmwareBytes = File.ReadAllBytes(firmwarePath);
            var image = FirmwareImage.Read(firmwarePath, FirmwareKind.Elf);
            var output = new List<string>();
            foreach (var section in image.LoadSections)
            {
                output.Add($"Loading section {section.Name}, size 0x{section.Length:x} lma 0x{section.Address:x}");
                output.Add($"Section {section.Name}, range 0x{section.Address:x} -- 0x{section.Address + section.Length:x}: matched.");
            }
            var flash = Result(output.ToArray());
            foreach (var line in flash.Output) onLine?.Invoke(line);
            BeforeReturn?.Invoke();
            return Task.FromResult(new GdbGuardedRunResult(scan, flash));
        }

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

    private sealed class WaitingRemoteProvider : IRemoteFirmwareProvider
    {
        public static TaskCompletionSource Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> AcquireAsync(
            FirmwareRelease release,
            CancellationToken cancellationToken)
        {
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
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
