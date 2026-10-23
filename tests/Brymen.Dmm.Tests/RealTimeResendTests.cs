using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Tests;

/// <summary>
/// O reenvio do tempo real do BM82x (§8.5): o pedido perdido sai de novo sem fechar o cabo, e só o orçamento esgotado é
/// falha. Relógio falso no cabo, no medidor e nas opções.
/// </summary>
public sealed class RealTimeResendTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // O ET-2517A atrás do cabo simulado, com uma latência dentro da medida (36 a 237 ms) e 8 ms entre os relatórios.
    private static SimulatedSetup Et2517a(FakeTimeProvider time, SimulatedBm52xMeter? meter = null)
    {
        var setup = new SimulatedSetup(meter ?? SimulatedBm52xMeter.CreateBm82x(timeProvider: time), time);
        setup.Device.ResponseLatency = TimeSpan.FromMilliseconds(60);
        setup.Device.InterReportDelay = TimeSpan.FromMilliseconds(8);
        return setup;
    }

    [Fact]
    public async Task Lost_requests_are_resent_and_are_not_failures()
    {
        var time = new FakeTimeProvider();
        var setup = Et2517a(time);
        int realTime = 0;
        // O 2º e o 3º pedidos se perdem: a 2ª consulta sai na 3ª tentativa, 600 ms depois do 1º envio.
        setup.Meter.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Increment(ref realTime) is 2 or 3 ? SimulatedFault.Drop : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);
        long start = time.GetTimestamp();
        var reading = await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);

        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromMilliseconds(676), TimeSpan.FromMilliseconds(720));
        Assert.Equal(Convert.ToHexString(SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames[1]), reading.RawHex);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(["8266", "8266", "8266", "8266"], setup.Requests);
        Assert.Equal(new RealTimeStatistics(Queries: 2, Requests: 4, Resends: 2, LostRequests: 2, RecoveredQueries: 1, FailedQueries: 0), meter.RealTimeStatistics);
        Assert.True(setup.Device.IsOpen);
    }

    [Fact]
    public async Task An_exhausted_budget_is_W7012_with_the_number_of_sends()
    {
        var time = new FakeTimeProvider();
        var setup = Et2517a(time);
        int realTime = 0;
        setup.Meter.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Increment(ref realTime) > 1 ? SimulatedFault.Drop : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);

        long start = time.GetTimestamp();
        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit));

        // Envios a cada 300 ms até o orçamento de 2 s: 0, 300, ..., 1800 ms.
        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2050));
        Assert.Equal(BrymenErrorCodes.NoDataReceived, ex.Code);
        var timeout = Assert.IsType<Bu86xTimeoutException>(ex.InnerException);
        Assert.Equal((0x82, 7, 0), (timeout.Family, timeout.Attempts, timeout.ReportsReceived));
        Assert.Contains("comando 82 66 em 7 envios", ex.Message, StringComparison.Ordinal);
        Assert.Equal([BrymenErrorCodes.NoDataReceived], events.Errors.Select(e => e.Code));
        Assert.Equal(new RealTimeStatistics(Queries: 2, Requests: 8, Resends: 6, LostRequests: 7, RecoveredQueries: 0, FailedQueries: 1), meter.RealTimeStatistics);
    }

    [Fact]
    public async Task The_budget_and_the_window_come_from_the_options()
    {
        var time = new FakeTimeProvider();
        var setup = Et2517a(time);
        setup.Options.RealTimeResendTimeout = TimeSpan.FromMilliseconds(100);
        setup.Options.RealTimeResendBudget = TimeSpan.FromMilliseconds(450);
        int realTime = 0;
        setup.Meter.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Increment(ref realTime) > 1 ? SimulatedFault.Drop : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();
        await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit));

        // Envios em 0, 100, 200, 300 e 400 ms; o último espera os 50 ms que sobram.
        Assert.Equal(5, Assert.IsType<Bu86xTimeoutException>(ex.InnerException).Attempts);
    }

    [Fact]
    public async Task The_long_wait_resends_with_an_unknown_or_BM82x_family_and_is_a_single_send_with_a_known_BM52x()
    {
        // §8.5.4: na espera longa do 1º 66, o pedido é reenviado na detecção (família desconhecida) e no BM82x, e não no
        // BM52x conhecido. Depois, o BM82x usa a janela de reenvio e o BM52x, o ReportTimeout.
        var et2517a = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        var fixedBm82x = new SimulatedSetup(SimulatedBm52xMeter.CreateBm82x());
        fixedBm82x.Options.Family = MeterFamilySelection.Bm82x;
        var bm525s = new SimulatedSetup();
        var fixedBm52x = new SimulatedSetup();
        fixedBm52x.Options.Family = MeterFamilySelection.Bm52x;
        var timeouts = new List<IReadOnlyList<(byte, TimeSpan)>>();
        foreach (var setup in new[] { et2517a, fixedBm82x, bm525s, fixedBm52x })
        {
            var recording = new TimeoutRecordingProvider(setup.Provider);
            setup.Options.DeviceProvider = recording;
            setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);
            setup.Options.RealTimeResendTimeout = TimeSpan.FromMilliseconds(250);
            await using var meter = await BrymenMeter.OpenAsync(setup.Options);
            await meter.ReadAsync();
            await meter.ReadAsync();
            await meter.ReadAsync();
            timeouts.Add(recording.FirstReportTimeouts);
        }

        TimeSpan slow = TimeSpan.FromSeconds(3), window = TimeSpan.FromMilliseconds(250), report = TimeSpan.FromMilliseconds(150);
        Assert.Equal([(0x66, window), (0x66, window), (0x66, window)], timeouts[0].Select(t => ((int)t.Item1, t.Item2)));
        Assert.Equal([(0x66, window), (0x66, window), (0x66, window)], timeouts[1].Select(t => ((int)t.Item1, t.Item2)));
        Assert.Equal([(0x66, window), (0x66, report), (0x66, report)], timeouts[2].Select(t => ((int)t.Item1, t.Item2)));
        Assert.Equal([(0x66, slow), (0x66, report), (0x66, report)], timeouts[3].Select(t => ((int)t.Item1, t.Item2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task After_an_answered_memory_command_the_long_wait_is_a_single_send(bool verifyModel)
    {
        // Só o BM52x responde aos comandos de memória: depois deles, a espera longa não reenvia, também com a família ainda
        // desconhecida (sem a conferência do modelo, a importação não passa pela detecção).
        var setup = new SimulatedSetup();
        var recording = new TimeoutRecordingProvider(setup.Provider);
        setup.Options.DeviceProvider = recording;
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);
        setup.Options.RealTimeResendTimeout = TimeSpan.FromMilliseconds(250);
        setup.Options.LoggerRead.VerifyModel = verifyModel;
        await using var meter = await BrymenMeter.OpenAsync(setup.Options);

        await meter.ReadLoggerMemoryAsync();
        Assert.Equal(verifyModel ? MeterFamily.Bm52x : null, meter.Family);
        int before = recording.FirstReportTimeouts.Count;
        var reading = await meter.ReadAsync();

        Assert.Equal(MeterFamily.Bm52x, reading.Family);
        Assert.Equal((0x66, TimeSpan.FromSeconds(3)), ((int)recording.FirstReportTimeouts[before].Command, recording.FirstReportTimeouts[before].Timeout));
        Assert.Equal(verifyModel ? "5266" : "8266", setup.Requests[^1]);
    }

    [Fact]
    public async Task A_dropped_first_request_after_a_long_idle_time_is_resent_within_the_long_wait()
    {
        // O 1º pedido depois da abertura vem depois do maior tempo ocioso, e é o que o ET-2517A mais descarta (§8.5.1).
        // Aqui, o medidor ficou 10 s sem responder e descarta os 7 primeiros pedidos: com os reenvios até o prazo de 3 s
        // da espera longa, o 8º, 2,1 s depois do 1º, é atendido, sem W7012 e sem reabrir o cabo.
        var time = new FakeTimeProvider();
        var setup = Et2517a(time);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);
        setup.Meter.RequestDropper = (idle, consecutiveDrops) => idle >= TimeSpan.FromSeconds(5) && consecutiveDrops < 7;
        time.Advance(TimeSpan.FromSeconds(10));
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        long start = time.GetTimestamp();
        var reading = await TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit);

        // 2100 ms até o 8º envio, 60 ms de latência e 2 × 8 ms entre os relatórios.
        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromMilliseconds(2176), TimeSpan.FromMilliseconds(2220));
        Assert.Equal(MeterFamily.Bm82x, reading.Family);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(Enumerable.Repeat("8266", 8), setup.Requests);
        Assert.Equal(7, setup.Meter.DroppedRequestCount);
        Assert.Equal(new RealTimeStatistics(Queries: 1, Requests: 8, Resends: 7, LostRequests: 7, RecoveredQueries: 1, FailedQueries: 0), meter.RealTimeStatistics);
        Assert.True(setup.Device.IsOpen);
    }

    [Fact]
    public async Task A_silent_meter_at_the_first_query_is_resent_until_the_long_deadline_and_then_tried_with_52_66()
    {
        var time = new FakeTimeProvider();
        var setup = Et2517a(time);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);
        setup.Meter.IsPoweredOn = false;
        await using var meter = await setup.OpenAsync();

        long start = time.GetTimestamp();
        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => TestWait.PumpAsync(time, meter.ReadAsync(), Step, Limit));

        // 82 66 a cada 300 ms até os 3 s (10 envios), o descarte de recuperação de 300 ms (a janela do reenvio) e o
        // 52 66 de reserva, com o ReportTimeout de 150 ms.
        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromMilliseconds(3450), TimeSpan.FromMilliseconds(3500));
        Assert.Equal(BrymenErrorCodes.NoDataReceived, ex.Code);
        Assert.Equal([.. Enumerable.Repeat("8266", 10), "5266"], setup.Requests);
        Assert.Equal(new RealTimeStatistics(Queries: 1, Requests: 11, Resends: 9, LostRequests: 11, RecoveredQueries: 0, FailedQueries: 1), meter.RealTimeStatistics);
        Assert.Null(meter.Family);
    }

    [Fact]
    public async Task The_BM525s_never_resends()
    {
        var setup = new SimulatedSetup();
        int realTime = 0;
        setup.Meter.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Increment(ref realTime) == 2 ? SimulatedFault.Drop : SimulatedFault.None;
        await using var meter = await setup.OpenAsync();

        await meter.ReadAsync();
        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());
        await meter.ReadAsync();

        Assert.Equal(BrymenErrorCodes.NoDataReceived, ex.Code);
        Assert.Equal("Sem resposta do medidor ao comando 52 66.", ex.InnerException!.Message);
        Assert.Equal(["8266", "5266", "5266"], setup.Requests);
        Assert.Equal(new RealTimeStatistics(Queries: 3, Requests: 3, Resends: 0, LostRequests: 1, RecoveredQueries: 0, FailedQueries: 1), meter.RealTimeStatistics);
    }

    [Fact]
    public async Task Continuous_reading_of_the_ET2517A_at_200_ms_has_no_W7012_nor_E9016()
    {
        // O ET-2517A com a curva de descarte medida (§8.5.1) e o polling padrão de 200 ms. Com a política antiga (1 s,
        // W7012 e o cabo reaberto a cada perda), ≈ 27 % das consultas virariam W7012 e as perdas agrupadas chegariam ao
        // E9016; com os reenvios, toda perda recuperável é recuperada, e só a estatística as mostra.
        var time = new FakeTimeProvider();
        var et2517a = SimulatedBm52xMeter.FromEt2517aCaptures(time);
        var setup = Et2517a(time, et2517a);
        setup.Options.DrainTimeout = TimeSpan.FromMilliseconds(5);
        setup.Options.RecoveryDrainTimeout = TimeSpan.FromMilliseconds(100);
        setup.Options.RealTimeAfterMemoryTimeout = MeterConnection.DefaultRealTimeAfterMemoryTimeout;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        const int Count = 50;

        var readings = await TestWait.PumpAsync(time, Task.Run(async () =>
        {
            var list = new List<DmmReading>();
            await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(200)))
            {
                list.Add(reading);
                if (list.Count == Count)
                    break;
            }
            return list;
        }), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(2));

        Assert.Empty(events.Errors);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
        var frames = SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames;
        Assert.Equal(Enumerable.Range(0, Count).Select(i => Convert.ToHexString(frames[i % frames.Count])), readings.Select(r => r.RawHex));
        Assert.All(readings, r => Assert.Equal(MeterFamily.Bm82x, r.Family));

        var statistics = meter.RealTimeStatistics;
        Assert.Equal(Count, statistics.Queries);
        Assert.Equal(0, statistics.FailedQueries);
        Assert.True(statistics.Resends > 0, $"nenhum reenvio: {statistics}");
        Assert.True(statistics.RecoveredQueries > 0, $"nenhuma consulta recuperada: {statistics}");
        Assert.Equal(statistics.Resends, statistics.LostRequests);
        Assert.Equal(statistics.Queries + statistics.Resends, statistics.Requests);
        Assert.Equal(et2517a.DroppedRequestCount, statistics.LostRequests);
        Assert.All(setup.Requests, r => Assert.Equal("8266", r));
    }
}
