namespace Iskra.Core;

/// <summary>
/// Builds the <c>-ex</c> argument list for an <c>arm-none-eabi-gdb --batch</c> invocation
/// that drives a Black Magic Probe. Target-agnostic — no MCU-family knowledge here.
/// </summary>
public static class GdbCommandBuilder
{
    private const int MaxEndpointLength = 512;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var trimmed = endpoint.Trim();
        if (trimmed.Length > MaxEndpointLength)
            throw new ArgumentException("probe endpoint is too long", nameof(endpoint));

        // This value is eventually consumed by GDB's command parser. Keep the
        // accepted grammar deliberately narrow even though Process.ArgumentList
        // already prevents shell injection: newlines, quotes, pipes, and other
        // GDB metacharacters must never become a second debugger command.
        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("probe endpoint contains whitespace or control characters", nameof(endpoint));

        if (TryNormalizeWindowsComName(trimmed, out var windowsCom))
            return windowsCom;

        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            trimmed.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("invalid Windows COM endpoint", nameof(endpoint));

        if (trimmed.StartsWith("/dev/", StringComparison.Ordinal))
        {
            ValidateUnixDevicePath(trimmed, nameof(endpoint));
            return trimmed;
        }

        if (trimmed.Contains(':'))
        {
            ValidateTcpEndpoint(trimmed, nameof(endpoint));
            return trimmed;
        }

        // Retain compatibility with explicit relative tty names while still
        // excluding all GDB command-language punctuation.
        if (trimmed.All(IsSafeEndpointAtomCharacter))
            return trimmed;

        throw new ArgumentException("invalid probe endpoint", nameof(endpoint));
    }

    /// <summary>Backward-compatible name for existing callers.</summary>
    public static string NormalizeComPort(string comPort) => NormalizeProbeEndpoint(comPort);

    private static bool TryNormalizeWindowsComName(string value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = value.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? value[4..]
            : value;
        if (candidate.Length <= 3 || !candidate.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return false;
        for (var i = 3; i < candidate.Length; i++)
        {
            if (!char.IsAsciiDigit(candidate[i])) return false;
        }

        if (!int.TryParse(candidate.AsSpan(3), out var number) || number <= 0)
            return false;

        normalized = $@"\\.\COM{number}";
        return true;
    }

    private static void ValidateUnixDevicePath(string endpoint, string paramName)
    {
        var relative = endpoint[5..];
        var segments = relative.Split('/');
        if (segments.Length == 0 ||
            segments.Any(s => s.Length == 0 || s is "." or "..") ||
            !relative.All(c => IsSafeEndpointAtomCharacter(c) || c is '/' or ':'))
        {
            throw new ArgumentException("invalid Unix serial-device endpoint", paramName);
        }
    }

    private static void ValidateTcpEndpoint(string endpoint, string paramName)
    {
        string host;
        string portText;
        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            var close = endpoint.IndexOf(']');
            if (close <= 1 || close + 1 >= endpoint.Length || endpoint[close + 1] != ':')
                throw new ArgumentException("invalid bracketed TCP probe endpoint", paramName);
            host = endpoint[1..close];
            portText = endpoint[(close + 2)..];
            if (!System.Net.IPAddress.TryParse(host, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("invalid IPv6 probe endpoint", paramName);
            }
        }
        else
        {
            var separator = endpoint.LastIndexOf(':');
            if (separator <= 0 || endpoint.AsSpan(0, separator).Contains(':'))
                throw new ArgumentException("invalid TCP probe endpoint", paramName);
            host = endpoint[..separator];
            portText = endpoint[(separator + 1)..];
            if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ||
                host.StartsWith("-", StringComparison.Ordinal) ||
                host.EndsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException("invalid TCP probe host", paramName);
            }
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65_535)
            throw new ArgumentException("invalid TCP probe port", paramName);
    }

    private static bool IsSafeEndpointAtomCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '+' or '-';
}
