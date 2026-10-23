using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Brymen.Dmm.Api.Tests;

public sealed class MeterFamilyOptionsTests
{
    [Fact]
    public void Defaults_detect_the_family_and_use_the_core_resend_policy()
    {
        var options = new MeterOptions();

        Assert.Equal(MeterFamilySelection.Auto, options.Family);
        Assert.Equal(MeterConnection.DefaultRealTimeResendTimeout, options.RealTimeResendTimeout);
        Assert.Equal(MeterConnection.DefaultRealTimeResendBudget, options.RealTimeResendBudget);
        Assert.Equal(SimulatedMeterModel.Bm525s, options.SimulatedModel);
        Assert.True(new MeterOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(MeterFamilySelection.Auto, true)]
    [InlineData(MeterFamilySelection.Bm52x, true)]
    [InlineData(MeterFamilySelection.Bm82x, true)]
    [InlineData((MeterFamilySelection)0x53, false)]
    public void Family_is_validated(MeterFamilySelection family, bool valid)
        => Assert.Equal(valid, new MeterOptionsValidator().Validate(null, new MeterOptions { Family = family }).Succeeded);

    [Theory]
    [InlineData(SimulatedMeterModel.Bm525s, true)]
    [InlineData(SimulatedMeterModel.Et2517a, true)]
    [InlineData((SimulatedMeterModel)7, false)]
    public void Simulated_model_is_validated(SimulatedMeterModel model, bool valid)
        => Assert.Equal(valid, new MeterOptionsValidator().Validate(null, new MeterOptions { SimulatedModel = model }).Succeeded);

    [Theory]
    [InlineData(0, 2000, false)]
    [InlineData(5, 2000, false)]
    [InlineData(10, 2000, true)]
    [InlineData(300, 2000, true)]
    [InlineData(30_001, 2000, false)]
    [InlineData(300, 0, false)]
    [InlineData(300, 10, true)]
    [InlineData(300, 60_000, true)]
    [InlineData(300, 60_001, false)]
    public void Resend_window_and_budget_are_validated(int windowMs, int budgetMs, bool valid)
    {
        var options = new MeterOptions
        {
            RealTimeResendTimeout = TimeSpan.FromMilliseconds(windowMs),
            RealTimeResendBudget = TimeSpan.FromMilliseconds(budgetMs),
        };

        var result = new MeterOptionsValidator().Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
            Assert.Contains("Meter:RealTimeResend", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Simulated_model_selects_the_simulated_meter()
    {
        var bm525s = MeterTransportFactory.CreateSimulatedMeter(SimulatedMeterModel.Bm525s, TimeProvider.System);
        var et2517a = MeterTransportFactory.CreateSimulatedMeter(SimulatedMeterModel.Et2517a, TimeProvider.System);

        Assert.Equal(Bm52xCommands.Bm52xFamily, bm525s.FamilyCode);
        Assert.True(bm525s.HasDataLogger);
        Assert.Null(bm525s.RequestDropper);
        Assert.Equal(SimulatedBm52xMeter.RealMeterCurrentAfterHeadWindow, bm525s.CurrentAfterHeadWindow);
        Assert.Equal(Bm52xCommands.Bm82xFamily, et2517a.FamilyCode);
        Assert.False(et2517a.HasDataLogger);
        // O descarte de pedidos do ET-2517A fica ligado no transporte Simulated.
        Assert.NotNull(et2517a.RequestDropper);
    }
}

public sealed class MeterSessionFamilyTests
{
    [Fact]
    public async Task Model_check_of_the_import_follows_the_family_and_fails_with_E9014_on_a_BM82x()
    {
        // Sem o descarte: a conferência do modelo é o 82 66 da detecção, que o ET-2517A responde com o Model ID 82h.
        var meter = SimulatedBm52xMeter.CreateBm82x();
        var provider = MeterTransportFactory.CreateSimulated(meter, TimeSpan.Zero);
        using var session = CreateSession(provider);
        Assert.Null(session.Family);
        Assert.True(session.SupportsDataLogger);
        Assert.Null(session.DataLoggerNotSupportedError());

        BrymenMeterException failure;
        await using (var access = await session.AcquireExclusiveAsync(CancellationToken.None))
            failure = await Assert.ThrowsAsync<BrymenMeterException>(() => access.ReadLoggerImageAsync(null, CancellationToken.None));

        Assert.Equal(ErrorCodes.DataLoggerNotSupported, failure.Code);
        var device = provider.Devices[0];
        Assert.DoesNotContain(device.Writes, w => w.Data[2] is Bm52xCommands.MemoryHead or Bm52xCommands.MemoryNext or Bm52xCommands.MemoryCurrent);
        Assert.Contains(device.Writes, w => w.Data[1] == Bm52xCommands.Bm82xFamily && w.Data[2] == Bm52xCommands.RealTime);
        Assert.Equal(MeterFamily.Bm82x, session.Family);
        Assert.Equal(MeterFamily.Bm82x, session.GetSnapshot().Family);
        Assert.False(session.SupportsDataLogger);
        var refusal = session.DataLoggerNotSupportedError();
        Assert.NotNull(refusal);
        Assert.Equal(ErrorCodes.DataLoggerNotSupported, refusal.Code);
        Assert.Contains("Model ID 82h", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detection_signals_the_status_change_and_a_silent_meter_leaves_the_family_unknown()
    {
        var meter = SimulatedBm52xMeter.CreateBm82x();
        meter.IsPoweredOn = false;
        var provider = MeterTransportFactory.CreateSimulated(meter, TimeSpan.Zero);
        using var session = CreateSession(provider);

        await using (var access = await session.AcquireExclusiveAsync(CancellationToken.None))
        {
            // Medidor desligado: sem resposta, a família continua desconhecida e não há erro (a importação decide).
            Assert.Null(await access.DetectFamilyAsync(CancellationToken.None));
            Assert.Null(session.GetSnapshot().LastError);

            meter.IsPoweredOn = true;
            var changed = session.Changed;
            Assert.Equal(MeterFamily.Bm82x, await access.DetectFamilyAsync(CancellationToken.None));
            await changed.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var statistics = session.RealTimeStatistics;
        Assert.True(statistics.Queries >= 2);
        Assert.True(statistics.FailedQueries >= 1);
    }

    [Fact]
    public async Task Polling_reads_the_BM82x_with_82_66_and_recovers_dropped_requests_without_closing_the_cable()
    {
        // Os dois primeiros pedidos de cada consulta se perdem: o reenvio os recupera sem W7012 e sem reabrir o cabo. A
        // 1ª consulta (a detecção, com os reenvios da espera longa) vai sem descarte.
        var meter = SimulatedBm52xMeter.CreateBm82x();
        var provider = MeterTransportFactory.CreateSimulated(meter, TimeSpan.Zero);
        using var session = CreateSession(provider);
        Assert.Null(await session.SetLinkAsync(true, CancellationToken.None));
        Assert.NotNull(await session.PollRealTimeAsync(CancellationToken.None));
        Assert.Equal(MeterFamily.Bm82x, session.Family);
        meter.RequestDropper = (_, consecutiveDrops) => consecutiveDrops < 2;

        for (int i = 0; i < 3; i++)
        {
            var reading = await session.PollRealTimeAsync(CancellationToken.None);
            Assert.NotNull(reading);
            Assert.Equal(MeterFamily.Bm82x, reading.Family);
            Assert.True(session.GetSnapshot().DeviceOpen);
        }

        var statistics = session.RealTimeStatistics;
        Assert.Equal(0, statistics.FailedQueries);
        Assert.True(statistics.Resends >= 4, $"Reenvios: {statistics.Resends}");
        Assert.True(statistics.RecoveredQueries >= 2, $"Consultas recuperadas: {statistics.RecoveredQueries}");
        Assert.All(provider.Devices[0].Writes, w => Assert.Equal(Bm52xCommands.Bm82xFamily, w.Data[1]));
    }

    private static MeterSession CreateSession(IHidDeviceProvider provider)
        => new(provider, Microsoft.Extensions.Options.Options.Create(new MeterOptions
        {
            ReportTimeout = TimeSpan.FromMilliseconds(200),
            RealTimeResendTimeout = TimeSpan.FromMilliseconds(100),
            RealTimeResendBudget = TimeSpan.FromSeconds(1),
        }), TimeProvider.System, NullLogger<MeterSession>.Instance);
}
