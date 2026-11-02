namespace Iskra.Core;

/// <summary>
/// Fast file sanity checks before invoking gdb. ELF keeps a shallow magic
/// check here; Intel HEX delegates to the bounded semantic parser so this
/// compatibility API cannot reintroduce an allocating, attacker-sized line
/// read alongside the production image validation path.
/// </summary>
public static class FirmwarePreflight
{
    public enum CheckResult { Ok, NotFound, InvalidFormat, IoError }

    public static CheckResult Check(string path, FirmwareKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return CheckResult.NotFound;
        if (!File.Exists(path)) return CheckResult.NotFound;

        try
        {
            if (new FileInfo(path).Length > FirmwareImage.MaxFirmwareFileBytes)
                return CheckResult.InvalidFormat;
        }
        catch (IOException) { return CheckResult.IoError; }
        catch (UnauthorizedAccessException) { return CheckResult.IoError; }

        return kind switch
        {
            FirmwareKind.Elf => CheckElf(path),
            FirmwareKind.Hex => CheckIntelHex(path),
            _                => CheckResult.InvalidFormat,
        };
    }

    public static string DisplayName(FirmwareKind kind) => kind switch
    {
        FirmwareKind.Hex => "HEX",
        _                => "ELF",
    };

    private static CheckResult CheckElf(string path) => ElfPreflight.Check(path) switch
    {
        ElfPreflight.CheckResult.Ok       => CheckResult.Ok,
        ElfPreflight.CheckResult.NotFound => CheckResult.NotFound,
        ElfPreflight.CheckResult.IoError  => CheckResult.IoError,
        _                                 => CheckResult.InvalidFormat,
    };

    private static CheckResult CheckIntelHex(string path) => FirmwareImage.Read(path, FirmwareKind.Hex).Status switch
    {
        FirmwareImageStatus.Ok => CheckResult.Ok,
        FirmwareImageStatus.NotFound => CheckResult.NotFound,
        FirmwareImageStatus.IoError => CheckResult.IoError,
        _ => CheckResult.InvalidFormat,
    };
}
