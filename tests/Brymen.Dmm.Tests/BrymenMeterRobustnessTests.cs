using System.Diagnostics;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Tests;

/// <summary>
/// O <see cref="BrymenMeter"/> com transportes que fogem do caminho feliz: um <see cref="IHidDevice"/> que bloqueia de
/// forma síncrona (como o <c>poll()</c> do hidraw) e um <see cref="IHidDeviceProvider"/> que lança fora do contrato.
/// </summary>
public sealed class BrymenMeterRobustnessTests
{
    private static readonly TimeSpan Block = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Operations_return_to_the_caller_before_any_blocking_io()
    {
        var setup = new SimulatedSetup();
        var provider = new BlockingProvider(setup.Provider);
        setup.Options.DeviceProvider = provider;
        await using var meter = await BrymenMeter.OpenAsync(setup.Options);

        // ReadAsync: a 1ª leitura do relatório bloqueia; a chamada volta antes, com a tarefa em andamento.
        provider.BlockNext(Block);
        var sw = Stopwatch.StartNew();
        var read = meter.ReadAsync();
        var returnedAfter = sw.Elapsed;
        Assert.False(read.IsCompleted);
        Assert.True(returnedAfter < Block, $"ReadAsync prendeu a thread de quem chamou por {returnedAfter}.");
        Assert.Equal(Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[0]), (await read).RawHex);

        // ReadLoggerImageAsync.
        provider.BlockNext(Block);
        sw.Restart();
        var image = meter.ReadLoggerImageAsync();
        returnedAfter = sw.Elapsed;
        Assert.False(image.IsCompleted);
        Assert.True(returnedAfter < Block, $"ReadLoggerImageAsync prendeu a thread de quem chamou por {returnedAfter}.");
        Assert.Equal(95, (await image).BlockCount);

        // ReadContinuouslyAsync com intervalo zero: cada MoveNextAsync também volta antes da E/S.
        await using var readings = meter.ReadContinuouslyAsync(TimeSpan.Zero).GetAsyncEnumerator();
        for (int i = 0; i < 3; i++)
        {
            provider.BlockNext(Block);
            sw.Restart();
            var move = readings.MoveNextAsync();
            returnedAfter = sw.Elapsed;
            Assert.False(move.IsCompleted);
            Assert.True(returnedAfter < Block, $"MoveNextAsync prendeu a thread de quem chamou por {returnedAfter}.");
            Assert.True(await move);
        }
    }

    [Fact]
    public async Task A_provider_that_throws_outside_the_contract_is_an_INTERNAL_failure_and_the_meter_recovers()
    {
        var setup = new SimulatedSetup();
        var provider = new FlakyProvider(setup.Provider);
        setup.Options.DeviceProvider = provider;
        await using var meter = await BrymenMeter.OpenAsync(setup.Options);
        var events = new EventRecorder(meter);
        await meter.ReadAsync();

        // Uma falha de comunicação fecha o cabo; a leitura seguinte o reabre pelo provider, que agora lança.
        setup.Meter.IsPoweredOn = false;
        var noData = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());
        Assert.Equal(BrymenErrorCodes.NoDataReceived, noData.Code);
        setup.Meter.IsPoweredOn = true;
        provider.OpenFailure = new InvalidOperationException("provider com defeito");

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());

        Assert.Equal(BrymenErrorCodes.Internal, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        // Antes, o estado ficava em Connecting para sempre, sem LastError nem ErrorOccurred.
        Assert.Equal(MeterState.Faulted, meter.State);
        Assert.Equal(BrymenErrorCodes.Internal, meter.LastError?.Code);
        Assert.Contains(events.Errors, e => e.Code == BrymenErrorCodes.Internal && e.Exception?.InnerException is InvalidOperationException);
        Assert.Equal(MeterState.Faulted, events.States[^1]);

        // O mesmo na leitura da memória.
        var logger = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());
        Assert.Equal(BrymenErrorCodes.Internal, logger.Code);
        Assert.Equal(MeterState.Faulted, meter.State);

        provider.OpenFailure = null;
        await meter.ReadAsync();
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task An_exception_from_the_progress_handler_is_an_INTERNAL_failure_of_the_import()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync(new ThrowingProgress()));

        Assert.Equal(BrymenErrorCodes.Internal, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(MeterState.Faulted, meter.State);
        // A próxima operação reabre o cabo e funciona.
        Assert.Equal(95, (await meter.ReadLoggerImageAsync()).BlockCount);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task The_simulated_meter_of_OpenSimulatedAsync_is_the_one_of_the_default_provider()
    {
        Assert.Equal(SimulatedHidDevice.TypicalResponseLatency, BrymenMeter.SimulatedLatency);
        await using var simulated = await BrymenMeter.OpenSimulatedAsync();
        await using var provided = await BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = new SimulatedHidDeviceProvider() });

        for (int i = 0; i < 4; i++)
            Assert.Equal((await simulated.ReadAsync()).RawHex, (await provided.ReadAsync()).RawHex);
        Assert.Equal(simulated.Device, provided.Device);
    }

    [Fact]
    public async Task The_facade_interface_carries_the_operations()
    {
        await using var meter = await BrymenMeter.OpenSimulatedAsync();
        IBrymenMeter operations = meter;

        DmmReading reading = await operations.ReadAsync();
        LoggerMemory memory = await operations.ReadLoggerMemoryAsync();

        Assert.Equal("V", reading.Main!.Unit);
        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Same(meter.Device, operations.Device);
        Assert.Equal(MeterState.Connected, operations.State);
        Assert.Null(operations.LastError);
    }

    /// <summary>Repassa ao provider simulado, mas os dispositivos abertos bloqueiam a thread na próxima leitura armada.</summary>
    private sealed class BlockingProvider(SimulatedHidDeviceProvider inner) : IHidDeviceProvider
    {
        private long _blockTicks;

        public void BlockNext(TimeSpan duration) => Interlocked.Exchange(ref _blockTicks, duration.Ticks);

        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => inner.Enumerate(vendorId, productId);

        public IHidDevice Open(HidDeviceInfo device) => new BlockingDevice(inner.Open(device), this);

        public TimeSpan TakeBlock() => TimeSpan.FromTicks(Interlocked.Exchange(ref _blockTicks, 0));
    }

    /// <summary>Tudo síncrono: a escrita e a leitura terminam antes de devolver, como o hidraw antes da correção.</summary>
    private sealed class BlockingDevice(IHidDevice inner, BlockingProvider owner) : IHidDevice
    {
        public HidDeviceInfo Info => inner.Info;

        public int InputReportLength => inner.InputReportLength;

        public int OutputReportLength => inner.OutputReportLength;

        public ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            try
            {
                inner.WriteReportAsync(reportId, data, cancellationToken).AsTask().GetAwaiter().GetResult();
                return ValueTask.CompletedTask;
            }
            catch (Exception ex)
            {
                return ValueTask.FromException(ex);
            }
        }

        public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var block = owner.TakeBlock();
            if (block > TimeSpan.Zero)
                Thread.Sleep(block);
            try
            {
                return ValueTask.FromResult(inner.ReadReportAsync(buffer, timeout, cancellationToken).AsTask().GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Repassa ao provider simulado, mas a abertura lança <see cref="OpenFailure"/> enquanto ele não for nulo.</summary>
    private sealed class FlakyProvider(SimulatedHidDeviceProvider inner) : IHidDeviceProvider
    {
        public Exception? OpenFailure { get; set; }

        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => inner.Enumerate(vendorId, productId);

        public IHidDevice Open(HidDeviceInfo device) => OpenFailure is { } failure ? throw failure : inner.Open(device);
    }

    private sealed class ThrowingProgress : IProgress<LoggerReadProgress>
    {
        public void Report(LoggerReadProgress value) => throw new InvalidOperationException("progresso com defeito");
    }
}
