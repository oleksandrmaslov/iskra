using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class GdbOutputParserTests
{
    private static IReadOnlyList<GdbLine> Lines(params string[] text)
        => text.Select(t => new GdbLine(DateTime.UtcNow, GdbStream.Stdout, t)).ToList();

    [Fact]
    public void Parses_swdp_scan_target_block()
    {
        var ev = GdbOutputParser.Parse(Lines(
            "Target voltage: 3.3V",
            "Available Targets:",
            "No. Att Driver",
            " 1      PY32F002A M0+",
            "")).ToList();

        var targets = ev.Where(e => e.Kind == GdbEventKind.TargetDetected).ToList();
        Assert.Single(targets);
        Assert.Equal("PY32F002A M0+", targets[0].Detail);
    }

    [Fact]
    public void Parses_multiple_targets_when_present()
    {
        var ev = GdbOutputParser.Parse(Lines(
            "Available Targets:",
            "No. Att Driver",
            " 1      STM32F103",
            " 2      STM32F103 M3",
            ""));

        var targets = ev.Where(e => e.Kind == GdbEventKind.TargetDetected).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal("STM32F103", targets[0].Detail);
        Assert.Equal("STM32F103 M3", targets[1].Detail);
    }

    [Fact]
    public void Detects_loading_section_and_matched_verify()
    {
        var ev = GdbOutputParser.Parse(Lines(
            "Loading section .text, size 0x1234 lma 0x8000000",
            "Loading section .data, size 0x100 lma 0x8001234",
            "Section .text, range 0x8000000 -- 0x8001234: matched.",
            "Section .data, range 0x8001234 -- 0x8001334: matched."));

        Assert.Equal(2, ev.Count(e => e.Kind == GdbEventKind.LoadingSection));
        Assert.Equal(2, ev.Count(e => e.Kind == GdbEventKind.SectionMatched));
        Assert.DoesNotContain(ev, e => e.Kind == GdbEventKind.SectionMismatched);
    }

    [Fact]
    public void Detects_mismatched_section()
    {
        var ev = GdbOutputParser.Parse(Lines(
            "Section .text, range 0x8000000 -- 0x8001234: MIS-MATCHED!"));
        var mis = ev.Single(e => e.Kind == GdbEventKind.SectionMismatched);
        Assert.Equal(".text", mis.Detail);
    }

    [Fact]
    public void Detects_attach_failure()
    {
        var ev = GdbOutputParser.Parse(Lines("attach: Cannot find target #1"));
        Assert.Contains(ev, e => e.Kind == GdbEventKind.AttachFailed);
    }

    [Fact]
    public void Detects_usb_error()
    {
        var ev = GdbOutputParser.Parse(Lines("libusb_open failed: LIBUSB_ERROR_NOT_FOUND"));
        Assert.Contains(ev, e => e.Kind == GdbEventKind.UsbError);
    }

    [Fact]
    public void Detects_remote_error()
    {
        var ev = GdbOutputParser.Parse(Lines(
            @":\\.\COM30: The system cannot find the file specified."));
        Assert.Contains(ev, e => e.Kind == GdbEventKind.RemoteError);
    }

    [Fact]
    public void Detects_probe_busy()
    {
        var ev = GdbOutputParser.Parse(Lines("Access is denied."));
        Assert.Contains(ev, e => e.Kind == GdbEventKind.ProbeBusy);
    }

    [Fact]
    public void Empty_output_yields_no_events()
    {
        Assert.Empty(GdbOutputParser.Parse(Array.Empty<GdbLine>()));
    }

    [Fact]
    public void Target_block_terminates_on_blank_line()
    {
        // After the blank line we should NOT keep matching numeric lines as targets.
        var ev = GdbOutputParser.Parse(Lines(
            "Available Targets:",
            "No. Att Driver",
            " 1      PY32F002A",
            "",
            " 99   some unrelated numbered line"));

        Assert.Single(ev.Where(e => e.Kind == GdbEventKind.TargetDetected));
    }
}
