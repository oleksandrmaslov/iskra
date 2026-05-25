using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class FlashOptionsTests
{
    private static string[] Required(params string[] extras) => new[]
    {
        "--elf", @"C:\fw\test.elf",
        "--port", "COM30",
        "--product", "pocket-light",
        "--operator", "Iryna",
        "--batch", "B-2026-001",
        "--target", "PY32F002A",
        "--flash-kb", "32",
    }.Concat(extras).ToArray();

    [Fact]
    public void Parse_minimal_required_args_succeeds_with_defaults()
    {
        var o = FlashOptions.Parse(Required());
        Assert.NotNull(o);
        Assert.Equal(@"C:\fw\test.elf", o!.ElfPath);
        Assert.Equal("COM30", o.Port);
        Assert.Equal(PowerMode.External, o.Power);
        Assert.Equal(1_000_000, o.BmpFrequencyHz);
        Assert.False(o.ConnectUnderReset);
        Assert.Equal("pocket-light", o.Product);
        Assert.Equal("PY32F002A", o.TargetBmpMatch);
        Assert.Equal(32, o.TargetFlashKb);
        Assert.Equal("unknown", o.FirmwareVersion);
        Assert.Equal("unknown", o.FirmwareSha256);
        Assert.Equal(Environment.MachineName, o.StationId);
        Assert.Null(o.GdbPath);
        Assert.Null(o.DbPath);
    }

    [Fact]
    public void Parse_missing_target_fails()
    {
        var args = new[]
        {
            "--elf", "x.elf", "--port", "COM3",
            "--product", "p", "--operator", "o", "--batch", "b",
            "--flash-kb", "32",
        };
        Assert.Null(FlashOptions.Parse(args));
    }

    [Fact]
    public void Parse_missing_flash_kb_fails()
    {
        var args = new[]
        {
            "--elf", "x.elf", "--port", "COM3",
            "--product", "p", "--operator", "o", "--batch", "b",
            "--target", "STM32F1",
        };
        Assert.Null(FlashOptions.Parse(args));
    }

    [Fact]
    public void Parse_zero_flash_kb_fails()
    {
        var o = FlashOptions.Parse(Required("--flash-kb", "0"));
        // --flash-kb appears twice; last wins → 0 → invalid
        Assert.Null(o);
    }

    [Fact]
    public void Parse_power_probe_sets_enum()
    {
        var o = FlashOptions.Parse(Required("--power", "probe"));
        Assert.NotNull(o);
        Assert.Equal(PowerMode.Probe, o!.Power);
    }

    [Fact]
    public void Parse_bad_power_value_fails()
    {
        var o = FlashOptions.Parse(Required("--power", "bus"));
        Assert.Null(o);
    }

    [Fact]
    public void Parse_connect_reset_flag()
    {
        var o = FlashOptions.Parse(Required("--connect-reset"));
        Assert.NotNull(o);
        Assert.True(o!.ConnectUnderReset);
    }

    [Fact]
    public void Parse_overrides_for_station_firmware_db()
    {
        var o = FlashOptions.Parse(Required(
            "--station-id", "BENCH-3",
            "--firmware-version", "1.2.3",
            "--firmware-sha256", "abcdef",
            "--db-path", @"D:\logs\flash.db"));
        Assert.NotNull(o);
        Assert.Equal("BENCH-3", o!.StationId);
        Assert.Equal("1.2.3", o.FirmwareVersion);
        Assert.Equal("abcdef", o.FirmwareSha256);
        Assert.Equal(@"D:\logs\flash.db", o.DbPath);
    }

    [Fact]
    public void Parse_unknown_flag_fails()
    {
        var o = FlashOptions.Parse(Required("--nope"));
        Assert.Null(o);
    }
}
