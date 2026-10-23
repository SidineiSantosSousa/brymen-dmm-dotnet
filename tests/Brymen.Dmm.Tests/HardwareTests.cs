using System.Diagnostics;
using System.Globalization;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Xunit.Abstractions;

namespace Brymen.Dmm.Tests;

/// <summary>
/// <see cref="BrymenMeter"/> com o cabo BC-86X e o BM525s reais (<c>BRYMEN_HW_TESTS=1</c>, medidor ligado). Só envia
/// <c>52 66</c>, <c>88</c>, <c>89</c> e <c>8A</c>. A importação é comparada com docs/protocol/captures/memory-dump-1.txt:
/// a memória do medidor não pode ter mudado desde a captura.
/// </summary>
[Collection(nameof(HardwareTests))]
public sealed class HardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task Opens_the_real_cable_and_reads_the_meter()
    {
        var devices = BrymenMeter.FindDevices();
        Assert.NotEmpty(devices);

        await using var meter = await BrymenMeter.OpenAsync();
        var sw = Stopwatch.StartNew();
        DmmReading first;
        try
        {
            first = await meter.ReadAsync();
        }
        catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.NoDataReceived)
        {
            // Visto uma vez no BM525s (2026-10-22): o 1º 66 depois de muito tempo sem comunicação ficou sem resposta em
            // 3 s, e os seguintes responderam normalmente. Uma nova tentativa, como faz a leitura contínua.
            output.WriteLine($"1º 66 sem resposta depois de {sw.Elapsed.TotalMilliseconds:0} ms; nova tentativa. ({ex.Message})");
            sw.Restart();
            first = await meter.ReadAsync();
        }
        var firstTime = sw.Elapsed;
        sw.Restart();
        var second = await meter.ReadAsync();

        output.WriteLine($"Cabo: {meter.Device.Path} ({meter.Device.Manufacturer} / {meter.Device.Product}, bcdDevice {meter.Device.ReleaseNumber:X4})");
        output.WriteLine($"1ª leitura em {firstTime.TotalMilliseconds:0} ms: {Describe(first)}");
        output.WriteLine($"2ª leitura em {sw.Elapsed.TotalMilliseconds:0} ms: {Describe(second)}");
        Assert.Equal((Bu86xLink.VendorId, Bu86xLink.ProductId), (meter.Device.VendorId, meter.Device.ProductId));
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal("52525252", first.Diagnostics!.ModelBytes);
        Assert.Equal("52525252", second.Diagnostics!.ModelBytes);
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

        output.WriteLine($"{readings.Count} leituras em 3 s ({readings.Count / 3.0:0.0}/s com PollInterval de 200 ms).");
        foreach (var r in readings.Take(3))
            output.WriteLine(Describe(r));
        Assert.InRange(readings.Count, 10, 16);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
    }

    [HardwareFact]
    public async Task Event_loop_delivers_readings_from_the_real_meter()
    {
        await using var meter = await BrymenMeter.OpenAsync();
        var events = new EventRecorder(meter);

        meter.Start();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await meter.StopAsync();

        output.WriteLine($"{events.Readings.Count} leituras por evento em 2 s.");
        Assert.InRange(events.Readings.Count, 6, 12);
        Assert.Empty(events.Errors);
    }

    [HardwareFact]
    public async Task Imports_the_memory_identical_to_the_capture_and_reads_again_without_W7012()
    {
        await using var meter = await BrymenMeter.OpenAsync();
        var events = new EventRecorder(meter);
        var progress = new ProgressRecorder();
        byte[] expected = LoadCaptureImage();

        var sw = Stopwatch.StartNew();
        var image = await meter.ReadLoggerImageAsync(progress);
        var importTime = sw.Elapsed;
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
        Assert.Equal(expected, image.ToArray());
        Assert.Equal(95, progress.Items[^1].BlocksRead);
        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
    }

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
