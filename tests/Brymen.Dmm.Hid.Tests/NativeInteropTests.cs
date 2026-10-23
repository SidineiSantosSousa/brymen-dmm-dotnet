using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Layout das structs passadas à libc e à hid.dll/setupapi.dll e funções puras dos transportes nativos.
/// Roda em qualquer SO: nenhum teste chama código nativo.
/// </summary>
#pragma warning disable CA1416 // Só layout e funções gerenciadas dos tipos específicos de plataforma.
public class NativeInteropTests
{
    [Fact]
    public void PollFdMatchesStructPollfd()
    {
        // struct pollfd { int fd; short events; short revents; } em x64, arm64 e arm.
        Assert.Equal(8, Unsafe.SizeOf<LibC.PollFd>());
        Assert.Equal(0, (int)Marshal.OffsetOf<LibC.PollFd>(nameof(LibC.PollFd.Fd)));
        Assert.Equal(4, (int)Marshal.OffsetOf<LibC.PollFd>(nameof(LibC.PollFd.Events)));
        Assert.Equal(6, (int)Marshal.OffsetOf<LibC.PollFd>(nameof(LibC.PollFd.Revents)));
    }

    [Fact]
    public void LinuxConstantsAreTheAsmGenericOnes()
    {
        Assert.Equal(0x80802, LibC.O_RDWR | LibC.O_NONBLOCK | LibC.O_CLOEXEC); // 02004002 em octal
        Assert.Equal(0x100, LibC.O_NOCTTY); // 00000400 em octal
        Assert.Equal((20, 21, 25), (HidrawErrors.ENOTDIR, HidrawErrors.EISDIR, HidrawErrors.ENOTTY));
        Assert.Equal((1, 8, 16, 32), ((int)LibC.POLLIN, (int)LibC.POLLERR, (int)LibC.POLLHUP, (int)LibC.POLLNVAL));
        Assert.Equal((5, 11, 13, 19), (HidrawErrors.EIO, HidrawErrors.EAGAIN, HidrawErrors.EACCES, HidrawErrors.ENODEV));
    }

    [Fact]
    public void HidrawStructsMatchLinuxHidrawH()
    {
        // struct hidraw_devinfo { __u32 bustype; __s16 vendor; __s16 product; }
        Assert.Equal(8, Unsafe.SizeOf<HidrawDevInfo>());
        Assert.Equal(0, (int)Marshal.OffsetOf<HidrawDevInfo>(nameof(HidrawDevInfo.BusType)));
        Assert.Equal(4, (int)Marshal.OffsetOf<HidrawDevInfo>(nameof(HidrawDevInfo.Vendor)));
        Assert.Equal(6, (int)Marshal.OffsetOf<HidrawDevInfo>(nameof(HidrawDevInfo.Product)));

        // struct hidraw_report_descriptor { __u32 size; __u8 value[HID_MAX_DESCRIPTOR_SIZE = 4096]; }
        Assert.Equal(4096, HidrawIoctl.MaxDescriptorSize);
        Assert.Equal(4100, Unsafe.SizeOf<HidrawReportDescriptor>());
        var descriptor = default(HidrawReportDescriptor);
        Assert.Equal(4, (int)Unsafe.ByteOffset(ref Unsafe.As<HidrawReportDescriptor, byte>(ref descriptor), ref descriptor.Value[0]));
    }

    [Theory]
    // _IOC(dir, type, nr, size) = dir << 30 | size << 16 | type << 8 | nr, com _IOC_READ = 2 (asm-generic: x64, arm64, arm).
    [InlineData(HidrawIoctl.HIDIOCGRDESCSIZE, 0x80044801u, 0x01u, 4)]        // _IOR('H', 0x01, int)
    [InlineData(HidrawIoctl.HIDIOCGRDESC, 0x90044802u, 0x02u, 4100)]         // _IOR('H', 0x02, struct hidraw_report_descriptor)
    [InlineData(HidrawIoctl.HIDIOCGRAWINFO, 0x80084803u, 0x03u, 8)]          // _IOR('H', 0x03, struct hidraw_devinfo)
    [InlineData(HidrawIoctl.HIDIOCGRAWNAME_256, 0x81004804u, 0x04u, 256)]    // HIDIOCGRAWNAME(256)
    [InlineData(HidrawIoctl.HIDIOCGRAWUNIQ_256, 0x81004808u, 0x08u, 256)]    // HIDIOCGRAWUNIQ(256)
    public void HidrawIoctlNumbersFollowTheAsmGenericEncoding(uint constant, uint expected, uint number, int size)
    {
        Assert.Equal(expected, constant);
        Assert.Equal(expected, 2u << 30 | (uint)size << 16 | (uint)'H' << 8 | number);
        Assert.Equal(expected, HidrawIoctl.Read('H', number, size));
        Assert.Equal(size, HidrawIoctl.SizeOf(constant));
    }

    [Fact]
    public void HidrawIoctlSizesComeFromTheStructs()
    {
        Assert.Equal(HidrawIoctl.HIDIOCGRDESCSIZE, HidrawIoctl.Read('H', 0x01, sizeof(int)));
        Assert.Equal(HidrawIoctl.HIDIOCGRDESC, HidrawIoctl.Read('H', 0x02, Unsafe.SizeOf<HidrawReportDescriptor>()));
        Assert.Equal(HidrawIoctl.HIDIOCGRAWINFO, HidrawIoctl.Read('H', 0x03, Unsafe.SizeOf<HidrawDevInfo>()));
        Assert.Equal(HidrawIoctl.HIDIOCGRAWNAME_256, HidrawIoctl.Read('H', 0x04, HidrawIoctl.StringLength));
        Assert.Equal(HidrawIoctl.HIDIOCGRAWUNIQ_256, HidrawIoctl.Read('H', 0x08, HidrawIoctl.StringLength));
    }

    [Fact]
    public void LibcIoctlRefusesAnArgumentSmallerThanTheKernelWrites()
    {
        // A verificação vem antes da chamada nativa: roda em qualquer SO.
        var ex = Assert.Throws<ArgumentException>(() => LibCHidrawSyscalls.Instance.Ioctl(-1, HidrawIoctl.HIDIOCGRDESC, new byte[4096]));

        Assert.Contains("4100", ex.Message);
    }

    [Fact]
    public void IoctlEncodingRejectsFieldsThatDoNotFit()
    {
        Assert.Equal(0x00004801u, HidrawIoctl.Encode(HidrawIoctl.IOC_NONE, 'H', 0x01, 0)); // _IO('H', 0x01)
        Assert.Equal(0xC0044807u, HidrawIoctl.Encode(HidrawIoctl.IOC_READ | HidrawIoctl.IOC_WRITE, 'H', 0x07, 4)); // _IOWR
        Assert.Equal(0x3FFF, HidrawIoctl.SizeOf(HidrawIoctl.Read('H', 0, 0x3FFF)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Read('H', 0x01, 0x4000)); // _IOC_SIZEBITS = 14
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Read('H', 0x100, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Read('\u0100', 0x01, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Encode(4, 'H', 0x01, 4));
    }

    [Fact]
    public void SetupApiStructsMatchTheWindowsHeaders()
    {
        // SP_DEVICE_INTERFACE_DATA: 32 bytes em Win64 (Reserved é ULONG_PTR alinhado a 8) e 28 em 32 bits.
        Assert.Equal(Environment.Is64BitProcess ? 32 : 28, Unsafe.SizeOf<WindowsNative.SpDeviceInterfaceData>());
        Assert.Equal(4, (int)Marshal.OffsetOf<WindowsNative.SpDeviceInterfaceData>(nameof(WindowsNative.SpDeviceInterfaceData.InterfaceClassGuid)));
        Assert.Equal(20, (int)Marshal.OffsetOf<WindowsNative.SpDeviceInterfaceData>(nameof(WindowsNative.SpDeviceInterfaceData.Flags)));
        Assert.Equal(24, (int)Marshal.OffsetOf<WindowsNative.SpDeviceInterfaceData>(nameof(WindowsNative.SpDeviceInterfaceData.Reserved)));

        // SP_DEVICE_INTERFACE_DETAIL_DATA_W { DWORD cbSize; WCHAR DevicePath[1]; }: pack 8 em Win64, pack 1 em 32 bits.
        uint expected = (uint)(Environment.Is64BitProcess ? Unsafe.SizeOf<DetailDataPack8>() : Unsafe.SizeOf<DetailDataPack1>());
        Assert.Equal(expected, WindowsNative.DeviceInterfaceDetailDataCbSize);
        Assert.Equal((8, 6), (Unsafe.SizeOf<DetailDataPack8>(), Unsafe.SizeOf<DetailDataPack1>()));
    }

    [Fact]
    public void HidStructsMatchTheWindowsHeaders()
    {
        Assert.Equal(12, Unsafe.SizeOf<WindowsNative.HiddAttributes>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WindowsNative.HiddAttributes>(nameof(WindowsNative.HiddAttributes.VersionNumber)));

        // HIDP_CAPS: 5 USHORT, Reserved[17] e 10 contadores USHORT.
        Assert.Equal(64, Unsafe.SizeOf<WindowsNative.HidpCaps>());
        Assert.Equal(4, (int)Marshal.OffsetOf<WindowsNative.HidpCaps>(nameof(WindowsNative.HidpCaps.InputReportByteLength)));
        Assert.Equal(44, (int)Marshal.OffsetOf<WindowsNative.HidpCaps>(nameof(WindowsNative.HidpCaps.NumberLinkCollectionNodes)));
        Assert.Equal(62, (int)Marshal.OffsetOf<WindowsNative.HidpCaps>(nameof(WindowsNative.HidpCaps.NumberFeatureDataIndices)));
    }

    [Fact]
    public void UnnumberedHidrawReportIsCopiedAsIs()
    {
        byte[] report = [1, 2, 3, 4, 5, 6, 7, 8];
        var buffer = new byte[8];

        Assert.Equal(8, LinuxHidrawDevice.CopyReport(report, numbered: false, inputReportLength: 8, buffer));
        Assert.Equal(report, buffer);
    }

    [Fact]
    public void NumberedHidrawReportLosesTheReportId()
    {
        var buffer = new byte[8];

        Assert.Equal(8, LinuxHidrawDevice.CopyReport([0x05, 1, 2, 3, 4, 5, 6, 7, 8], numbered: true, inputReportLength: 8, buffer));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, buffer);
    }

    [Fact]
    public void ShortHidrawReportIsPaddedWithZerosLikeOnWindows()
    {
        var buffer = Enumerable.Repeat((byte)0xFF, 10).ToArray();

        Assert.Equal(8, LinuxHidrawDevice.CopyReport([1, 2, 3], numbered: false, inputReportLength: 8, buffer));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0, 0xFF, 0xFF }, buffer);

        Assert.Equal(8, LinuxHidrawDevice.CopyReport([0x05], numbered: true, inputReportLength: 8, buffer));
        Assert.All(buffer[..8], b => Assert.Equal(0, b));
    }

    [Fact]
    public void LongHidrawReportIsTruncatedToTheBuffer()
    {
        var buffer = new byte[8];

        Assert.Equal(8, LinuxHidrawDevice.CopyReport([.. Enumerable.Range(1, 12).Select(i => (byte)i)], numbered: false, inputReportLength: 8, buffer));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, buffer);
    }

    [Fact]
    public void WindowsIoErrorsAreTranslated()
    {
        var unplugged = WindowsHidErrors.ForIo(new IOException("x", unchecked((int)0x8007048F)), @"\\?\hid#x");
        Assert.Contains("desconectado", unplugged.Message);
        Assert.Equal(WindowsNative.ERROR_DEVICE_NOT_CONNECTED, unplugged.NativeError);

        // ERROR_ACCESS_DENIED chega do FileStream como UnauthorizedAccessException, que não é IOException.
        var denied = WindowsHidErrors.ForIo(new UnauthorizedAccessException("x"), @"\\?\hid#x");
        Assert.Equal(WindowsNative.ERROR_ACCESS_DENIED, denied.NativeError);
        Assert.IsAssignableFrom<IOException>(denied);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct DetailDataPack8
    {
        public uint CbSize;
        public char DevicePath;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DetailDataPack1
    {
        public uint CbSize;
        public char DevicePath;
    }
}
