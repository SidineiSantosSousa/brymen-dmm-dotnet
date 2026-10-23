namespace Brymen.Dmm.Hid.Tests;

/// <summary>O medidor simulado, atrás do <see cref="Bu86xLink"/>, reproduz byte a byte as capturas reais.</summary>
public class SimulatedBm52xMeterTests
{
    private static readonly Bu86xLinkOptions Options = new() { ReportTimeout = TimeSpan.FromMilliseconds(100) };

    private static Bu86xLink CreateLink(SimulatedBm52xMeter meter) => new(SimulatedHidDevice.CreateBm52x(meter), Options);

    [Fact]
    public async Task RealTimeReturnsTheCapturedFramesInRotation()
    {
        using var link = CreateLink(new SimulatedBm52xMeter());

        for (int round = 0; round < 2; round++)
        {
            foreach (var expected in SimulatedBm52xMeter.DefaultRealTimeFrames)
                Assert.Equal(expected, await link.ExecuteAsync(0x66, 3));
        }
    }

    [Fact]
    public async Task MemoryDumpMatchesTheCaptureAndEndsWithoutResponse()
    {
        var capture = TestData.ReadCapture("memory-dump-1.txt");
        var meter = new SimulatedBm52xMeter();
        using var link = CreateLink(meter);

        Assert.Equal(95, capture.Count);
        foreach (var (command, expected) in capture)
        {
            byte[] frame = await link.ExecuteAsync(Convert.ToByte(command, 16), 4);
            Assert.Equal(expected, frame);
            Assert.True(TestData.HasValidChecksum(frame));
        }

        Assert.Equal(95 * 24, meter.MemoryPointer);
        // §2.7: o 89 além do fim da memória não tem resposta.
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));
    }

    [Fact]
    public async Task CurrentBlockCommandRereadsTheLastBlock()
    {
        var sequence = TestData.ReadCapture("cmd-sequences-2026-10-22.txt").Where(c => c.Command.StartsWith("528", StringComparison.Ordinal)).ToList();
        using var link = CreateLink(new SimulatedBm52xMeter());

        Assert.Equal(["5288", "528A", "5289", "528A"], sequence.Select(s => s.Command));
        foreach (var (command, expected) in sequence)
            Assert.Equal(expected, await link.ExecuteAsync(Convert.ToByte(command[2..], 16), 4));
    }

    [Fact]
    public void DefaultMemoryImageIsTheRealDump()
    {
        byte[] image = SimulatedBm52xMeter.LoadDefaultMemoryImage();

        Assert.Equal(2280, image.Length);
        int totalBytes = image[1] | image[2] << 8 | image[3] << 16;
        Assert.Equal(2272, totalBytes);
        image[0] ^= 0xFF;
        Assert.NotEqual(image[0], SimulatedBm52xMeter.LoadDefaultMemoryImage()[0]); // cópia independente
    }

    [Fact]
    public async Task UnknownCommandsAndPoweredOffMeterDoNotRespond()
    {
        var meter = new SimulatedBm52xMeter();
        using var link = CreateLink(meter);

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x82, 3));
        meter.IsPoweredOn = false;
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3));
        meter.IsPoweredOn = true;
        Assert.Equal(27, (await link.ExecuteAsync(0x66, 3)).Length);
    }

    [Fact]
    public void ResponderIgnoresOtherFamiliesAndReportIds()
    {
        var meter = new SimulatedBm52xMeter();

        // O BM525s também aceita o 82 66, o tempo real comum às duas famílias (§8.1); da família 82h, só ele.
        Assert.Equal(3, meter.Respond(0x00, new byte[] { 0x00, 0x82, 0x66 })?.Count);
        Assert.Null(meter.Respond(0x00, new byte[] { 0x00, 0x82, 0x88 }));
        Assert.Null(meter.Respond(0x00, new byte[] { 0x00, 0x83, 0x66 }));
        Assert.Null(meter.Respond(0x01, new byte[] { 0x00, 0x52, 0x66 }));
        Assert.Equal(3, meter.Respond(0x00, new byte[] { 0x00, 0x52, 0x66 })?.Count);
    }

    [Fact]
    public async Task CustomFramesAndMemoryAreServed()
    {
        var frame = Convert.FromHexString("00002080A0DBE9E400000000000000006000005252525210000000");
        var meter = new SimulatedBm52xMeter([frame], [.. Enumerable.Range(1, 30).Select(i => (byte)i)]);
        using var link = new Bu86xLink(SimulatedHidDevice.CreateBm52x(meter), Options);

        Assert.Equal(frame, await link.ExecuteAsync(0x66, 3));
        byte[] head = await link.ExecuteAsync(0x88, 4);
        byte[] tail = await link.ExecuteAsync(0x89, 4);

        Assert.Equal(Enumerable.Range(1, 8).Select(i => (byte)i), head[1..9]);
        Assert.Equal(new byte[] { 25, 26, 27, 28, 29, 30, 0, 0 }, tail[1..9]);
        Assert.True(TestData.HasValidChecksum(head));
        Assert.True(TestData.HasValidChecksum(tail));
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));

        Assert.Throws<ArgumentException>(() => meter.SetRealTimeFrames(new byte[26]));
    }
}
