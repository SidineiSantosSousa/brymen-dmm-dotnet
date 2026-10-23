using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Tests;

/// <summary>Importação da memória pelo <see cref="BrymenMeter"/>, com o cabo simulado e falhas injetadas.</summary>
public sealed class BrymenMeterLoggerTests
{
    private static readonly string[] Dump = ["66", "88", .. Enumerable.Repeat("89", 94)];

    [Fact]
    public async Task Reads_the_whole_memory_with_progress()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        var progress = new ProgressRecorder();

        var memory = await meter.ReadLoggerMemoryAsync(progress);

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage()[0], memory.Header.ModelId);
        Assert.Equal(2272, memory.Header.TotalBytes);
        var session = Assert.Single(memory.Sessions);
        Assert.Equal(375, session.Samples.Count);
        Assert.Equal("ACV", session.MainFunction);
        Assert.Equal(Dump, setup.Commands);
        Assert.Equal(Enumerable.Range(1, 95), progress.Items.Select(p => p.BlocksRead));
        Assert.All(progress.Items, p => Assert.Equal((95, 2272), (p.BlocksTotal, p.TotalBytes)));
        Assert.Equal(100, progress.Items[^1].Percent);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task Image_keeps_the_raw_blocks_retries_and_restarts()
    {
        var setup = new SimulatedSetup();
        int nexts = 0;
        // 89 nº 10 com checksum errado (8A), 89 nº 50 perdido sem processar (recomeço do 88).
        setup.Meter.FaultInjector = (command, _) => command != Bm52xCommands.MemoryNext ? SimulatedFault.None : ++nexts switch
        {
            10 => SimulatedFault.CorruptChecksum,
            50 => SimulatedFault.Drop,
            _ => SimulatedFault.None,
        };
        await using var meter = await setup.OpenAsync();

        var image = await meter.ReadLoggerImageAsync();

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        Assert.Equal(95, image.BlockCount);
        Assert.Equal(1, image.Retries);
        Assert.Equal(1, image.Restarts);
        Assert.Single(setup.Commands, c => c == "8A");
        Assert.Equal(2, setup.Commands.Count(c => c == "88"));
        Assert.Contains(image.Warnings, w => w.Contains("Recomeço 1", StringComparison.Ordinal));
        Assert.Null(meter.LastError);
    }

    [Theory]
    [InlineData(nameof(SimulatedFault.CorruptChecksum))]
    [InlineData(nameof(SimulatedFault.Truncate))]
    [InlineData(nameof(SimulatedFault.Drop))]
    [InlineData(nameof(SimulatedFault.DropAfterProcessing))]
    public async Task Failing_88_is_retried_with_88_never_with_8A(string faultName)
    {
        // FromCaptures: o simulado também avança como 89 um 8A logo depois do 88, como o medidor real.
        var fault = Enum.Parse<SimulatedFault>(faultName);
        var setup = new SimulatedSetup();
        Assert.Equal(SimulatedBm52xMeter.RealMeterCurrentAfterHeadWindow, setup.Meter.CurrentAfterHeadWindow);
        int heads = 0;
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryHead && ++heads == 1 ? fault : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();

        var image = await meter.ReadLoggerImageAsync();

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        Assert.Equal(["66", "88", "88", .. Enumerable.Repeat("89", 94)], setup.Commands);
        Assert.Equal(1, image.Retries);
    }

    [Theory]
    [InlineData(nameof(SimulatedFault.CorruptChecksum), true)]
    [InlineData(nameof(SimulatedFault.Truncate), true)]
    [InlineData(nameof(SimulatedFault.DropAfterProcessing), false)]
    [InlineData(nameof(SimulatedFault.Drop), false)]
    public async Task Failing_89_uses_8A_only_when_it_was_processed(string faultName, bool expect8A)
    {
        var fault = Enum.Parse<SimulatedFault>(faultName);
        var setup = new SimulatedSetup();
        int nexts = 0;
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryNext && ++nexts == 30 ? fault : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();

        var image = await meter.ReadLoggerImageAsync();

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        Assert.Equal(expect8A, setup.Commands.Contains("8A"));
        Assert.Equal(expect8A ? 0 : 1, image.Restarts);
    }

    [Fact]
    public async Task First_89_failing_right_after_the_88_is_recovered_with_8A_after_the_settle_delay()
    {
        // O 8A só sai depois do 89 e dos 200 ms de espera depois do 88: fora da janela em que ele avançaria como um 89.
        var setup = new SimulatedSetup();
        int nexts = 0;
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryNext && ++nexts == 1 ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();

        var image = await meter.ReadLoggerImageAsync();

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        Assert.Equal(["66", "88", "89", "8A", .. Enumerable.Repeat("89", 93)], setup.Commands);
    }

    [Fact]
    public async Task The_simulated_8A_right_after_the_88_advances_like_an_89()
    {
        // O risco que o leitor evita: 88 e 8A imediato devolvem o bloco 1, e não o cabeçalho (§2.7).
        var setup = new SimulatedSetup();
        using var connection = new MeterConnection(setup.Options);

        byte[] head = await connection.ExecuteAsync(Bm52xCommands.MemoryHead, 4);
        byte[] current = await connection.ExecuteAsync(Bm52xCommands.MemoryCurrent, 4);
        byte[] expectedBlock1 = SimulatedBm52xMeter.LoadDefaultMemoryImage()[24..48];

        Assert.Equal(expectedBlock1, Bm52xMemory.ParseBlock(current).Data);
        Assert.NotEqual(head, current);
    }

    [Fact]
    public async Task Import_failure_is_recorded_and_the_next_read_reopens_the_cable()
    {
        var setup = new SimulatedSetup();
        setup.Options.LoggerRead.HeadSettleDelay = TimeSpan.Zero;
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryHead ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerMemoryAsync());

        Assert.Equal(BrymenErrorCodes.TransmissionTimeout, ex.Code);
        Assert.Equal(MeterState.Faulted, meter.State);
        Assert.Equal(ex, meter.LastError!.Exception);
        Assert.Equal([BrymenErrorCodes.TransmissionTimeout], events.Errors.Select(e => e.Code));

        setup.Meter.FaultInjector = null;
        await meter.ReadAsync();
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task Meter_without_data_logger_is_E9017()
    {
        var setup = new SimulatedSetup();
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.RealTime ? SimulatedFault.CorruptModel : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());

        Assert.Equal(BrymenErrorCodes.NoDataLogger, ex.Code);
        Assert.Equal(["66"], setup.Commands);
    }

    [Fact]
    public async Task Continuous_reading_is_paused_during_the_import_and_resumed_afterwards()
    {
        var setup = new SimulatedSetup();
        setup.Options.PollInterval = TimeSpan.Zero;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        // Laço de eventos e uma enumeração ao mesmo tempo.
        meter.Start();
        using var cts = new CancellationTokenSource();
        int enumerated = 0;
        var enumeration = Task.Run(async () =>
        {
            await foreach (var _ in meter.ReadContinuouslyAsync(TimeSpan.Zero, cts.Token))
                Interlocked.Increment(ref enumerated);
        });
        await TestWait.UntilAsync(() => events.Readings.Count >= 5 && Volatile.Read(ref enumerated) >= 5, "as leituras contínuas");

        var image = await meter.ReadLoggerImageAsync();
        int eventsAfterImport = events.Readings.Count;
        int enumeratedAfterImport = Volatile.Read(ref enumerated);
        await TestWait.UntilAsync(() => events.Readings.Count >= eventsAfterImport + 5 && Volatile.Read(ref enumerated) >= enumeratedAfterImport + 5,
            "a retomada das leituras");
        await meter.StopAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        // Entre o 88 e o último 89 da importação, nenhum 66: a leitura contínua esperou.
        var commands = setup.Commands;
        int head = commands.ToList().IndexOf("88");
        Assert.True(head > 0);
        Assert.Equal(Enumerable.Repeat("89", 94), commands.Skip(head + 1).Take(94));
        Assert.Equal("66", commands[head - 1]); // a conferência do modelo
        Assert.Equal("66", commands[head + 95]);
        Assert.Empty(events.Errors);
        Assert.Equal(MeterState.Connected, meter.State);
    }
}
