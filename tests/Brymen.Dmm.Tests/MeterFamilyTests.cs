using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Tests;

/// <summary>Detecção da família na 1ª leitura (§8.6), a família fixa nas opções e a troca do medidor no cabo.</summary>
public sealed class MeterFamilyTests
{
    private static string Bm525sHex(int index) => Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[index]);

    private static string Et2517aHex(int index) => Convert.ToHexString(SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames[index]);

    [Fact]
    public async Task The_BM525s_is_detected_by_the_first_reading_and_then_read_with_52_66()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();

        Assert.Null(meter.Family);
        Assert.Null(meter.ModelDescription);
        Assert.True(meter.SupportsDataLogger);
        Assert.Empty(setup.Device.Writes); // a detecção não faz consulta extra na abertura

        var first = await meter.ReadAsync();
        var second = await meter.ReadAsync();

        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.Equal((MeterFamily.Bm52x, MeterFamily.Bm52x), (first.Family, second.Family));
        Assert.Equal((Bm525sHex(0), Bm525sHex(1)), (first.RawHex, second.RawHex));
        Assert.Equal(["8266", "5266"], setup.Requests);
        Assert.Equal("Minipa ET-2615A (Brymen BM525s, BM52x)", meter.ModelDescription);
        Assert.True(meter.SupportsDataLogger);
        Assert.Equal(new RealTimeStatistics(Queries: 2, Requests: 2, Resends: 0, LostRequests: 0, RecoveredQueries: 0, FailedQueries: 0), meter.RealTimeStatistics);
    }

    [Fact]
    public async Task The_ET2517A_is_detected_and_always_read_with_82_66()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        await using var meter = await setup.OpenAsync();
        IBrymenMeter view = meter;

        var readings = new List<DmmReading>();
        for (int i = 0; i < 3; i++)
            readings.Add(await meter.ReadAsync());

        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.All(readings, r => Assert.Equal(MeterFamily.Bm82x, r.Family));
        Assert.Equal([Et2517aHex(0), Et2517aHex(1), Et2517aHex(2)], readings.Select(r => r.RawHex));
        Assert.Equal(["8266", "8266", "8266"], setup.Requests);
        Assert.Equal("0.013", readings[0].Main!.Text);
        Assert.Equal("V", readings[0].Main!.UnitSymbol);
        Assert.Equal(MeasurementMode.AC, readings[0].Main!.Mode);
        Assert.False(meter.SupportsDataLogger);
        Assert.Equal("Minipa ET-2517A (Brymen BM827s, BM82x)", meter.ModelDescription);
        Assert.Equal((meter.Family, meter.SupportsDataLogger, meter.ModelDescription), (view.Family, view.SupportsDataLogger, view.ModelDescription));
        Assert.Equal(meter.RealTimeStatistics, view.RealTimeStatistics);
        Assert.Equal(3, meter.RealTimeStatistics.Queries);
        Assert.Null(meter.LastError);
    }

    [Fact]
    public async Task The_detection_falls_back_to_52_66_for_a_BM52x_that_ignores_82_66()
    {
        var bm52x = SimulatedBm52xMeter.FromCaptures();
        var setup = new SimulatedSetup(bm52x, responder: (id, data) => data.Span[1] == 0x82 ? null : bm52x.Respond(id, data));
        // A detecção é a espera longa, com reenvios do 82 66 até o prazo (aqui, 600 ms: envios em 0, 200 e 400 ms).
        setup.Options.RealTimeResendTimeout = TimeSpan.FromMilliseconds(200);
        setup.Options.RealTimeResendBudget = TimeSpan.FromMilliseconds(600);
        await using var meter = await setup.OpenAsync();

        var first = await meter.ReadAsync();
        await meter.ReadAsync();

        Assert.Equal(Bm525sHex(0), first.RawHex);
        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.Equal(["8266", "8266", "8266", "5266", "5266"], setup.Requests);
        var statistics = meter.RealTimeStatistics;
        Assert.Equal((2, 5, 2, 3, 0, 0), (statistics.Queries, statistics.Requests, statistics.Resends, statistics.LostRequests, statistics.RecoveredQueries, statistics.FailedQueries));
        Assert.Null(meter.LastError);
    }

    [Theory]
    [InlineData(MeterFamilySelection.Bm52x, "5266")]
    [InlineData(MeterFamilySelection.Bm82x, "8266")]
    public async Task A_fixed_family_skips_the_detection(MeterFamilySelection family, string request)
    {
        var setup = new SimulatedSetup(family == MeterFamilySelection.Bm82x ? SimulatedBm52xMeter.CreateBm82x() : null);
        setup.Options.Family = family;
        await using var meter = await setup.OpenAsync();

        Assert.Equal((MeterFamily)family, meter.Family);
        Assert.Equal(family == MeterFamilySelection.Bm52x, meter.SupportsDataLogger);
        var reading = await meter.ReadAsync();

        Assert.Equal((MeterFamily)family, reading.Family);
        Assert.Equal([request], setup.Requests);
    }

    [Fact]
    public async Task A_fixed_BM82x_family_refuses_the_frames_of_a_BM525s()
    {
        var setup = new SimulatedSetup();
        setup.Options.Family = MeterFamilySelection.Bm82x;
        await using var meter = await setup.OpenAsync();

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());

        Assert.Equal(BrymenErrorCodes.InvalidFrame, ex.Code);
        Assert.Contains("bytes 20..23 = 52525252 (BM52x), esperado 82828282 (BM82x)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.Equal(["8266"], setup.Requests);
    }

    [Fact]
    public async Task A_fixed_BM52x_family_is_never_detected_again()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        setup.Options.Family = MeterFamilySelection.Bm52x;
        await using var meter = await setup.OpenAsync();

        for (int i = 0; i < MeterConnection.FailuresBeforeRedetection + 1; i++)
            Assert.Equal(BrymenErrorCodes.NoDataReceived, (await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync())).Code);

        Assert.Equal(MeterFamily.Bm52x, meter.Family);
        Assert.All(setup.Requests, r => Assert.Equal("5266", r));
        Assert.Equal(4, meter.RealTimeStatistics.FailedQueries);
    }

    [Fact]
    public async Task A_meter_swapped_on_the_cable_is_detected_again()
    {
        var bm525s = SimulatedBm52xMeter.FromCaptures();
        var et2517a = SimulatedBm52xMeter.CreateBm82x();
        var current = bm525s;
        var setup = new SimulatedSetup(bm525s, responder: (id, data) => Volatile.Read(ref current).Respond(id, data));
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        await meter.ReadAsync();
        Assert.Equal(MeterFamily.Bm52x, meter.Family);

        // BM525s → ET-2517A: o 52 66 fica sem resposta, e depois de 3 consultas a família é detectada de novo.
        Volatile.Write(ref current, et2517a);
        for (int i = 0; i < MeterConnection.FailuresBeforeRedetection; i++)
        {
            Assert.Equal(MeterFamily.Bm52x, meter.Family);
            Assert.Equal(BrymenErrorCodes.NoDataReceived, (await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync())).Code);
        }
        Assert.Null(meter.Family);
        var bm82x = await meter.ReadAsync();
        Assert.Equal((MeterFamily.Bm82x, MeterFamily.Bm82x), (meter.Family, bm82x.Family));
        Assert.Equal(Et2517aHex(0), bm82x.RawHex);

        // ET-2517A → BM525s: o 82 66 já traz o Model ID 52, e a família muda sem nenhuma falha.
        Volatile.Write(ref current, bm525s);
        int errors = events.Errors.Count;
        var bm52x = await meter.ReadAsync();
        await meter.ReadAsync();

        Assert.Equal((MeterFamily.Bm52x, MeterFamily.Bm52x), (meter.Family, bm52x.Family));
        Assert.Equal(errors, events.Errors.Count);
        Assert.Equal(["8266", "5266", "5266", "5266", "8266", "8266", "5266"], setup.Requests);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task The_detection_is_the_first_query_of_the_connection_too()
    {
        var setup = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        using var connection = new MeterConnection(setup.Options);

        Assert.Null(connection.Family);
        Assert.True(connection.SupportsDataLogger);
        byte[] frame = await connection.ExecuteRealTimeAsync();
        var reading = await connection.ReadRealTimeAsync();
        connection.Close();
        await connection.ReadRealTimeAsync(); // reabrir o cabo não perde a família

        Assert.Equal(SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames[0], frame);
        Assert.Equal(MeterFamily.Bm82x, reading.Family);
        Assert.Equal(MeterFamily.Bm82x, connection.Family);
        Assert.False(connection.SupportsDataLogger);
        Assert.Equal(["8266", "8266", "8266"], setup.Requests);

        // Os comandos crus continuam com a família pedida: 52 por padrão, ou a do argumento.
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => connection.ExecuteAsync(Bm52xCommands.RealTime, 3));
        var result = await connection.TryExecuteAsync(Bm52xCommands.Bm82xFamily, Bm52xCommands.RealTime, 3);
        Assert.True(result.Succeeded);
        Assert.Equal(["8266", "8266", "8266", "5266", "8266"], setup.Requests);
    }

    [Fact]
    public void Invalid_family_options_are_refused()
    {
        var provider = new SimulatedHidDeviceProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, Family = (MeterFamilySelection)0x53 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, RealTimeResendTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, RealTimeResendBudget = TimeSpan.FromMilliseconds(-1) }));

        var defaults = new BrymenMeterOptions();
        Assert.Equal(MeterFamilySelection.Auto, defaults.Family);
        Assert.Equal(TimeSpan.FromMilliseconds(300), defaults.RealTimeResendTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.RealTimeResendBudget);
        Assert.Equal((MeterConnection.DefaultRealTimeResendTimeout, MeterConnection.DefaultRealTimeResendBudget), (defaults.RealTimeResendTimeout, defaults.RealTimeResendBudget));
    }
}
