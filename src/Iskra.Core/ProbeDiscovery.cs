using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
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
    internal const int BmpUsbVendorId = 0x1D50;
    internal const int BmpUsbProductId = 0x6018;
    private const string BmpVidPidPrefix = "VID_1D50&PID_6018";
    private const string UsbEnumRoot = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const int MaxIoRegistryBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<ProbeInfo> FindAll()
    {
        if (OperatingSystem.IsWindows()) return EnumerateWindows();
        if (OperatingSystem.IsLinux()) return FindLinux();
        if (OperatingSystem.IsMacOS()) return FindMacOs();
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
        // Never apply this heuristic to Linux/macOS: their native adapters
        // expose the USB interface number, and guessing would turn an
        // unidentified serial endpoint into a flash-capable GDB endpoint.
        if (!OperatingSystem.IsWindows() || all.Count == 0) return named;
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

    /// <summary>
    /// Produces the stable physical key used by the machine-wide probe lock.
    /// USB VID/PID and serial take precedence; an OS physical instance is the
    /// fail-safe fallback, followed by a canonical endpoint only when discovery
    /// cannot provide hardware metadata.
    /// </summary>
    public static string ResolveProbeLockIdentity(string endpoint, string? knownSerial = null)
    {
        var normalizedEndpoint = CanonicalizeEndpointAlias(endpoint);
        if (!string.IsNullOrWhiteSpace(knownSerial))
            return BmpSerialIdentity(knownSerial);

        try
        {
            var probe = FindAll().FirstOrDefault(candidate =>
                string.Equals(
                    CanonicalizeEndpointAlias(candidate.PortName),
                    normalizedEndpoint,
                    EndpointComparison()));
            if (probe is not null) return ProbeLockIdentity(probe);
        }
        catch
        {
            // Endpoint identity remains a fail-closed compatibility fallback
            // for explicit remote/manual transports and discovery failures.
        }

        return $"endpoint:{normalizedEndpoint}";
    }

    internal static string ProbeLockIdentity(ProbeInfo probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!string.IsNullOrWhiteSpace(probe.SerialNumber))
            return BmpSerialIdentity(probe.SerialNumber);

        if (!string.IsNullOrWhiteSpace(probe.DeviceInstanceId))
        {
            var instance = NormalizeIdentityComponent(probe.DeviceInstanceId);
            return $"usb:{BmpUsbVendorId:x4}:{BmpUsbProductId:x4}:instance:{instance}";
        }

        return $"endpoint:{CanonicalizeEndpointAlias(probe.PortName)}";
    }

    internal static string CanonicalizeEndpointAlias(string endpoint)
    {
        var normalized = GdbCommandBuilder.NormalizeProbeEndpoint(endpoint);
        if (normalized.StartsWith(@"\\.\COM", StringComparison.OrdinalIgnoreCase))
            return normalized.ToUpperInvariant();

        if (!normalized.StartsWith("/dev/", StringComparison.Ordinal))
            return normalized.ToLowerInvariant();

        var fullPath = Path.GetFullPath(normalized);
        try
        {
            var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
            if (target is not null) fullPath = Path.GetFullPath(target.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }
        return fullPath;
    }

    private static StringComparison EndpointComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string BmpSerialIdentity(string serial) =>
        $"usb:{BmpUsbVendorId:x4}:{BmpUsbProductId:x4}:serial:{NormalizeIdentityComponent(serial)}";

    private static string NormalizeIdentityComponent(string value) =>
        value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

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
    /// Enumerates official Black Magic Probe interfaces on macOS. Production
    /// reads the native IOUSB registry through <c>/usr/sbin/ioreg</c> and binds
    /// each callout device to VID/PID, interface number, and serial/location
    /// identity. The filename convention is retained only for an injected test
    /// root and is never used by the production <c>/dev</c> path.
    /// </summary>
    public static IReadOnlyList<ProbeInfo> FindMacOs(string devRoot = "/dev")
    {
        if (string.Equals(Path.GetFullPath(devRoot), "/dev", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsMacOS()) return Array.Empty<ProbeInfo>();
            var snapshot = ReadMacOsIoRegistry();
            return snapshot is null
                ? Array.Empty<ProbeInfo>()
                : ParseMacOsIoRegistry(snapshot.Value, devRoot);
        }

        return FindMacOsFilenameFixture(devRoot);
    }

    private static IReadOnlyList<ProbeInfo> FindMacOsFilenameFixture(string devRoot)
    {
        var results = new List<ProbeInfo>();
        if (!Directory.Exists(devRoot)) return results;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFiles(devRoot, "cu.usbmodem*").ToArray(); }
        catch { return results; }

        foreach (var path in entries.OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var suffix = name["cu.usbmodem".Length..];
            if (suffix.Length == 0) continue;

            var interfaceDigit = suffix[^1];
            var serial = suffix[..^1];
            var probeInterface = interfaceDigit switch
            {
                '1' => ProbeInterface.Gdb,
                '3' => ProbeInterface.Uart,
                _ => ProbeInterface.Unknown,
            };

            results.Add(new ProbeInfo(
                PortName: path,
                FriendlyName: probeInterface switch
                {
                    ProbeInterface.Gdb => "Black Magic GDB Server",
                    ProbeInterface.Uart => "Black Magic UART",
                    _ => name,
                },
                DeviceInstanceId: path,
                Interface: probeInterface,
                SerialNumber: string.IsNullOrEmpty(serial) ? null : serial));
        }

        return results;
    }

    internal static IReadOnlyList<ProbeInfo> ParseMacOsIoRegistry(
        ReadOnlyMemory<byte> plistBytes,
        string devRoot = "/dev")
    {
        var results = new List<ProbeInfo>();
        try
        {
            using var stream = new MemoryStream(plistBytes.ToArray(), writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaxIoRegistryBytes,
            };
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var top = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName is "array" or "dict");
            if (top is null) return results;

            if (top.Name.LocalName == "dict")
                VisitMacRegistryDictionary(top, default, devRoot, results);
            else
            {
                foreach (var dictionary in top.Elements().Where(e => e.Name.LocalName == "dict"))
                    VisitMacRegistryDictionary(dictionary, default, devRoot, results);
            }
        }
        catch (InvalidDataException) { }
        catch (XmlException) { }
        catch (IOException) { }

        return results
            .GroupBy(p => CanonicalizeEndpointAlias(p.PortName), EndpointComparer())
            .Select(group => group.First())
            .OrderBy(p => p.PortName, StringComparer.Ordinal)
            .ToList();
    }

    private static void VisitMacRegistryDictionary(
        XElement dictionary,
        MacUsbContext inherited,
        string devRoot,
        List<ProbeInfo> results)
    {
        var properties = ReadPlistDictionary(dictionary);
        var context = inherited with
        {
            VendorId = PlistInteger(properties, "idVendor") ?? inherited.VendorId,
            ProductId = PlistInteger(properties, "idProduct") ?? inherited.ProductId,
            InterfaceNumber = PlistInteger(properties, "bInterfaceNumber") ?? inherited.InterfaceNumber,
            LocationId = PlistInteger(properties, "locationID") ?? inherited.LocationId,
            RegistryEntryId = PlistInteger(properties, "IORegistryEntryID") ?? inherited.RegistryEntryId,
            SerialNumber = PlistString(properties, "USB Serial Number") ?? inherited.SerialNumber,
            ProductName = PlistString(properties, "USB Product Name")
                ?? PlistString(properties, "Product Name")
                ?? inherited.ProductName,
        };

        var callout = PlistString(properties, "IOCalloutDevice");
        if (!string.IsNullOrWhiteSpace(callout)
            && context.VendorId == BmpUsbVendorId
            && context.ProductId == BmpUsbProductId)
        {
            var name = Path.GetFileName(callout);
            if (name.StartsWith("cu.", StringComparison.Ordinal))
            {
                var port = devRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/" + name;
                var physical = context.SerialNumber
                    ?? (context.LocationId is int location ? $"location-{location:x8}" : null)
                    ?? (context.RegistryEntryId is int entry ? $"registry-{entry:x}" : null)
                    ?? name;
                var interfaceNumber = context.InterfaceNumber is int number
                    ? number.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                var role = ClassifyUsbInterfaceNumber(interfaceNumber);
                results.Add(new ProbeInfo(
                    PortName: port,
                    FriendlyName: context.ProductName ?? "Black Magic Probe",
                    DeviceInstanceId: $"IOKit/{physical}/if-{interfaceNumber ?? "unknown"}",
                    Interface: role,
                    SerialNumber: context.SerialNumber));
            }
        }

        if (properties.TryGetValue("IORegistryEntryChildren", out var children)
            && children.Name.LocalName == "array")
        {
            foreach (var child in children.Elements().Where(e => e.Name.LocalName == "dict"))
                VisitMacRegistryDictionary(child, context, devRoot, results);
        }
    }

    private static Dictionary<string, XElement> ReadPlistDictionary(XElement dictionary)
    {
        var properties = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var elements = dictionary.Elements().ToList();
        for (var i = 0; i + 1 < elements.Count; i += 2)
        {
            if (elements[i].Name.LocalName != "key") continue;
            properties[elements[i].Value] = elements[i + 1];
        }
        return properties;
    }

    private static string? PlistString(IReadOnlyDictionary<string, XElement> properties, string key) =>
        properties.TryGetValue(key, out var value) && value.Name.LocalName == "string"
            ? value.Value
            : null;

    private static int? PlistInteger(IReadOnlyDictionary<string, XElement> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)
            || value.Name.LocalName is not ("integer" or "string"))
            return null;

        var text = value.Value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex))
            return hex;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static IEqualityComparer<string> EndpointComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [SupportedOSPlatform("macos")]
    private static ReadOnlyMemory<byte>? ReadMacOsIoRegistry()
    {
        const string ioreg = "/usr/sbin/ioreg";
        if (!File.Exists(ioreg)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ioreg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[] { "-a", "-p", "IOUSB", "-l", "-w", "0" })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C";

        try
        {
            if (!process.Start()) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxIoRegistryBytes, cts.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, cts.Token);
            process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            var snapshot = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? snapshot : null;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes)
                throw new InvalidDataException($"ioreg output exceeds {maxBytes} bytes");
            output.Write(buffer, 0, read);
        }
    }

    private readonly record struct MacUsbContext(
        int? VendorId,
        int? ProductId,
        int? InterfaceNumber,
        int? LocationId,
        int? RegistryEntryId,
        string? SerialNumber,
        string? ProductName);

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

            // Every hop here is a sysfs symlink with a relative target:
            // /sys/class/tty/ttyACM0 -> ../../devices/.../1-2:1.0/tty/ttyACM0,
            // whose device -> ../../../1-2:1.0. ResolveLinkTarget and
            // DirectoryInfo.Parent both work on the path text, so they climb
            // from /sys/class/tty instead of /sys/devices and never reach the
            // USB device's idVendor. Resolve the physical path first.
            var physicalDevicePath = ResolvePhysicalPath(devicePath);
            if (physicalDevicePath is null) continue;
            DirectoryInfo? current = new DirectoryInfo(physicalDevicePath);

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

    /// <summary>
    /// Resolves every symbolic link in <paramref name="path"/> the way the
    /// kernel does: a relative target is taken from the directory that really
    /// holds the link, and <c>..</c> climbs from where a link actually points.
    /// Returns null past 40 links, the kernel's own loop limit.
    /// </summary>
    private static string? ResolvePhysicalPath(string path)
    {
        const int maxFollowedLinks = 40;
        var unresolved = Path.IsPathFullyQualified(path)
            ? path
            : Path.Join(Directory.GetCurrentDirectory(), path);
        var root = Path.GetPathRoot(unresolved) ?? string.Empty;
        var pending = new List<string>(SplitPathComponents(unresolved[root.Length..]));
        var resolved = root;
        var followed = 0;

        while (pending.Count > 0)
        {
            var component = pending[0];
            pending.RemoveAt(0);
            if (component == ".") continue;
            if (component == "..")
            {
                // resolved never contains a link, so its textual parent is real.
                resolved = Path.GetDirectoryName(resolved) ?? root;
                continue;
            }

            var candidate = Path.Join(resolved, component);
            string? target;
            try { target = new FileInfo(candidate).LinkTarget; }
            catch (IOException) { target = null; }
            catch (UnauthorizedAccessException) { target = null; }

            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (++followed > maxFollowedLinks) return null;
            if (Path.IsPathRooted(target))
            {
                resolved = Path.GetPathRoot(target) ?? root;
                pending.InsertRange(0, SplitPathComponents(target[resolved.Length..]));
            }
            else
            {
                pending.InsertRange(0, SplitPathComponents(target));
            }
        }

        return resolved;
    }

    private static string[] SplitPathComponents(string path) =>
        path.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

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
            return File.Exists(path)
                ? BoundedFileReader.ReadUtf8String(path, 4_096).Trim()
                : null;
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
