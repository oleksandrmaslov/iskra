namespace FlashlightApp.Core;

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
    string? DbPath)
{
    public static FlashOptions? Parse(string[] args)
    {
        string? elf = null, port = null, product = null, op = null, batch = null;
        string? gdbPath = null, dbPath = null;
        string? target = null;
        string station = Environment.MachineName;
        string fwVersion = "unknown";
        string fwSha = "unknown";
        int flashKb = 0;
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
                case "--firmware-version": fwVersion = Next(args, ref i) ?? fwVersion; break;
                case "--firmware-sha256":  fwSha = Next(args, ref i) ?? fwSha; break;
                case "--gdb-path":      gdbPath = Next(args, ref i); break;
                case "--db-path":       dbPath = Next(args, ref i); break;
                default:                return null;
            }
        }

        if (elf is null || port is null || product is null || op is null || batch is null
            || target is null || flashKb <= 0)
            return null;

        return new FlashOptions(
            elf, port, power, freq, connectReset,
            product, op, batch, station,
            target, flashKb, fwVersion, fwSha,
            gdbPath, dbPath);
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) return null;
        return args[++i];
    }
}
