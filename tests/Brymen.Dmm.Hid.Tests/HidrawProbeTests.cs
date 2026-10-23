using System.Buffers.Binary;
using System.Text;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Abertura direta de um nó hidraw fora da enumeração (contêiner com <c>--device /dev/hidraw3:/dev/hidraw0</c>,
/// <c>/sys</c> invisível) com um kernel falso: nenhum teste faz chamadas nativas. O caminho real é coberto pelos
/// testes com /dev/uhid e com o cabo (<see cref="LinuxUhidTests"/>, <see cref="HardwareTests"/>).
/// </summary>
public sealed class HidrawProbeTests : IDisposable
{
    private const string Serial = "£⑉"; // lixo não-ASCII, como o serial do BC-86X real
    private const string Node = "/dev/hidraw0";

    // Os números dos ioctls, escritos aqui por extenso para conferir os que o código usa.
    private const uint HIDIOCGRDESCSIZE = 0x80044801;
    private const uint HIDIOCGRDESC = 0x90044802;
    private const uint HIDIOCGRAWINFO = 0x80084803;
    private const uint HIDIOCGRAWNAME_256 = 0x81004804;
    private const uint HIDIOCGRAWUNIQ_256 = 0x81004808;

    private readonly TempDirectory _temp = new();
    private readonly FakeHidrawSyscalls _kernel = new();

    private string Sys => Path.Combine(_temp.Path, "sys");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ProbeIdentifiesTheNodeByIoctl()
    {
        _kernel.Add(Node, FakeHidrawNode.Bc86x(uniq: Serial));

        var info = CreateProvider().Probe(Node);

        Assert.Equal(new HidDeviceInfo(Node, 0x0820, 0x0001, Product: "Brymen Superior DMM", SerialNumber: Serial), info);
        Assert.Equal([HIDIOCGRAWINFO, HIDIOCGRAWNAME_256, HIDIOCGRAWUNIQ_256], _kernel.Requests);
        Assert.Equal(0, _kernel.OpenDescriptors);
    }

    [Fact]
    public void FindDeviceOpensARenamedNodeThatTheEnumerationDoesNotShow()
    {
        // O sysfs do contêiner mostra o nome do host (hidraw3); o nó repassado se chama /dev/hidraw0.
        AddSysfsDevice("hidraw3");
        _kernel.Add(Node, FakeHidrawNode.Bc86x(uniq: Serial));
        var provider = CreateProvider();
        Assert.Equal(Path.Combine("/dev", "hidraw3"), Assert.Single(provider.Enumerate(0x0820, 0x0001)).Path);

        var info = Bu86xLink.FindDevice(provider, Node);

        Assert.NotNull(info);
        Assert.Equal((Node, "Brymen Superior DMM", Serial), (info.Path, info.Product, info.SerialNumber));
        Assert.Equal([Node], _kernel.Opened);
    }

    [Fact]
    public void FindDeviceDoesNotProbeAPathThatTheEnumerationHas()
    {
        AddSysfsDevice("hidraw3");
        string enumerated = Path.Combine("/dev", "hidraw3");

        Assert.Equal(enumerated, Bu86xLink.FindDevice(CreateProvider(), enumerated)?.Path);
        Assert.Empty(_kernel.Opened);
    }

    [Fact]
    public void FindDeviceWorksWithoutSysfs()
    {
        _kernel.Add(Node, FakeHidrawNode.Bc86x());
        var provider = CreateProvider();

        Assert.Empty(provider.EnumerateAll());
        Assert.Equal(Node, Bu86xLink.FindDevice(provider, Node)?.Path);
        // Sem caminho configurado não há o que consultar.
        Assert.Null(Bu86xLink.FindDevice(provider));
    }

    [Fact]
    public void NodeWithAnotherVidPidIsNotTheCable()
    {
        _kernel.Add(Node, new FakeHidrawNode { Vendor = 0x046D, Product = 0xC31C, Name = "Logitech USB Keyboard" });
        var provider = CreateProvider();

        var other = provider.Probe(Node);
        Assert.NotNull(other);
        Assert.Equal((0x046D, 0xC31C), (other.VendorId, other.ProductId));
        Assert.Null(Bu86xLink.FindDevice(provider, Node));
        var ex = Assert.Throws<HidException>(() => Bu86xLink.Open(provider, Node));
        Assert.Contains($"não encontrado em {Node}", ex.Message);
        Assert.Equal(0, _kernel.OpenDescriptors);
    }

    [Theory]
    [InlineData(HidrawErrors.ENOENT)]
    [InlineData(HidrawErrors.ENOTDIR)]
    [InlineData(HidrawErrors.EISDIR)]
    [InlineData(HidrawErrors.ENODEV)] // nó estático de um contêiner com o cabo desligado
    [InlineData(HidrawErrors.ENXIO)]
    public void PathThatCannotBeOpenedAsADeviceIsNotFound(int errno)
    {
        var node = _kernel.Add(Node, FakeHidrawNode.Bc86x());
        node.OpenErrors.Enqueue(errno);

        Assert.Null(Bu86xLink.FindDevice(CreateProvider(), Node));
        Assert.Null(CreateProvider().Probe("/dev/nada"));
    }

    [Fact]
    public void NodeThatIsNotHidrawIsNotFoundAndIsClosed()
    {
        _kernel.Add("/dev/ttyS0", new FakeHidrawNode { IsHidraw = false });
        var removed = _kernel.Add(Node, FakeHidrawNode.Bc86x());
        removed.IoctlErrors.Enqueue(HidrawErrors.ENODEV); // removido entre o open e o ioctl
        var provider = CreateProvider();

        Assert.Null(provider.Probe("/dev/ttyS0"));
        Assert.Null(provider.Probe(Node));
        Assert.Equal(2, _kernel.Opened.Count);
        Assert.Equal(0, _kernel.OpenDescriptors);
    }

    [Theory]
    [InlineData(HidrawErrors.EACCES)]
    [InlineData(HidrawErrors.EPERM)]
    public void PermissionDeniedOnTheNodeIsReportedWithTheUdevHint(int errno)
    {
        _kernel.Add(Node, FakeHidrawNode.Bc86x()).OpenErrors.Enqueue(errno);

        var ex = Assert.Throws<HidException>(() => Bu86xLink.FindDevice(CreateProvider(), Node));

        Assert.Equal(errno, ex.NativeError);
        Assert.Contains(Node, ex.Message);
        Assert.Contains(HidrawErrors.UdevRulePath, ex.Message);
    }

    [Fact]
    public void InterruptedCallsAreRetried()
    {
        var node = _kernel.Add(Node, FakeHidrawNode.Bc86x());
        node.OpenErrors.Enqueue(HidrawErrors.EINTR);
        node.IoctlErrors.Enqueue(HidrawErrors.EINTR);

        Assert.Equal(Node, Bu86xLink.FindDevice(CreateProvider(), Node)?.Path);
        Assert.Equal([Node, Node], _kernel.Opened);
        Assert.Equal([HIDIOCGRAWINFO, HIDIOCGRAWINFO, HIDIOCGRAWNAME_256, HIDIOCGRAWUNIQ_256], _kernel.Requests);
    }

    [Fact]
    public void VendorAndProductAreUnsignedAlthoughTheKernelUsesS16()
    {
        _kernel.Add(Node, new FakeHidrawNode { Vendor = 0x8086, Product = 0xFFFF });

        var info = CreateProvider().Probe(Node);

        Assert.NotNull(info);
        Assert.Equal((0x8086, 0xFFFF), (info.VendorId, info.ProductId));
    }

    [Fact]
    public void NameAndUniqAreCleanedAndBoundedByTheBuffer()
    {
        _kernel.Add("/dev/long", new FakeHidrawNode { Name = new string('A', 300), Uniq = " abc\n" });
        _kernel.Add("/dev/old", new FakeHidrawNode { Name = "", Uniq = null }); // kernel < 5.6: sem HIDIOCGRAWUNIQ
        var provider = CreateProvider();

        var longName = provider.Probe("/dev/long");
        var old = provider.Probe("/dev/old");

        Assert.Equal(new string('A', 256), longName?.Product);
        Assert.Equal("abc", longName?.SerialNumber);
        Assert.NotNull(old);
        Assert.Null(old.Product);
        Assert.Null(old.SerialNumber);
    }

    [Fact]
    public void ReportLayoutComesFromTheNodeDescriptor()
    {
        _kernel.Add("/dev/bc86x", FakeHidrawNode.Bc86x());
        _kernel.Add("/dev/numbered", new FakeHidrawNode { Descriptor = [0x85, 0x05, 0x75, 0x08, 0x95, 0x0A, 0x81, 0x02, 0x95, 0x02, 0x91, 0x02] });

        Assert.Equal(new HidReportLayout(8, 3, 0, false), ReadLayout("/dev/bc86x"));
        Assert.Equal([HIDIOCGRDESCSIZE, HIDIOCGRDESC], _kernel.Requests);
        Assert.Equal(new HidReportLayout(10, 2, 0, true), ReadLayout("/dev/numbered"));
    }

    [Fact]
    public void ReportLayoutIsNullWhenTheDescriptorIsUnusable()
    {
        _kernel.Add("/dev/no-main-items", new FakeHidrawNode { Descriptor = [0x75, 0x08] });
        _kernel.Add("/dev/empty", new FakeHidrawNode { Descriptor = [] });
        _kernel.Add("/dev/tty", new FakeHidrawNode { IsHidraw = false });
        _kernel.Add("/dev/failing", FakeHidrawNode.Bc86x()).IoctlErrors.Enqueue(HidrawErrors.EIO);

        Assert.Null(ReadLayout("/dev/no-main-items"));
        Assert.Null(ReadLayout("/dev/empty"));
        Assert.Null(ReadLayout("/dev/tty"));
        Assert.Null(ReadLayout("/dev/failing"));
    }

    [Fact]
    public void DescriptorTooLargeForHidiocgrdescIsNotRequested()
    {
        // O kernel recusa size > HID_MAX_DESCRIPTOR_SIZE − 1 com EINVAL.
        _kernel.Add("/dev/huge", new FakeHidrawNode { Descriptor = new byte[HidrawIoctl.MaxDescriptorSize] });
        // 4095 bytes, o maior aceito: itens principais de tag 0 (ignorados) e, no fim, um Input de 8 bytes.
        _kernel.Add("/dev/max", new FakeHidrawNode { Descriptor = [.. new byte[HidrawIoctl.MaxDescriptorSize - 7], 0x75, 0x08, 0x95, 0x08, 0x81, 0x02] });

        Assert.Null(ReadLayout("/dev/huge"));
        Assert.Equal([HIDIOCGRDESCSIZE], _kernel.Requests);
        Assert.Equal(new HidReportLayout(8, 0, 0, false), ReadLayout("/dev/max"));
        Assert.Equal([HIDIOCGRDESCSIZE, HIDIOCGRDESCSIZE, HIDIOCGRDESC], _kernel.Requests);
    }

    [Fact]
    public void ProbeOutsideLinuxFindsNothing()
    {
        if (OperatingSystem.IsLinux())
            return;

        Assert.Null(new LinuxHidrawDeviceProvider(Sys).Probe(Node));
        Assert.Null(Bu86xLink.FindDevice(new LinuxHidrawDeviceProvider(Sys), Node));
    }

    private LinuxHidrawDeviceProvider CreateProvider() => new(Sys, "/dev", _kernel);

    private HidReportLayout? ReadLayout(string path)
    {
        var (fd, errno) = _kernel.Open(path);
        Assert.Equal(0, errno);
        try
        {
            return HidrawNode.TryReadReportLayout(_kernel, fd);
        }
        finally
        {
            _kernel.Close(fd);
        }
    }

    /// <summary>Entrada do sysfs sem links, com o nome do host.</summary>
    private void AddSysfsDevice(string name)
    {
        string device = Path.Combine(Sys, "class", "hidraw", name, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "uevent"), "HID_ID=0003:00000820:00000001\nHID_NAME=Brymen Superior DMM\n");
    }
}

/// <summary>Nó do kernel falso de <see cref="FakeHidrawSyscalls"/>.</summary>
internal sealed class FakeHidrawNode
{
    /// <summary><see langword="false"/>: um nó que não é hidraw (todo ioctl do hidraw devolve ENOTTY).</summary>
    public bool IsHidraw { get; init; } = true;

    public uint Bus { get; init; } = 0x03; // BUS_USB

    public ushort Vendor { get; init; } = 0x0820;

    public ushort Product { get; init; } = 0x0001;

    /// <summary><c>hid->name</c>; no BC-86X, fabricante e produto do USB.</summary>
    public string Name { get; init; } = "Brymen Superior DMM";

    /// <summary><c>hid->uniq</c>; <see langword="null"/> simula um kernel sem <c>HIDIOCGRAWUNIQ</c>.</summary>
    public string? Uniq { get; init; } = "";

    public byte[] Descriptor { get; init; } = HidReportDescriptorTests.Bu86xLikeDescriptor;

    /// <summary>Erros devolvidos pelas próximas chamadas de <c>open</c>, um por chamada.</summary>
    public Queue<int> OpenErrors { get; } = new();

    /// <summary>Erros devolvidos pelas próximas chamadas de <c>ioctl</c>, um por chamada.</summary>
    public Queue<int> IoctlErrors { get; } = new();

    public static FakeHidrawNode Bc86x(string? uniq = "") => new() { Uniq = uniq };
}

/// <summary>
/// Kernel falso para <see cref="IHidrawSyscalls"/>: responde só aos números de ioctl do <c>linux/hidraw.h</c>
/// (escritos por extenso), com as structs no layout do kernel em little-endian (x64 e arm64), e confere que o
/// argumento tem os <c>_IOC_SIZE</c> bytes que o kernel escreveria.
/// </summary>
internal sealed class FakeHidrawSyscalls : IHidrawSyscalls
{
    private const int EBADF = 9;

    private readonly Dictionary<string, FakeHidrawNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FakeHidrawNode> _open = [];
    private int _nextFd = 3;

    public List<string> Opened { get; } = [];

    public List<uint> Requests { get; } = [];

    public int OpenDescriptors => _open.Count;

    public FakeHidrawNode Add(string path, FakeHidrawNode node)
    {
        _nodes[path] = node;
        return node;
    }

    public (int Result, int Errno) Open(string path)
    {
        Opened.Add(path);
        if (!_nodes.TryGetValue(path, out var node))
            return (-1, HidrawErrors.ENOENT);
        if (node.OpenErrors.TryDequeue(out int errno))
            return (-1, errno);
        int fd = _nextFd++;
        _open.Add(fd, node);
        return (fd, 0);
    }

    public (int Result, int Errno) Ioctl(int fd, nuint request, Span<byte> argument)
    {
        uint cmd = (uint)request;
        Requests.Add(cmd);
        int size = (int)((cmd >> 16) & 0x3FFF);
        Assert.True(argument.Length >= size, $"Argumento de {argument.Length} bytes para o ioctl {cmd:X8}, que escreve {size}.");
        if (!_open.TryGetValue(fd, out var node))
            return (-1, EBADF);
        if (node.IoctlErrors.TryDequeue(out int errno))
            return (-1, errno);
        if (!node.IsHidraw)
            return (-1, HidrawErrors.ENOTTY);

        switch (cmd)
        {
            case 0x80084803: // HIDIOCGRAWINFO: bustype (u32), vendor (s16), product (s16)
                BinaryPrimitives.WriteUInt32LittleEndian(argument, node.Bus);
                BinaryPrimitives.WriteInt16LittleEndian(argument[4..], unchecked((short)node.Vendor));
                BinaryPrimitives.WriteInt16LittleEndian(argument[6..], unchecked((short)node.Product));
                return (0, 0);
            case 0x81004804: // HIDIOCGRAWNAME(256)
                return CopyString(node.Name, argument[..size]);
            case 0x81004808: // HIDIOCGRAWUNIQ(256)
                return node.Uniq is null ? (-1, HidrawErrors.ENOTTY) : CopyString(node.Uniq, argument[..size]);
            case 0x80044801: // HIDIOCGRDESCSIZE
                BinaryPrimitives.WriteInt32LittleEndian(argument, node.Descriptor.Length);
                return (0, 0);
            case 0x90044802: // HIDIOCGRDESC: size (u32) na entrada, value[4096] na saída
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(argument);
                if (length > 4096 - 1)
                    return (-1, HidrawErrors.EINVAL);
                node.Descriptor.AsSpan(0, (int)Math.Min(length, (uint)node.Descriptor.Length)).CopyTo(argument[4..]);
                return (0, 0);
            default:
                return (-1, HidrawErrors.ENOTTY);
        }
    }

    public void Close(int fd) => Assert.True(_open.Remove(fd), $"close({fd}) de um descritor que não está aberto.");

    // Como o kernel: copia a string com o NUL, truncada ao tamanho do buffer, e devolve quantos bytes copiou.
    private static (int, int) CopyString(string value, Span<byte> destination)
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes(value), 0];
        int length = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, length).CopyTo(destination);
        return (length, 0);
    }
}
