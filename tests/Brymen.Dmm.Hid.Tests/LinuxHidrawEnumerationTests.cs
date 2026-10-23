using System.Text;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Enumeração do hidraw sobre uma árvore sysfs falsa. A árvore "real" reproduz a do kernel
/// (<c>class/hidraw/hidrawN</c> → <c>devices/.../hidraw/hidrawN</c>, <c>device</c> → dispositivo HID, pais USB);
/// no Windows os ':' dos nomes viram '-'.
/// </summary>
public sealed class LinuxHidrawEnumerationTests : IDisposable
{
    private const string Serial = "£⑉"; // lixo não-ASCII, como o serial do BC-86X real

    private readonly TempDirectory _temp = new();

    private string Sys => Path.Combine(_temp.Path, "sys");

    private string Dev => Path.Combine(_temp.Path, "dev");

    public void Dispose() => _temp.Dispose();

    [SymlinkFact]
    public void FindsTheCableWithUsbParentAttributes()
    {
        AddLinkedDevice(1, "1-2", 0x0820, 0x0001, "Brymen Superior DMM", usb: ("Brymen", "Superior DMM", Serial, "8603"));
        AddLinkedDevice(0, "1-1", 0x046D, 0xC31C, "Logitech USB Keyboard", usb: ("Logitech", "USB Keyboard", null, "6400"));

        var provider = new LinuxHidrawDeviceProvider(Sys, Dev);
        var device = Assert.Single(provider.Enumerate(0x0820, 0x0001));

        Assert.Equal(Path.Combine(Dev, "hidraw1"), device.Path);
        Assert.Equal((ushort)0x0820, device.VendorId);
        Assert.Equal((ushort)0x0001, device.ProductId);
        Assert.Equal((ushort)0x8603, device.ReleaseNumber);
        Assert.Equal("Brymen", device.Manufacturer);
        Assert.Equal("Superior DMM", device.Product);
        Assert.Equal(Serial, device.SerialNumber);
        Assert.Equal(2, provider.EnumerateAll().Count);
    }

    [SymlinkFact]
    public void ReadsTheReportLayoutThroughTheDeviceLink()
    {
        AddLinkedDevice(3, "1-4", 0x0820, 0x0001, "Brymen Superior DMM", usb: ("Brymen", "Superior DMM", null, "8603"),
            descriptor: [0x85, 0x05, 0x75, 0x08, 0x95, 0x0A, 0x81, 0x02, 0x95, 0x02, 0x91, 0x02]);

        var provider = new LinuxHidrawDeviceProvider(Sys, Dev);
        var device = Assert.Single(provider.Enumerate(0x0820, 0x0001));

        Assert.Equal(new HidReportLayout(10, 2, 0, true), provider.ReadReportLayout(device.Path));
    }

    [Fact]
    public void WithoutUsbParentFallsBackToHidNameAndUniq()
    {
        AddFlatDevice("hidraw0", "HID_ID=0003:00000820:00000001\nHID_NAME=Brymen Superior DMM\nHID_PHYS=usb-0000:00:14.0-2/input0\nHID_UNIQ=abc123\n");

        var device = Assert.Single(new LinuxHidrawDeviceProvider(Sys, Dev).Enumerate(0x0820, 0x0001));

        Assert.Equal(Path.Combine(Dev, "hidraw0"), device.Path);
        Assert.Null(device.Manufacturer);
        Assert.Null(device.ReleaseNumber);
        Assert.Equal("Brymen Superior DMM", device.Product);
        Assert.Equal("abc123", device.SerialNumber);
    }

    [Fact]
    public void OrdersNumericallyAndUsesDevName()
    {
        AddFlatDevice("hidraw10", "HID_ID=0003:00000820:00000001\nHID_NAME=B\nHID_UNIQ=\n", devName: "hidraw10");
        AddFlatDevice("hidraw2", "HID_ID=0003:00000820:00000001\nHID_NAME=A\n", devName: "custom/hidraw2");

        var devices = new LinuxHidrawDeviceProvider(Sys, Dev).Enumerate(0x0820, 0x0001);

        Assert.Equal([Path.Combine(Dev, "custom/hidraw2"), Path.Combine(Dev, "hidraw10")], devices.Select(d => d.Path));
        Assert.Null(devices[1].SerialNumber); // HID_UNIQ vazio
    }

    [Fact]
    public void IgnoresEntriesWithoutValidHidId()
    {
        AddFlatDevice("hidraw0", "HID_NAME=sem id\n");
        AddFlatDevice("hidraw1", "HID_ID=lixo\n");
        AddFlatDevice("hidraw2", "HID_ID=0003:00010820:00000001\n"); // vendor fora de 16 bits
        Directory.CreateDirectory(Path.Combine(Sys, "class", "hidraw", "hidraw3")); // sem device/uevent

        Assert.Empty(new LinuxHidrawDeviceProvider(Sys, Dev).EnumerateAll());
    }

    [Fact]
    public void MissingSysfsYieldsNoDevices()
    {
        Assert.Empty(new LinuxHidrawDeviceProvider(Path.Combine(_temp.Path, "nada"), Dev).Enumerate(0x0820, 0x0001));
    }

    [Fact]
    public void ReportLayoutFallsBackToBu86xDefaults()
    {
        AddFlatDevice("hidraw0", "HID_ID=0003:00000820:00000001\n", descriptor: [0x75, 0x08]); // sem itens principais
        var provider = new LinuxHidrawDeviceProvider(Sys, Dev);

        Assert.Equal(HidReportLayout.Bu86xDefault, provider.ReadReportLayout(Path.Combine(Dev, "hidraw0")));
        Assert.Equal(HidReportLayout.Bu86xDefault, provider.ReadReportLayout(Path.Combine(Dev, "hidraw9")));
    }

    [Fact]
    public void ReportLayoutComesFromTheSysfsDescriptor()
    {
        AddFlatDevice("hidraw0", "HID_ID=0003:00000820:00000001\n", descriptor: HidReportDescriptorTests.Bu86xLikeDescriptor);

        var layout = new LinuxHidrawDeviceProvider(Sys, Dev).ReadReportLayout(Path.Combine(Dev, "hidraw0"));

        Assert.Equal(new HidReportLayout(8, 3, 0, false), layout);
    }

    [Theory]
    [InlineData("0003:00000820:00000001", true, 0x0820, 0x0001)]
    [InlineData("0005:0000046D:0000B01A", true, 0x046D, 0xB01A)]
    [InlineData(" 0003:00000820:00000001\n", true, 0x0820, 0x0001)]
    [InlineData("0003:00000820", false, 0, 0)]
    [InlineData("0003:XYZ:00000001", false, 0, 0)]
    [InlineData("0003:00000820:00010000", false, 0, 0)]
    public void ParsesHidId(string value, bool ok, int vendorId, int productId)
    {
        Assert.Equal(ok, LinuxHidrawDeviceProvider.TryParseHidId(value, out ushort vid, out ushort pid));
        Assert.Equal(vendorId, vid);
        Assert.Equal(productId, pid);
    }

    [Fact]
    public void OpenRequiresLinux()
    {
        if (OperatingSystem.IsLinux())
            return;
        var provider = new LinuxHidrawDeviceProvider(Sys, Dev);

        Assert.Throws<PlatformNotSupportedException>(() => provider.Open(new HidDeviceInfo(Path.Combine(Dev, "hidraw0"), 0x0820, 0x0001)));
    }

    private static string Name(string sysfsName) => OperatingSystem.IsWindows() ? sysfsName.Replace(':', '-') : sysfsName;

    /// <summary>Dispositivo no layout do kernel, com links relativos.</summary>
    private void AddLinkedDevice(int index, string usbPort, ushort vid, ushort pid, string hidName,
        (string? Manufacturer, string? Product, string? Serial, string? Bcd) usb, byte[]? descriptor = null)
    {
        string usbDevice = Path.Combine(Sys, "devices", Name("pci0000:00"), Name("0000:00:14.0"), "usb1", usbPort);
        string iface = Path.Combine(usbDevice, Name($"{usbPort}:1.0"));
        string hidDevice = Path.Combine(iface, Name($"0003:{vid:X4}:{pid:X4}.{index + 1:X4}"));
        string hidraw = Path.Combine(hidDevice, "hidraw", $"hidraw{index}");
        Directory.CreateDirectory(hidraw);

        Write(usbDevice, "idVendor", $"{vid:x4}\n");
        Write(usbDevice, "idProduct", $"{pid:x4}\n");
        Write(usbDevice, "manufacturer", usb.Manufacturer is null ? null : usb.Manufacturer + "\n");
        Write(usbDevice, "product", usb.Product is null ? null : usb.Product + "\n");
        Write(usbDevice, "serial", usb.Serial is null ? null : usb.Serial + "\n");
        Write(usbDevice, "bcdDevice", usb.Bcd is null ? null : usb.Bcd + "\n");
        Write(iface, "bInterfaceClass", "03\n");
        Write(hidDevice, "uevent", $"DRIVER=hid-generic\nHID_ID=0003:{vid:X8}:{pid:X8}\nHID_NAME={hidName}\nHID_PHYS=usb-0000:00:14.0-{usbPort}/input0\nHID_UNIQ=\nMODALIAS=hid:b0003g0001v{vid:X8}p{pid:X8}\n");
        File.WriteAllBytes(Path.Combine(hidDevice, "report_descriptor"), descriptor ?? HidReportDescriptorTests.Bu86xLikeDescriptor);
        Write(hidraw, "uevent", $"MAJOR=242\nMINOR={index}\nDEVNAME=hidraw{index}\n");
        Write(hidraw, "dev", $"242:{index}\n");

        // hidrawN/device → ../../  (o dispositivo HID); class/hidraw/hidrawN → ../../devices/.../hidrawN
        Directory.CreateSymbolicLink(Path.Combine(hidraw, "device"), Path.Combine("..", ".."));
        string classDir = Path.Combine(Sys, "class", "hidraw");
        Directory.CreateDirectory(classDir);
        Directory.CreateSymbolicLink(Path.Combine(classDir, $"hidraw{index}"), Path.GetRelativePath(classDir, hidraw));
    }

    /// <summary>Entrada sem links (diretórios reais), como num sysfs sem o dispositivo USB pai.</summary>
    private void AddFlatDevice(string name, string hidUevent, string? devName = null, byte[]? descriptor = null)
    {
        string entry = Path.Combine(Sys, "class", "hidraw", name);
        string device = Path.Combine(entry, "device");
        Directory.CreateDirectory(device);
        Write(device, "uevent", hidUevent);
        if (devName is not null)
            Write(entry, "uevent", $"MAJOR=242\nMINOR=0\nDEVNAME={devName}\n");
        if (descriptor is not null)
            File.WriteAllBytes(Path.Combine(device, "report_descriptor"), descriptor);
    }

    private static void Write(string directory, string name, string? content)
    {
        if (content is not null)
            File.WriteAllText(Path.Combine(directory, name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
