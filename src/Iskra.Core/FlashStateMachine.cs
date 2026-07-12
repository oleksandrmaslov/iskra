namespace Iskra.Core;

/// <summary>
/// Two-phase factory-safe driver:
/// <list type="number">
///   <item><description><b>Scan</b> — gdb connects, runs <c>swdp_scan</c>, quits.
///     No <c>attach</c>, no <c>load</c>. If the detected target family doesn't
///     match <c>TargetBmpMatch</c>, we bail out with <c>E_TARGET_MISMATCH</c>
///     before any flash write is attempted.</description></item>
///   <item><description><b>Flash</b> — only reached when scan classified clean.
///     Runs the canonical attach/load/compare-sections sequence.</description></item>
/// </list>
/// Each phase produces one <see cref="GdbRunResult"/>; the per-phase classifier
/// is pure (no IO), making the test suite deterministic.
/// </summary>
public static class FlashStateMachine
{
    /// <summary>
    /// Default backoff before retrying scan on <c>E_PROBE_BUSY</c>. BMP usually
    /// frees the USB endpoint within a few hundred ms after a previous session
    /// closes; 500 ms is long enough to ride out the typical re-enumerate without
    /// noticeably slowing the operator down on a real failure.
    /// </summary>
    public static readonly TimeSpan ProbeBusyRetryDelay = TimeSpan.FromMilliseconds(500);

    public static async Task<FlashOutcome> RunAsync(
        GdbProcess gdb,
        FlashOptions options,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default,
        int probeBusyRetries = 1)
    {
        // Phase 1: scan only — bail safely before touching flash on a wrong board.
        // On E_PROBE_BUSY, retry up to probeBusyRetries times — BMP occasionally
        // fumbles a USB re-enumerate between consecutive flashes; a quick retry
        // smooths that out without misclassifying real probe-conflict failures.
        var scanTimeout = timeout < TimeSpan.FromSeconds(8) ? timeout : TimeSpan.FromSeconds(8);
        TimeSpan accumulatedScanDuration = TimeSpan.Zero;
        GdbRunResult scanRun = null!;
        FlashOutcome? scanOutcome;
        int attempt = 0;
        while (true)
        {
            scanRun = await gdb.RunScanAsync(
                options.Port,
                options.Power,
                options.BmpFrequencyHz,
                options.ConnectUnderReset,
                scanTimeout,
                onLine,
                ct).ConfigureAwait(false);
            accumulatedScanDuration += scanRun.Duration;

            scanOutcome = ClassifyScan(scanRun, options.TargetBmpMatch);
            if (scanOutcome is null) break; // clean scan — proceed to flash
            if (scanOutcome.ErrorCode != "E_PROBE_BUSY" || attempt >= probeBusyRetries)
                return scanOutcome with { Duration = accumulatedScanDuration };

            attempt++;
            try { await Task.Delay(ProbeBusyRetryDelay, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return scanOutcome with { Duration = accumulatedScanDuration }; }
        }

        // Phase 2: flash — only reached when scan passed.
        var flashRun = await gdb.RunFlashAsync(
            options.Port,
            options.Power,
            options.BmpFrequencyHz,
            options.ConnectUnderReset,
            options.ElfPath,
            timeout,
            onLine,
            ct).ConfigureAwait(false);

        var outcome = Classify(flashRun, options.TargetBmpMatch);
        // Roll the scan duration into the reported wall-clock so logs reflect
        // true end-to-end time. Tail stays from the flash phase (operators want
        // verify lines), the scan run is captured live via onLine if the caller
        // is logging.
        return outcome with { Duration = accumulatedScanDuration + outcome.Duration };
    }

    /// <summary>
    /// Pure scan-phase classifier. Returns a non-null FAIL outcome if the scan
    /// detected a fatal condition (timeout, probe error, no targets, family
    /// mismatch); returns <c>null</c> when the scan is clean and the caller
    /// should proceed to flash.
    /// </summary>
    public static FlashOutcome? ClassifyScan(GdbRunResult run, string expectedBmpMatch)
    {
        var tail = run.Tail();

        if (run.TimedOut)
            return Fail("E_TIMEOUT", "gdb scan-phase wall-clock timeout exceeded", null, run.Duration, tail);

        var events = GdbOutputParser.Parse(run.Output);

        var probeFail = ClassifyProbeError(events, run.Duration, tail);
        if (probeFail is not null) return probeFail;

        var targets = events
            .Where(e => e.Kind == GdbEventKind.TargetDetected)
            .Select(e => e.Detail)
            .ToList();
        if (targets.Count == 0)
            return Fail("E_SCAN_NO_TARGET", "swdp_scan returned no targets", null, run.Duration, tail);

        // The flash command attaches target #1. If BMP exposes more than one
        // target we cannot safely infer which physical device the catalog
        // describes, even when one of the rows happens to match.
        if (targets.Count != 1)
            return Fail("E_MULTIPLE_TARGETS",
                $"swdp_scan returned {targets.Count} targets; refusing ambiguous attach #1",
                string.Join(" | ", targets), run.Duration, tail);

        var detected = targets[0];
        if (!string.IsNullOrEmpty(expectedBmpMatch) &&
            !detected.Contains(expectedBmpMatch, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("E_TARGET_MISMATCH",
                $"expected '{expectedBmpMatch}', detected '{detected}'",
                detected, run.Duration, tail);
        }

        return null;
    }

    /// <summary>
    /// Pure flash-phase classifier: given a captured gdb run and the expected BMP target match string,
    /// return PASS or FAIL with the right E_* code. Order of checks matters — earlier checks
    /// take precedence so we report the *cause*, not a downstream symptom.
    /// </summary>
    public static FlashOutcome Classify(GdbRunResult run, string expectedBmpMatch)
    {
        var tail = run.Tail();

        if (run.TimedOut)
            return Fail("E_TIMEOUT", "gdb wall-clock timeout exceeded", null, run.Duration, tail);

        var events = GdbOutputParser.Parse(run.Output);

        var probeFail = ClassifyProbeError(events, run.Duration, tail);
        if (probeFail is not null) return probeFail;

        var targets = events
            .Where(e => e.Kind == GdbEventKind.TargetDetected)
            .Select(e => e.Detail)
            .ToList();
        string? detected = targets.FirstOrDefault();

        if (targets.Count == 0)
            return Fail("E_SCAN_NO_TARGET", "swdp_scan returned no targets", null, run.Duration, tail);

        if (targets.Count != 1)
            return Fail("E_MULTIPLE_TARGETS",
                $"swdp_scan returned {targets.Count} targets; attach #1 is ambiguous",
                string.Join(" | ", targets), run.Duration, tail);

        if (!string.IsNullOrEmpty(expectedBmpMatch) &&
            !detected!.Contains(expectedBmpMatch, StringComparison.OrdinalIgnoreCase))
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

        var loadedSections = events
            .Where(e => e.Kind == GdbEventKind.LoadingSection)
            .Select(e => e.Detail)
            .ToList();
        if (loadedSections.Count == 0)
        {
            var why = run.ExitCode != 0
                ? $"gdb exit {run.ExitCode}; load signal absent"
                : "load signal absent in gdb output";
            return Fail("E_LOAD_FAILED", why, detected, run.Duration, tail);
        }

        var matchedCounts = events
            .Where(e => e.Kind == GdbEventKind.SectionMatched)
            .GroupBy(e => e.Detail, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var loaded in loadedSections.GroupBy(s => s, StringComparer.Ordinal))
        {
            matchedCounts.TryGetValue(loaded.Key, out var verifiedCount);
            if (verifiedCount < loaded.Count())
            {
                return Fail("E_VERIFY_MISMATCH",
                    $"section {loaded.Key} was loaded but not verified as matched",
                    detected, run.Duration, tail);
            }
        }

        if (run.ExitCode != 0)
            return Fail("E_GDB_CRASHED", $"gdb exit code {run.ExitCode}", detected, run.Duration, tail);

        return new FlashOutcome(FlashResult.Pass, null, null, detected, run.Duration, tail);
    }

    private static FlashOutcome? ClassifyProbeError(
        IReadOnlyList<GdbEvent> events, TimeSpan duration, string tail)
    {
        var usb = events.FirstOrDefault(e => e.Kind == GdbEventKind.UsbError);
        if (usb is not null)
            return Fail("E_PROBE_NOT_FOUND", usb.Detail, null, duration, tail);

        var busy = events.FirstOrDefault(e => e.Kind == GdbEventKind.ProbeBusy);
        if (busy is not null)
            return Fail("E_PROBE_BUSY", busy.Detail, null, duration, tail);

        var remote = events.FirstOrDefault(e => e.Kind == GdbEventKind.RemoteError);
        if (remote is not null)
            return Fail("E_PROBE_NOT_FOUND", remote.Detail, null, duration, tail);

        return null;
    }

    private static FlashOutcome Fail(string code, string msg, string? detected, TimeSpan dur, string tail)
        => new(FlashResult.Fail, code, msg, detected, dur, tail);
}
