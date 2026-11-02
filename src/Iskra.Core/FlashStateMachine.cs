namespace Iskra.Core;

/// <summary>
/// Guarded factory-safe driver:
/// <list type="number">
///   <item><description><b>Guard</b> — GDB connects, runs <c>swdp_scan</c>, and
///     safely attaches target #1 to bind the physical device. No firmware is
///     opened and no <c>load</c> runs. If the detected target family doesn't
///     match <c>TargetBmpMatch</c>, we bail out with <c>E_TARGET_MISMATCH</c>
///     before any flash write is attempted.</description></item>
///   <item><description><b>Flash</b> — only reached when the application gate
///     classifies that scan as clean. The same attached target and GDB process
///     then run the canonical file/load/compare-sections sequence.</description></item>
/// </list>
/// The per-phase classifiers are pure (no IO), making the test suite deterministic.
/// </summary>
internal static class FlashStateMachine
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
        ArgumentNullException.ThrowIfNull(gdb);
        ArgumentNullException.ThrowIfNull(options);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (probeBusyRetries < 0) throw new ArgumentOutOfRangeException(nameof(probeBusyRetries));

        // Every attempt is a held GDB/MI session. A busy pre-scan may start a
        // fresh retry, but a successful swdp_scan is never disconnected and
        // re-opened between classification and load.
        var scanTimeout = timeout < TimeSpan.FromSeconds(8) ? timeout : TimeSpan.FromSeconds(8);
        TimeSpan accumulatedScanDuration = TimeSpan.Zero;
        int attempt = 0;
        while (true)
        {
            FlashOutcome? gateOutcome = null;
            var guardedRun = await gdb.RunGuardedAsync(
                options.Port,
                options.Power,
                options.BmpFrequencyHz,
                options.ConnectUnderReset,
                options.ElfPath,
                scanTimeout,
                timeout,
                scanRun =>
                {
                    gateOutcome = ClassifyScan(scanRun, options.TargetBmpMatch);
                    return gateOutcome is null;
                },
                onLine,
                ct,
                options.ProbeLockIdentity).ConfigureAwait(false);
            accumulatedScanDuration += guardedRun.Scan.Duration;

            // GdbMiSession does not invoke the gate when a scan command itself
            // fails or times out, so classify once more from the returned
            // snapshot. This is pure and therefore must agree when the gate did
            // run; either path fails closed.
            var scanOutcome = gateOutcome ?? ClassifyScan(
                guardedRun.Scan,
                options.TargetBmpMatch);

            if (guardedRun.Flash is not null)
            {
                if (scanOutcome is not null)
                    return scanOutcome with { Duration = accumulatedScanDuration };

                // Flash output is captured separately so UI progress remains
                // phase-accurate. Classification still needs the target row
                // from swdp_scan, so join snapshots without replaying callbacks.
                var combined = new GdbRunResult(
                    guardedRun.Flash.ExitCode,
                    guardedRun.Flash.TimedOut,
                    guardedRun.Flash.Duration,
                    guardedRun.Scan.Output.Concat(guardedRun.Flash.Output).ToArray());
                var outcome = Classify(
                    combined,
                    options.TargetBmpMatch,
                    options.ExpectedLoadSections);
                return outcome with
                {
                    Duration = accumulatedScanDuration + guardedRun.Flash.Duration,
                };
            }

            if (scanOutcome is null)
            {
                // A passed gate must yield a flash result. Treat a broken or
                // prematurely exited session as a hard failure, never as a
                // reason to launch an unguarded second process.
                return Fail(
                    "E_GDB_CRASHED",
                    "gdb session ended after scan gate but before flash completed",
                    null,
                    accumulatedScanDuration,
                    guardedRun.Scan.Tail());
            }

            // Only a confirmed busy condition is transient. Target mismatch,
            // ambiguity, timeout, and every other failure return immediately.
            if (scanOutcome.ErrorCode != "E_PROBE_BUSY" || attempt >= probeBusyRetries)
                return scanOutcome with { Duration = accumulatedScanDuration };

            attempt++;
            await Task.Delay(ProbeBusyRetryDelay, ct).ConfigureAwait(false);
        }
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

        var attachFail = events.FirstOrDefault(e => e.Kind == GdbEventKind.AttachFailed);
        if (attachFail is not null)
            return Fail("E_ATTACH_FAILED", attachFail.Detail, detected, run.Duration, tail);

        if (run.ExitCode != 0)
            return Fail("E_GDB_CRASHED", $"gdb scan failed with exit code {run.ExitCode}", detected, run.Duration, tail);

        return null;
    }

    /// <summary>
    /// Pure flash-phase classifier: given a captured gdb run and the expected BMP target match string,
    /// return PASS or FAIL with the right E_* code. Order of checks matters — earlier checks
    /// take precedence so we report the *cause*, not a downstream symptom.
    /// </summary>
    public static FlashOutcome Classify(
        GdbRunResult run,
        string expectedBmpMatch,
        IReadOnlyList<FirmwareLoadSection>? expectedLoadSections = null)
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

        var loadEvents = events
            .Where(e => e.Kind == GdbEventKind.LoadingSection)
            .ToList();
        if (loadEvents.Count == 0)
        {
            var why = run.ExitCode != 0
                ? $"gdb exit {run.ExitCode}; load signal absent"
                : "load signal absent in gdb output";
            return Fail("E_LOAD_FAILED", why, detected, run.Duration, tail);
        }

        if (expectedLoadSections is { Count: > 0 })
        {
            var expected = CountLoadPlan(expectedLoadSections.Select(
                section => LoadPlanKey(section.Name, section.Address, section.Length)));
            var observed = CountLoadPlan(loadEvents.Select(
                section => LoadPlanKey(
                    section.Detail,
                    section.Address ?? ulong.MaxValue,
                    section.Length ?? ulong.MaxValue)));
            if (!LoadPlansMatch(expected, observed, out var difference))
            {
                return Fail(
                    "E_LOAD_FAILED",
                    $"GDB/BFD load plan differs from the validated ELF section plan: {difference}",
                    detected,
                    run.Duration,
                    tail);
            }
        }

        var loadedSections = loadEvents.Select(e => e.Detail).ToList();

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

    private static string LoadPlanKey(string name, ulong address, ulong length) =>
        $"{name}\u001f0x{address:X}\u001f0x{length:X}";

    private static Dictionary<string, int> CountLoadPlan(IEnumerable<string> items) =>
        items.GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static bool LoadPlansMatch(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> observed,
        out string difference)
    {
        foreach (var item in expected)
        {
            observed.TryGetValue(item.Key, out var actualCount);
            if (actualCount != item.Value)
            {
                difference = $"expected {item.Key.Replace('\u001f', ' ')} x{item.Value}, observed x{actualCount}";
                return false;
            }
        }
        foreach (var item in observed)
        {
            if (!expected.ContainsKey(item.Key))
            {
                difference = $"unexpected {item.Key.Replace('\u001f', ' ')} x{item.Value}";
                return false;
            }
        }
        difference = string.Empty;
        return true;
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
