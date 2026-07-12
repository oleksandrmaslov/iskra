using Iskra.Core;

namespace Iskra.Core.Tests;

public class GdbCommandBuilderTests
{
    [Theory]
    [InlineData("COM30",        @"\\.\COM30")]
    [InlineData("com3",         @"\\.\COM3")]
    [InlineData(@"\\.\COM12",   @"\\.\COM12")]
    [InlineData("localhost:2000", "localhost:2000")]
    [InlineData("/dev/ttyACM0", "/dev/ttyACM0")]
    [InlineData("/dev/cu.usbmodem-BMP", "/dev/cu.usbmodem-BMP")]
    [InlineData("ttyACM0", "ttyACM0")]
    public void NormalizeComPort_canonicalises_input(string input, string expected)
    {
        Assert.Equal(expected, GdbCommandBuilder.NormalizeComPort(input));
    }

    [Theory]
    [InlineData("/dev/ttyACM0")]
    [InlineData("/dev/cu.usbmodem-BMP")]
    public void BuildExCommands_preserves_unix_serial_device_path(string endpoint)
    {
        var commands = GdbCommandBuilder.BuildExCommands(
            endpoint, PowerMode.External, 1_000_000, connectUnderReset: false);

        Assert.Contains($"target extended-remote {endpoint}", commands);
    }

    [Fact]
    public void BuildExCommands_baseline_external_no_reset()
    {
        var cmds = GdbCommandBuilder.BuildExCommands(
            comPort: "COM30",
            power: PowerMode.External,
            frequencyHz: 1_000_000,
            connectUnderReset: false);

        Assert.Equal(new[]
        {
            "set confirm off",
            "set pagination off",
            @"target extended-remote \\.\COM30",
            "monitor frequency 1000000",
            "monitor swdp_scan",
            "attach 1",
            "load",
            "compare-sections",
            "kill",
            "quit",
        }, cmds);
    }

    [Fact]
    public void BuildExCommands_probe_power_emits_tpwr_enable()
    {
        var cmds = GdbCommandBuilder.BuildExCommands(
            "COM30", PowerMode.Probe, 1_000_000, false);

        Assert.Contains("monitor tpwr enable", cmds);
        // tpwr must come BEFORE frequency (BMP ordering rule from rules.mk)
        var iTpwr = cmds.ToList().IndexOf("monitor tpwr enable");
        var iFreq = cmds.ToList().IndexOf("monitor frequency 1000000");
        Assert.True(iTpwr < iFreq, "tpwr must precede frequency");
    }

    [Fact]
    public void BuildExCommands_connect_reset_inserts_before_swdp_scan()
    {
        var cmds = GdbCommandBuilder.BuildExCommands(
            "COM30", PowerMode.External, 1_000_000, connectUnderReset: true).ToList();

        var iRst = cmds.IndexOf("monitor connect_rst enable");
        var iScan = cmds.IndexOf("monitor swdp_scan");
        Assert.True(iRst > 0);
        Assert.True(iRst < iScan, "connect_rst must precede swdp_scan");
    }

    [Fact]
    public void BuildExCommands_omits_optionals_when_disabled()
    {
        var cmds = GdbCommandBuilder.BuildExCommands(
            "COM30", PowerMode.External, 1_000_000, false);
        Assert.DoesNotContain("monitor tpwr enable", cmds);
        Assert.DoesNotContain("monitor connect_rst enable", cmds);
    }

    [Theory]
    [InlineData(500_000)]
    [InlineData(1_000_000)]
    [InlineData(4_000_000)]
    public void BuildExCommands_frequency_is_emitted_verbatim(int hz)
    {
        var cmds = GdbCommandBuilder.BuildExCommands(
            "COM30", PowerMode.External, hz, false);
        Assert.Contains($"monitor frequency {hz}", cmds);
    }

    [Fact]
    public void BuildExCommands_rejects_empty_port()
    {
        Assert.Throws<ArgumentException>(() =>
            GdbCommandBuilder.BuildExCommands("", PowerMode.External, 1_000_000, false));
    }

    [Fact]
    public void BuildExCommands_rejects_nonpositive_frequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GdbCommandBuilder.BuildExCommands("COM30", PowerMode.External, 0, false));
    }

    [Fact]
    public void BuildProcessArgs_wraps_each_ex_command_with_dash_ex_and_appends_elf()
    {
        var args = GdbCommandBuilder.BuildProcessArgs(
            "COM30", PowerMode.External, 1_000_000, false,
            elfPath: @"C:\fw\ci-clop_v1.0.0_PY32F002Ax5.elf").ToList();

        Assert.Equal("-nx", args[0]);
        Assert.Equal("--batch", args[1]);
        Assert.Equal("-iex", args[2]);
        Assert.Equal("set auto-load off", args[3]);
        Assert.Equal("-iex", args[4]);
        Assert.Equal("set debuginfod enabled off", args[5]);

        // Every -ex should be followed by exactly one command argument.
        for (int i = 6; i < args.Count - 1; i += 2)
        {
            Assert.Equal("-ex", args[i]);
            Assert.False(args[i + 1].StartsWith('-'), $"expected command at index {i + 1}, got {args[i + 1]}");
        }

        Assert.EndsWith("ci-clop_v1.0.0_PY32F002Ax5.elf", args[^1]);
        Assert.True(args.IndexOf("set auto-load off") < args.Count - 1,
            "auto-load must be disabled before the firmware operand is opened");
    }

    [Fact]
    public void BuildProcessArgs_rejects_empty_elf_path()
    {
        Assert.Throws<ArgumentException>(() =>
            GdbCommandBuilder.BuildProcessArgs(
                "COM30", PowerMode.External, 1_000_000, false, elfPath: ""));
    }

    [Fact]
    public void BuildScanExCommands_stops_at_swdp_scan_no_attach_or_load()
    {
        var cmds = GdbCommandBuilder.BuildScanExCommands(
            "COM30", PowerMode.External, 1_000_000, connectUnderReset: false);

        Assert.Equal(new[]
        {
            "set confirm off",
            "set pagination off",
            @"target extended-remote \\.\COM30",
            "monitor frequency 1000000",
            "monitor swdp_scan",
            "quit",
        }, cmds);
    }

    [Fact]
    public void BuildScanExCommands_must_not_emit_load_or_attach_or_compare()
    {
        // Factory safety: scan phase exists precisely so we never touch flash on
        // a wrong-target-family board. If anyone adds load/attach back into the
        // scan phase, this test fails loudly.
        var cmds = GdbCommandBuilder.BuildScanExCommands(
            "COM30", PowerMode.Probe, 1_000_000, connectUnderReset: true);
        Assert.DoesNotContain("attach 1", cmds);
        Assert.DoesNotContain("load", cmds);
        Assert.DoesNotContain("compare-sections", cmds);
        Assert.DoesNotContain("kill", cmds);
    }

    [Fact]
    public void BuildScanExCommands_honors_probe_power_and_connect_reset()
    {
        var cmds = GdbCommandBuilder.BuildScanExCommands(
            "COM30", PowerMode.Probe, 1_000_000, connectUnderReset: true).ToList();
        Assert.Contains("monitor tpwr enable", cmds);
        Assert.Contains("monitor connect_rst enable", cmds);
        Assert.True(cmds.IndexOf("monitor tpwr enable") < cmds.IndexOf("monitor frequency 1000000"));
        Assert.True(cmds.IndexOf("monitor connect_rst enable") < cmds.IndexOf("monitor swdp_scan"));
    }

    [Fact]
    public void BuildScanProcessArgs_does_not_append_an_elf_path()
    {
        var args = GdbCommandBuilder.BuildScanProcessArgs(
            "COM30", PowerMode.External, 1_000_000, false).ToList();
        Assert.Equal("-nx", args[0]);
        Assert.Equal("--batch", args[1]);
        Assert.Equal("set auto-load off", args[3]);
        Assert.Equal("set debuginfod enabled off", args[5]);
        Assert.Equal("quit", args[^1]); // final ex-command, not a positional ELF
        Assert.Equal("-ex", args[^2]);
        Assert.DoesNotContain(args, a => a.EndsWith(".elf", StringComparison.OrdinalIgnoreCase));
    }
}
