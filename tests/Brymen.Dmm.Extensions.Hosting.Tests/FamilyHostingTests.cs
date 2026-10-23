using System.Runtime.CompilerServices;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Extensions.Hosting.Tests;

/// <summary>A família do medidor no serviço hospedado e no <see cref="IBrymenMeterAccessor"/> (BM52x e BM82x).</summary>
public sealed class FamilyHostingTests
{
    [Fact]
    public async Task The_accessor_shows_the_family_of_the_ET2517A_and_refuses_its_memory()
    {
        var cable = new SimulatedCable(meter: SimulatedBm52xMeter.CreateBm82x());
        await using var host = new TestHost(cable);

        await host.StartAsync();
        var meter = await host.Accessor.WaitForMeterAsync();
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 2, "2 leituras");

        Assert.Equal(MeterFamily.Bm82x, host.Accessor.Family);
        Assert.Equal(MeterFamily.Bm82x, meter.Family);
        Assert.False(meter.SupportsDataLogger);
        Assert.Equal("BM82x (Minipa ET-2517A)", meter.ModelDescription);
        Assert.True(meter.RealTimeStatistics.Queries >= 2);
        Assert.All(host.Events.Readings, r => Assert.Equal(MeterFamily.Bm82x, r.Family));

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadLoggerMemoryAsync());
        Assert.Equal(BrymenErrorCodes.DataLoggerNotSupported, ex.Code);
        Assert.Null(host.Accessor.LastError);
        Assert.Empty(host.Events.Errors);

        var family = Assert.Single(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 8);
        Assert.Equal(LogLevel.Information, family.Level);
        Assert.Equal("Família do multímetro: BM82x (Minipa ET-2517A), sem datalogger.", family.Message);

        await host.StopAsync();

        // Sem o medidor, a família é a da última leitura.
        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterFamily.Bm82x, host.Accessor.Family);
    }

    [Fact]
    public async Task The_BM525s_family_is_logged_once()
    {
        await using var host = new TestHost();

        await host.StartAsync();
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 5, "5 leituras");
        await host.StopAsync();

        var family = Assert.Single(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 8);
        Assert.Equal("Família do multímetro: BM52x (BM525s / Minipa ET-2615A), com datalogger.", family.Message);
        Assert.Equal(MeterFamily.Bm52x, host.Accessor.Family);
    }

    [Fact]
    public void The_family_options_come_from_the_configuration()
    {
        var root = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Brymen:Family"] = "Bm82x",
                ["Brymen:RealTimeResendTimeout"] = "00:00:00.250",
                ["Brymen:RealTimeResendBudget"] = "00:00:03",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddBrymenMeter(root.GetSection("Brymen"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<BrymenMeterOptions>>().Value;
        using var plain = new ServiceCollection().AddBrymenMeter(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var defaults = plain.GetRequiredService<IOptions<BrymenMeterOptions>>().Value;

        Assert.Equal(MeterFamilySelection.Bm82x, options.Family);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RealTimeResendTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), options.RealTimeResendBudget);
        Assert.Equal(MeterFamilySelection.Auto, defaults.Family);
        Assert.Equal(MeterConnection.DefaultRealTimeResendTimeout, defaults.RealTimeResendTimeout);
        Assert.Equal(MeterConnection.DefaultRealTimeResendBudget, defaults.RealTimeResendBudget);
    }

    [Fact]
    public void The_new_interface_members_have_default_implementations()
    {
        IBrymenMeter meter = new MinimalMeter();
        IBrymenMeterAccessor empty = new MinimalAccessor(null);
        IBrymenMeterAccessor withMeter = new MinimalAccessor(new Bm82xMeter());

        Assert.Null(meter.Family);
        Assert.True(meter.SupportsDataLogger);
        Assert.Null(meter.ModelDescription);
        Assert.Equal(default, meter.RealTimeStatistics);
        Assert.Null(empty.Family);
        Assert.Equal(MeterFamily.Bm82x, withMeter.Family);
        Assert.False(withMeter.Meter!.SupportsDataLogger);
        Assert.Equal("BM82x (Minipa ET-2517A)", withMeter.Meter.ModelDescription);
    }

    /// <summary>Um <see cref="IBrymenMeter"/> só com os membros sem implementação padrão (o de quem simula a interface).</summary>
    private class MinimalMeter : IBrymenMeter
    {
        public HidDeviceInfo Device => SimulatedHidDevice.DefaultInfo;

        public MeterState State => MeterState.Connected;

        public BrymenMeterError? LastError => null;

        public Task<DmmReading> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LoggerMemory> ReadLoggerMemoryAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Bm82xMeter : MinimalMeter, IBrymenMeter
    {
        public MeterFamily? Family => MeterFamily.Bm82x;
    }

    /// <summary>Um <see cref="IBrymenMeterAccessor"/> só com os membros sem implementação padrão.</summary>
    private sealed class MinimalAccessor(IBrymenMeter? meter) : IBrymenMeterAccessor
    {
        public IBrymenMeter? Meter => meter;

        public MeterState State => MeterState.Closed;

        public DmmReading? LastReading => null;

        public BrymenMeterError? LastError => null;

        public event EventHandler<DmmReading>? ReadingReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<MeterStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<BrymenMeterError>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public Task<IBrymenMeter> WaitForMeterAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<DmmReading> ReadingsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
