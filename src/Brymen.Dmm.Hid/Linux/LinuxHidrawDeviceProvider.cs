using System.Globalization;

namespace Brymen.Dmm.Hid;

/// <summary>
/// Dispositivos hidraw do Linux. A enumeração lê o sysfs (<c>class/hidraw/hidrawN/device/uevent</c> e os
/// atributos do dispositivo USB pai) e funciona em qualquer SO, o que permite testá-la com uma árvore falsa.
/// <see cref="Probe(string)"/> identifica um nó fora da enumeração pelos ioctls do hidraw, sem o sysfs (um nó com
/// outro nome num contêiner, como <c>docker run --device /dev/hidraw3:/dev/hidraw0</c>, ou um <c>/sys</c> invisível).
/// <see cref="Open(HidDeviceInfo)"/> e <see cref="Probe(string)"/> exigem Linux.
/// </summary>
public sealed class LinuxHidrawDeviceProvider : IHidDeviceProvider, IHidDevicePathProbe
{
    // Do dispositivo HID sobe-se à interface USB e dela ao dispositivo USB; a margem cobre hubs e variações.
    private const int MaxUsbParentDepth = 4;

    // Só os testes trocam as chamadas de sistema de Probe; null = libc.
    private readonly IHidrawSyscalls? _syscalls;

    /// <summary>Provider do sistema: o sysfs em <c>/sys</c> e os nós em <c>/dev</c>.</summary>
    public LinuxHidrawDeviceProvider()
        : this("/sys", "/dev")
    {
    }

    /// <summary>Provider sobre uma árvore sysfs e um diretório de nós falsos (testes).</summary>
    internal LinuxHidrawDeviceProvider(string sysfsRoot, string devRoot = "/dev")
    {
        SysfsRoot = sysfsRoot;
        DevRoot = devRoot;
    }

    /// <summary>Provider cujo <see cref="Probe(string)"/> usa <paramref name="syscalls"/> no lugar da libc (testes).</summary>
    internal LinuxHidrawDeviceProvider(string sysfsRoot, string devRoot, IHidrawSyscalls syscalls)
        : this(sysfsRoot, devRoot)
    {
        _syscalls = syscalls ?? throw new ArgumentNullException(nameof(syscalls));
    }

    /// <summary>Raiz do sysfs usada na enumeração (<c>/sys</c>; outra só em testes).</summary>
    internal string SysfsRoot { get; }

    /// <summary>Raiz dos nós de dispositivo (<c>/dev</c>; outra só em testes).</summary>
    internal string DevRoot { get; }

    private string ClassDirectory => Path.Combine(SysfsRoot, "class", "hidraw");

    /// <inheritdoc/>
    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) =>
        [.. EnumerateAll().Where(d => d.VendorId == vendorId && d.ProductId == productId)];

    /// <summary>Todos os dispositivos hidraw, em ordem numérica (hidraw2 antes de hidraw10).</summary>
    public IReadOnlyList<HidDeviceInfo> EnumerateAll()
    {
        if (!Directory.Exists(ClassDirectory))
            return [];

        var found = new List<(int Index, HidDeviceInfo Info)>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(ClassDirectory, "hidraw*"))
        {
            try
            {
                if (TryReadDevice(entry) is { } info)
                    found.Add((ParseIndex(Path.GetFileName(entry)), info));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dispositivo removido durante a enumeração ou atributo ilegível: ignora só esta entrada.
            }
        }
        return [.. found.OrderBy(f => f.Index).ThenBy(f => f.Info.Path, StringComparer.Ordinal).Select(f => f.Info)];
    }

    /// <summary>
    /// Abre o nó <see cref="HidDeviceInfo.Path"/>. Os tamanhos dos relatórios vêm do report descriptor do próprio nó
    /// (<c>HIDIOCGRDESC</c>); se o ioctl falhar, de <see cref="ReadReportLayout(string)"/>.
    /// </summary>
    public IHidDevice Open(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("O transporte hidraw só está disponível no Linux.");
        return LinuxHidrawDevice.Open(device, () => ReadReportLayout(device.Path));
    }

    /// <summary>
    /// Identifica o nó <paramref name="devicePath"/> abrindo-o e consultando <c>HIDIOCGRAWINFO</c> (VID e PID),
    /// <c>HIDIOCGRAWNAME</c> (<see cref="HidDeviceInfo.Product"/>) e <c>HIDIOCGRAWUNIQ</c>
    /// (<see cref="HidDeviceInfo.SerialNumber"/>), sem o sysfs; o <see cref="HidDeviceInfo.Path"/> devolvido é o
    /// caminho pedido. Devolve <see langword="null"/> se o caminho não existir ou não for um hidraw, e sempre fora do Linux.
    /// </summary>
    /// <exception cref="HidException">O nó existe mas não pôde ser aberto (permissão, por exemplo).</exception>
    public HidDeviceInfo? Probe(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        if (_syscalls is not null)
            return HidrawNode.Probe(_syscalls, devicePath);
        if (!OperatingSystem.IsLinux())
            return null;
        return HidrawNode.Probe(LibCHidrawSyscalls.Instance, devicePath);
    }

    /// <summary>
    /// Tamanhos dos relatórios de <paramref name="devicePath"/> (<c>/dev/hidrawN</c> ou um link para ele), lidos do
    /// <c>report_descriptor</c> do sysfs; usa <see cref="HidReportLayout.Bu86xDefault"/> se não for possível.
    /// O sysfs mostra os nomes do host: num contêiner com o nó renomeado, o resultado pode ser de outro dispositivo.
    /// </summary>
    internal HidReportLayout ReadReportLayout(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        string name = Path.GetFileName(PathResolver.TryGetRealPath(devicePath));
        string descriptorPath = Path.Combine(ClassDirectory, name, "device", "report_descriptor");
        try
        {
            if (File.Exists(descriptorPath)
                && HidReportDescriptor.TryParse(ReadAllBytes(descriptorPath), out var layout)
                && layout.InputReportLength > 0)
            {
                return layout;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return HidReportLayout.Bu86xDefault;
    }

    private HidDeviceInfo? TryReadDevice(string entry)
    {
        string hidDirectory = Path.Combine(entry, "device");
        var hidEvent = ReadUevent(Path.Combine(hidDirectory, "uevent"));
        if (hidEvent is null
            || !hidEvent.TryGetValue("HID_ID", out var hidId)
            || !TryParseHidId(hidId, out ushort vendorId, out ushort productId))
        {
            return null;
        }

        string devName = ReadUevent(Path.Combine(entry, "uevent"))?.GetValueOrDefault("DEVNAME") is { Length: > 0 } dn
            ? dn
            : Path.GetFileName(entry);

        string? usb = FindUsbDevice(hidDirectory);
        return new HidDeviceInfo(
            Path: Path.Combine(DevRoot, devName),
            VendorId: vendorId,
            ProductId: productId,
            ReleaseNumber: usb is null ? null : ParseHex16(ReadAttribute(usb, "bcdDevice")),
            Manufacturer: usb is null ? null : ReadAttribute(usb, "manufacturer"),
            Product: (usb is null ? null : ReadAttribute(usb, "product")) ?? HidStrings.Clean(hidEvent.GetValueOrDefault("HID_NAME")),
            SerialNumber: (usb is null ? null : ReadAttribute(usb, "serial")) ?? HidStrings.Clean(hidEvent.GetValueOrDefault("HID_UNIQ")));
    }

    /// <summary>Sobe a partir do dispositivo HID real até o primeiro diretório com <c>idVendor</c>/<c>idProduct</c> (o dispositivo USB).</summary>
    private string? FindUsbDevice(string hidDirectory)
    {
        string real = PathResolver.TryGetRealPath(hidDirectory);
        string? dir = Path.GetDirectoryName(real);
        for (int depth = 0; dir is not null && depth < MaxUsbParentDepth; depth++, dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "idVendor")) && File.Exists(Path.Combine(dir, "idProduct")))
                return dir;
        }
        return null;
    }

    internal static bool TryParseHidId(string value, out ushort vendorId, out ushort productId)
    {
        // Formato do kernel: "%04X:%08X:%08X" (barramento:vendor:produto); 0003 = USB.
        vendorId = productId = 0;
        var parts = value.Trim().Split(':');
        if (parts.Length != 3
            || !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint vid)
            || !uint.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint pid)
            || vid > ushort.MaxValue
            || pid > ushort.MaxValue)
        {
            return false;
        }
        vendorId = (ushort)vid;
        productId = (ushort)pid;
        return true;
    }

    private static ushort? ParseHex16(string? value) =>
        ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v) ? v : null;

    private static int ParseIndex(string name) =>
        int.TryParse(name.AsSpan("hidraw".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

    private static Dictionary<string, string>? ReadUevent(string path)
    {
        if (!File.Exists(path))
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in ReadAllText(path).Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
                values[line[..eq].Trim()] = line[(eq + 1)..].TrimEnd('\r');
        }
        return values;
    }

    private static string? ReadAttribute(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        return File.Exists(path) ? HidStrings.Clean(ReadAllText(path)) : null;
    }

    // Atributos do sysfs informam tamanho de 4096 bytes e devolvem menos: lê até o EOF em vez de confiar no tamanho.
    private static string ReadAllText(string path)
    {
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
