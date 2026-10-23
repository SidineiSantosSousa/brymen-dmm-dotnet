using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit.Abstractions;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Teste com um BC-86X virtual criado pelo /dev/uhid do kernel: exercita o hidraw de verdade (open/poll/read/write
/// da libc, relatórios não numerados, remoção a quente). Só roda no Linux, com <c>BRYMEN_UHID_TESTS=1</c> e como
/// root (o /dev/uhid e o /dev/hidrawN criado são do root): <c>sudo -E BRYMEN_UHID_TESTS=1 dotnet test</c>.
/// </summary>
public sealed class UhidFactAttribute : FactAttribute
{
    public UhidFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Teste com /dev/uhid: só roda no Linux.";
        else if (Environment.GetEnvironmentVariable("BRYMEN_UHID_TESTS") != "1")
            Skip = "Teste com /dev/uhid desativado: defina BRYMEN_UHID_TESTS=1 e rode como root (sudo -E dotnet test).";
        else if (!File.Exists("/dev/uhid") || !Directory.Exists("/sys/class/hidraw"))
            Skip = "Sem /dev/uhid ou /sys/class/hidraw: o kernel precisa de CONFIG_UHID e CONFIG_HIDRAW.";
    }
}

public class LinuxUhidTests(ITestOutputHelper output)
{
    private static readonly Bu86xLinkOptions Options = new() { ReportTimeout = TimeSpan.FromSeconds(1) };

    [UhidFact]
    public async Task FramesTravelThroughTheRealHidrawNode()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var meter = new SimulatedBm52xMeter();
        using var uhid = new UhidBu86x(meter.Respond);
        var provider = new LinuxHidrawDeviceProvider();
        var info = uhid.WaitForHidraw(provider);
        output.WriteLine($"{info.Path} '{info.Product}' uniq={info.SerialNumber}");

        // Sem dispositivo USB pai (o pai é /sys/devices/virtual/misc/uhid): HID_NAME e HID_UNIQ.
        Assert.Equal(UhidBu86x.Name, info.Product);
        Assert.Equal(uhid.Uniq, info.SerialNumber);
        Assert.Null(info.ReleaseNumber);
        Assert.Equal(new HidReportLayout(8, 3, 0, false), provider.ReadReportLayout(info.Path));

        int before = CountOpenDescriptors(info.Path);
        using (var link = new Bu86xLink(provider.Open(info), Options))
        {
            Assert.Equal(before + 1, CountOpenDescriptors(info.Path));
            Assert.Equal(SimulatedBm52xMeter.DefaultRealTimeFrames[0], await link.ExecuteAsync(0x66, 3));
            Assert.True(TestData.HasValidChecksum(await link.ExecuteAsync(0x88, 4)));

            // O write leva o report ID 0 na frente; o kernel o entrega ao uhid junto com os 3 bytes do comando.
            var first = uhid.Outputs[0];
            output.WriteLine($"Saída vista pelo uhid: {Convert.ToHexString(first)}");
            Assert.Equal(new byte[] { 0x00, 0x52, 0x66 }, first[^3..]);

            meter.IsPoweredOn = false;
            var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3));
            Assert.Equal(0, ex.ReportsReceived);
            meter.IsPoweredOn = true;
            Assert.Equal(SimulatedBm52xMeter.DefaultRealTimeFrames[1], await link.ExecuteAsync(0x66, 3));
        }
        Assert.Equal(before, CountOpenDescriptors(info.Path));
    }

    [UhidFact]
    public async Task TimeoutCancellationAndLateReports()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var uhid = new UhidBu86x(new SimulatedBm52xMeter().Respond) { ResponseDelay = TimeSpan.FromMilliseconds(150) };
        var provider = new LinuxHidrawDeviceProvider();
        using var device = provider.Open(uhid.WaitForHidraw(provider));
        var buffer = new byte[8];

        var sw = Stopwatch.StartNew();
        // Sem relatório na fila, a espera roda no pool: a chamada volta na hora, com a tarefa em andamento.
        var pending = device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(100));
        Assert.False(pending.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50), $"ReadReportAsync bloqueou {sw.Elapsed} na thread de quem chamou.");
        await Assert.ThrowsAsync<TimeoutException>(async () => await pending);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(90), TimeSpan.FromSeconds(2));

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            sw.Restart();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(30), cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Cancelamento levou {sw.Elapsed}.");
        }

        // A resposta chega 150 ms depois: a leitura de 20 ms expira e os relatórios ficam na fila do kernel.
        await device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52, 0x66 });
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(20)));
        var frame = new byte[27];
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(2)));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        Assert.Equal(SimulatedBm52xMeter.DefaultRealTimeFrames[0], frame);
    }

    [UhidFact]
    public async Task DisposeDuringAReadFailsPromptlyAndClosesTheDescriptor()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var uhid = new UhidBu86x(new SimulatedBm52xMeter().Respond);
        var provider = new LinuxHidrawDeviceProvider();
        var info = uhid.WaitForHidraw(provider);
        int before = CountOpenDescriptors(info.Path);
        var device = provider.Open(info);

        // A espera (poll em fatias) já roda no pool; o Task.Run só garante que nada aqui bloqueia o teste.
        var pending = Task.Run(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        device.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"A leitura levou {sw.Elapsed} para perceber o Dispose.");
        Assert.Equal(before, CountOpenDescriptors(info.Path));
    }

    [UhidFact]
    public async Task HotUnplugFailsPendingAndLaterCallsAndTheDeviceCanBeReopened()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var uhid = new UhidBu86x(new SimulatedBm52xMeter().Respond);
        var provider = new LinuxHidrawDeviceProvider();
        var info = uhid.WaitForHidraw(provider);
        var device = provider.Open(info);

        var pending = Task.Run(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        uhid.Destroy();

        var readError = await Assert.ThrowsAsync<HidException>(() => pending);
        output.WriteLine($"Leitura pendente: {readError.Message} ({sw.ElapsedMilliseconds} ms)");
        Assert.Contains("desconectado", readError.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"A remoção levou {sw.Elapsed} para ser percebida.");
        var writeError = await Assert.ThrowsAsync<HidException>(async () => await device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52, 0x66 }));
        output.WriteLine($"Escrita depois da remoção: {writeError.Message}");
        Assert.Contains("desconectado", writeError.Message);
        await Assert.ThrowsAsync<HidException>(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromMilliseconds(100)));
        device.Dispose();
        Assert.Equal(0, CountOpenDescriptors(info.Path));

        // "Religa" o cabo: um novo hidrawN aparece na enumeração e funciona.
        uhid.Create();
        var again = uhid.WaitForHidraw(provider);
        output.WriteLine($"Reconectado em {again.Path}");
        using var link = new Bu86xLink(provider.Open(again), Options);
        Assert.Equal(27, (await link.ExecuteAsync(0x66, 3)).Length);
    }

    [UhidFact]
    public async Task NodeOutsideTheEnumerationIsIdentifiedAndOpenedByIoctl()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var uhid = new UhidBu86x(new SimulatedBm52xMeter().Respond);
        var enumerated = uhid.WaitForHidraw(new LinuxHidrawDeviceProvider());

        // Sem sysfs (como num contêiner sem /sys/class/hidraw) e por um link com outro nome.
        using var temp = new TempDirectory();
        string renamed = Path.Combine(temp.Path, "bc86x");
        File.CreateSymbolicLink(renamed, enumerated.Path);
        var provider = new LinuxHidrawDeviceProvider(Path.Combine(temp.Path, "sys"), Path.Combine(temp.Path, "dev"));
        Assert.Empty(provider.EnumerateAll());

        var info = provider.Probe(renamed);
        Assert.NotNull(info);
        output.WriteLine($"{info.Path} '{info.Product}' uniq={info.SerialNumber}");
        Assert.Equal((renamed, UhidBu86x.VendorId, UhidBu86x.ProductId, UhidBu86x.Name), (info.Path, info.VendorId, info.ProductId, info.Product));
        // HIDIOCGRAWUNIQ existe desde o Linux 5.6.
        Assert.True(info.SerialNumber is null || info.SerialNumber == uhid.Uniq, $"uniq inesperado: {info.SerialNumber}");
        Assert.Null(provider.Probe(Path.Combine(temp.Path, "nada")));
        Assert.Null(provider.Probe("/dev/null")); // não é hidraw: ENOTTY

        int before = CountOpenDescriptors(enumerated.Path);
        using (var link = new Bu86xLink(provider.Open(info), Options))
        {
            Assert.Equal((8, 3), (link.Device.InputReportLength, link.Device.OutputReportLength));
            Assert.Equal(SimulatedBm52xMeter.DefaultRealTimeFrames[0], await link.ExecuteAsync(0x66, 3));
        }
        Assert.Equal(before, CountOpenDescriptors(enumerated.Path));

        // O descriptor vem do próprio nó, não do sysfs (ausente neste provider).
        int fd = LibC.Open(renamed, LibC.O_RDWR | LibC.O_CLOEXEC | LibC.O_NONBLOCK);
        Assert.True(fd >= 0, $"open({renamed}) falhou: {HidrawErrors.Name(Marshal.GetLastPInvokeError())}.");
        try
        {
            Assert.Equal(new HidReportLayout(8, 3, 0, false), HidrawNode.TryReadReportLayout(LibCHidrawSyscalls.Instance, fd));
        }
        finally
        {
            LibC.Close(fd);
        }
    }

    /// <summary>Descritores deste processo abertos em <paramref name="devicePath"/> (inclusive os de um nó já removido).</summary>
    private static int CountOpenDescriptors(string devicePath)
    {
        int count = 0;
        foreach (string fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith(devicePath, StringComparison.Ordinal))
                    count++;
            }
            catch (IOException)
            {
                // Descritor fechado durante a enumeração.
            }
        }
        return count;
    }
}

/// <summary>
/// BC-86X virtual pelo /dev/uhid (Documentation/hid/uhid.rst): descriptor do BC-86X (8 bytes de entrada, 3 de
/// saída, sem report IDs) e um responder chamado a cada relatório de saída, numa thread própria.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class UhidBu86x : IDisposable
{
    public const ushort VendorId = 0x0820;

    // Diferente do cabo real (PID 0001), para não colidir com ele nem com o serviço, se estiverem na mesma máquina.
    public const ushort ProductId = 0x7E57;

    public const string Name = "Brymen BC-86X uhid";

    // struct uhid_event (empacotada): u32 type + união; a maior variante é uhid_create2_req (4372 bytes).
    private const int EventSize = 4376;
    private const uint UHID_DESTROY = 1;
    private const uint UHID_OUTPUT = 6;
    private const uint UHID_CREATE2 = 11;
    private const uint UHID_INPUT2 = 12;
    private const ushort BUS_USB = 3;

    private readonly Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> _responder;
    private readonly int _fd;
    private readonly Lock _lock = new();
    private readonly List<byte[]> _outputs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _loop;
    private Exception? _loopError;

    public UhidBu86x(Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> responder)
    {
        _responder = responder;
        _fd = LibC.Open("/dev/uhid", LibC.O_RDWR | LibC.O_CLOEXEC | LibC.O_NONBLOCK);
        if (_fd < 0)
            throw new IOException($"open(/dev/uhid) falhou: {HidrawErrors.Name(Marshal.GetLastPInvokeError())} (rode como root).");
        try
        {
            Create();
        }
        catch
        {
            LibC.Close(_fd);
            throw;
        }
        _loop = new Thread(Loop) { IsBackground = true, Name = "uhid BC-86X" };
        _loop.Start();
    }

    /// <summary>Identifica este dispositivo na enumeração (HID_UNIQ).</summary>
    public string Uniq { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Atraso entre o relatório de saída e o 1º relatório da resposta.</summary>
    public TimeSpan ResponseDelay { get; set; }

    /// <summary>Relatórios de saída recebidos, como o kernel os entrega ao uhid.</summary>
    public IReadOnlyList<byte[]> Outputs
    {
        get
        {
            lock (_lock)
            {
                if (_loopError is not null)
                    throw new InvalidOperationException("A thread do uhid falhou.", _loopError);
                return [.. _outputs];
            }
        }
    }

    /// <summary>"Liga" o cabo: UHID_CREATE2.</summary>
    public void Create()
    {
        var ev = new byte[EventSize];
        var span = ev.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, UHID_CREATE2);
        System.Text.Encoding.ASCII.GetBytes(Name, span.Slice(4, 127));
        System.Text.Encoding.ASCII.GetBytes("brymen-hid-tests", span.Slice(132, 63));
        System.Text.Encoding.ASCII.GetBytes(Uniq, span.Slice(196, 63));
        byte[] descriptor = HidReportDescriptorTests.Bu86xLikeDescriptor;
        BinaryPrimitives.WriteUInt16LittleEndian(span[260..], (ushort)descriptor.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[262..], BUS_USB);
        BinaryPrimitives.WriteUInt32LittleEndian(span[264..], VendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[268..], ProductId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[272..], 0x8603);
        BinaryPrimitives.WriteUInt32LittleEndian(span[276..], 0);
        descriptor.CopyTo(span[280..]);
        WriteEvent(ev);
    }

    /// <summary>"Arranca" o cabo: UHID_DESTROY remove o dispositivo HID e o seu hidrawN.</summary>
    public void Destroy()
    {
        var ev = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ev, UHID_DESTROY);
        WriteEvent(ev);
    }

    /// <summary>Espera o hidrawN deste dispositivo aparecer na enumeração e em /dev.</summary>
    public HidDeviceInfo WaitForHidraw(LinuxHidrawDeviceProvider provider)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var info = provider.Enumerate(VendorId, ProductId).FirstOrDefault(d => d.SerialNumber == Uniq);
            if (info is not null && File.Exists(info.Path))
                return info;
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"O hidraw do dispositivo uhid {Uniq} não apareceu em 5 s (hid-generic e CONFIG_HIDRAW ativos?).");
            Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loop.Join(TimeSpan.FromSeconds(2));
        try
        {
            Destroy();
        }
        catch (IOException)
        {
            // Já removido pelo teste.
        }
        LibC.Close(_fd);
    }

    private void Loop()
    {
        var ev = new byte[EventSize];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pfd = new LibC.PollFd { Fd = _fd, Events = LibC.POLLIN };
                if (LibC.Poll(&pfd, 1, 50) <= 0 || (pfd.Revents & LibC.POLLIN) == 0)
                    continue;
                nint n;
                fixed (byte* p = ev)
                    n = LibC.Read(_fd, p, (nuint)ev.Length);
                if (n < 4 || BinaryPrimitives.ReadUInt32LittleEndian(ev) != UHID_OUTPUT)
                    continue; // UHID_START, UHID_OPEN, UHID_CLOSE, UHID_STOP...

                // uhid_output_req: data[4096], u16 size, u8 rtype.
                int size = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(ev.AsSpan(4 + 4096)), 4096);
                byte[] data = ev.AsSpan(4, size).ToArray();
                lock (_lock)
                    _outputs.Add(data);
                if (data.Length == 0 || _responder(data[0], data.AsMemory(1)) is not { } reports)
                    continue;
                if (ResponseDelay > TimeSpan.Zero)
                    Thread.Sleep(ResponseDelay);
                foreach (var report in reports)
                {
                    SendInput(report);
                    Thread.Sleep(8); // intervalo entre relatórios do BC-86X real
                }
            }
        }
        catch (Exception ex)
        {
            // Uma exceção não tratada numa Thread derrubaria o processo de teste.
            lock (_lock)
                _loopError = ex;
        }
    }

    private void SendInput(ReadOnlySpan<byte> report)
    {
        // uhid_input2_req: u16 size, data[4096]; o resto do evento pode ser omitido.
        var ev = new byte[6 + report.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(ev, UHID_INPUT2);
        BinaryPrimitives.WriteUInt16LittleEndian(ev.AsSpan(4), (ushort)report.Length);
        report.CopyTo(ev.AsSpan(6));
        WriteEvent(ev);
    }

    private void WriteEvent(byte[] ev)
    {
        nint n;
        fixed (byte* p = ev)
            n = LibC.Write(_fd, p, (nuint)ev.Length);
        if (n < 0)
            throw new IOException($"write(/dev/uhid) falhou: {HidrawErrors.Name(Marshal.GetLastPInvokeError())}.");
    }
}
