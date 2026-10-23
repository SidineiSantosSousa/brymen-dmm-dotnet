using System.Diagnostics;

namespace Brymen.Dmm.Hid.Tests;

public class SimulatedHidDeviceTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task ResponderReceivesReportIdAndPaddedData()
    {
        byte? seenId = null;
        byte[]? seenData = null;
        using var device = new SimulatedHidDevice((id, data) =>
        {
            seenId = id;
            seenData = data.ToArray();
            return null;
        });

        await device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52 });

        Assert.Equal((byte)0x00, seenId);
        Assert.Equal(new byte[] { 0x00, 0x52, 0x00 }, seenData);
        Assert.Equal(new byte[] { 0x00, 0x52, 0x00 }, Assert.Single(device.Writes).Data);
    }

    [Fact]
    public async Task ReadsConsumeTheQueueInOrder()
    {
        using var device = new SimulatedHidDevice((_, _) => [new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 3, 4, 5 }]);
        await device.WriteReportAsync(0, new byte[3]);
        var buffer = new byte[8];

        Assert.Equal(8, await device.ReadReportAsync(buffer, Short));
        Assert.Equal(1, buffer[0]);
        Assert.Equal(8, await device.ReadReportAsync(buffer, Short));
        Assert.Equal(2, buffer[0]);
        Assert.Equal(3, await device.ReadReportAsync(buffer, Short));
        Assert.Equal(new byte[] { 3, 4, 5 }, buffer[..3]);
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, Short));
    }

    [Fact]
    public async Task EmptyQueueTimesOutAfterTheTimeout()
    {
        using var device = new SimulatedHidDevice((_, _) => null);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromMilliseconds(100)));

        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(90), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ZeroTimeoutReturnsQueuedReportsAndOtherwiseTimesOut()
    {
        using var device = new SimulatedHidDevice((_, _) => null);
        device.EnqueueReport([9, 9, 9, 9, 9, 9, 9, 9]);
        var buffer = new byte[8];

        Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, TimeSpan.Zero));
    }

    [Fact]
    public async Task ResponseLatencyDelaysTheFirstReport()
    {
        using var device = new SimulatedHidDevice((_, _) => [new byte[8], new byte[8]])
        {
            ResponseLatency = TimeSpan.FromMilliseconds(200),
            InterReportDelay = TimeSpan.FromMilliseconds(100),
        };
        var buffer = new byte[8];

        var sw = Stopwatch.StartNew();
        await device.WriteReportAsync(0, new byte[3]);
        await Assert.ThrowsAsync<TimeoutException>(async () => await device.ReadReportAsync(buffer, Short));
        await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(5));
        var first = sw.Elapsed;
        await device.ReadReportAsync(buffer, TimeSpan.FromSeconds(5));
        var second = sw.Elapsed;

        Assert.InRange(first, TimeSpan.FromMilliseconds(190), TimeSpan.FromSeconds(4));
        // 100 ms entre os relatórios, menos a granularidade do timer (≈ 15,6 ms no Windows), que pode atrasar só o 1º.
        Assert.True(second - first >= TimeSpan.FromMilliseconds(75), $"Intervalo entre relatórios: {second - first}.");
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task CancellationInterruptsARead()
    {
        using var device = new SimulatedHidDevice((_, _) => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await device.ReadReportAsync(new byte[8], TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task DisconnectFailsPendingAndFutureOperations()
    {
        using var device = new SimulatedHidDevice((_, _) => null);
        var pending = device.ReadReportAsync(new byte[8], TimeSpan.FromSeconds(30)).AsTask();

        device.Disconnect();

        var ex = await Assert.ThrowsAsync<HidException>(() => pending);
        Assert.Contains("desconectado", ex.Message);
        await Assert.ThrowsAsync<HidException>(async () => await device.WriteReportAsync(0, new byte[3]));
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task ProviderHidesDisconnectedDevicesAndReopensAfterReconnect()
    {
        var device = SimulatedHidDevice.CreateBm52x();
        var provider = new SimulatedHidDeviceProvider(device);
        var info = Assert.Single(provider.Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId));

        device.Disconnect();
        Assert.Empty(provider.Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId));
        Assert.Throws<HidException>(() => provider.Open(info));

        device.Reconnect();
        // O handle antigo continua inválido até o provider reabrir o dispositivo.
        Assert.Throws<HidException>(() => device.EnqueueReport(new byte[8]));
        using var reopened = provider.Open(info);
        Assert.Same(device, reopened);
        Assert.Equal(0, device.PendingReportCount);
        await reopened.WriteReportAsync(0, new byte[] { 0x00, 0x52, 0x66 });
        Assert.Equal(8, await reopened.ReadReportAsync(new byte[8], Short));
    }

    [Fact]
    public async Task DisposedDeviceRejectsOperationsUntilReopened()
    {
        var device = new SimulatedHidDevice((_, _) => [new byte[8]]);
        var provider = new SimulatedHidDeviceProvider(device);

        device.Dispose();

        Assert.False(device.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await device.ReadReportAsync(new byte[8], Short));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await device.WriteReportAsync(0, new byte[3]));

        provider.Open(device.Info);
        Assert.True(device.IsOpen);
        await device.WriteReportAsync(0, new byte[3]);
        Assert.Equal(8, await device.ReadReportAsync(new byte[8], Short));
    }

    [Fact]
    public async Task OversizedOutputReportIsRejected()
    {
        using var device = new SimulatedHidDevice((_, _) => null);

        await Assert.ThrowsAsync<ArgumentException>(async () => await device.WriteReportAsync(0, new byte[4]));
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void ProviderFiltersByVendorAndProduct()
    {
        var cable = SimulatedHidDevice.CreateBm52x();
        var keyboard = new SimulatedHidDevice((_, _) => null, new HidDeviceInfo("sim://kbd", 0x046D, 0xC31C));
        var provider = new SimulatedHidDeviceProvider(keyboard, cable);

        Assert.Equal(SimulatedHidDevice.DefaultInfo, Assert.Single(provider.Enumerate(0x0820, 0x0001)));
        Assert.Equal("sim://kbd", Assert.Single(provider.Enumerate(0x046D, 0xC31C)).Path);
        Assert.Throws<HidException>(() => provider.Open(new HidDeviceInfo("sim://nada", 0x0820, 0x0001)));
        Assert.Throws<ArgumentException>(() => new SimulatedHidDeviceProvider(cable, SimulatedHidDevice.CreateBm52x()));
    }

    [Fact]
    public async Task DefaultSimulatorsReproduceTheCapturedMeterLikeOpenSimulatedAsync()
    {
        // O provider sem argumentos: as capturas reais (14 quadros) e a latência típica, como o OpenSimulatedAsync.
        var provider = new SimulatedHidDeviceProvider();
        var cable = Assert.Single(provider.Devices);
        Assert.Equal(TimeSpan.FromMilliseconds(20), SimulatedHidDevice.TypicalResponseLatency);
        Assert.Equal(SimulatedHidDevice.TypicalResponseLatency, cable.ResponseLatency);
        Assert.Equal(SimulatedHidDevice.TypicalResponseLatency / 8, cable.InterReportDelay);

        using (var link = Bu86xLink.Open(provider, options: new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(1) }))
        {
            foreach (var expected in SimulatedBm52xMeter.CapturedRealTimeFrames.Take(5))
                Assert.Equal(expected, await link.ExecuteAsync(0x66, 3));
        }

        // CreateBm52x sem medidor: o mesmo medidor, sem latência, com o 8A que avança como um 89 logo depois do 88 (§2.7).
        var device = SimulatedHidDevice.CreateBm52x();
        Assert.Equal(TimeSpan.Zero, device.ResponseLatency);
        var fastOptions = new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromMilliseconds(200) };
        using var fast = new Bu86xLink(device, fastOptions);
        Assert.Equal(SimulatedBm52xMeter.CapturedRealTimeFrames[0], await fast.ExecuteAsync(0x66, 3));
        var head = await fast.ExecuteAsync(0x88, 4);
        var currentRightAfterHead = await fast.ExecuteAsync(0x8A, 4);

        using var reference = new Bu86xLink(SimulatedHidDevice.CreateBm52x(new SimulatedBm52xMeter()), fastOptions);
        Assert.Equal(head, await reference.ExecuteAsync(0x88, 4));
        Assert.Equal(await reference.ExecuteAsync(0x89, 4), currentRightAfterHead);
    }

    [Fact]
    public void DefaultProviderExposesOneSimulatedBu86x()
    {
        var provider = new SimulatedHidDeviceProvider();

        var info = Assert.Single(provider.Enumerate(0x0820, 0x0001));
        Assert.Equal("Brymen", info.Manufacturer);
        Assert.Equal("Superior DMM", info.Product);
        Assert.Equal((ushort)0x8603, info.ReleaseNumber);
        using var device = provider.Open(info);
        Assert.Equal(8, device.InputReportLength);
        Assert.Equal(3, device.OutputReportLength);
    }
}
