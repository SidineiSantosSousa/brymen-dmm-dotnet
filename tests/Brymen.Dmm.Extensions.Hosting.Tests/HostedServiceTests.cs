using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Brymen.Dmm.Extensions.Hosting.Tests;

/// <summary>O serviço hospedado e o <see cref="IBrymenMeterAccessor"/> sobre o medidor simulado com as capturas reais.</summary>
public sealed class HostedServiceTests
{
    [Fact]
    public async Task Host_start_opens_the_meter_and_streams_the_readings()
    {
        await using var host = new TestHost();

        await host.StartAsync();
        var meter = host.Accessor.Meter;

        Assert.NotNull(meter);
        Assert.True(host.OwnedMeter!.IsRunning);
        Assert.Equal(MeterState.Connected, host.Accessor.State);
        Assert.Same(meter, await host.Accessor.WaitForMeterAsync());
        Assert.Equal("sim://bu86x/0", meter.Device.Path);

        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 3, "3 leituras");
        var readings = host.Events.Readings;
        Assert.Equal([SimulatedCable.Hex(0), SimulatedCable.Hex(1), SimulatedCable.Hex(2)], readings.Take(3).Select(r => r.RawHex));
        Assert.Equal("V", readings[0].Main!.Unit);
        Assert.NotNull(host.Accessor.LastReading);
        Assert.All(host.Events.Senders, s => Assert.Same(host.Accessor, s));
        Assert.Null(host.Accessor.LastError);
        var opened = Assert.Single(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 1);
        Assert.Equal(LogLevel.Information, opened.Level);
        Assert.Contains("sim://bu86x/0", opened.Message, StringComparison.Ordinal);

        await host.StopAsync();

        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterState.Closed, host.Accessor.State);
        Assert.Equal(MeterState.Disposed, meter.State);
        Assert.False(host.Cable.Device.IsOpen);
        Assert.NotNull(host.Accessor.LastReading);
        Assert.Equal(
            [(MeterState.Closed, MeterState.Connecting), (MeterState.Connecting, MeterState.Connected), (MeterState.Connected, MeterState.Closed)],
            host.Events.States.Select(s => (s.Previous, s.Current)));
        Assert.Contains(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 7);
    }

    [Fact]
    public async Task A_missing_cable_does_not_block_the_start_and_is_retried_until_it_appears()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        await using var host = new TestHost(cable);

        await host.StartAsync();
        var waiting = host.Accessor.WaitForMeterAsync();

        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterState.Faulted, host.Accessor.State);
        Assert.Equal(BrymenErrorCodes.CableNotFound, host.Accessor.LastError?.Code);
        Assert.False(waiting.IsCompleted);

        // Algumas tentativas a cada ReconnectDelay (50 ms), cada uma com o seu E9015.
        await TestWait.UntilAsync(() => host.Events.Errors.Count >= 3, "3 tentativas de abertura");
        Assert.All(host.Events.Errors, e => Assert.Equal(BrymenErrorCodes.CableNotFound, e.Code));

        cable.Device.Reconnect();
        var meter = await waiting.WaitAsync(TestWait.Default);

        Assert.Same(meter, host.Accessor.Meter);
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 2, "2 leituras");
        Assert.Equal(MeterState.Connected, host.Accessor.State);
        Assert.Equal(SimulatedCable.Hex(0), host.Events.Readings[0].RawHex);
        // A falha repetida vai para o log como aviso uma vez só; as seguintes, em Debug.
        var logs = host.Logs.Of<BrymenMeterHostedService>();
        Assert.Single(logs, e => e.EventId == 2 && e.Level == LogLevel.Warning && e.Message.Contains("E9015", StringComparison.Ordinal));
        Assert.Contains(logs, e => e.EventId == 3 && e.Level == LogLevel.Debug);
        Assert.Contains(logs, e => e.EventId == 1);

        await host.StopAsync();
        Assert.Equal(MeterState.Disposed, meter.State);
    }

    [Fact]
    public async Task Without_AutoReconnect_a_missing_cable_is_not_retried()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        await using var host = new TestHost(cable, o => o.AutoReconnect = false);

        await host.StartAsync();
        cable.Device.Reconnect();
        await Task.Delay(300);

        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterState.Faulted, host.Accessor.State);
        Assert.Single(host.Events.Errors);
        Assert.Contains(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 6 && e.Level == LogLevel.Warning);

        await host.StopAsync();
        Assert.Equal(MeterState.Closed, host.Accessor.State);
    }

    [Fact]
    public async Task Without_AutoReconnect_the_waits_and_the_enumerations_end_when_the_service_gives_up()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        await using var host = new TestHost(cable, o => o.AutoReconnect = false);
        // Antes da partida: as duas esperam o medidor.
        var waiting = host.Accessor.WaitForMeterAsync();
        var readings = ReadAllAsync(host.Accessor.ReadingsAsync());

        await host.StartAsync();

        var gaveUp = await Assert.ThrowsAsync<BrymenMeterException>(() => waiting.WaitAsync(TestWait.Default));
        Assert.Equal(BrymenErrorCodes.CableNotFound, gaveUp.Code);
        var ended = await Assert.ThrowsAsync<BrymenMeterException>(() => readings.WaitAsync(TestWait.Default));
        Assert.Equal(BrymenErrorCodes.CableNotFound, ended.Code);
        // Uma chamada depois da desistência falha na hora.
        var later = host.Accessor.WaitForMeterAsync();
        Assert.True(later.IsFaulted);
        await Assert.ThrowsAsync<BrymenMeterException>(() => later);
        await Assert.ThrowsAsync<BrymenMeterException>(() => ReadAllAsync(host.Accessor.ReadingsAsync()));

        await host.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Accessor.WaitForMeterAsync());
    }

    [Fact]
    public async Task Invalid_options_fail_the_host_start()
    {
        await using var host = new TestHost(configure: o => o.ReportTimeout = TimeSpan.Zero);
        var waiting = host.Accessor.WaitForMeterAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(host.StartAsync);

        // Quem esperava o medidor recebe a falha da partida, e não fica preso.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => waiting.WaitAsync(TestWait.Default));
        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterState.Closed, host.Accessor.State);
        Assert.Empty(host.Cable.Device.Writes);
    }

    [Fact]
    public async Task Failures_of_the_open_meter_are_forwarded_and_it_recovers_without_a_new_instance()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var meter = host.Accessor.Meter!;
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 2, "2 leituras");

        host.Cable.Device.Disconnect();
        await TestWait.UntilAsync(() => host.Events.Errors.Any(e => e.Code == BrymenErrorCodes.CableNotFound), "o E9015 do cabo removido");

        Assert.NotEqual(MeterState.Connected, host.Accessor.State);
        Assert.NotNull(host.Accessor.LastError);
        Assert.Contains(host.Events.States, s => s.Current == MeterState.Faulted);

        int before = host.Events.Readings.Count;
        host.Cable.Device.Reconnect();
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= before + 2, "leituras depois da reconexão");

        Assert.Same(meter, host.Accessor.Meter);
        Assert.Equal(MeterState.Connected, host.Accessor.State);
        Assert.Contains(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 4 && e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_readings_nor_the_other_handlers()
    {
        await using var host = new TestHost();
        int received = 0;
        host.Accessor.ReadingReceived += (_, _) => throw new InvalidOperationException("handler com defeito");
        host.Accessor.ReadingReceived += (_, _) => Interlocked.Increment(ref received);
        host.Accessor.ErrorOccurred += (_, _) => throw new InvalidOperationException("handler de erro com defeito");

        await host.StartAsync();
        await TestWait.UntilAsync(() => Volatile.Read(ref received) >= 3, "3 leituras no 2º handler");

        Assert.Contains(host.Events.Errors, e => e.Code == BrymenErrorCodes.EventHandlerFailed && e.Exception is InvalidOperationException);
        Assert.Null(host.Accessor.LastError);
        Assert.Equal(MeterState.Connected, host.Accessor.State);
        Assert.True(host.OwnedMeter!.IsRunning);
        Assert.Contains(host.Logs.Of<BrymenMeterHostedService>(), e => e.EventId == 5 && e.Level == LogLevel.Error);
        Assert.Contains(host.Logs.Of<BrymenMeterAccessor>(), e => e.EventId == 20 && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Logger_import_through_the_accessor_pauses_and_resumes_the_readings()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var meter = await host.Accessor.WaitForMeterAsync();
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 1, "1 leitura");

        var memory = await meter.ReadLoggerMemoryAsync();
        int after = host.Events.Readings.Count;

        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= after + 2, "leituras depois da importação");
        Assert.True(host.OwnedMeter!.IsRunning);
    }

    [Fact]
    public async Task Stopping_during_the_retries_ends_them()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        await using var host = new TestHost(cable);
        await host.StartAsync();
        await TestWait.UntilAsync(() => host.Events.Errors.Count >= 2, "2 tentativas de abertura");

        await host.StopAsync();
        int attempts = host.Events.Errors.Count;
        cable.Device.Reconnect();
        await Task.Delay(300);

        Assert.Null(host.Accessor.Meter);
        Assert.Equal(MeterState.Closed, host.Accessor.State);
        Assert.Equal(attempts, host.Events.Errors.Count);
        Assert.Empty(cable.Device.Writes);
    }

    [Fact]
    public async Task Disposing_the_host_without_stopping_it_closes_the_meter()
    {
        var host = new TestHost();
        await host.StartAsync();
        var meter = host.Accessor.Meter!;

        await host.DisposeAsync();

        Assert.Equal(MeterState.Disposed, meter.State);
        Assert.False(host.Cable.Device.IsOpen);
        Assert.Null(host.Accessor.Meter);
    }

    [Fact]
    public async Task Synchronous_dispose_of_the_container_also_closes_the_meter()
    {
        var cable = new SimulatedCable();
        var services = new ServiceCollection();
        services.AddBrymenMeter(cable.Configure);
        var provider = services.BuildServiceProvider();
        var service = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Single();
        await service.StartAsync(CancellationToken.None);
        var meter = provider.GetRequiredService<IBrymenMeterAccessor>().Meter!;

        provider.Dispose();

        Assert.Equal(MeterState.Disposed, meter.State);
        Assert.False(cable.Device.IsOpen);
    }

    [Fact]
    public async Task WaitForMeterAsync_honours_cancellation_and_the_container_disposal()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        var host = new TestHost(cable);
        await host.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Accessor.WaitForMeterAsync(cts.Token));
        var pending = host.Accessor.WaitForMeterAsync();

        await host.StopAsync();

        // A parada encerra a espera em andamento e as seguintes, que já não têm medidor a esperar.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TestWait.Default));
        Assert.True(host.Accessor.WaitForMeterAsync().IsFaulted);

        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Accessor.WaitForMeterAsync());
    }

    [Fact]
    public async Task Disposing_the_container_during_a_wait_ends_it()
    {
        var cable = new SimulatedCable();
        cable.Device.Disconnect();
        var host = new TestHost(cable);
        await host.StartAsync();
        var pending = host.Accessor.WaitForMeterAsync();

        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TestWait.Default));
    }

    [Fact]
    public async Task The_accessor_exposes_only_the_operations_and_cannot_stop_the_service()
    {
        await using var host = new TestHost();
        await host.StartAsync();

        var meter = await host.Accessor.WaitForMeterAsync();

        // Nem descarte (await using) nem StopAsync: a fachada não é o BrymenMeter do serviço.
        Assert.IsNotType<BrymenMeter>(meter);
        Assert.False(meter is IDisposable);
        Assert.False(meter is IAsyncDisposable);
        var reading = await meter.ReadAsync();
        Assert.Equal("V", reading.Main!.Unit);
        int before = host.Events.Readings.Count;
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= before + 3, "leituras depois da operação avulsa");
        Assert.True(host.OwnedMeter!.IsRunning);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task A_service_registered_before_can_wait_for_the_meter_in_its_StartAsync_and_the_first_reading_reaches_everyone()
    {
        var cable = new SimulatedCable();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        // Antes e depois do AddBrymenMeter: os dois esperam o medidor e assinam os eventos no próprio StartAsync.
        builder.Services.AddSingleton<EarlyConsumer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<EarlyConsumer>());
        builder.Services.AddBrymenMeter(cable.Configure);
        builder.Services.AddSingleton<LateConsumer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<LateConsumer>());
        using var host = builder.Build();

        await host.StartAsync().WaitAsync(TestWait.Default);
        var early = host.Services.GetRequiredService<EarlyConsumer>();
        var late = host.Services.GetRequiredService<LateConsumer>();
        await TestWait.UntilAsync(() => early.Readings.Count >= 2 && late.Readings.Count >= 2, "leituras nos dois serviços");

        Assert.NotNull(early.Meter);
        Assert.NotNull(late.Meter);
        Assert.Equal(SimulatedCable.Hex(0), early.Readings[0].RawHex);
        Assert.Equal(SimulatedCable.Hex(0), late.Readings[0].RawHex);
        await host.StopAsync();
    }

    [Fact]
    public async Task ReadingsAsync_shares_the_loop_of_the_service()
    {
        await using var host = new TestHost();
        using var cts = new CancellationTokenSource(TestWait.Default);
        // Inscritas antes da partida: esperam o medidor.
        var first = TakeAsync(host.Accessor.ReadingsAsync(cts.Token), 20);
        var second = TakeAsync(host.Accessor.ReadingsAsync(cts.Token), 20);

        await host.StartAsync();
        var a = await first;
        var b = await second;
        int polls = host.Cable.Device.Writes.Count(w => w.Data[2] == 0x66);
        int loopReadings = host.Events.Readings.Count;

        // As mesmas leituras do laço (as mesmas instâncias), na ordem, sem consultas a mais no cabo.
        var events = host.Events.Readings;
        Assert.All(a.Concat(b), r => Assert.Contains(events, e => ReferenceEquals(e, r)));
        Assert.Equal(SimulatedCable.Hex(0), a[0].RawHex);
        Assert.Equal(a.Select(r => r.RawHex), b.Select(r => r.RawHex));
        Assert.InRange(polls, loopReadings, loopReadings + 2);
    }

    [Fact]
    public async Task ReadingsAsync_ends_at_the_stop_and_a_new_one_ends_at_once()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var all = ReadAllAsync(host.Accessor.ReadingsAsync());
        await TestWait.UntilAsync(() => host.Events.Readings.Count >= 3, "3 leituras");

        await host.StopAsync();

        var received = await all.WaitAsync(TestWait.Default);
        Assert.NotEmpty(received);
        Assert.Empty(await ReadAllAsync(host.Accessor.ReadingsAsync()).WaitAsync(TestWait.Default));
    }

    [Fact]
    public async Task ReadingsAsync_drops_the_oldest_readings_of_a_slow_consumer()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        await using var enumerator = host.Accessor.ReadingsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var firstSeen = enumerator.Current;

        // O consumidor para; o laço segue.
        await TestWait.UntilAsync(
            () => IndexOf(host.Events.Readings, firstSeen) + BrymenMeterAccessor.ReadingsCapacity + 20 < host.Events.Readings.Count,
            "o laço passar da capacidade");
        Assert.True(await enumerator.MoveNextAsync());
        var next = enumerator.Current;

        // As mais antigas foram descartadas: a próxima não é a seguinte à 1ª.
        Assert.True(IndexOf(host.Events.Readings, next) > IndexOf(host.Events.Readings, firstSeen) + 1);
    }

    [Fact]
    public async Task Two_named_meters_run_side_by_side_with_their_own_cables_and_accessors()
    {
        var first = new SimulatedCable("sim://bancada/1");
        var second = new SimulatedCable("sim://bancada/2");
        var logs = new LogCollector();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddBrymenMeter("bancada1", first.Configure);
        builder.Services.AddBrymenMeter("bancada2", second.Configure);
        // De novo com o mesmo nome: só acrescenta configuração.
        builder.Services.AddBrymenMeter("bancada1", o => o.DevicePath = "sim://bancada/1");
        using var host = builder.Build();

        var one = host.Services.GetRequiredKeyedService<IBrymenMeterAccessor>("bancada1");
        var two = host.Services.GetRequiredKeyedService<IBrymenMeterAccessor>("bancada2");
        Assert.NotSame(one, two);
        Assert.Equal(2, host.Services.GetServices<IHostedService>().Count());
        Assert.Null(host.Services.GetService<IBrymenMeterAccessor>());

        await host.StartAsync();
        var meterOne = await one.WaitForMeterAsync().WaitAsync(TestWait.Default);
        var meterTwo = await two.WaitForMeterAsync().WaitAsync(TestWait.Default);
        await TestWait.UntilAsync(() => one.LastReading is not null && two.LastReading is not null, "leituras nos dois medidores");

        Assert.Equal("sim://bancada/1", meterOne.Device.Path);
        Assert.Equal("sim://bancada/2", meterTwo.Device.Path);
        Assert.Contains(first.Device.Writes, w => w.Data[2] == 0x66);
        Assert.Contains(second.Device.Writes, w => w.Data[2] == 0x66);
        Assert.Contains(logs.Entries, e => e.Category == "Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService.bancada1" && e.EventId == 1);
        Assert.Contains(logs.Entries, e => e.Category == "Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService.bancada2" && e.EventId == 1);

        await host.StopAsync();
        Assert.Equal(MeterState.Disposed, meterOne.State);
        Assert.Equal(MeterState.Disposed, meterTwo.State);
    }

    private static async Task<List<DmmReading>> ReadAllAsync(IAsyncEnumerable<DmmReading> readings)
    {
        var list = new List<DmmReading>();
        await foreach (var reading in readings)
            list.Add(reading);
        return list;
    }

    private static async Task<List<DmmReading>> TakeAsync(IAsyncEnumerable<DmmReading> readings, int count)
    {
        var list = new List<DmmReading>();
        await foreach (var reading in readings)
        {
            list.Add(reading);
            if (list.Count == count)
                break;
        }
        return list;
    }

    private static int IndexOf(IReadOnlyList<DmmReading> readings, DmmReading reading)
    {
        for (int i = 0; i < readings.Count; i++)
        {
            if (ReferenceEquals(readings[i], reading))
                return i;
        }
        return -1;
    }

    /// <summary>Serviço que, no próprio StartAsync, espera o medidor e assina as leituras.</summary>
    private abstract class StartAsyncConsumer(IBrymenMeterAccessor accessor) : IHostedService
    {
        private readonly Lock _lock = new();
        private readonly List<DmmReading> _readings = [];

        public IBrymenMeter? Meter { get; private set; }

        public IReadOnlyList<DmmReading> Readings
        {
            get { lock (_lock) return [.. _readings]; }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Meter = await accessor.WaitForMeterAsync(cancellationToken);
            accessor.ReadingReceived += (_, r) => { lock (_lock) _readings.Add(r); };
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EarlyConsumer(IBrymenMeterAccessor accessor) : StartAsyncConsumer(accessor);

    private sealed class LateConsumer(IBrymenMeterAccessor accessor) : StartAsyncConsumer(accessor);
}
