using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Brymen.Dmm.Hid;

/// <summary>Chamadas de sistema usadas para consultar um nó hidraw; os testes injetam uma implementação falsa.</summary>
internal interface IHidrawSyscalls
{
    /// <summary>
    /// <c>open(2)</c> para leitura e escrita, sem bloquear e sem tornar um terminal o terminal de controle do processo:
    /// o descritor (≥ 0) ou −1 com o errno.
    /// </summary>
    (int Result, int Errno) Open(string path);

    /// <summary><c>ioctl(2)</c> com <paramref name="argument"/> como ponteiro: o retorno (≥ 0) ou −1 com o errno.</summary>
    (int Result, int Errno) Ioctl(int fd, nuint request, Span<byte> argument);

    /// <summary><c>close(2)</c>.</summary>
    void Close(int fd);
}

/// <summary>As chamadas de <see cref="IHidrawSyscalls"/> pela libc.</summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LibCHidrawSyscalls : IHidrawSyscalls
{
    public static LibCHidrawSyscalls Instance { get; } = new();

    private LibCHidrawSyscalls()
    {
    }

    public (int Result, int Errno) Open(string path)
    {
        // O_NOCTTY: o caminho configurado pode ser um terminal (um erro de configuração); não vira o terminal de controle.
        int fd = LibC.Open(path, LibC.O_RDWR | LibC.O_CLOEXEC | LibC.O_NONBLOCK | LibC.O_NOCTTY);
        return (fd, fd < 0 ? Marshal.GetLastPInvokeError() : 0);
    }

    public (int Result, int Errno) Ioctl(int fd, nuint request, Span<byte> argument)
    {
        // O kernel escreve _IOC_SIZE(request) bytes no argumento: um buffer menor seria corrompido.
        int size = HidrawIoctl.SizeOf(request);
        if (argument.Length < size)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"O ioctl {(ulong)request:X8} usa {size} bytes de argumento; recebidos {argument.Length}."), nameof(argument));
        }
        fixed (byte* p = argument)
        {
            int rc = LibC.Ioctl(fd, request, p);
            return (rc, rc < 0 ? Marshal.GetLastPInvokeError() : 0);
        }
    }

    public void Close(int fd) => LibC.Close(fd);
}

/// <summary>
/// Consultas feitas ao próprio nó hidraw por ioctl, sem o sysfs: identificação (<c>HIDIOCGRAWINFO</c>,
/// <c>HIDIOCGRAWNAME</c>, <c>HIDIOCGRAWUNIQ</c>) e report descriptor (<c>HIDIOCGRDESCSIZE</c>, <c>HIDIOCGRDESC</c>).
/// Valem para qualquer caminho, inclusive um nó com outro nome dentro de um contêiner.
/// </summary>
internal static class HidrawNode
{
    /// <summary>
    /// Abre <paramref name="path"/> e o identifica com <c>HIDIOCGRAWINFO</c>. Devolve <see langword="null"/> se o
    /// caminho não existir, o dispositivo tiver sido removido ou o nó não for hidraw (<c>ENOTTY</c>); lança
    /// <see cref="HidException"/> para os demais erros de abertura (permissão, por exemplo).
    /// </summary>
    public static HidDeviceInfo? Probe(IHidrawSyscalls syscalls, string path)
    {
        ArgumentNullException.ThrowIfNull(syscalls);
        ArgumentException.ThrowIfNullOrEmpty(path);

        int fd, errno;
        do
        {
            (fd, errno) = syscalls.Open(path);
        }
        while (fd < 0 && errno == HidrawErrors.EINTR);

        if (fd < 0)
        {
            if (errno is HidrawErrors.ENOENT or HidrawErrors.ENOTDIR or HidrawErrors.EISDIR or HidrawErrors.ENODEV or HidrawErrors.ENXIO)
                return null;
            throw HidrawErrors.ForOpen(errno, path);
        }

        try
        {
            var info = default(HidrawDevInfo);
            if (Ioctl(syscalls, fd, HidrawIoctl.HIDIOCGRAWINFO, AsBytes(ref info)) < 0)
                return null;
            // vendor e product são __s16: um VID ≥ 8000h chega negativo.
            return new HidDeviceInfo(
                Path: path,
                VendorId: unchecked((ushort)info.Vendor),
                ProductId: unchecked((ushort)info.Product),
                Product: ReadString(syscalls, fd, HidrawIoctl.HIDIOCGRAWNAME_256),
                SerialNumber: ReadString(syscalls, fd, HidrawIoctl.HIDIOCGRAWUNIQ_256));
        }
        finally
        {
            syscalls.Close(fd);
        }
    }

    /// <summary>
    /// Lê o report descriptor do nó aberto em <paramref name="fd"/> e calcula os tamanhos dos relatórios; devolve
    /// <see langword="null"/> se o ioctl falhar ou o descriptor não tiver relatório de entrada.
    /// </summary>
    public static HidReportLayout? TryReadReportLayout(IHidrawSyscalls syscalls, int fd)
    {
        ArgumentNullException.ThrowIfNull(syscalls);

        int size = 0;
        // O kernel recusa (EINVAL) um tamanho acima de HID_MAX_DESCRIPTOR_SIZE − 1.
        if (Ioctl(syscalls, fd, HidrawIoctl.HIDIOCGRDESCSIZE, AsBytes(ref size)) < 0 || size <= 0 || size >= HidrawIoctl.MaxDescriptorSize)
            return null;

        var descriptor = new HidrawReportDescriptor { Size = (uint)size };
        if (Ioctl(syscalls, fd, HidrawIoctl.HIDIOCGRDESC, AsBytes(ref descriptor)) < 0)
            return null;
        return HidReportDescriptor.TryParse(descriptor.Value[..size], out var layout) && layout.InputReportLength > 0 ? layout : null;
    }

    /// <summary>
    /// <c>HIDIOCGRAWNAME</c>/<c>HIDIOCGRAWUNIQ</c>: o kernel devolve quantos bytes copiou, com o NUL final se couber.
    /// Kernels sem o ioctl (o <c>HIDIOCGRAWUNIQ</c> antes do 5.6) respondem <c>ENOTTY</c> ou <c>EINVAL</c>.
    /// </summary>
    private static string? ReadString(IHidrawSyscalls syscalls, int fd, uint request)
    {
        Span<byte> buffer = stackalloc byte[HidrawIoctl.StringLength];
        buffer.Clear();
        int n = Ioctl(syscalls, fd, request, buffer);
        if (n <= 0)
            return null;
        var text = buffer[..Math.Min(n, buffer.Length)];
        int nul = text.IndexOf((byte)0);
        return HidStrings.Clean(Encoding.UTF8.GetString(nul >= 0 ? text[..nul] : text));
    }

    private static int Ioctl(IHidrawSyscalls syscalls, int fd, uint request, Span<byte> argument)
    {
        while (true)
        {
            var (result, errno) = syscalls.Ioctl(fd, request, argument);
            if (result >= 0 || errno != HidrawErrors.EINTR)
                return result;
        }
    }

    private static Span<byte> AsBytes<T>(ref T value) where T : unmanaged => MemoryMarshal.AsBytes(new Span<T>(ref value));
}
