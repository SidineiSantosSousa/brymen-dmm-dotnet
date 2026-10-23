using System.Diagnostics;
using Xunit.Abstractions;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Testes com o cabo BC-86X real e o medidor ligado (<c>BRYMEN_HW_TESTS=1</c>), pelo transporte nativo da plataforma:
/// hidraw no Linux e hid.dll no Windows. Servem para o BM525s/ET-2615A (família BM52x) e para o ET-2517A (BM82x): cada
/// teste detecta a família com <c>82 66</c>, que as duas aceitam, pelo Model ID dos bytes 20–23 (§8.6 da especificação).
/// Só usam os comandos de leitura <c>82 66</c> e <c>52 66</c> e, no BM52x, <c>52 88</c>, <c>52 89</c> e <c>52 8A</c>; no
/// BM82x, que não tem datalogger, nenhum comando de memória é enviado. O BM82x descarta parte dos pedidos de tempo real
/// (§8.5): os testes reenviam o pedido sem resposta, como a biblioteca.
/// </summary>
public class HardwareTests(ITestOutputHelper output)
{
    private const byte Bm52x = 0x52;
    private const byte Bm82x = 0x82;
    private const byte RealTime = 0x66;

    // Envios de um mesmo pedido de tempo real, nos testes que falam com o dispositivo cru, antes de desistir no BM82x.
    private const int MaxRawAttempts = 8;

    private static readonly Bu86xLinkOptions Options = new() { ReportTimeout = TimeSpan.FromSeconds(1) };

    // A detecção reenvia o 82 66 a cada 300 ms sem o 1º relatório, até 3 s: o ET-2517A pode descartar justamente ele.
    private static readonly Bu86xResendPolicy Detection = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3));

    // Espera pelo 1º relatório antes de reenviar o pedido cru no BM82x: acima da latência medida (36–258 ms).
    private static readonly TimeSpan RawResendWindow = TimeSpan.FromMilliseconds(400);

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
        byte family = await DetectAsync(link);

        var sw = Stopwatch.StartNew();
        if (family == Bm82x)
        {
            // O BM82x só responde ao 82 66 (§8.1): o 52 66 fica sem nenhum relatório. Sem datalogger, 88/89/8A não saem.
            var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(RealTime, 3));
            output.WriteLine($"52 66 ({sw.ElapsedMilliseconds} ms): {ex.Message}");
            Assert.Equal((Bm52x, RealTime, 0, 1), (ex.Family, ex.Command, ex.ReportsReceived, ex.Attempts));

            sw.Restart();
            byte[] frame82 = await link.ExecuteAsync(Bm82x, RealTime, 3, Bu86xResendPolicy.Default);
            output.WriteLine($"82 66 ({sw.ElapsedMilliseconds} ms, {link.LastCommandAttempts} envio(s)): {Convert.ToHexString(frame82)}");
            AssertRealTimeFrame(frame82, Bm82x);
            output.WriteLine("BM82x sem datalogger: 88, 89 e 8A não foram enviados.");
            return;
        }

        byte[] rtd = await link.ExecuteAsync(RealTime, 3);
        output.WriteLine($"52 66 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(rtd)}");
        AssertRealTimeFrame(rtd, Bm52x);

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
        byte[] rtd2 = await link.ExecuteAsync(RealTime, 3);
        output.WriteLine($"52 66 ({sw.ElapsedMilliseconds} ms): {Convert.ToHexString(rtd2)}");
        AssertRealTimeFrame(rtd2, Bm52x);
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
        await DetectAsync(link);
    }

    [HardwareFact]
    public async Task ReadTimeoutCancelsThePendingReadWithoutLosingTheNextReport()
    {
        using var device = OpenCable(out _);
        var buffer = new byte[8];
        byte family = await DetectAsync(device);
        await DrainAsync(device);

        int n;
        for (int attempt = 1; ; attempt++)
        {
            await device.WriteReportAsync(0x00, new byte[] { 0x00, family, RealTime });
            // O 1º relatório do 66 leva ≥ 36 ms: esta leitura expira e o ReadFile/poll pendente é abandonado.
            await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(1)));
            try
            {
                n = await device.ReadReportAsync(buffer, FirstReportWait(family));
                break;
            }
            catch (TimeoutException) when (family == Bm82x && attempt < MaxRawAttempts)
            {
                output.WriteLine($"Envio {attempt} sem resposta (pedido descartado pelo BM82x): reenvio.");
            }
        }

        var frame = new byte[27];
        Assert.Equal(8, n);
        buffer.CopyTo(frame, 1);
        for (int r = 1; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(1)));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        output.WriteLine($"{family:X2} 66 após timeout: {Convert.ToHexString(frame)}");
        AssertRealTimeFrame(frame, family);
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.FromMilliseconds(100)));
    }

    [HardwareFact]
    public async Task ZeroTimeoutReturnsReportsAlreadyQueued()
    {
        using var device = OpenCable(out _);
        var buffer = new byte[8];
        byte family = await DetectAsync(device);
        await DrainAsync(device);

        var frame = new byte[27];
        for (int attempt = 1; ; attempt++)
        {
            await device.WriteReportAsync(0x00, new byte[] { 0x00, family, RealTime });
            // Os 3 relatórios chegam em ≈ 70–90 ms no BM525s e em até ≈ 260 ms no ET-2517A.
            await Task.Delay(family == Bm82x ? 600 : 400);
            try
            {
                Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
                break;
            }
            catch (TimeoutException) when (family == Bm82x && attempt < MaxRawAttempts)
            {
                output.WriteLine($"Envio {attempt} sem resposta (pedido descartado pelo BM82x): reenvio.");
            }
        }
        buffer.CopyTo(frame, 1);
        for (int r = 1; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        output.WriteLine($"{family:X2} 66 com timeout 0: {Convert.ToHexString(frame)}");
        AssertRealTimeFrame(frame, family);

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
        await DetectAsync(link);
    }

    [HardwareFact]
    public async Task LateReportsOfACancelledCommandAreDrainedBeforeTheNextOne()
    {
        // O descarte de recuperação precisa cobrir a latência do medidor: ≈ 65 ms no BM525s, até ≈ 260 ms no ET-2517A.
        var options = new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(1), RecoveryDrainTimeout = TimeSpan.FromMilliseconds(300) };
        using var link = Bu86xLink.Open(HidDeviceProviders.CreateForCurrentPlatform(), options: options);
        byte family = await DetectAsync(link);
        // Conclui o descarte inicial com uma troca de um envio só: depois de um reenvio, o próximo comando começaria com o
        // descarte longo, e o cancelamento abaixo cairia nele, antes da escrita.
        for (int i = 0; i < 5 && link.LastCommandAttempts != 1; i++)
            await RealTimeAsync(link, family);
        Assert.Equal(1, link.LastCommandAttempts);

        // Cancelado ≈ 30 ms depois da escrita, antes do 1º relatório (≥ 36 ms): a resposta inteira chega depois. No BM82x,
        // o pedido pode ter sido descartado, e então não há resposta atrasada.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RealTimeAsync(link, family, cts.Token));

        var sw = Stopwatch.StartNew();
        byte[] frame = await RealTimeAsync(link, family);
        output.WriteLine($"{family:X2} 66 depois do cancelamento ({sw.ElapsedMilliseconds} ms, {link.LastCommandAttempts} envio(s)): {Convert.ToHexString(frame)}");
        AssertRealTimeFrame(frame, family);

        // Se a resposta atrasada tivesse sido tomada pela do 2º comando, a do 2º ainda estaria na fila.
        await Assert.ThrowsAsync<TimeoutException>(async () => await link.Device.ReadReportAsync(new byte[8], TimeSpan.FromMilliseconds(300)));
    }

    // Detecção pelo Bu86xLink: 82 66 com reenvio; devolve o Model ID (52h ou 82h).
    private async Task<byte> DetectAsync(Bu86xLink link)
    {
        var sw = Stopwatch.StartNew();
        byte[] frame = await link.ExecuteAsync(Bm82x, RealTime, 3, Detection);
        output.WriteLine($"82 66 ({sw.ElapsedMilliseconds} ms, {link.LastCommandAttempts} envio(s)): {Convert.ToHexString(frame)}");
        return AssertRealTimeFrame(frame);
    }

    // Detecção pelo dispositivo cru: 82 66, reenviado a cada 400 ms sem o 1º relatório; devolve o Model ID. Também deixa o
    // medidor recém-atendido: logo depois de uma troca, o ET-2517A quase não descarta pedidos (§8.5.1).
    private async Task<byte> DetectAsync(IHidDevice device)
    {
        var buffer = new byte[8];
        var frame = new byte[27];
        await DrainAsync(device);
        for (int attempt = 1; ; attempt++)
        {
            await device.WriteReportAsync(0x00, new byte[] { 0x00, Bm82x, RealTime });
            try
            {
                Assert.Equal(8, await device.ReadReportAsync(buffer, RawResendWindow));
                output.WriteLine($"82 66 respondido no envio {attempt}.");
                break;
            }
            catch (TimeoutException) when (attempt < MaxRawAttempts)
            {
            }
        }
        buffer.CopyTo(frame, 1);
        for (int r = 1; r < 3; r++)
        {
            Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(1)));
            buffer.CopyTo(frame, r * 9 + 1);
        }
        output.WriteLine($"82 66: {Convert.ToHexString(frame)}");
        return AssertRealTimeFrame(frame);
    }

    // Tempo real com a família: 52 66 num envio só no BM52x (como antes da BM82x), 82 66 com reenvio no BM82x.
    private static Task<byte[]> RealTimeAsync(Bu86xLink link, byte family, CancellationToken cancellationToken = default)
        => family == Bm82x
            ? link.ExecuteAsync(Bm82x, RealTime, 3, Bu86xResendPolicy.Default, cancellationToken)
            : link.ExecuteAsync(RealTime, 3, cancellationToken);

    // No BM52x, um envio com a espera de sempre; no BM82x, a janela antes do reenvio.
    private static TimeSpan FirstReportWait(byte family) => family == Bm82x ? RawResendWindow : TimeSpan.FromSeconds(1);

    // Report IDs 00h e o Model ID dos bytes 20–23: os quatro iguais, 52h (BM52x) ou 82h (BM82x). Devolve o Model ID.
    private static byte AssertRealTimeFrame(byte[] frame, byte? expectedFamily = null)
    {
        Assert.Equal(27, frame.Length);
        Assert.All(new[] { 0, 9, 18 }, i => Assert.Equal(0x00, frame[i]));
        byte model = frame[19];
        Assert.Contains(model, new[] { Bm52x, Bm82x });
        Assert.Equal(new[] { model, model, model, model }, frame[19..23]); // bytes 20–23 (Model ID)
        if (expectedFamily is { } expected)
            Assert.Equal(expected, model);
        return model;
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
