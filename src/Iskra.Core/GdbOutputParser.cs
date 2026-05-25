using System.Text.RegularExpressions;

namespace FlashlightApp.Core;

public enum GdbEventKind
{
    TargetDetected,
    AttachFailed,
    LoadingSection,
    SectionMatched,
    SectionMismatched,
    RemoteError,
    UsbError,
    ProbeBusy,
    Warning,
}

public sealed record GdbEvent(GdbEventKind Kind, string Detail, int LineNumber);

/// <summary>
/// Parses Black Magic Probe gdb stdout/stderr into typed events. Pure (no IO);
/// recognises the patterns the state machine cares about and ignores the rest.
/// Target-agnostic: we capture whatever swdp_scan reports as the detected target.
/// </summary>
public static class GdbOutputParser
{
    private static readonly Regex TargetRowRegex =
        new(@"^\s*\d+\s+(?<name>.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex LoadingSectionRegex =
        new(@"^Loading section\s+(?<sec>\S+?)\s*,", RegexOptions.Compiled);

    private static readonly Regex SectionMatchedRegex =
        new(@"Section\s+(?<sec>\S+?)\s*,.*?:\s*matched\.", RegexOptions.Compiled);

    private static readonly Regex SectionMismatchedRegex =
        new(@"Section\s+(?<sec>\S+?)\s*,.*?MIS-MATCHED", RegexOptions.Compiled);

    public static IReadOnlyList<GdbEvent> Parse(IEnumerable<GdbLine> lines)
    {
        var events = new List<GdbEvent>();
        bool inTargetList = false;
        int idx = 0;

        foreach (var line in lines)
        {
            idx++;
            var t = line.Text;
            if (string.IsNullOrWhiteSpace(t))
            {
                inTargetList = false;
                continue;
            }

            if (t.Contains("Available Targets:", StringComparison.Ordinal))
            {
                inTargetList = true;
                continue;
            }

            if (inTargetList)
            {
                var trimmed = t.TrimStart();
                if (trimmed.StartsWith("No.", StringComparison.Ordinal))
                    continue;
                var m = TargetRowRegex.Match(t);
                if (m.Success)
                {
                    events.Add(new GdbEvent(
                        GdbEventKind.TargetDetected,
                        m.Groups["name"].Value.Trim(),
                        idx));
                    continue;
                }
                inTargetList = false;
                // fall through — line may still be an error/info
            }

            if (t.Contains("Cannot find target #", StringComparison.Ordinal) ||
                t.Contains("Don't know how to attach", StringComparison.Ordinal) ||
                t.Contains("Cannot attach", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new GdbEvent(GdbEventKind.AttachFailed, t.Trim(), idx));
                continue;
            }

            if (t.Contains("Cannot open USB", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("libusb", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("USB device not found", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new GdbEvent(GdbEventKind.UsbError, t.Trim(), idx));
                continue;
            }

            if (t.Contains("Resource busy", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new GdbEvent(GdbEventKind.ProbeBusy, t.Trim(), idx));
                continue;
            }

            if (t.Contains("Remote communication error", StringComparison.Ordinal) ||
                t.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Couldn't establish connection", StringComparison.Ordinal) ||
                t.Contains("Target disconnected", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new GdbEvent(GdbEventKind.RemoteError, t.Trim(), idx));
                continue;
            }

            var lm = LoadingSectionRegex.Match(t);
            if (lm.Success)
            {
                events.Add(new GdbEvent(GdbEventKind.LoadingSection, lm.Groups["sec"].Value, idx));
                continue;
            }

            var sm = SectionMatchedRegex.Match(t);
            if (sm.Success)
            {
                events.Add(new GdbEvent(GdbEventKind.SectionMatched, sm.Groups["sec"].Value, idx));
                continue;
            }

            var sx = SectionMismatchedRegex.Match(t);
            if (sx.Success)
            {
                events.Add(new GdbEvent(GdbEventKind.SectionMismatched, sx.Groups["sec"].Value, idx));
                continue;
            }

            if (t.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
                events.Add(new GdbEvent(GdbEventKind.Warning, t.Trim(), idx));
        }

        return events;
    }
}
