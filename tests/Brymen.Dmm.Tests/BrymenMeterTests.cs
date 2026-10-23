using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Tests;

/// <summary><see cref="BrymenMeter"/> sobre o medidor simulado com as capturas reais do BM525s.</summary>
public sealed class BrymenMeterTests
{
    private static string Hex(int index) => Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[index]);

    [Fact]
    public async Task OpenAsync_opens_the_first_cable_and_ReadAsync_decodes_the_real_frames()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();

        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(SimulatedHidDevice.DefaultInfo, meter.Device);
        Assert.Null(meter.LastError);
        Assert.False(meter.IsRunning);

        var first = await meter.ReadAsync();
        var second = await meter.ReadAsync();

        Assert.Equal(Hex(0), first.RawHex);
        Assert.Equal(Hex(1), second.RawHex);
        Assert.Equal("V", first.Main!.Unit);
        Assert.Equal(MeasurementMode.AC, first.Main.Mode);
        Assert.Equal(0.287, first.Main.Value);
        Assert.Equal("Hz", first.Secondary!.Unit);
        Assert.Equal(["66", "66"], setup.Commands);
    }

    [Fact]
    public async Task OpenSimulatedAsync_needs_no_hardware()
    {
        await using var meter = await BrymenMeter.OpenSimulatedAsync();

        var reading = await meter.ReadAsync();
        var memory = await meter.ReadLoggerMemoryAsync();

        Assert.Equal(Hex(0), reading.RawHex);
        Assert.Equal(375, Assert.Single(memory.Sessions).Samples.Count);
        Assert.Equal("sim://bu86x/0", meter.Device.Path);
    }

    [Fact]
    public async Task Missing_cable_is_E9015()
    {
        var empty = new SimulatedHidDeviceProvider(Array.Empty<SimulatedHidDevice>());

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = empty }));
        var byPath = await Assert.ThrowsAsync<BrymenMeterException>(() => BrymenMeter.OpenAsync(new BrymenMeterOptions
        {
            DeviceProvider = new SimulatedHidDeviceProvider(),
            DevicePath = "sim://bu86x/9",
        }));

        Assert.Equal("E9015", ex.Code);
        Assert.Equal("E9015", byPath.Code);
        Assert.Empty(BrymenMeter.FindDevices(empty));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrymenMeter.OpenAsync(new BrymenMeterOptions { DeviceProvider = empty }, new CancellationToken(true)));
    }

    [Fact]
    public async Task Cable_removed_between_reads_is_W7012_then_E9015_and_the_meter_recovers()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        await meter.ReadAsync();

        setup.Device.Disconnect();
        var lost = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());
        var missing = await Assert.ThrowsAsync<BrymenMeterException>(() => meter.ReadAsync());
        Assert.Equal(MeterState.Faulted, meter.State);
        setup.Device.Reconnect();
        await meter.ReadAsync();

        Assert.Equal(BrymenErrorCodes.NoDataReceived, lost.Code);
        Assert.IsAssignableFrom<IOException>(lost.InnerException);
        Assert.Equal(BrymenErrorCodes.CableNotFound, missing.Code);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(BrymenErrorCodes.CableNotFound, meter.LastError!.Code);
        Assert.Equal([BrymenErrorCodes.NoDataReceived, BrymenErrorCodes.CableNotFound], events.Errors.Select(e => e.Code));
        Assert.Same(missing, events.Errors[1].Exception);
        Assert.Equal(
            [MeterState.Faulted, MeterState.Connecting, MeterState.Faulted, MeterState.Connecting, MeterState.Connected],
            events.States);
    }

    [Fact]
    public async Task ReadContinuouslyAsync_reads_at_the_interval_until_canceled()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readings = new List<DmmReading>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(20), cts.Token))
            {
                readings.Add(reading);
                if (readings.Count == 16)
                    await cts.CancelAsync();
            }
        });

        Assert.Equal(16, readings.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => Hex(i % 14)), readings.Select(r => r.RawHex));
        Assert.True(readings.Zip(readings.Skip(1)).All(p => p.Second.Timestamp >= p.First.Timestamp));
        // 15 intervalos de 20 ms entre a 1ª e a 16ª (com folga para a 1ª consulta, que pode demorar mais).
        Assert.True(readings[^1].Timestamp - readings[0].Timestamp >= TimeSpan.FromMilliseconds(200));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task ReadContinuouslyAsync_tolerates_two_failures_and_reports_the_third_as_E9016()
    {
        var setup = new SimulatedSetup();
        setup.Options.AutoReconnect = false;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        int count = 0;
        // Do 3º 66 em diante, sem resposta: 2 falhas toleradas (W7012) e a 3ª termina a enumeração com E9016.
        setup.Meter.FaultInjector = (command, _) => command == Bm52xCommands.RealTime && ++count >= 3 ? SimulatedFault.Drop : SimulatedFault.None;
        var readings = new List<DmmReading>();

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(async () =>
        {
            await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.Zero))
                readings.Add(reading);
        });

        Assert.Equal(2, readings.Count);
        Assert.Equal(BrymenErrorCodes.TransmissionTimeout, ex.Code);
        Assert.Equal(["W7012", "W7012", "E9016"], events.Errors.Select(e => e.Code));
        Assert.Equal(MeterState.Faulted, meter.State);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task ReadContinuouslyAsync_with_AutoReconnect_survives_a_disconnection()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int received = 0;
        bool reconnected = false;

        await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(5), cts.Token))
        {
            received++;
            if (received == 3)
            {
                setup.Device.Disconnect();
                // Volta depois de algumas tentativas com o cabo ausente (E9015, a cada ReconnectDelay).
                _ = Task.Run(async () =>
                {
                    await TestWait.UntilAsync(() => events.Errors.Count(e => e.Code == BrymenErrorCodes.CableNotFound) >= 2, "as falhas E9015");
                    reconnected = true;
                    setup.Device.Reconnect();
                });
            }
            if (reconnected && received >= 6)
                break;
        }

        Assert.True(received >= 6);
        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(BrymenErrorCodes.NoDataReceived, events.Errors[0].Code);
        Assert.True(events.Errors.Count(e => e.Code == BrymenErrorCodes.CableNotFound) >= 2);
        Assert.Contains(MeterState.Faulted, events.States);
        Assert.Equal(MeterState.Connected, events.States[^1]);
    }

    [Fact]
    public async Task ReadContinuouslyAsync_with_AutoReconnect_survives_a_meter_switched_off()
    {
        var setup = new SimulatedSetup();
        setup.Options.ReportTimeout = TimeSpan.FromMilliseconds(40);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromMilliseconds(40);
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int received = 0;

        await foreach (var reading in meter.ReadContinuouslyAsync(TimeSpan.Zero, cts.Token))
        {
            if (++received == 2)
            {
                setup.Meter.IsPoweredOn = false;
                _ = Task.Run(async () =>
                {
                    await TestWait.UntilAsync(() => events.Errors.Any(e => e.Code == BrymenErrorCodes.TransmissionTimeout), "o E9016");
                    setup.Meter.IsPoweredOn = true;
                });
            }
            if (received == 4)
                break;
        }

        Assert.Equal(["W7012", "W7012", "E9016"], events.Errors.Take(3).Select(e => e.Code));
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task Start_raises_ReadingReceived_until_StopAsync()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        meter.Start();
        meter.Start(); // já rodando: nada muda
        Assert.True(meter.IsRunning);
        await TestWait.UntilAsync(() => events.Readings.Count >= 5, "5 leituras");
        await meter.StopAsync();
        int count = events.Readings.Count;
        int commands = setup.Commands.Count;
        await Task.Delay(100);

        Assert.False(meter.IsRunning);
        Assert.Equal(count, events.Readings.Count);
        Assert.Equal(commands, setup.Commands.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(i => Hex(i % 14)), events.Readings.Select(r => r.RawHex));
        Assert.Empty(events.Errors);

        // De novo, depois de parar.
        meter.Start();
        await TestWait.UntilAsync(() => events.Readings.Count >= count + 3, "mais leituras");
        await meter.StopAsync();
        await meter.StopAsync();
        Assert.False(meter.IsRunning);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_loop_nor_the_other_handlers()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        int throwing = 0, healthy = 0;
        var errors = new List<BrymenMeterError>();
        meter.ReadingReceived += (_, _) =>
        {
            Interlocked.Increment(ref throwing);
            throw new InvalidOperationException("handler com defeito");
        };
        meter.ReadingReceived += (_, _) => Interlocked.Increment(ref healthy);
        meter.StateChanged += (_, _) => throw new InvalidOperationException("handler de estado com defeito");
        meter.ErrorOccurred += (_, e) =>
        {
            lock (errors)
                errors.Add(e);
            throw new InvalidOperationException("exceção no ErrorOccurred é ignorada");
        };

        meter.Start();
        await TestWait.UntilAsync(() => Volatile.Read(ref healthy) >= 5, "5 leituras no handler saudável");
        await meter.StopAsync();

        Assert.Equal(Volatile.Read(ref throwing), Volatile.Read(ref healthy));
        lock (errors)
        {
            Assert.True(errors.Count >= 5);
            Assert.All(errors, e => Assert.Equal(BrymenErrorCodes.EventHandlerFailed, e.Code));
            Assert.All(errors, e => Assert.IsType<InvalidOperationException>(e.Exception));
        }
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task Start_with_AutoReconnect_reconnects_after_the_cable_comes_back()
    {
        var setup = new SimulatedSetup();
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        meter.Start();
        await TestWait.UntilAsync(() => events.Readings.Count >= 3, "3 leituras");
        setup.Device.Disconnect();
        await TestWait.UntilAsync(() => events.Errors.Count(e => e.Code == BrymenErrorCodes.CableNotFound) >= 2, "as falhas E9015");
        Assert.True(meter.IsRunning);
        Assert.Equal(MeterState.Faulted, meter.State);
        int before = events.Readings.Count;
        setup.Device.Reconnect();
        await TestWait.UntilAsync(() => events.Readings.Count >= before + 3, "as leituras depois da reconexão");
        await meter.StopAsync();

        Assert.Equal(MeterState.Connected, meter.State);
        Assert.Equal(BrymenErrorCodes.CableNotFound, meter.LastError!.Code);
    }

    [Fact]
    public async Task Start_without_AutoReconnect_stops_on_a_persistent_failure()
    {
        var setup = new SimulatedSetup();
        setup.Options.AutoReconnect = false;
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);

        meter.Start();
        await TestWait.UntilAsync(() => events.Readings.Count >= 2, "2 leituras");
        setup.Device.Disconnect();
        await TestWait.UntilAsync(() => !meter.IsRunning, "o fim do laço");

        Assert.Equal(BrymenErrorCodes.CableNotFound, events.Errors[^1].Code);
        Assert.Equal(MeterState.Faulted, meter.State);
    }

    [Fact]
    public async Task Cancellation_of_each_operation()
    {
        var setup = new SimulatedSetup();
        setup.Options.ReportTimeout = TimeSpan.FromSeconds(30);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(30);
        await using var meter = await setup.OpenAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => meter.ReadAsync(new CancellationToken(true)));

        // Medidor calado: a consulta fica esperando até o cancelamento.
        setup.Meter.IsPoweredOn = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => meter.ReadAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        setup.Meter.IsPoweredOn = true;

        // Importação cancelada no meio.
        using (var cts = new CancellationTokenSource())
        {
            var progress = new CancelAtProgress(cts, blocks: 20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => meter.ReadLoggerMemoryAsync(progress, cts.Token));
        }

        // O medidor continua utilizável, sem falha registrada.
        Assert.Equal(27, (await meter.ReadAsync()).RawHex.Length / 2);
        Assert.Null(meter.LastError);
        Assert.Equal(MeterState.Connected, meter.State);
    }

    [Fact]
    public async Task Concurrent_dispose_ends_every_operation()
    {
        var setup = new SimulatedSetup();
        setup.Device.ResponseLatency = TimeSpan.FromMilliseconds(2);
        await using var meter = await setup.OpenAsync();
        var events = new EventRecorder(meter);
        meter.Start();
        await TestWait.UntilAsync(() => events.Readings.Count >= 2, "o laço de eventos");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var enumerations = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            int n = 0;
            await foreach (var _ in meter.ReadContinuouslyAsync(TimeSpan.Zero, stop.Token))
                n++;
            return n;
        })).ToArray();
        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (true)
                await meter.ReadAsync(stop.Token);
        })).ToArray();
        var import = Task.Run(() => meter.ReadLoggerImageAsync(cancellationToken: stop.Token));
        await Task.Delay(150);

        var disposers = Enumerable.Range(0, 4).Select(i => i % 2 == 0 ? Task.Run(() => meter.Dispose()) : Task.Run(async () => await meter.DisposeAsync())).ToArray();
        await Task.WhenAll(disposers).WaitAsync(TimeSpan.FromSeconds(10));

        // As enumerações terminam sem exceção; as leituras avulsas e a importação, com ObjectDisposedException.
        Assert.All(await Task.WhenAll(enumerations).WaitAsync(TimeSpan.FromSeconds(10)), n => Assert.True(n >= 0));
        foreach (var read in reads)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            await import.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
            // Descartado antes do fim da importação (o caso comum).
        }

        Assert.Equal(MeterState.Disposed, meter.State);
        Assert.False(meter.IsRunning);
        Assert.Single(events.States, s => s == MeterState.Disposed);
        Assert.Equal(MeterState.Disposed, events.States[^1]);
        int readings = events.Readings.Count;
        await Task.Delay(50);
        Assert.Equal(readings, events.Readings.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => meter.ReadAsync());
        Assert.Throws<ObjectDisposedException>(() => meter.Start());
        Assert.Throws<ObjectDisposedException>(() => meter.ReadContinuouslyAsync());
        Assert.False(setup.Device.IsOpen);
    }

    [Fact]
    public async Task Dispose_from_inside_a_handler_does_not_deadlock()
    {
        var setup = new SimulatedSetup();
        var meter = await setup.OpenAsync();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        meter.ReadingReceived += (_, _) =>
        {
            meter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            meter.StopAsync().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };

        meter.Start();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(() => !meter.IsRunning, "o fim do laço");
        Assert.Equal(MeterState.Disposed, meter.State);
    }

    /// <summary>Cancela a importação quando o progresso chega a <paramref name="blocks"/> blocos.</summary>
    private sealed class CancelAtProgress(CancellationTokenSource cts, int blocks) : IProgress<LoggerReadProgress>
    {
        public void Report(LoggerReadProgress value)
        {
            if (value.BlocksRead >= blocks)
                cts.Cancel();
        }
    }
}
