namespace FlashlightApp.Core;

/// <summary>
/// Builds the <c>-ex</c> argument list for an <c>arm-none-eabi-gdb --batch</c> invocation
/// that drives a Black Magic Probe. Target-agnostic — no MCU-family knowledge here.
/// </summary>
public static class GdbCommandBuilder
{
    public static IReadOnlyList<string> BuildExCommands(
        string comPort,
        PowerMode power,
        int frequencyHz,
        bool connectUnderReset)
    {
        if (string.IsNullOrWhiteSpace(comPort))
            throw new ArgumentException("comPort required", nameof(comPort));
        if (frequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));

        var port = NormalizeComPort(comPort);
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
        list.Add("attach 1");
        list.Add("load");
        list.Add("compare-sections");
        list.Add("kill");
        list.Add("quit");
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

        var args = new List<string> { "-nx", "--batch" };
        foreach (var ex in BuildExCommands(comPort, power, frequencyHz, connectUnderReset))
        {
            args.Add("-ex");
            args.Add(ex);
        }
        args.Add(elfPath);
        return args;
    }

    /// <summary>
    /// Black Magic Probe on Windows is reached via the raw device path <c>\\.\COMxx</c>
    /// (required for ports above COM9). Accept any of: <c>COM30</c>, <c>\\.\COM30</c>,
    /// or a TCP host:port (e.g. <c>localhost:2000</c>) for the rare TCP case — pass through.
    /// </summary>
    public static string NormalizeComPort(string comPort)
    {
        var trimmed = comPort.Trim();
        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
            return trimmed;
        if (trimmed.Contains(':'))
            return trimmed; // host:port, leave alone
        return @"\\.\" + trimmed.ToUpperInvariant();
    }
}
