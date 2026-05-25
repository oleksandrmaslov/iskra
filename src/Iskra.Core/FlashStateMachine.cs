namespace FlashlightApp.Core;

/// <summary>
/// Conceptually a state machine over the gdb run lifecycle
/// (IDLE → PREPARING → PROBE_CHECK → ATTACH → LOADING → VERIFYING → PASS/FAIL → LOGGED).
/// In practice, gdb --batch runs end-to-end in one shot and emits all phases to stdout;
/// we drive the process, then *classify* the captured output into a single FlashOutcome.
/// Keeping classification pure (no IO) makes the test suite deterministic.
/// </summary>
public static class FlashStateMachine
{
    public static async Task<FlashOutcome> RunAsync(
        GdbProcess gdb,
        FlashOptions options,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default)
    {
        var run = await gdb.RunFlashAsync(
            options.Port,
            options.Power,
            options.BmpFrequencyHz,
            options.ConnectUnderReset,
            options.ElfPath,
            timeout,
            onLine,
            ct).ConfigureAwait(false);
        return Classify(run, options.TargetBmpMatch);
    }

    /// <summary>
    /// Pure classification: given a captured gdb run and the expected BMP target match string,
    /// return PASS or FAIL with the right E_* code. Order of checks matters — earlier checks
    /// take precedence so we report the *cause*, not a downstream symptom.
    /// </summary>
    public static FlashOutcome Classify(GdbRunResult run, string expectedBmpMatch)
    {
        var tail = run.Tail();

        if (run.TimedOut)
            return Fail("E_TIMEOUT", "gdb wall-clock timeout exceeded", null, run.Duration, tail);

        var events = GdbOutputParser.Parse(run.Output);

        var usb = events.FirstOrDefault(e => e.Kind == GdbEventKind.UsbError);
        if (usb is not null)
            return Fail("E_PROBE_NOT_FOUND", usb.Detail, null, run.Duration, tail);

        var busy = events.FirstOrDefault(e => e.Kind == GdbEventKind.ProbeBusy);
        if (busy is not null)
            return Fail("E_PROBE_BUSY", busy.Detail, null, run.Duration, tail);

        var remote = events.FirstOrDefault(e => e.Kind == GdbEventKind.RemoteError);
        if (remote is not null)
            return Fail("E_PROBE_NOT_FOUND", remote.Detail, null, run.Duration, tail);

        var targets = events
            .Where(e => e.Kind == GdbEventKind.TargetDetected)
            .Select(e => e.Detail)
            .ToList();
        string? detected = targets.FirstOrDefault();

        if (targets.Count == 0)
            return Fail("E_SCAN_NO_TARGET", "swdp_scan returned no targets", null, run.Duration, tail);

        if (!string.IsNullOrEmpty(expectedBmpMatch) &&
            !targets.Any(t => t.Contains(expectedBmpMatch, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("E_TARGET_MISMATCH",
                $"expected '{expectedBmpMatch}', detected '{detected}'",
                detected, run.Duration, tail);
        }

        var attachFail = events.FirstOrDefault(e => e.Kind == GdbEventKind.AttachFailed);
        if (attachFail is not null)
            return Fail("E_ATTACH_FAILED", attachFail.Detail, detected, run.Duration, tail);

        var mismatch = events.FirstOrDefault(e => e.Kind == GdbEventKind.SectionMismatched);
        if (mismatch is not null)
            return Fail("E_VERIFY_MISMATCH",
                $"section {mismatch.Detail} verify failed",
                detected, run.Duration, tail);

        bool loaded  = events.Any(e => e.Kind == GdbEventKind.LoadingSection);
        bool matched = events.Any(e => e.Kind == GdbEventKind.SectionMatched);
        if (!loaded || !matched)
        {
            var why = run.ExitCode != 0
                ? $"gdb exit {run.ExitCode}; load/verify signal absent"
                : "load/verify signal absent in gdb output";
            return Fail("E_LOAD_FAILED", why, detected, run.Duration, tail);
        }

        if (run.ExitCode != 0)
            return Fail("E_GDB_CRASHED", $"gdb exit code {run.ExitCode}", detected, run.Duration, tail);

        return new FlashOutcome(FlashResult.Pass, null, null, detected, run.Duration, tail);
    }

    private static FlashOutcome Fail(string code, string msg, string? detected, TimeSpan dur, string tail)
        => new(FlashResult.Fail, code, msg, detected, dur, tail);
}
