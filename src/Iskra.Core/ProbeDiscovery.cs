using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Iskra.Core;

public enum ProbeInterface { Gdb, Uart, Unknown }

public sealed record ProbeInfo(
    string PortName,
    string? FriendlyName,
    string DeviceInstanceId,
    ProbeInterface Interface,
    string? SerialNumber = null);

/// <summary>
/// Locates Black Magic Probe COM ports on Windows by walking the USB device tree
/// in the registry. BMP exposes two CDC-ACM serial interfaces; we identify the
/// GDB one by FriendlyName and fall back to "lowest COM number" if naming differs.
/// VID 0x1D50, PID 0x6018 is the official Black Magic Probe USB ID.
/// </summary>
public static class ProbeDiscovery
{
    private const string BmpVidPidPrefix = "VID_1D50&PID_6018";
    private const string UsbEnumRoot = @"SYSTEM\CurrentControlSet\Enum\USB";

    public static IReadOnlyList<ProbeInfo> FindAll()
    {
        if (OperatingSystem.IsWindows()) return EnumerateWindows();
        if (OperatingSystem.IsLinux()) return FindLinux();
        // macOS flashing already accepts an explicit /dev/cu.usbmodem* path;
        // metadata-backed auto-discovery is a separate platform adapter.
        return Array.Empty<ProbeInfo>();
    }

    public static IReadOnlyList<ProbeInfo> FindGdbPorts()
    {
        var all = FindAll();
        var named = all.Where(p => p.Interface == ProbeInterface.Gdb).ToList();
        if (named.Count > 0) return named;

        // Fallback: Windows 10/11 uses the generic CDC-ACM driver, which gives
        // both BMP interfaces the same nondescript FriendlyName. BMP firmware
        // convention is GDB on the lower-numbered port of each consecutive pair;
        // sort and keep one port per pair.
        if (all.Count == 0) return all;
        var sorted = all.OrderBy(p => ParseComNumber(p.PortName)).ToList();
        return sorted.Where((_, i) => i % 2 == 0).ToList();
    }

    /// <summary>
    /// Pure: classify a FriendlyName string into GDB / UART / Unknown. Tested directly.
    /// </summary>
    public static ProbeInterface ClassifyInterface(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return ProbeInterface.Unknown;
        var s = friendlyName;
        if (s.Contains("GDB", StringComparison.OrdinalIgnoreCase)) return ProbeInterface.Gdb;
        if (s.Contains("UART", StringComparison.OrdinalIgnoreCase)) return ProbeInterface.Uart;
        return ProbeInterface.Unknown;
    }

    public static ProbeInterface ClassifyInterface(string? friendlyName, string? deviceInstanceId)
    {
        var byName = ClassifyInterface(friendlyName);
        if (byName != ProbeInterface.Unknown) return byName;

        // Generic usbser.sys names on Windows 10/11 often say only
        // "USB Serial Device (COMx)". Official BMP exposes GDB on MI_00 and
        // UART on MI_02, so use the interface number when the friendly name is
        // not descriptive.
        if (string.IsNullOrEmpty(deviceInstanceId)) return ProbeInterface.Unknown;
        if (deviceInstanceId.Contains("&MI_00", StringComparison.OrdinalIgnoreCase)) return ProbeInterface.Gdb;
        if (deviceInstanceId.Contains("&MI_02", StringComparison.OrdinalIgnoreCase)) return ProbeInterface.Uart;
        return ProbeInterface.Unknown;
    }

    public static int ParseComNumber(string portName)
    {
        // "COM30" → 30; non-numeric tail → int.MaxValue (sorts last)
        if (string.IsNullOrEmpty(portName)) return int.MaxValue;
        int i = 0;
        while (i < portName.Length && !char.IsDigit(portName[i])) i++;
        return int.TryParse(portName.AsSpan(i), out var n) ? n : int.MaxValue;
    }

    public static string? FindSerialForPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return null;
        return FindAll()
            .FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))
            ?.SerialNumber;
    }

    public static string? StableSerialFromInstanceName(string? instanceName, string? parentIdPrefix = null)
    {
        if (!string.IsNullOrWhiteSpace(instanceName) &&
            !instanceName.Contains('&', StringComparison.Ordinal) &&
            !instanceName.Contains('\\', StringComparison.Ordinal))
            return instanceName;
        if (!string.IsNullOrWhiteSpace(parentIdPrefix))
            return parentIdPrefix;
        return null;
    }

    /// <summary>
    /// Enumerates official Black Magic Probe CDC interfaces through Linux
    /// sysfs. The optional roots make the platform adapter deterministic in
    /// tests; production uses <c>/sys/class/tty</c> and <c>/dev</c>.
    /// </summary>
    public static IReadOnlyList<ProbeInfo> FindLinux(
        string sysClassTtyRoot = "/sys/class/tty",
        string devRoot = "/dev")
    {
        var results = new List<ProbeInfo>();
        if (!Directory.Exists(sysClassTtyRoot)) return results;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateDirectories(sysClassTtyRoot).ToArray(); }
        catch { return results; }

        foreach (var entryPath in entries)
        {
            var ttyName = Path.GetFileName(entryPath);
            if (!ttyName.StartsWith("ttyACM", StringComparison.Ordinal)
                && !ttyName.StartsWith("ttyUSB", StringComparison.Ordinal))
                continue;

            var devicePath = Path.Combine(entryPath, "device");
            if (!Directory.Exists(devicePath)) continue;

            DirectoryInfo? current;
            try
            {
                var device = new DirectoryInfo(devicePath);
                current = device.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? device;
            }
            catch
            {
                current = new DirectoryInfo(devicePath);
            }

            string? vendor = null;
            string? productId = null;
            string? interfaceNumber = null;
            string? serial = null;
            string? productName = null;
            var instancePath = current.FullName;

            // USB interface metadata and device metadata commonly live on
            // adjacent ancestor levels, so walk a small bounded chain.
            for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
            {
                interfaceNumber ??= ReadSysfsValue(current.FullName, "bInterfaceNumber");
                vendor ??= ReadSysfsValue(current.FullName, "idVendor");
                productId ??= ReadSysfsValue(current.FullName, "idProduct");
                serial ??= ReadSysfsValue(current.FullName, "serial");
                productName ??= ReadSysfsValue(current.FullName, "product");
            }

            if (!string.Equals(vendor, "1d50", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(productId, "6018", StringComparison.OrdinalIgnoreCase))
                continue;

            var role = ClassifyUsbInterfaceNumber(interfaceNumber);
            results.Add(new ProbeInfo(
                PortName: Path.Combine(devRoot, ttyName),
                FriendlyName: productName ?? "Black Magic Probe",
                DeviceInstanceId: instancePath,
                Interface: role,
                SerialNumber: string.IsNullOrWhiteSpace(serial) ? null : serial));
        }

        return results
            .OrderBy(p => p.PortName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Official BMP exposes GDB on USB interface 00 and UART on 02.</summary>
    public static ProbeInterface ClassifyUsbInterfaceNumber(string? interfaceNumber)
    {
        if (string.Equals(interfaceNumber?.Trim(), "00", StringComparison.OrdinalIgnoreCase))
            return ProbeInterface.Gdb;
        if (string.Equals(interfaceNumber?.Trim(), "02", StringComparison.OrdinalIgnoreCase))
            return ProbeInterface.Uart;
        return ProbeInterface.Unknown;
    }

    private static string? ReadSysfsValue(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<ProbeInfo> EnumerateWindows()
    {
        var results = new List<ProbeInfo>();
        var activeComPorts = ActiveWindowsComPorts();
        using var enumKey = Registry.LocalMachine.OpenSubKey(UsbEnumRoot);
        if (enumKey is null) return results;

        foreach (var vidPidName in enumKey.GetSubKeyNames())
        {
            if (!vidPidName.StartsWith(BmpVidPidPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            using var vidPidKey = enumKey.OpenSubKey(vidPidName);
            if (vidPidKey is null) continue;

            foreach (var instanceName in vidPidKey.GetSubKeyNames())
            {
                using var instanceKey = vidPidKey.OpenSubKey(instanceName);
                if (instanceKey is null) continue;

                var friendly = instanceKey.GetValue("FriendlyName") as string;
                using var devParams = instanceKey.OpenSubKey("Device Parameters");
                var port = devParams?.GetValue("PortName") as string;

                if (port is null) continue;
                if (activeComPorts.Count > 0 && !activeComPorts.Contains(port))
                    continue;

                var deviceInstanceId = $"{vidPidName}/{instanceName}";

                var parentIdPrefix = instanceKey.GetValue("ParentIdPrefix") as string;
                var serial = StableSerialFromInstanceName(instanceName, parentIdPrefix)
                    ?? TryFindParentSerial(enumKey, instanceName);

                results.Add(new ProbeInfo(
                    PortName: port,
                    FriendlyName: friendly,
                    DeviceInstanceId: deviceInstanceId,
                    Interface: ClassifyInterface(friendly, deviceInstanceId),
                    SerialNumber: serial));
            }
        }
        return results;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryFindParentSerial(RegistryKey enumKey, string interfaceInstanceName)
    {
        try
        {
            using var parentKey = enumKey.OpenSubKey(BmpVidPidPrefix);
            if (parentKey is null) return null;
            foreach (var parentInstanceName in parentKey.GetSubKeyNames())
            {
                using var parentInstanceKey = parentKey.OpenSubKey(parentInstanceName);
                var parentPrefix = parentInstanceKey?.GetValue("ParentIdPrefix") as string;
                if (!string.IsNullOrWhiteSpace(parentPrefix) &&
                    interfaceInstanceName.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
                    return StableSerialFromInstanceName(parentInstanceName, parentPrefix);
            }
        }
        catch
        {
            // Registry shape differs between Windows driver stacks; serial is
            // useful but non-critical, so fall back to null.
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<string> ActiveWindowsComPorts()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return ports;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    ports.Add(port);
            }
        }
        catch
        {
            // If the live COM map cannot be read, fall back to the USB enum data
            // instead of hiding a real probe.
        }
        return ports;
    }
}
