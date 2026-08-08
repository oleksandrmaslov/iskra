namespace Iskra.Core;

public enum PowerMode { External, Probe }

public sealed record FlashOptions(
    string ElfPath,
    string Port,
    PowerMode Power,
    int BmpFrequencyHz,
    bool ConnectUnderReset,
    string Product,
    string Operator,
    string Batch,
    string StationId,
    string TargetBmpMatch,
    int TargetFlashKb,
    string FirmwareVersion,
    string FirmwareSha256,
    string? GdbPath,
    string? DbPath,
    FirmwareKind FirmwareKind = FirmwareKind.Elf,
    int TimeoutSeconds = 15,
    // Optional target memory map, populated from the signed catalog. Manual
    // --target mode leaves these null, which limits validation to total size.
    ulong? TargetFlashOrigin = null,
    ulong? TargetRamOrigin = null,
    int? TargetRamKb = null)
{
    /// <summary>
    /// The subset of the catalog target descriptor that firmware range checking
    /// needs, rebuilt from the flattened options the CLI parses.
    /// </summary>
    public TargetDescriptor ToTargetDescriptor() => new(
        BmpMatch: TargetBmpMatch,
        PartNumber: string.IsNullOrWhiteSpace(Product) ? TargetBmpMatch : Product,
        FlashKb: TargetFlashKb,
        FlashOrigin: TargetFlashOrigin,
        RamOrigin: TargetRamOrigin,
        RamKb: TargetRamKb);

    public static FlashOptions? Parse(string[] args)
    {
        string? elf = null, port = null, product = null, op = null, batch = null;
        string? gdbPath = null, dbPath = null;
        string? target = null;
        ulong? flashOrigin = null, ramOrigin = null;
        int? ramKb = null;
        string station = Environment.MachineName;
        string fwVersion = "unknown";
        string fwSha = "unknown";
        FirmwareKind firmwareKind = FirmwareKind.Elf;
        int flashKb = 0;
        int timeoutSeconds = 15;
        PowerMode power = PowerMode.External;
        int freq = 1_000_000;
        bool connectReset = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elf":           elf = Next(args, ref i); break;
                case "--port":          port = Next(args, ref i); break;
                case "--power":
                    var p = Next(args, ref i)?.ToLowerInvariant();
                    if (p == "probe") power = PowerMode.Probe;
                    else if (p == "external") power = PowerMode.External;
                    else return null;
                    break;
                case "--freq":
                    if (!int.TryParse(Next(args, ref i), out freq)) return null;
                    break;
                case "--connect-reset": connectReset = true; break;
                case "--product":       product = Next(args, ref i); break;
                case "--operator":      op = Next(args, ref i); break;
                case "--batch":         batch = Next(args, ref i); break;
                case "--station-id":    station = Next(args, ref i) ?? station; break;
                case "--target":        target = Next(args, ref i); break;
                case "--flash-kb":
                    if (!int.TryParse(Next(args, ref i), out flashKb)) return null;
                    break;
                case "--flash-origin":
                    if (!TryParseAddress(Next(args, ref i), out flashOrigin)) return null;
                    break;
                case "--ram-origin":
                    if (!TryParseAddress(Next(args, ref i), out ramOrigin)) return null;
                    break;
                case "--ram-kb":
                    if (!int.TryParse(Next(args, ref i), out var parsedRamKb) || parsedRamKb <= 0) return null;
                    ramKb = parsedRamKb;
                    break;
                case "--firmware-version": fwVersion = Next(args, ref i) ?? fwVersion; break;
                case "--firmware-sha256":  fwSha = Next(args, ref i) ?? fwSha; break;
                case "--firmware-kind":
                    if (!TryParseFirmwareKind(Next(args, ref i), out firmwareKind)) return null;
                    break;
                case "--timeout":
                    if (!int.TryParse(Next(args, ref i), out timeoutSeconds) || timeoutSeconds <= 0) return null;
                    break;
                case "--gdb-path":      gdbPath = Next(args, ref i); break;
                case "--db-path":       dbPath = Next(args, ref i); break;
                default:                return null;
            }
        }

        if (elf is null || port is null || product is null || op is null || batch is null
            || target is null || flashKb <= 0)
            return null;

        // A half-declared RAM window would silently widen the accepted address
        // space, so require both halves or neither. Mirrors the catalog rule.
        if ((ramOrigin is null) != (ramKb is null)) return null;

        return new FlashOptions(
            elf, port, power, freq, connectReset,
            product, op, batch, station,
            target, flashKb, fwVersion, fwSha,
            gdbPath, dbPath, firmwareKind, timeoutSeconds,
            flashOrigin, ramOrigin, ramKb);
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) return null;
        return args[++i];
    }

    /// <summary>
    /// Memory addresses are hex with or without the 0x prefix, matching the
    /// catalog form and the linker scripts they are copied from.
    /// </summary>
    private static bool TryParseAddress(string? value, out ulong? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var span = value.Trim().AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        if (span.IsEmpty) return false;

        if (!ulong.TryParse(
                span,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryParseFirmwareKind(string? value, out FirmwareKind kind)
    {
        kind = FirmwareKind.Elf;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() switch
        {
            "elf" => Set(out kind, FirmwareKind.Elf),
            "hex" => Set(out kind, FirmwareKind.Hex),
            _     => false,
        };
    }

    private static bool Set(out FirmwareKind target, FirmwareKind value)
    {
        target = value;
        return true;
    }
}
