using Iskra.Core;

namespace Iskra.Core.Tests;

public class ProbeDiscoveryTests
{
    [Theory]
    [InlineData("Black Magic GDB Server (COM30)", ProbeInterface.Gdb)]
    [InlineData("Black Magic UART Port (COM31)",  ProbeInterface.Uart)]
    [InlineData("BMP gdb interface",              ProbeInterface.Gdb)]
    [InlineData("Some other serial device",       ProbeInterface.Unknown)]
    [InlineData("",                               ProbeInterface.Unknown)]
    [InlineData(null,                             ProbeInterface.Unknown)]
    public void ClassifyInterface_routes_by_keyword(string? friendly, ProbeInterface expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ClassifyInterface(friendly));
    }

    [Theory]
    [InlineData("Устройство с последовательным интерфейсом USB (COM30)", "VID_1D50&PID_6018&MI_00/6&abc&0&0000", ProbeInterface.Gdb)]
    [InlineData("USB Serial Device (COM31)", "VID_1D50&PID_6018&MI_02/6&abc&0&0002", ProbeInterface.Uart)]
    [InlineData("Black Magic GDB Server (COM30)", "VID_1D50&PID_6018&MI_02/6&abc&0&0002", ProbeInterface.Gdb)]
    [InlineData("USB Serial Device (COM32)", "VID_1D50&PID_6018&MI_04/6&abc&0&0004", ProbeInterface.Unknown)]
    public void ClassifyInterface_uses_usb_interface_number_when_name_is_generic(
        string? friendly,
        string? deviceInstanceId,
        ProbeInterface expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ClassifyInterface(friendly, deviceInstanceId));
    }

    [Theory]
    [InlineData("COM30", 30)]
    [InlineData("COM3",  3)]
    [InlineData("COM",   int.MaxValue)]
    [InlineData("",      int.MaxValue)]
    public void ParseComNumber_extracts_trailing_digits(string port, int expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ParseComNumber(port));
    }

    [Theory]
    [InlineData("BMP123456", null, "BMP123456")]
    [InlineData("6&2b8a123&0&0000", "6&2b8a123&0", "6&2b8a123&0")]
    [InlineData("", "parent-prefix", "parent-prefix")]
    [InlineData(null, null, null)]
    public void StableSerialFromInstanceName_prefers_real_usb_serial_then_parent_prefix(
        string? instanceName,
        string? parentPrefix,
        string? expected)
    {
        Assert.Equal(expected, ProbeDiscovery.StableSerialFromInstanceName(instanceName, parentPrefix));
    }

    [Fact]
    public void Probe_lock_identity_uses_vid_pid_and_normalized_serial_before_endpoint()
    {
        var first = new ProbeInfo(
            "COM30", "BMP", "instance-a", ProbeInterface.Gdb, " bmp-001 ");
        var reenumerated = new ProbeInfo(
            "COM77", "BMP", "instance-b", ProbeInterface.Gdb, "BMP-001");

        Assert.Equal(
            "usb:1d50:6018:serial:BMP-001",
            ProbeDiscovery.ProbeLockIdentity(first));
        Assert.Equal(
            ProbeDiscovery.ProbeLockIdentity(first),
            ProbeDiscovery.ProbeLockIdentity(reenumerated));
    }

    [Fact]
    public void Windows_com_endpoint_aliases_have_one_canonical_lock_identity()
    {
        Assert.Equal(
            ProbeDiscovery.ResolveProbeLockIdentity("com030"),
            ProbeDiscovery.ResolveProbeLockIdentity(@"\\.\COM30"));
    }

    [Theory]
    [InlineData("00", ProbeInterface.Gdb)]
    [InlineData("02", ProbeInterface.Uart)]
    [InlineData("01", ProbeInterface.Unknown)]
    [InlineData(null, ProbeInterface.Unknown)]
    public void ClassifyUsbInterfaceNumber_maps_official_bmp_interfaces(
        string? number, ProbeInterface expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ClassifyUsbInterfaceNumber(number));
    }

    [Fact]
    public void FindLinux_reads_bmp_metadata_and_ignores_other_usb_serial_devices()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-sysfs-{Guid.NewGuid():N}");
        var sysTty = Path.Combine(root, "sys", "class", "tty");
        var dev = Path.Combine(root, "dev");
        try
        {
            CreateLinuxTtyFixture(sysTty, "ttyACM0", "1d50", "6018", "00", "BMP-SERIAL");
            CreateLinuxTtyFixture(sysTty, "ttyACM1", "1D50", "6018", "02", "BMP-SERIAL");
            CreateLinuxTtyFixture(sysTty, "ttyUSB0", "1234", "5678", "00", "OTHER");

            var probes = ProbeDiscovery.FindLinux(sysTty, dev);

            Assert.Equal(2, probes.Count);
            Assert.Equal(Path.Combine(dev, "ttyACM0"), probes[0].PortName);
            Assert.Equal(ProbeInterface.Gdb, probes[0].Interface);
            Assert.Equal("BMP-SERIAL", probes[0].SerialNumber);
            Assert.Equal(ProbeInterface.Uart, probes[1].Interface);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindLinux_rejects_oversized_sysfs_metadata_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-sysfs-{Guid.NewGuid():N}");
        var sysTty = Path.Combine(root, "sys", "class", "tty");
        try
        {
            CreateLinuxTtyFixture(
                sysTty,
                "ttyACM0",
                new string('1', 4_097),
                "6018",
                "00",
                "BMP-SERIAL");

            Assert.Empty(ProbeDiscovery.FindLinux(sysTty, Path.Combine(root, "dev")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindAll_returns_empty_or_real_list_without_throwing()
    {
        // On Windows we may or may not have BMP plugged in; on non-Windows we get empty.
        var result = ProbeDiscovery.FindAll();
        Assert.NotNull(result);
    }

    [UnixOnlyFact("the kernel's sysfs layout needs POSIX symlinks and ':' in names")]
    public void FindLinux_resolves_relative_sysfs_symlinks_like_the_kernel()
    {
        // On a real kernel /sys/class/tty/ttyACM0 is a symlink to
        // ../../devices/.../1-2:1.0/tty/ttyACM0, and its device entry is a
        // symlink to ../../../1-2:1.0. Resolving those targets against their
        // textual parent lands outside /sys/devices, which hid every connected
        // probe on Linux while the plain-directory fixture above still passed.
        var root = Path.Combine(Path.GetTempPath(), $"iskra-sysfs-{Guid.NewGuid():N}");
        var sys = Path.Combine(root, "sys");
        try
        {
            CreateKernelSysfsUsbDevice(sys, "usb1/1-2", "1d50", "6018", "7BB180B4",
                ("1-2:1.0", "00", "ttyACM0"), ("1-2:1.2", "02", "ttyACM1"));
            CreateKernelSysfsUsbDevice(sys, "usb1/1-3", "0403", "6001", "FTDI-SERIAL",
                ("1-3:1.0", "00", "ttyUSB0"));

            var probes = ProbeDiscovery.FindLinux(Path.Combine(sys, "class", "tty"), "/dev");

            Assert.Equal(2, probes.Count);
            Assert.Equal("/dev/ttyACM0", probes[0].PortName);
            Assert.Equal(ProbeInterface.Gdb, probes[0].Interface);
            Assert.Equal("7BB180B4", probes[0].SerialNumber);
            Assert.EndsWith("/devices/pci0000:00/usb1/1-2/1-2:1.0", probes[0].DeviceInstanceId, StringComparison.Ordinal);
            Assert.Equal("/dev/ttyACM1", probes[1].PortName);
            Assert.Equal(ProbeInterface.Uart, probes[1].Interface);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Builds one USB device the way sysfs presents it: real directories under
    /// devices/, reached only through relative symlinks from class/tty.
    /// </summary>
    private static void CreateKernelSysfsUsbDevice(
        string sys,
        string usbPath,
        string vendor,
        string product,
        string serial,
        params (string Interface, string Number, string Tty)[] interfaces)
    {
        var device = Path.Combine(sys, "devices", "pci0000:00", usbPath);
        var classTty = Path.Combine(sys, "class", "tty");
        Directory.CreateDirectory(classTty);
        foreach (var (iface, number, tty) in interfaces)
        {
            var interfaceDir = Path.Combine(device, iface);
            var ttyDir = Path.Combine(interfaceDir, "tty", tty);
            Directory.CreateDirectory(ttyDir);
            File.WriteAllText(Path.Combine(interfaceDir, "bInterfaceNumber"), number + "\n");
            Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), $"../../../{iface}");
            Directory.CreateSymbolicLink(
                Path.Combine(classTty, tty),
                $"../../devices/pci0000:00/{usbPath}/{iface}/tty/{tty}");
        }
        File.WriteAllText(Path.Combine(device, "idVendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(device, "idProduct"), product + "\n");
        File.WriteAllText(Path.Combine(device, "serial"), serial + "\n");
    }

    private static void CreateLinuxTtyFixture(
        string sysTty,
        string ttyName,
        string vendor,
        string product,
        string interfaceNumber,
        string serial)
    {
        var device = Path.Combine(sysTty, ttyName, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "idVendor"), vendor);
        File.WriteAllText(Path.Combine(device, "idProduct"), product);
        File.WriteAllText(Path.Combine(device, "bInterfaceNumber"), interfaceNumber);
        File.WriteAllText(Path.Combine(device, "serial"), serial);
        File.WriteAllText(Path.Combine(device, "product"), "Black Magic Probe");
    }
}
