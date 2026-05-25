using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

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
    public void Happy_path_but_nonzero_exit_yields_gdb_crashed()
    {
        var outcome = FlashStateMachine.Classify(Run(HappyPath, exitCode: 1), "PY32F002A");
        Assert.Equal("E_GDB_CRASHED", outcome.ErrorCode);
        Assert.Equal("PY32F002A M0+", outcome.DetectedTarget);
    }
}
