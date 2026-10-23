using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Tests;

/// <summary>
/// A memória numa família sem datalogger (BM82x): E9014 sem nenhum comando de memória, na hora com a família fixa ou
/// confirmada há pouco, e depois da conferência do modelo com a família conhecida há mais tempo.
/// </summary>
public sealed class DataLoggerFamilyTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task The_ET2517A_refuses_the_memory_at_once_with_E9014()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        await meter.ReadAsync();
        int writes = setup.Device.Writes.Count;

        var memory = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerMemoryAsync());
        var image = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());

        Assert.Equal("E9014", BrymenErrorCodes.DataLoggerNotSupported);
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, memory.Code);
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, image.Code);
        Assert.Contains("BM82x (Minipa ET-2517A)", memory.Message, StringComparison.Ordinal);
        Assert.Contains("Model ID 82h", memory.Message, StringComparison.Ordinal);
        Assert.Contains("nenhum comando de memória", memory.Message, StringComparison.Ordinal);
        Assert.Equal(writes, setup.Device.Writes.Count);
        Assert.False(meter.SupportsDataLogger);
        // Não é falha de comunicação: nada muda no estado, e o tempo real continua.
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Null(meter.LastError);
        Assert.Empty(events.Errors);
        Assert.Equal(MeterFamily.Bm82x, (await meter.ReadAsync()).Family);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fixed_BM82x_family_refuses_the_memory_without_any_command(bool continuousReading)
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        setup.Options.Family = MeterFamilySelection.Bm82x;
        await using var meter = await setup.OpenAsync();
        if (continuousReading)
        {
            meter.Start();
            await TestWait.UntilAsync(() => setup.Device.Writes.Count >= 2, "a leitura contínua");
        }

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());
        await meter.StopAsync();

        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        Assert.All(setup.Requests, r => Assert.Equal("8266", r));
        if (!continuousReading)
            Assert.Empty(setup.Device.Writes);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task An_unknown_family_is_detected_by_the_model_check_of_the_import()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        Assert.Null(meter.Family);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerMemoryAsync());

        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        // Só a conferência do modelo (o 82 66 da detecção): nenhum 88.
        Assert.Equal(["8266"], setup.Requests);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Empty(events.Errors);
    }

    [Fact]
    public async Task A_BM82x_family_known_for_a_while_is_confirmed_by_the_model_check_and_a_swapped_BM525s_is_imported()
    {
        // O ET-2517A é lido, e o BM525s toma o lugar dele no cabo sem leitura contínua no meio: depois da janela de
        // confirmação, a importação não recusa pela família guardada, e a conferência do modelo (82 66) acha o BM52x.
        var time = new FakeTimeProvider();
        var et2517a = SimulatedBm52xMeter.CreateBm82x(timeProvider: time);
        var bm525s = SimulatedBm52xMeter.FromCaptures(time);
        var current = et2517a;
        var setup = new SimulatedSetup(et2517a, time, (id, data) => Volatile.Read(ref current).Respond(id, data));
        await using var meter = await setup.OpenAsync();
        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);

        // Logo depois da leitura, a família confirmada recusa na hora, sem nenhum comando.
        int writes = setup.Device.Writes.Count;
        var refused = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, refused.Code);
        Assert.Equal(writes, setup.Device.Writes.Count);

        time.Advance(MeterConnection.FamilyConfirmationWindow);
        Volatile.Write(ref current, bm525s);
        var memory = await TestWait.PumpAsync(time, meter.ReadLoggerMemoryAsync(), Step, Limit);

        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Equal(["8266", "8266", "5288", .. Enumerable.Repeat("5289", 94)], setup.Requests);
        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.True(meter.SupportsDataLogger);
    }

    [Fact]
    public async Task A_BM82x_family_known_for_a_while_is_refused_after_the_model_check()
    {
        var time = new FakeTimeProvider();
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x(timeProvider: time), time);
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);
        time.Advance(MeterConnection.FamilyConfirmationWindow);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => TestWait.PumpAsync(time, meter.ReadLoggerImageAsync(), Step, Limit));

        // Uma consulta de tempo real a mais (a conferência do modelo) e nenhum comando de memória.
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        Assert.Equal(["8266", "8266"], setup.Requests);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Null(meter.LastError);
        Assert.Empty(events.Errors);
    }

    [Fact]
    public async Task Without_the_model_check_a_known_BM82x_family_is_refused_at_once()
    {
        var time = new FakeTimeProvider();
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x(timeProvider: time), time);
        setup.Options.LoggerRead.VerifyModel = false;
        await using var meter = await setup.OpenAsync();
        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);
        time.Advance(TimeSpan.FromMinutes(1));

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerImageAsync());

        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        Assert.Equal(["8266"], setup.Requests);
    }

    [Fact]
    public async Task The_family_is_confirmed_only_for_a_while_after_the_last_frame()
    {
        var time = new FakeTimeProvider();
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x(timeProvider: time), time);
        using var connection = new MeterConnection(setup.Options);
        Assert.False(connection.IsFamilyConfirmed);

        await TestWait.PumpAsync(time, connection.ReadRealTimeAsync(), Step, Limit);
        Assert.True(connection.IsFamilyConfirmed);
        time.Advance(MeterConnection.FamilyConfirmationWindow - TimeSpan.FromMilliseconds(100));
        Assert.True(connection.IsFamilyConfirmed);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.False(connection.IsFamilyConfirmed);
        Assert.Equal(MeterFamily.Bm82x, connection.Family);
        Assert.False(connection.SupportsDataLogger);
        Assert.Equal(TimeSpan.FromSeconds(2), MeterConnection.FamilyConfirmationWindow);

        // A família fixa vale sempre, sem consulta.
        setup.Options.Family = MeterFamilySelection.Bm82x;
        using var fixedFamily = new MeterConnection(setup.Options);
        Assert.True(fixedFamily.IsFamilyConfirmed);
    }

    [Fact]
    public async Task The_BM525s_import_starts_with_the_detection_and_uses_52_for_the_memory()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();

        var memory = await meter.ReadLoggerMemoryAsync();

        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Equal(["8266", "5288", .. Enumerable.Repeat("5289", 94)], setup.Requests);
        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.True(meter.SupportsDataLogger);
    }

    [Fact]
    public async Task The_connection_refuses_the_memory_of_a_known_BM82x()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        setup.Options.Family = MeterFamilySelection.Bm82x;
        using var connection = new MeterConnection(setup.Options);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => connection.ReadLoggerImageAsync());

        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        Assert.False(connection.IsOpen);
        Assert.Empty(setup.Device.Writes);
    }

    [Fact]
    public async Task The_reader_refuses_a_BM82x_model_with_E9014_and_other_models_with_E9017()
    {
        var bm82x = new FrameTransport(SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames[0]);
        var unknown = new FrameTransport(Convert.FromHexString("00001000BFAFA0E90000BFAFAFAF00012006000000000010000000"));

        var refused = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(bm82x).ReadAsync());
        var noLogger = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(unknown).ReadAsync());

        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, refused.Code);
        Assert.Equal(1, bm82x.Commands);
        Assert.Equal(BrymenErrorCodes.NoDataLogger, noLogger.Code);
        Assert.Equal(1, unknown.Commands);
    }

    /// <summary>Transporte que responde a todo comando com o mesmo quadro de tempo real.</summary>
    private sealed class FrameTransport(byte[] frame) : ILoggerMemoryTransport
    {
        private int _commands;

        public int Commands => Volatile.Read(ref _commands);

        public Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _commands);
            return Task.FromResult(new MeterCommandResult(frame, null, reportCount));
        }

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
