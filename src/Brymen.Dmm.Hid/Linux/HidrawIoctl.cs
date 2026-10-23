using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Brymen.Dmm.Hid;

/// <summary>
/// Números de ioctl do hidraw (<c>linux/hidraw.h</c>) na codificação do <c>asm-generic/ioctl.h</c>, a de x64, arm64 e
/// arm (32 bits): direção nos bits 30–31, tamanho do argumento nos bits 16–29, tipo nos bits 8–15 e número nos bits 0–7.
/// Os valores são fixos, como nos cabeçalhos; os testes conferem cada um com <see cref="Encode"/> e o tamanho da struct.
/// </summary>
internal static class HidrawIoctl
{
    public const uint IOC_NONE = 0;
    public const uint IOC_WRITE = 1;
    public const uint IOC_READ = 2;

    private const int NumberShift = 0;
    private const int TypeShift = 8;
    private const int SizeShift = 16;
    private const int DirectionShift = 30;
    private const uint SizeMask = (1u << 14) - 1;

    /// <summary><c>HID_MAX_DESCRIPTOR_SIZE</c> (<c>linux/hid.h</c>): capacidade de <see cref="HidrawReportDescriptor.Value"/>.</summary>
    public const int MaxDescriptorSize = 4096;

    /// <summary>Capacidade do buffer de <see cref="HIDIOCGRAWNAME_256"/> e <see cref="HIDIOCGRAWUNIQ_256"/>.</summary>
    public const int StringLength = 256;

    /// <summary><c>HIDIOCGRDESCSIZE</c> = <c>_IOR('H', 0x01, int)</c>: tamanho do report descriptor.</summary>
    public const uint HIDIOCGRDESCSIZE = 0x80044801;

    /// <summary><c>HIDIOCGRDESC</c> = <c>_IOR('H', 0x02, struct hidraw_report_descriptor)</c> (4 + 4096 bytes).</summary>
    public const uint HIDIOCGRDESC = 0x90044802;

    /// <summary><c>HIDIOCGRAWINFO</c> = <c>_IOR('H', 0x03, struct hidraw_devinfo)</c> (8 bytes): barramento, VID e PID.</summary>
    public const uint HIDIOCGRAWINFO = 0x80084803;

    /// <summary><c>HIDIOCGRAWNAME(256)</c> = <c>_IOC(_IOC_READ, 'H', 0x04, 256)</c>: nome do dispositivo HID (<c>HID_NAME</c>).</summary>
    public const uint HIDIOCGRAWNAME_256 = 0x81004804;

    /// <summary><c>HIDIOCGRAWUNIQ(256)</c> = <c>_IOC(_IOC_READ, 'H', 0x08, 256)</c>: identificador único (<c>HID_UNIQ</c>), desde o Linux 5.6.</summary>
    public const uint HIDIOCGRAWUNIQ_256 = 0x81004808;

    /// <summary><c>_IOC(dir, type, nr, size)</c>.</summary>
    public static uint Encode(uint direction, char type, uint number, int size)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(direction, IOC_READ | IOC_WRITE);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type, (char)0xFF);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 0xFFu);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)size, SizeMask);
        return direction << DirectionShift | (uint)size << SizeShift | (uint)type << TypeShift | number << NumberShift;
    }

    /// <summary><c>_IOR(type, nr, size)</c>: o kernel escreve <paramref name="size"/> bytes no argumento.</summary>
    public static uint Read(char type, uint number, int size) => Encode(IOC_READ, type, number, size);

    /// <summary><c>_IOC_SIZE(request)</c>: quantos bytes do argumento o kernel lê ou escreve.</summary>
    public static int SizeOf(nuint request) => (int)((request >> SizeShift) & SizeMask);
}

/// <summary><c>struct hidraw_devinfo { __u32 bustype; __s16 vendor; __s16 product; }</c>: 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HidrawDevInfo
{
    public uint BusType;
    public short Vendor;
    public short Product;
}

/// <summary><c>struct hidraw_report_descriptor { __u32 size; __u8 value[HID_MAX_DESCRIPTOR_SIZE]; }</c>: 4100 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HidrawReportDescriptor
{
    /// <summary>Entrada: quantos bytes copiar (no máximo <see cref="HidrawIoctl.MaxDescriptorSize"/> − 1).</summary>
    public uint Size;

    public DescriptorBytes Value;

    [InlineArray(HidrawIoctl.MaxDescriptorSize)]
    public struct DescriptorBytes
    {
        private byte _element0;
    }
}
