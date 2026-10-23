using System.Diagnostics;
using System.Globalization;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Xunit.Abstractions;

namespace Brymen.Dmm.Tests;

/// <summary>
/// <see cref="BrymenMeter"/> com o cabo BC-86X e um medidor real (<c>BRYMEN_HW_TESTS=1</c>, medidor ligado): o BM525s/ET-2615A
/// (família BM52x) ou o ET-2517A (BM82x), detectado pelo Model ID da 1ª leitura. Só envia <c>82 66</c> e <c>52 66</c> e, no
/// BM52x, <c>52 88</c>, <c>52 89</c> e <c>52 8A</c>. No BM52x, a importação é comparada com
/// docs/protocol/captures/memory-dump-1.txt (a memória do medidor não pode ter mudado desde a captura); no BM82x, que não
/// tem datalogger, ela falha com <see cref="BrymenErrorCodes.DataLoggerNotSupported"/> sem nenhum comando de memória.
/// </summary>
[Collection(nameof(HardwareTests))]
public sealed class HardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task Opens_the_real_cable_and_reads_the_meter()
    {
        var devices = BrymenMeter.FindDevices();
        Assert.NotEmpty(devices);

        var provider = new RequestRecordingProvider(HidDeviceProviders.CreateForCurrentPlatform());
        await using var meter = await BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = provider });
        // A detecção não faz consulta extra: a família só é conhecida depois da 1ª leitura.
        Assert.Null(meter.Family);
        Assert.Empty(provider.Requests);

        var sw = Stopwatch.StartNew();
        DmmReading first;
        int failures = 0;
        try
        {
            first = await meter.ReadAsync();
        }
        catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.NoDataReceived)
        {
            // Visto uma vez no BM525s (2026-10-22): o 1º 66 depois de muito tempo sem comunicação ficou sem resposta em
            // 3 s, e os seguintes responderam normalmente. No ET-2517A, a espera longa do 1º 66 reenvia o pedido até 3 s,
            // mas as perdas se agrupam. Uma nova tentativa, como faz a leitura contínua; a falha fechou o cabo, e a nova
            // tentativa o reabre.
            output.WriteLine($"1º 66 sem resposta depois de {sw.Elapsed.TotalMilliseconds:0} ms; nova tentativa. ({ex.Message})");
            failures++;
            sw.Restart();
            first = await meter.ReadAsync();
        }
        var firstTime = sw.Elapsed;
        var family = Assert.NotNull(meter.Family);
        sw.Restart();
        var second = await meter.ReadAsync();

        output.WriteLine($"Cabo: {meter.Device.Path} ({meter.Device.Manufacturer} / {meter.Device.Product}, bcdDevice {meter.Device.ReleaseNumber:X4})");
        output.WriteLine($"Medidor: {meter.ModelDescription}, {(meter.SupportsDataLogger ? "com" : "sem")} datalogger.");
        output.WriteLine($"1ª leitura em {firstTime.TotalMilliseconds:0} ms: {Describe(first)}");
        output.WriteLine($"2ª leitura em {sw.Elapsed.TotalMilliseconds:0} ms: {Describe(second)}");
        output.WriteLine($"Pedidos: {string.Join(" ", provider.Requests)}; {meter.RealTimeStatistics}");
        Assert.Equal((Bu86xLink.VendorId, Bu86xLink.ProductId), (meter.Device.VendorId, meter.Device.ProductId));
        Assert.Equal(MeterState.Connected, meter.State);
        string model = ModelBytes(family);
        Assert.Equal(model, first.Diagnostics!.ModelBytes);
        Assert.Equal(model, second.Diagnostics!.ModelBytes);
        Assert.Equal((family, family), (first.Family, second.Family));
        Assert.Equal(family.HasDataLogger(), meter.SupportsDataLogger);
        Assert.Equal(family.Describe(), meter.ModelDescription);
        // A detecção é o 82 66 (as duas famílias o aceitam); depois, o tempo real usa o código da família.
        Assert.Equal("8266", provider.Requests[0]);
        Assert.Equal($"{family.Code():X2}66", provider.Requests[^1]);
        // Cada falha fecha o cabo, e a leitura seguinte o reabre.
        Assert.Equal(1 + failures, provider.Opens);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [HardwareFact]
    public async Task Reads_continuously_for_3_seconds()
    {
        await using var meter = await BrymenMeter.OpenAsync();
        var events = new EventRecorder(meter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var readings = new List<DmmReading>();

        try
        {
            await foreach (var reading in meter.ReadContinuouslyAsync(cancellationToken: cts.Token))
                readings.Add(reading);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        output.WriteLine($"{meter.ModelDescription}: {readings.Count} leituras em 3 s ({readings.Count / 3.0:0.0}/s com PollInterval de 200 ms).");
        output.WriteLine(meter.RealTimeStatistics.ToString());
        foreach (var r in readings.Take(3))
            output.WriteLine(Describe(r));
        // No BM82x, cada pedido descartado custa a janela de 300 ms do reenvio, e uma consulta com o orçamento esgotado
        // (2 s) é um W7012 isolado.
        Assert.InRange(readings.Count, meter.Family == MeterFamily.Bm82x ? 4 : 10, 16);
        AssertOnlyIsolatedLosses(meter, events);
    }

    [HardwareFact]
    public async Task Event_loop_delivers_readings_from_the_real_meter()
    {
        await using var meter = await BrymenMeter.OpenAsync();
        var events = new EventRecorder(meter);

        meter.Start();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await meter.StopAsync();

        output.WriteLine($"{meter.ModelDescription}: {events.Readings.Count} leituras por evento em 2 s. {meter.RealTimeStatistics}");
        Assert.InRange(events.Readings.Count, meter.Family == MeterFamily.Bm82x ? 2 : 6, 12);
        AssertOnlyIsolatedLosses(meter, events);
    }

    // BM52x: nenhuma falha. BM82x, que descarta pedidos: só W7012 isolados, de consultas com o orçamento dos reenvios
    // esgotado, e nenhum E9016 (3 falhas seguidas).
    private void AssertOnlyIsolatedLosses(BrymenMeter meter, EventRecorder events)
    {
        var stats = meter.RealTimeStatistics;
        foreach (var error in events.Errors)
            output.WriteLine($"{error.Code}: {error.Message}");
        if (meter.Family != MeterFamily.Bm82x)
        {
            Assert.Empty(events.Errors);
            Assert.Null(meter.LastError);
            Assert.Equal(0, stats.FailedQueries);
            return;
        }
        Assert.DoesNotContain(events.Errors, e => e.Code == BrymenErrorCodes.TransmissionTimeout);
        Assert.All(events.Errors, e => Assert.Equal(BrymenErrorCodes.NoDataReceived, e.Code));
        Assert.Equal(stats.FailedQueries, events.Errors.Count);
    }

    [HardwareFact]
    public async Task Reads_continuously_at_500_ms_without_reopening_the_cable_for_recoverable_losses()
    {
        // A 500 ms, o ET-2517A ficou ocioso o bastante para descartar até ≈ 47 % dos pedidos na medição de 2026-10-23
        // (§8.5.1). Um pedido perdido é reenviado sem fechar o cabo: só a consulta com o orçamento esgotado é falha (W7012),
        // e só ela reabre o cabo.
        var provider = new RequestRecordingProvider(HidDeviceProviders.CreateForCurrentPlatform());
        await using var meter = await BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = provider });
        var events = new EventRecorder(meter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int readings = 0;

        try
        {
            await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(500), cts.Token))
                readings++;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        var stats = meter.RealTimeStatistics;
        output.WriteLine($"{meter.ModelDescription}: {readings} leituras em 10 s; {stats}; {provider.Requests.Count} pedidos, {provider.Opens} abertura(s) do cabo.");
        foreach (var error in events.Errors)
            output.WriteLine($"{error.Code}: {error.Message}");
        Assert.InRange(readings, 10, 21);
        Assert.DoesNotContain(events.Errors, e => e.Code == BrymenErrorCodes.TransmissionTimeout);
        Assert.All(events.Errors, e => Assert.Equal(BrymenErrorCodes.NoDataReceived, e.Code));
        Assert.Equal(stats.FailedQueries, events.Errors.Count);
        Assert.Equal(1 + stats.FailedQueries, provider.Opens);
        Assert.Equal(readings, stats.Queries - stats.FailedQueries);
        Assert.Equal(provider.Requests.Count, stats.Requests);
        // Cada leitura gastou 1 pedido atendido; os demais se perderam, menos os da consulta cancelada no fim (≤ 7 envios).
        Assert.InRange(stats.Requests - readings - stats.LostRequests, 0, 7);
        if (meter.Family == MeterFamily.Bm52x)
            Assert.Equal(0, stats.Resends); // o BM525s nunca descartou pedidos
    }

    [HardwareFact]
    public async Task Imports_the_memory_identical_to_the_capture_and_reads_again_without_W7012()
    {
        var provider = new RequestRecordingProvider(HidDeviceProviders.CreateForCurrentPlatform());
        await using var meter = await BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = provider });
        var events = new EventRecorder(meter);
        var progress = new ProgressRecorder();

        var sw = Stopwatch.StartNew();
        LoggerMemoryImage image;
        try
        {
            image = await meter.ReadLoggerImageAsync(progress);
        }
        catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.DataLoggerNotSupported)
        {
            await AssertNoDataLoggerAsync(meter, provider, events, progress, ex, sw.Elapsed);
            return;
        }
        var importTime = sw.Elapsed;
        byte[] expected = LoadCaptureImage();
        sw.Restart();
        // 1º 66 depois do último bloco: ≈ 2,06 s no BM525s, dentro dos 3 s da política.
        var after = await meter.ReadAsync();
        var slowTime = sw.Elapsed;
        sw.Restart();
        var next = await meter.ReadAsync();

        var memory = image.Decode();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Importação: {image.BlockCount} blocos em {importTime.TotalMilliseconds:0} ms, {image.Retries} retentativa(s), {image.Restarts} recomeço(s)."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TotalBytes {image.Header.TotalBytes}, {memory.Sessions.Count} sessão(ões), {memory.Sessions.Sum(s => s.Samples.Count)} amostras."));
        output.WriteLine($"1º 66 depois da memória: {slowTime.TotalMilliseconds:0} ms ({Describe(after)}); seguinte: {sw.Elapsed.TotalMilliseconds:0} ms ({Describe(next)}).");
        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.Equal(expected, image.ToArray());
        Assert.Equal(95, progress.Items[^1].BlocksRead);
        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    // BM82x: a conferência do modelo (uma consulta de tempo real, a detecção) achou a família sem datalogger, e a leitura
    // falhou com E9014 sem nenhum comando de memória, sem mudar o estado nem o LastError e sem ErrorOccurred. Se a 1ª
    // tentativa da conferência ficou sem resposta (pedidos descartados), o leitor fechou o cabo antes da 2ª, que o reabriu.
    private async Task AssertNoDataLoggerAsync(BrymenMeter meter, RequestRecordingProvider provider, EventRecorder events,
        ProgressRecorder progress, BrymenMeterException ex, TimeSpan elapsed)
    {
        output.WriteLine($"{ex.Code} em {elapsed.TotalMilliseconds:0} ms: {ex.Message}");
        output.WriteLine($"Pedidos: {string.Join(" ", provider.Requests)}; {provider.Opens} abertura(s) do cabo");
        int opens = provider.Opens;
        Assert.InRange(opens, 1, 2);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.False(meter.SupportsDataLogger);
        Assert.Empty(progress.Items);
        Assert.NotEmpty(provider.Requests);
        Assert.All(provider.Requests, r => Assert.EndsWith("66", r, StringComparison.Ordinal));
        Assert.Equal("8266", provider.Requests[^1]);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);

        // Com a família confirmada há pouco (menos de MeterConnection.FamilyConfirmationWindow), a recusa sai na hora, sem
        // nenhum comando.
        int before = provider.Requests.Count;
        var sw = Stopwatch.StartNew();
        var again = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerMemoryAsync());
        output.WriteLine($"2ª tentativa: {again.Code} em {sw.Elapsed.TotalMilliseconds:0} ms.");
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, again.Code);
        Assert.Equal(before, provider.Requests.Count);

        // O tempo real continua normal depois da recusa.
        var reading = await meter.ReadAsync();
        output.WriteLine($"Leitura depois do E9014: {Describe(reading)}");
        Assert.Equal(ModelBytes(MeterFamily.Bm82x), reading.Diagnostics!.ModelBytes);
        Assert.DoesNotContain(provider.Requests, r => r[2..] is "88" or "89" or "8A");
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        // A recusa e a leitura seguinte não reabriram o cabo.
        Assert.Equal(opens, provider.Opens);
    }

    private static string ModelBytes(MeterFamily family) => Convert.ToHexString([family.Code(), family.Code(), family.Code(), family.Code()]);

    private static string Describe(DmmReading r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.Main?.Text} {r.Main?.UnitSymbol} {r.Main?.Mode} | {r.Secondary?.Text} {r.Secondary?.UnitSymbol} | {r.RawHex}");

    // Os 24 bytes úteis de cada bloco da captura, na ordem do ponteiro (88 e depois os 89).
    private static byte[] LoadCaptureImage()
    {
        var image = new List<byte>();
        foreach (string line in File.ReadLines(TestData.PathOf("memory-dump-1.txt")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] is not ("88" or "89"))
                continue;
            var frame = Convert.FromHexString(parts[1]);
            image.AddRange(Bm52xMemory.ParseBlock(frame).Data);
        }
        return [.. image];
    }
}

/// <summary>
/// Os testes de hardware usam o mesmo cabo, um de cada vez. Com <c>BRYMEN_HW_TESTS=1</c>, o projeto também roda os alvos
/// net8.0 e net10.0 um depois do outro (<c>TestTfmsInParallel</c>); não rode ao mesmo tempo os de outro projeto.
/// </summary>
[CollectionDefinition(nameof(HardwareTests), DisableParallelization = true)]
public sealed class HardwareTestsCollection;
