using Iskra.Core;

namespace Iskra.Core.Tests;

public class FlashStateMachineTests
{
    private static GdbRunResult Run(IEnumerable<string> stdout, int exitCode = 0, bool timedOut = false,
        TimeSpan? duration = null)
    {
        var lines = stdout
            .Select(t => new GdbLine(DateTime.UtcNow, GdbStream.Stdout, t))
            .ToList();
        return new GdbRunResult(
            ExitCode: exitCode,
            TimedOut: timedOut,
            Duration: duration ?? TimeSpan.FromMilliseconds(800),
            Output: lines);
    }

    private static readonly string[] HappyPath = new[]
    {
        "Remote debugging using \\\\.\\COM30",
        "Available Targets:",
        "No. Att Driver",
        " 1      PY32F002A M0+",
        "",
        "Attaching to program: test.elf, Remote target",
        "Loading section .text, size 0x1234 lma 0x8000000",
        "Loading section .data, size 0x100 lma 0x8001234",
        "Start address 0x8000000, load size 4660",
        "Transfer rate: 12345 KB/sec, 1234 bytes/write.",
        "Section .text, range 0x8000000 -- 0x8001234: matched.",
        "Section .data, range 0x8001234 -- 0x8001334: matched.",
    };

    [Fact]
    public void Pass_when_target_matches_and_all_sections_verify()
    {
        var outcome = FlashStateMachine.Classify(Run(HappyPath), "PY32F002A");
        Assert.True(outcome.IsPass);
        Assert.Null(outcome.ErrorCode);
        Assert.Equal("PY32F002A M0+", outcome.DetectedTarget);
    }

    [Fact]
    public void Pass_when_expected_target_match_omitted()
    {
        var outcome = FlashStateMachine.Classify(Run(HappyPath), "");
        Assert.True(outcome.IsPass);
    }

    [Fact]
    public void Timeout_overrides_everything()
    {
        var outcome = FlashStateMachine.Classify(
            Run(HappyPath, exitCode: -1, timedOut: true, duration: TimeSpan.FromSeconds(15)),
            "PY32F002A");
        Assert.False(outcome.IsPass);
        Assert.Equal("E_TIMEOUT", outcome.ErrorCode);
    }

    [Fact]
    public void Empty_target_list_yields_scan_no_target()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            "",
        }), "PY32F002A");
        Assert.Equal("E_SCAN_NO_TARGET", outcome.ErrorCode);
        Assert.Null(outcome.DetectedTarget);
    }

    [Fact]
    public void Wrong_target_yields_target_mismatch_with_detected_string()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      STM32F103",
            "",
        }), expectedBmpMatch: "PY32F002A");
        Assert.Equal("E_TARGET_MISMATCH", outcome.ErrorCode);
        Assert.Equal("STM32F103", outcome.DetectedTarget);
        Assert.Contains("PY32F002A", outcome.ErrorMessage);
        Assert.Contains("STM32F103", outcome.ErrorMessage);
    }

    [Fact]
    public void Multiple_targets_are_rejected_even_when_one_matches()
    {
        var outcome = FlashStateMachine.ClassifyScan(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      STM32F103",
            " 2      PY32Fxxx M0+",
            "",
        }), "PY32Fxxx");

        Assert.NotNull(outcome);
        Assert.Equal("E_MULTIPLE_TARGETS", outcome!.ErrorCode);
    }

    [Fact]
    public void Usb_error_yields_probe_not_found()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "libusb_open failed",
        }, exitCode: 1), "PY32F002A");
        Assert.Equal("E_PROBE_NOT_FOUND", outcome.ErrorCode);
    }

    [Fact]
    public void Resource_busy_yields_probe_busy()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "Access is denied.",
        }, exitCode: 1), "PY32F002A");
        Assert.Equal("E_PROBE_BUSY", outcome.ErrorCode);
    }

    [Fact]
    public void Attach_failure_after_valid_scan_yields_attach_failed()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      PY32F002A M0+",
            "",
            "attach: Cannot find target #1",
        }, exitCode: 1), "PY32F002A");
        Assert.Equal("E_ATTACH_FAILED", outcome.ErrorCode);
        Assert.Equal("PY32F002A M0+", outcome.DetectedTarget);
    }

    [Fact]
    public void Section_mismatch_yields_verify_mismatch()
    {
        var lines = HappyPath
            .Select(l => l.Contains(".text") && l.Contains(": matched.")
                ? l.Replace(": matched.", ": MIS-MATCHED!")
                : l);
        var outcome = FlashStateMachine.Classify(Run(lines, exitCode: 0), "PY32F002A");
        Assert.Equal("E_VERIFY_MISMATCH", outcome.ErrorCode);
        Assert.Contains(".text", outcome.ErrorMessage);
    }

    [Fact]
    public void Missing_load_or_verify_yields_load_failed()
    {
        var outcome = FlashStateMachine.Classify(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      PY32F002A M0+",
            "",
            "Attaching to program: test.elf, Remote target",
            // no Loading section / no compare-sections
        }, exitCode: 1), "PY32F002A");
        Assert.Equal("E_LOAD_FAILED", outcome.ErrorCode);
    }

    [Fact]
    public void Every_loaded_section_must_have_a_matching_verify_result()
    {
        var lines = HappyPath.Where(l =>
            !l.StartsWith("Section .data,", StringComparison.Ordinal));

        var outcome = FlashStateMachine.Classify(Run(lines), "PY32F002A");

        Assert.Equal("E_VERIFY_MISMATCH", outcome.ErrorCode);
        Assert.Contains(".data", outcome.ErrorMessage);
    }

    [Fact]
    public void Happy_path_but_nonzero_exit_yields_gdb_crashed()
    {
        var outcome = FlashStateMachine.Classify(Run(HappyPath, exitCode: 1), "PY32F002A");
        Assert.Equal("E_GDB_CRASHED", outcome.ErrorCode);
        Assert.Equal("PY32F002A M0+", outcome.DetectedTarget);
    }

    /// <summary>
    /// Verbatim gdb output captured from the lab BMP flashing app.elf onto a
    /// PY32F002Ax5 board on 2026-05-25. Pins down what real BMP output looks
    /// like so the parser regexes can't silently regress. Note BMP reports the
    /// PY32 family generically as "PY32Fxxx M0+", not the specific part number —
    /// the catalog bmp_match for PY32 products must be "PY32Fxxx".
    /// </summary>
    private static readonly string[] RealBmpPy32Output = new[]
    {
        "C:\\Program Files (x86)\\GNU Arm Embedded Toolchain\\10 2021.10\\bin\\arm-none-eabi-gdb.exe: warning: Couldn't determine a path for the index cache directory.",
        "Debug iface frequency set to 1384615Hz",
        "Target voltage: 3.9V",
        "Available Targets:",
        "No. Att Driver",
        " 1      PY32Fxxx M0+",
        "HAL_InitTick (TickPriority=<optimized out>) at Libraries/PY32F0xx_HAL_Driver/Src/py32f0xx_hal.c:266",
        "266        return status;",
        "Loading section .isr_vector, size 0xc0 lma 0x8000000",
        "Loading section .text, size 0x2e74 lma 0x80000c0",
        "Loading section .rodata, size 0x144 lma 0x8002f34",
        "Loading section .init_array, size 0x4 lma 0x8003078",
        "Loading section .fini_array, size 0x4 lma 0x800307c",
        "Loading section .data, size 0x44 lma 0x8003080",
        "Start address 0x08002ec8, load size 12484",
        "Transfer rate: 8 KB/sec, 693 bytes/write.",
        "Section .isr_vector, range 0x8000000 -- 0x80000c0: matched.",
        "Section .text, range 0x80000c0 -- 0x8002f34: matched.",
        "Section .rodata, range 0x8002f34 -- 0x8003078: matched.",
        "Section .init_array, range 0x8003078 -- 0x800307c: matched.",
        "Section .fini_array, range 0x800307c -- 0x8003080: matched.",
        "Section .data, range 0x8003080 -- 0x80030c4: matched.",
        "[Inferior 1 (Remote target) killed]",
    };

    [Fact]
    public void Real_bmp_output_passes_classification()
    {
        var outcome = FlashStateMachine.Classify(Run(RealBmpPy32Output), "PY32Fxxx");
        Assert.True(outcome.IsPass, $"expected PASS, got {outcome.ErrorCode}: {outcome.ErrorMessage}");
        Assert.Equal("PY32Fxxx M0+", outcome.DetectedTarget);
    }

    [Fact]
    public void Real_bmp_output_fails_with_too_specific_target()
    {
        // Documents the trap that caused our first HIL run to false-fail:
        // BMP reports family granularity only, so part-number expectations mismatch.
        var outcome = FlashStateMachine.Classify(Run(RealBmpPy32Output), "PY32F002A");
        Assert.Equal("E_TARGET_MISMATCH", outcome.ErrorCode);
        Assert.Equal("PY32Fxxx M0+", outcome.DetectedTarget);
    }

    [Fact]
    public void Real_bmp_output_detects_all_six_loaded_sections()
    {
        var events = GdbOutputParser.Parse(Run(RealBmpPy32Output).Output);
        Assert.Equal(6, events.Count(e => e.Kind == GdbEventKind.LoadingSection));
        Assert.Equal(6, events.Count(e => e.Kind == GdbEventKind.SectionMatched));
    }

    // Scan-phase classification: ClassifyScan returns null on a clean scan and a
    // FAIL outcome on any condition that should abort before flash.

    private static readonly string[] ScanOnlyHappy = new[]
    {
        "Remote debugging using \\\\.\\COM30",
        "Available Targets:",
        "No. Att Driver",
        " 1      PY32Fxxx M0+",
        "",
    };

    [Fact]
    public void ClassifyScan_returns_null_on_matching_target()
    {
        var outcome = FlashStateMachine.ClassifyScan(Run(ScanOnlyHappy), "PY32Fxxx");
        Assert.Null(outcome);
    }

    [Fact]
    public void ClassifyScan_returns_null_when_expected_match_empty()
    {
        var outcome = FlashStateMachine.ClassifyScan(Run(ScanOnlyHappy), "");
        Assert.Null(outcome);
    }

    [Fact]
    public void ClassifyScan_fails_on_target_mismatch_before_any_flash_write()
    {
        var outcome = FlashStateMachine.ClassifyScan(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      STM32F103",
            "",
        }), "PY32Fxxx");
        Assert.NotNull(outcome);
        Assert.Equal("E_TARGET_MISMATCH", outcome!.ErrorCode);
        Assert.Equal("STM32F103", outcome.DetectedTarget);
    }

    [Fact]
    public void ClassifyScan_fails_on_empty_target_list()
    {
        var outcome = FlashStateMachine.ClassifyScan(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            "",
        }), "PY32Fxxx");
        Assert.NotNull(outcome);
        Assert.Equal("E_SCAN_NO_TARGET", outcome!.ErrorCode);
    }

    [Fact]
    public void ClassifyScan_fails_on_usb_error()
    {
        var outcome = FlashStateMachine.ClassifyScan(
            Run(new[] { "libusb_open failed" }, exitCode: 1), "PY32Fxxx");
        Assert.NotNull(outcome);
        Assert.Equal("E_PROBE_NOT_FOUND", outcome!.ErrorCode);
    }

    [Fact]
    public void ClassifyScan_fails_on_probe_busy()
    {
        var outcome = FlashStateMachine.ClassifyScan(
            Run(new[] { "Access is denied." }, exitCode: 1), "PY32Fxxx");
        Assert.NotNull(outcome);
        Assert.Equal("E_PROBE_BUSY", outcome!.ErrorCode);
    }

    [Fact]
    public void ClassifyScan_fails_on_timeout()
    {
        var outcome = FlashStateMachine.ClassifyScan(
            Run(ScanOnlyHappy, exitCode: -1, timedOut: true, duration: TimeSpan.FromSeconds(8)),
            "PY32Fxxx");
        Assert.NotNull(outcome);
        Assert.Equal("E_TIMEOUT", outcome!.ErrorCode);
    }

    // RunAsync end-to-end: scan + retry-on-busy + flash, via a fake GdbProcess.

    private sealed class FakeGdbProcess : GdbProcess
    {
        public Queue<GdbRunResult> ScanResults { get; } = new();
        public Queue<GdbRunResult> FlashResults { get; } = new();
        public int ScanCalls { get; private set; }
        public int FlashCalls { get; private set; }

        public FakeGdbProcess() : base("dummy-gdb-path") { }

        public override Task<GdbRunResult> RunScanAsync(
            string comPort, PowerMode power, int frequencyHz, bool connectUnderReset,
            TimeSpan timeout, Action<GdbLine>? onLine = null, CancellationToken ct = default)
        {
            ScanCalls++;
            return Task.FromResult(ScanResults.Dequeue());
        }

        public override Task<GdbRunResult> RunFlashAsync(
            string comPort, PowerMode power, int frequencyHz, bool connectUnderReset,
            string elfPath, TimeSpan timeout, Action<GdbLine>? onLine = null,
            CancellationToken ct = default)
        {
            FlashCalls++;
            return Task.FromResult(FlashResults.Dequeue());
        }
    }

    private static FlashOptions MakeOptions(string bmpMatch = "PY32Fxxx") => new(
        ElfPath:            "test.elf",
        Port:               "COM30",
        Power:              PowerMode.External,
        BmpFrequencyHz:     1_000_000,
        ConnectUnderReset:  false,
        Product:            "ci-clop",
        Operator:           "op",
        Batch:              "B1",
        StationId:          "station",
        TargetBmpMatch:     bmpMatch,
        TargetFlashKb:      32,
        FirmwareVersion:    "1.0.0",
        FirmwareSha256:     new string('a', 64),
        GdbPath:            null,
        DbPath:             null);

    [Fact]
    public async Task RunAsync_retries_scan_once_on_probe_busy_then_flashes()
    {
        var fake = new FakeGdbProcess();
        fake.ScanResults.Enqueue(Run(new[] { "Access is denied." }, exitCode: 1, duration: TimeSpan.FromMilliseconds(100)));
        fake.ScanResults.Enqueue(Run(ScanOnlyHappy, duration: TimeSpan.FromMilliseconds(200)));
        fake.FlashResults.Enqueue(Run(RealBmpPy32Output, duration: TimeSpan.FromMilliseconds(3000)));

        var outcome = await FlashStateMachine.RunAsync(fake, MakeOptions(), TimeSpan.FromSeconds(15));

        Assert.True(outcome.IsPass);
        Assert.Equal(2, fake.ScanCalls);
        Assert.Equal(1, fake.FlashCalls);
        // Duration = scan1 + scan2 + flash
        Assert.Equal(TimeSpan.FromMilliseconds(3300), outcome.Duration);
    }

    [Fact]
    public async Task RunAsync_returns_probe_busy_when_retries_exhausted()
    {
        var fake = new FakeGdbProcess();
        // Two busy results consumed; default retry budget is 1, so we stop here.
        fake.ScanResults.Enqueue(Run(new[] { "Access is denied." }, exitCode: 1, duration: TimeSpan.FromMilliseconds(100)));
        fake.ScanResults.Enqueue(Run(new[] { "Resource busy" }, exitCode: 1, duration: TimeSpan.FromMilliseconds(100)));

        var outcome = await FlashStateMachine.RunAsync(fake, MakeOptions(), TimeSpan.FromSeconds(15));

        Assert.False(outcome.IsPass);
        Assert.Equal("E_PROBE_BUSY", outcome.ErrorCode);
        Assert.Equal(2, fake.ScanCalls);
        Assert.Equal(0, fake.FlashCalls); // never even attempted
    }

    [Fact]
    public async Task RunAsync_does_not_retry_non_busy_failures()
    {
        // E_TARGET_MISMATCH should bail immediately without retrying — retrying
        // wouldn't help and just wastes operator time.
        var fake = new FakeGdbProcess();
        fake.ScanResults.Enqueue(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      STM32F103",
            "",
        }, duration: TimeSpan.FromMilliseconds(120)));

        var outcome = await FlashStateMachine.RunAsync(fake, MakeOptions(), TimeSpan.FromSeconds(15));

        Assert.Equal("E_TARGET_MISMATCH", outcome.ErrorCode);
        Assert.Equal(1, fake.ScanCalls);
        Assert.Equal(0, fake.FlashCalls);
    }

    [Fact]
    public async Task RunAsync_never_flashes_when_scan_has_multiple_targets()
    {
        var fake = new FakeGdbProcess();
        fake.ScanResults.Enqueue(Run(new[]
        {
            "Available Targets:",
            "No. Att Driver",
            " 1      PY32Fxxx M0+",
            " 2      STM32F103",
            "",
        }));

        var outcome = await FlashStateMachine.RunAsync(
            fake, MakeOptions(), TimeSpan.FromSeconds(15));

        Assert.Equal("E_MULTIPLE_TARGETS", outcome.ErrorCode);
        Assert.Equal(0, fake.FlashCalls);
    }

    [Fact]
    public async Task RunAsync_respects_probeBusyRetries_zero_disables_retry()
    {
        var fake = new FakeGdbProcess();
        fake.ScanResults.Enqueue(Run(new[] { "Access is denied." }, exitCode: 1, duration: TimeSpan.FromMilliseconds(100)));

        var outcome = await FlashStateMachine.RunAsync(
            fake, MakeOptions(), TimeSpan.FromSeconds(15),
            probeBusyRetries: 0);

        Assert.Equal("E_PROBE_BUSY", outcome.ErrorCode);
        Assert.Equal(1, fake.ScanCalls);
        Assert.Equal(0, fake.FlashCalls);
    }
}
