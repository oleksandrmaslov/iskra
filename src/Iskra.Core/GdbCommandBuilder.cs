namespace Iskra.Core;

/// <summary>
/// Builds the <c>-ex</c> argument list for an <c>arm-none-eabi-gdb --batch</c> invocation
/// that drives a Black Magic Probe. Target-agnostic — no MCU-family knowledge here.
/// </summary>
public static class GdbCommandBuilder
{
    private static readonly string[] SafeProcessPrefix =
    {
        "-nx",
        "--batch",
        // Early-init commands execute before GDB opens the positional firmware
        // file. Firmware must never be able to run embedded auto-load scripts
        // or trigger a debuginfod network lookup on a factory station.
        "-iex",
        "set auto-load off",
        "-iex",
        "set debuginfod enabled off",
    };

    public static IReadOnlyList<string> BuildExCommands(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset)
    {
        var list = BuildPreambleCommands(comPort, power, frequencyHz, connectUnderReset);
        list.Add("attach 1");
        list.Add("load");
        list.Add("compare-sections");
        list.Add("kill");
        list.Add("quit");
        return list;
    }

    /// <summary>
    /// Scan-only ex-commands: probe enumeration up to <c>swdp_scan</c>, then quit.
    /// Used by the pre-flash scan phase to detect the target family without
    /// touching flash. No <c>attach</c>, no <c>load</c>, no <c>compare-sections</c>,
    /// and the caller must NOT pass an ELF path on the gdb command line.
    /// </summary>
    public static IReadOnlyList<string> BuildScanExCommands(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset)
    {
        var list = BuildPreambleCommands(comPort, power, frequencyHz, connectUnderReset);
        list.Add("quit");
        return list;
    }

    private static List<string> BuildPreambleCommands(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset)
    {
        if (string.IsNullOrWhiteSpace(comPort))
            throw new ArgumentException("comPort required", nameof(comPort));
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));

        var port = NormalizeProbeEndpoint(comPort);
        var list = new List<string>
        {
            "set confirm off",
            "set pagination off",
            $"target extended-remote {port}",
        };

        if (power == PowerMode.Probe)
            list.Add("monitor tpwr enable");

        list.Add($"monitor frequency {frequencyHz}");

        if (connectUnderReset)
            list.Add("monitor connect_rst enable");

        list.Add("monitor swdp_scan");
        return list;
    }

    /// <summary>
    /// Builds the full process argument list (everything after the gdb executable path).
    /// </summary>
    public static IReadOnlyList<string> BuildProcessArgs(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset,
        string elfPath)
    {
        if (string.IsNullOrWhiteSpace(elfPath))
            throw new ArgumentException("elfPath required", nameof(elfPath));

        var args = new List<string>(SafeProcessPrefix);
        foreach (var ex in BuildExCommands(comPort, power, frequencyHz, connectUnderReset))
        {
            args.Add("-ex");
            args.Add(ex);
        }
        args.Add(elfPath);
        return args;
    }

    /// <summary>
    /// Process args for the scan-only phase. No ELF path: scan never touches flash.
    /// </summary>
    public static IReadOnlyList<string> BuildScanProcessArgs(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset)
    {
        var args = new List<string>(SafeProcessPrefix);
        foreach (var ex in BuildScanExCommands(comPort, power, frequencyHz, connectUnderReset))
        {
            args.Add("-ex");
            args.Add(ex);
        }
        return args;
    }

    /// <summary>
    /// Normalizes a Black Magic Probe transport endpoint. Windows COM names are
    /// converted to the raw device form required above COM9. Unix device paths
    /// and TCP endpoints are already valid GDB endpoints and pass through.
    /// </summary>
    public static string NormalizeProbeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
            return trimmed;
        if (trimmed.Contains(':'))
            return trimmed; // host:port, leave alone
        if (IsWindowsComName(trimmed))
            return @"\\.\" + trimmed.ToUpperInvariant();
        return trimmed;
    }

    /// <summary>Backward-compatible name for existing callers.</summary>
    public static string NormalizeComPort(string comPort) => NormalizeProbeEndpoint(comPort);

    private static bool IsWindowsComName(string value)
    {
        if (value.Length <= 3 || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return false;
        for (var i = 3; i < value.Length; i++)
        {
            if (!char.IsAsciiDigit(value[i])) return false;
        }
        return true;
    }
}
