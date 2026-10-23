using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Tests;

/// <summary>O bloco de baixo nível: abertura, E9015, comandos e a política de timeout do 1º <c>66</c> lento.</summary>
public sealed class MeterConnectionTests
{
    [Fact]
    public void Missing_cable_is_E9015_with_and_without_a_device_path()
    {
        var empty = new SimulatedHidDeviceProvider(Array.Empty<SimulatedHidDevice>());
        using var connection = new MeterConnection(new BrymenMeterOptions { DeviceProvider = empty });
        using var byPath = new MeterConnection(new BrymenMeterOptions { DeviceProvider = new SimulatedHidDeviceProvider(), DevicePath = "sim://outro" });

        var ex = Assert.Throws<BrymenMeterException>(() => connection.Open());
        var exPath = Assert.Throws<BrymenMeterException>(() => byPath.Open());

        Assert.Equal(BrymenErrorCodes.CableNotFound, ex.Code);
        Assert.StartsWith("Cabo adaptador USB não encontrado.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(BrymenErrorCodes.CableNotFound, exPath.Code);
        Assert.Contains("sim://outro", exPath.Message, StringComparison.Ordinal);
        Assert.False(connection.IsOpen);
        Assert.Null(connection.Device);
    }

    [Fact]
    public void Open_failure_of_the_provider_is_E9015_with_the_cause()
    {
        var provider = new FailingOpenProvider();
        using var connection = new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider });

        var ex = Assert.Throws<BrymenMeterException>(() => connection.Open());

        Assert.Equal(BrymenErrorCodes.CableNotFound, ex.Code);
        Assert.Contains("EACCES", ex.Message, StringComparison.Ordinal);
        Assert.IsType<HidException>(ex.InnerException);
    }

    [Fact]
    public async Task Commands_open_the_cable_and_reopen_after_close()
    {
        var setup = new SimulatedSetup();
        using var connection = new MeterConnection(setup.Options);
        Assert.False(connection.IsOpen);

        byte[] frame = await connection.ExecuteRealTimeAsync();
        Assert.True(connection.IsOpen);
        Assert.Equal(SimulatedHidDevice.DefaultInfo, connection.Device);
        Assert.Equal(SimulatedBm52xMeter.CapturedRealTimeFrames[0], frame);

        connection.Close();
        Assert.False(connection.IsOpen);
        Assert.Equal(SimulatedHidDevice.DefaultInfo, connection.Device);
        var reading = await connection.ReadRealTimeAsync();
        Assert.Equal(Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[1]), reading.RawHex);
        Assert.Equal(SimulatedHidDevice.DefaultInfo, connection.Reopen());
        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task TryExecute_reports_the_failure_and_the_reports_received()
    {
        var setup = new SimulatedSetup();
        using var connection = new MeterConnection(setup.Options);
        await connection.ExecuteAsync(Bm52xCommands.MemoryHead, Bm52xCommands.MemoryReportCount);

        setup.Meter.FaultInjector = (_, _) => SimulatedFault.Truncate;
        var truncated = await connection.TryExecuteAsync(Bm52xCommands.MemoryNext, Bm52xCommands.MemoryReportCount);
        setup.Meter.FaultInjector = (_, _) => SimulatedFault.Drop;
        var dropped = await connection.TryExecuteAsync(Bm52xCommands.MemoryNext, Bm52xCommands.MemoryReportCount);
        setup.Meter.FaultInjector = null;
        var ok = await connection.TryExecuteAsync(Bm52xCommands.MemoryCurrent, Bm52xCommands.MemoryReportCount);

        Assert.False(truncated.Succeeded);
        Assert.IsType<Bu86xTimeoutException>(truncated.Error);
        Assert.Equal(2, truncated.ReportsReceived);
        Assert.False(dropped.Succeeded);
        Assert.Equal(0, dropped.ReportsReceived);
        Assert.True(ok.Succeeded);
        Assert.Equal(4, ok.ReportsReceived);
        Assert.Equal(36, ok.Frame!.Length);
    }

    [Fact]
    public async Task Silent_meter_is_W7012_and_invalid_frame_is_W8011()
    {
        var setup = new SimulatedSetup();
        using var connection = new MeterConnection(setup.Options);

        setup.Meter.IsPoweredOn = false;
        var silent = await Assert.ThrowsAsync<BrymenMeterException>(() => connection.ReadRealTimeAsync());
        setup.Meter.IsPoweredOn = true;
        setup.Meter.FaultInjector = (_, _) => SimulatedFault.CorruptModel;
        var invalid = await Assert.ThrowsAsync<BrymenMeterException>(() => connection.ReadRealTimeAsync());

        Assert.Equal(BrymenErrorCodes.NoDataReceived, silent.Code);
        Assert.IsType<Bu86xTimeoutException>(silent.InnerException);
        Assert.Equal(BrymenErrorCodes.InvalidFrame, invalid.Code);
    }

    [Fact]
    public async Task First_66_after_open_and_after_memory_commands_waits_for_the_slow_answer()
    {
        var setup = new SimulatedSetup();
        var recording = new TimeoutRecordingProvider(setup.Provider);
        setup.Options.DeviceProvider = recording;
        setup.Options.ReportTimeout = TimeSpan.FromMilliseconds(250);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);
        setup.Options.RealTimeResendTimeout = TimeSpan.FromMilliseconds(400);
        using var connection = new MeterConnection(setup.Options);

        Assert.True(connection.IsNextRealTimeSlow);
        await connection.ExecuteRealTimeAsync();                     // 1º depois da criação, a detecção: reenvios até 3 s
        Assert.False(connection.IsNextRealTimeSlow);
        await connection.ExecuteRealTimeAsync();                     // BM52x: ReportTimeout
        await connection.ExecuteAsync(Bm52xCommands.MemoryHead, 4);  // memória: ReportTimeout
        Assert.True(connection.IsNextRealTimeSlow);
        await connection.ExecuteRealTimeAsync();                     // 1º depois da memória, BM52x: um envio, 3 s
        await connection.ExecuteRealTimeAsync();                     // ReportTimeout
        connection.Close();
        await connection.ExecuteRealTimeAsync();                     // reabrir não deixa o medidor lento: ReportTimeout

        TimeSpan slow = TimeSpan.FromSeconds(3), normal = TimeSpan.FromMilliseconds(250), window = TimeSpan.FromMilliseconds(400);
        Assert.Equal(
            [(0x66, window), (0x66, normal), (0x88, normal), (0x66, slow), (0x66, normal), (0x66, normal)],
            recording.FirstReportTimeouts.Select(t => ((int)t.Command, t.Timeout)));
    }

    [Fact]
    public async Task A_failed_slow_66_still_ends_the_long_wait_but_a_canceled_one_does_not()
    {
        var setup = new SimulatedSetup();
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(1);
        using var connection = new MeterConnection(setup.Options);

        setup.Meter.IsPoweredOn = false;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ExecuteRealTimeAsync(cts.Token));
        Assert.True(connection.IsNextRealTimeSlow);

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => connection.ExecuteRealTimeAsync());
        Assert.False(connection.IsNextRealTimeSlow);
    }

    [Fact]
    public async Task Slow_first_66_succeeds_within_3_s_and_a_later_slow_66_times_out_with_W7012()
    {
        // Relógio falso no cabo simulado e no medidor: o 1º 66 depois da memória só é respondido 2,06 s depois de
        // enviado, como no BM525s real (§2.7); os demais, na hora. Depois, um 66 lento fora dessa janela dá W7012.
        var time = new FakeTimeProvider();
        var meter = SimulatedBm52xMeter.FromCaptures(time);
        SimulatedHidDevice device = null!;
        bool afterMemory = true;
        bool slowNext = false;
        var delay = TimeSpan.FromMilliseconds(2060);
        device = new SimulatedHidDevice((id, data) =>
        {
            var reports = meter.Respond(id, data);
            byte command = data.Span[2];
            bool slow = command == Bm52xCommands.RealTime && (afterMemory || slowNext);
            afterMemory = command is Bm52xCommands.MemoryHead or Bm52xCommands.MemoryNext or Bm52xCommands.MemoryCurrent;
            if (!slow || reports is null)
                return reports;
            slowNext = false;
            foreach (var report in reports)
                device.EnqueueReport(report, delay);
            return null;
        }, timeProvider: time);
        var options = new BrymenMeterOptions
        {
            DeviceProvider = new SimulatedHidDeviceProvider(device),
            TimeProvider = time,
            ReportTimeout = TimeSpan.FromSeconds(1),
            DrainTimeout = TimeSpan.Zero,
            RecoveryDrainTimeout = TimeSpan.Zero,
        };
        using var connection = new MeterConnection(options);
        var step = TimeSpan.FromMilliseconds(20);

        // 1º 66 depois da abertura (a memória pode ter sido lida por outro programa): 2,06 s, dentro dos 3 s.
        long start = time.GetTimestamp();
        var first = await TestWait.PumpAsync(time, connection.ReadRealTimeAsync(), step, TimeSpan.FromSeconds(5));
        Assert.InRange(time.GetElapsedTime(start), delay, TimeSpan.FromSeconds(3));
        Assert.Equal(Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[0]), first.RawHex);

        // 66 seguinte: na hora, sem avançar o relógio.
        await connection.ReadRealTimeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Memória completa, e o 1º 66 depois dela: lento, e bem-sucedido.
        var image = await TestWait.PumpAsync(time, connection.ReadLoggerImageAsync(), step, TimeSpan.FromSeconds(10));
        Assert.Equal(95, image.BlockCount);
        start = time.GetTimestamp();
        await TestWait.PumpAsync(time, connection.ReadRealTimeAsync(), step, TimeSpan.FromSeconds(5));
        Assert.InRange(time.GetElapsedTime(start), delay, TimeSpan.FromSeconds(3));

        // Um 66 lento fora da janela: o ReportTimeout de 1 s vale, e o resultado é W7012.
        slowNext = true;
        start = time.GetTimestamp();
        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => TestWait.PumpAsync(time, connection.ReadRealTimeAsync(), step, TimeSpan.FromSeconds(5)));
        Assert.Equal(BrymenErrorCodes.NoDataReceived, ex.Code);
        Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public async Task Close_during_a_command_interrupts_it_and_dispose_refuses_new_ones()
    {
        var setup = new SimulatedSetup();
        setup.Options.ReportTimeout = TimeSpan.FromSeconds(10);
        setup.Options.RealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(10);
        var connection = new MeterConnection(setup.Options);
        setup.Meter.IsPoweredOn = false;

        var pending = connection.ExecuteRealTimeAsync();
        await TestWait.UntilAsync(() => setup.Device.Writes.Count == 1, "o 66");
        connection.Close();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        var result = connection.TryExecuteAsync(Bm52xCommands.RealTime, 3);
        await TestWait.UntilAsync(() => setup.Device.Writes.Count == 2, "o 2º 66");
        connection.Close();
        Assert.IsType<ObjectDisposedException>((await result.WaitAsync(TimeSpan.FromSeconds(5))).Error);

        connection.Dispose();
        connection.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.ExecuteRealTimeAsync());
        Assert.Throws<ObjectDisposedException>(() => connection.Open());
    }

    [Fact]
    public void FindDevices_lists_only_connected_cables()
    {
        var setup = new SimulatedSetup();

        Assert.Equal([SimulatedHidDevice.DefaultInfo], MeterConnection.FindDevices(setup.Provider));
        setup.Device.Disconnect();
        Assert.Empty(BrymenMeter.FindDevices(setup.Provider));
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        var provider = new SimulatedHidDeviceProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, ReportTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, PollInterval = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, LoggerRead = new LoggerReadOptions { MaxRestarts = -1 } }));
        Assert.Throws<ArgumentException>(() => new MeterConnection(new BrymenMeterOptions { DeviceProvider = provider, TimeProvider = null! }));
    }

    private sealed class FailingOpenProvider : IHidDeviceProvider
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => [SimulatedHidDevice.DefaultInfo];

        public IHidDevice Open(HidDeviceInfo device) => throw new HidException($"Sem permissão para abrir {device.Path} (EACCES).", nativeError: 13);
    }
}
