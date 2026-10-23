using System.Diagnostics;
using Xunit.Abstractions;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Testes com o cabo BC-86X real e o medidor ligado (<c>BRYMEN_HW_TESTS=1</c>), pelo transporte nativo da plataforma:
/// hidraw no Linux e hid.dll no Windows. Só usam os comandos de leitura 52 66, 52 88, 52 89 e 52 8A.
/// </summary>
public class HardwareTests(ITestOutputHelper output)
{
    private static readonly Bu86xLinkOptions Options = new() { ReportTimeout = TimeSpan.FromSeconds(1) };

    private static readonly byte[] ModelId = [0x52, 0x52, 0x52, 0x52];

    [HardwareFact]
    public void EnumeratesTheCable()
    {
        var provider = HidDeviceProviders.CreateForCurrentPlatform();
        var info = Assert.Single(provider.Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId));
        output.WriteLine($"{info.Path} ver=0x{info.ReleaseNumber:X4} '{info.Manufacturer}' '{info.Product}' serial={Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(info.SerialNumber ?? ""))}");

        if (OperatingSystem.IsWindows())
            Assert.Contains("vid_0820&pid_0001", info.Path, StringComparison.OrdinalIgnoreCase);
        else
            Assert.StartsWith("/dev/", info.Path);
        Assert.Equal((ushort)0x8603, info.ReleaseNumber);
        Assert.Equal("Brymen", info.Manufacturer);
        Assert.Equal("Superior DMM", info.Product);

        using var device = provider.Open(info);
        Assert.Equal(8, device.InputReportLength);
        Assert.Equal(3, device.OutputReportLength);
        if (OperatingSystem.IsWindows())
        {
            var windows = Assert.IsType<WindowsHidDevice>(device);
            Assert.Equal((ushort)0xFF00, windows.UsagePage);
            Assert.Equal((ushort)0x0001, windows.Usage);
        }
        else if (OperatingSystem.IsLinux())
        {
            // O BC-86X não usa report IDs: o read do hidraw devolve só os 8 bytes de dados.
            var layout = Assert.IsType<LinuxHidrawDeviceProvider>(provider).ReadReportLayout(info.Path);
            output.WriteLine($"report_descriptor: {layout}");
            Assert.False(layout.UsesReportIds);
        }
    }

    [HardwareFact]
    public async Task RealTimeAndMemoryFramesThroughBu86xLink()
    {
        using var link = Bu86xLink.Open(HidDeviceProviders.CreateForCurrentPlatform(), options: Options);

        var sw = Stopwatch.StartNew();
        byte[] rtd = await link.ExecuteAsync(0x66, 3);
        output.WriteLine($"66 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(rtd)}");
        Assert.Equal(27, rtd.Length);
        Assert.All(new[] { 0, 9, 18 }, i => Assert.Equal(0x00, rtd[i]));
        Assert.Equal(ModelId, rtd[19..23]); // bytes 20–23 (Model ID)

        sw.Restart();
        byte[] head = await link.ExecuteAsync(0x88, 4);
        output.WriteLine($"88 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(head)}");
        Assert.Equal(36, head.Length);
        Assert.All(new[] { 0, 9, 18, 27 }, i => Assert.Equal(0x00, head[i]));
        Assert.True(TestData.HasValidChecksum(head), "Checksum do 88 inválido.");

        sw.Restart();
        byte[] next = await link.ExecuteAsync(0x89, 4);
        output.WriteLine($"89 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(next)}");
        Assert.True(TestData.HasValidChecksum(next), "Checksum do 89 inválido.");

        // 8A logo após um 89 relê o bloco do 89 (24/24 em hardware). Logo após o 88 não é confiável:
        // em ~metade das tentativas o medidor avança como num 89.
        sw.Restart();
        byte[] again = await link.ExecuteAsync(0x8A, 4);
        output.WriteLine($"8A ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(again)}");
        Assert.Equal(next, again);

        sw.Restart();
        byte[] rtd2 = await link.ExecuteAsync(0x66, 3);
        output.WriteLine($"66 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(rtd2)}");
        Assert.Equal(ModelId, rtd2[19..23]);
    }

    [LinuxHardwareFact]
    public async Task CableIsFoundAndOpenedByPathWithoutSysfs()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var info = Assert.Single(new LinuxHidrawDeviceProvider().Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId));

        // Como num contêiner sem /sys/class/hidraw: a enumeração fica vazia e o nó é consultado por ioctl.
        using var temp = new TempDirectory();
        var provider = new LinuxHidrawDeviceProvider(Path.Combine(temp.Path, "sys"));
        var probed = Bu86xLink.FindDevice(provider, info.Path);
        Assert.NotNull(probed);
        output.WriteLine($"{probed.Path} '{probed.Product}' serial={Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(probed.SerialNumber ?? ""))}");
        Assert.Equal((info.Path, Bu86xLink.VendorId, Bu86xLink.ProductId), (probed.Path, probed.VendorId, probed.ProductId));
        Assert.Contains("Superior DMM", probed.Product); // hid->name: fabricante e produto do USB ("Brymen Superior DMM")

        using var link = Bu86xLink.Open(provider, info.Path, Options);
        Assert.Equal((8, 3), (link.Device.InputReportLength, link.Device.OutputReportLength));
        Assert.Equal(ModelId, (await link.ExecuteAsync(0x66, 3))[19..23]);
    }

    [HardwareFact]
    public async Task ReadTimeoutCancelsThePendingReadWithoutLosingTheNextReport()
    {
        using var device = OpenCable(out _);
        var buffer = new byte[8];
        await DrainAsync(device);

        await device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52, 0x66 });
        // O 1º relatório do 66 leva ≥ 50 ms: esta leitura expira e o ReadFile/poll pendente é abandonado.
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(1)));

        var frame = new byte[27];
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(1)));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        output.WriteLine($"66 após timeout: {Convert.ToHexString(frame)}");
        Assert.Equal(ModelId, frame[19..23]);
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(100)));
    }

    [HardwareFact]
    public async Task ZeroTimeoutReturnsReportsAlreadyQueued()
    {
        using var device = OpenCable(out _);
        var buffer = new byte[8];
        await DrainAsync(device);

        await device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52, 0x66 });
        await Task.Delay(400); // os 3 relatórios chegam em ≈ 70–90 ms

        var frame = new byte[27];
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        output.WriteLine($"66 com timeout 0: {Convert.ToHexString(frame)}");
        Assert.Equal(ModelId, frame[19..23]);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.Zero));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Leitura com timeout 0 levou {sw.Elapsed}.");
    }

    [HardwareFact]
    public async Task DisposeDuringAPendingReadFailsPromptlyAndTheCableReopens()
    {
        var device = OpenCable(out var provider);
        await DrainAsync(device);

        // Sem comando o medidor não envia nada: a leitura fica pendente até o Dispose.
        var pending = Task.Run(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        Assert.False(pending.IsCompleted, "A leitura terminou sem relatório nem Dispose.");

        var sw = Stopwatch.StartNew();
        device.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        output.WriteLine($"Leitura pendente falhou {sw.ElapsedMilliseconds} ms depois do Dispose.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"A leitura pendente levou {sw.Elapsed} para falhar.");

        using var link = Bu86xLink.Open(provider, options: Options);
        Assert.Equal(ModelId, (await link.ExecuteAsync(0x66, 3))[19..23]);
    }

    [HardwareFact]
    public async Task LateReportsOfACancelledCommandAreDrainedBeforeTheNextOne()
    {
        using var link = Bu86xLink.Open(HidDeviceProviders.CreateForCurrentPlatform(), options: Options);
        await link.ExecuteAsync(0x66, 3); // conclui o descarte inicial

        // Cancelado ≈ 30 ms depois da escrita, antes do 1º relatório (≈ 65 ms): a resposta inteira chega depois.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => link.ExecuteAsync(0x66, 3, cts.Token));

        var sw = Stopwatch.StartNew();
        byte[] frame = await link.ExecuteAsync(0x66, 3);
        output.WriteLine($"66 depois do cancelamento ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(frame)}");
        Assert.Equal(ModelId, frame[19..23]);

        // Se a resposta atrasada tivesse sido tomada pela do 2º comando, a do 2º ainda estaria na fila.
        await Assert.ThrowsAsync<TimeoutException>(async () => await link.Device.ReadReportAsync(new byte[8], TimeSpan.FromMilliseconds(300)));
    }

    private static IHidDevice OpenCable(out IHidDeviceProvider provider)
    {
        provider = HidDeviceProviders.CreateForCurrentPlatform();
        var info = Assert.Single(provider.Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId));
        return provider.Open(info);
    }

    private static async Task DrainAsync(IHidDevice device)
    {
        var buffer = new byte[device.InputReportLength];
        try
        {
            while (true)
                await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
        }
    }
}
