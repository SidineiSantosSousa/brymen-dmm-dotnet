using System.Diagnostics;

namespace Brymen.Dmm.Hid.Tests;

public class Bu86xLinkTests
{
    private static readonly Bu86xLinkOptions FastOptions = new()
    {
        ReportTimeout = TimeSpan.FromMilliseconds(200),
        DrainTimeout = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>Responde com <paramref name="count"/> relatórios de 8 bytes: [cmd, índice, 1..6].</summary>
    private static Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> Echo(int count) =>
        (_, data) => [.. Enumerable.Range(0, count).Select(i => new byte[] { data.Span[2], (byte)i, 1, 2, 3, 4, 5, 6 })];

    [Fact]
    public async Task RealTimeFrameHas27BytesWithReportIdPlaceholders()
    {
        using var device = new SimulatedHidDevice(Echo(3));
        using var link = new Bu86xLink(device, FastOptions);

        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.Equal(27, frame.Length);
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(0x00, frame[r * 9]);
            Assert.Equal(new byte[] { 0x66, (byte)r, 1, 2, 3, 4, 5, 6 }, frame[(r * 9 + 1)..(r * 9 + 9)]);
        }
        var write = Assert.Single(device.Writes);
        Assert.Equal(0x00, write.ReportId);
        Assert.Equal(new byte[] { 0x00, 0x52, 0x66 }, write.Data);
    }

    [Fact]
    public async Task MemoryFrameHas36Bytes()
    {
        using var device = new SimulatedHidDevice(Echo(4));
        using var link = new Bu86xLink(device, FastOptions);

        byte[] frame = await link.ExecuteAsync(0x88, 4);

        Assert.Equal(36, frame.Length);
        Assert.All(new[] { 0, 9, 18, 27 }, i => Assert.Equal(0x00, frame[i]));
        Assert.Equal(0x88, frame[28]);
        Assert.Equal(3, frame[29]);
    }

    [Fact]
    public async Task StaleReportsAreDrainedBeforeTheCommand()
    {
        using var device = new SimulatedHidDevice(Echo(3));
        for (int i = 0; i < 5; i++)
            device.EnqueueReport([0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE]);
        using var link = new Bu86xLink(device, FastOptions);

        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.DoesNotContain((byte)0xEE, frame);
        Assert.Equal(0x66, frame[1]);
        Assert.Equal(0, device.PendingReportCount);
    }

    [Fact]
    public async Task LateReportsOfATimedOutCommandDoNotShiftTheNextFrame()
    {
        // 1º relatório imediato, os demais 150 ms depois cada: com timeout de 50 ms só o 1º chega a tempo.
        using var device = new SimulatedHidDevice(Echo(3)) { InterReportDelay = TimeSpan.FromMilliseconds(150) };
        using var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromMilliseconds(50) });

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x88, 3));
        await Task.Delay(450); // os 2 relatórios atrasados do 88 chegam
        Assert.Equal(2, device.PendingReportCount);

        device.InterReportDelay = TimeSpan.Zero;
        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.Equal(new byte[] { 0x66, 0x66, 0x66 }, new[] { frame[1], frame[10], frame[19] });
        Assert.Equal(new byte[] { 0, 1, 2 }, new[] { frame[2], frame[11], frame[20] });
    }

    [Fact]
    public async Task ShortReportIsRejected()
    {
        using var device = new SimulatedHidDevice((_, _) => [new byte[8], new byte[5], new byte[8]]);
        using var link = new Bu86xLink(device, FastOptions);

        var ex = await Assert.ThrowsAsync<Bu86xShortReportException>(() => link.ExecuteAsync(0x66, 3));

        Assert.IsAssignableFrom<HidException>(ex);
        Assert.Equal((0x66, 2, 3, 5), (ex.Command, ex.ReportsReceived, ex.ReportsExpected, ex.ReportLength));
        Assert.Equal("Relatório 2/3 curto do BC-86X no comando 52 66: 5 bytes (esperado 8).", ex.Message);
    }

    [Fact]
    public async Task ShortFirstReportCountsAsReceivedAndTheLinkRecovers()
    {
        // O relatório curto é o 1º: o medidor respondeu (ReportsReceived = 1), e o resto da resposta fica na fila.
        int calls = 0;
        using var device = new SimulatedHidDevice((_, data) => Interlocked.Increment(ref calls) == 1
            ? [new byte[3], new byte[8], new byte[8], new byte[8]]
            : [.. Enumerable.Range(0, 4).Select(i => new byte[] { data.Span[2], (byte)i, 0, 0, 0, 0, 0, 0 })]);
        using var link = new Bu86xLink(device, FastOptions);

        var ex = await Assert.ThrowsAsync<Bu86xShortReportException>(() => link.ExecuteAsync(0x89, 4));
        byte[] frame = await link.ExecuteAsync(0x8A, 4);

        Assert.Equal((0x89, 1, 4, 3), (ex.Command, ex.ReportsReceived, ex.ReportsExpected, ex.ReportLength));
        Assert.Equal(new byte[] { 0x8A, 0x8A, 0x8A, 0x8A }, new[] { frame[1], frame[10], frame[19], frame[28] });
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, new[] { frame[2], frame[11], frame[20], frame[29] });
        Assert.Equal(0, device.PendingReportCount);
    }

    [Fact]
    public async Task MissingResponseTimesOut()
    {
        using var device = new SimulatedHidDevice((_, _) => null);
        using var link = new Bu86xLink(device, FastOptions);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));

        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IncompleteResponseTimesOutAndTheLinkRecovers()
    {
        int calls = 0;
        using var device = new SimulatedHidDevice((_, data) =>
        {
            int count = Interlocked.Increment(ref calls) == 1 ? 2 : 3;
            return [.. Enumerable.Range(0, count).Select(i => new byte[] { data.Span[2], (byte)i, 0, 0, 0, 0, 0, 0 })];
        });
        using var link = new Bu86xLink(device, FastOptions);

        var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3));
        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.Equal(new byte[] { 0, 1, 2 }, new[] { frame[2], frame[11], frame[20] });
        Assert.Equal((0x66, 2, 3), (ex.Command, ex.ReportsReceived, ex.ReportsExpected));
        Assert.Contains("52 66: 2 de 3", ex.Message);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ReportsReceivedAreCountedForSuccessTimeoutAndShortReport()
    {
        int calls = 0;
        using var device = new SimulatedHidDevice((_, data) => Interlocked.Increment(ref calls) switch
        {
            1 => [.. Enumerable.Range(0, 4).Select(i => new byte[] { data.Span[2], (byte)i, 0, 0, 0, 0, 0, 0 })],
            2 => [new byte[8], new byte[8]],
            3 => [new byte[8], new byte[8], new byte[5]],
            _ => null,
        });
        using var link = new Bu86xLink(device, FastOptions);
        Assert.Equal(0, link.LastCommandReportsReceived);

        await link.ExecuteAsync(0x88, 4);
        Assert.Equal(4, link.LastCommandReportsReceived);

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));
        Assert.Equal(2, link.LastCommandReportsReceived);

        var shortReport = await Assert.ThrowsAsync<Bu86xShortReportException>(() => link.ExecuteAsync(0x89, 4));
        Assert.Equal(3, link.LastCommandReportsReceived);
        Assert.Equal(shortReport.ReportsReceived, link.LastCommandReportsReceived);

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));
        Assert.Equal(0, link.LastCommandReportsReceived);
    }

    [Fact]
    public async Task ReportsReceivedBeforeTheCableIsRemovedAreCounted()
    {
        // A remoção do cabo no meio da resposta não traz a contagem na exceção: o link a guarda.
        using var device = new SimulatedHidDevice(Echo(4)) { InterReportDelay = TimeSpan.FromMilliseconds(300) };
        using var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(5) });
        var call = link.ExecuteAsync(0x89, 4);
        await TestWait.UntilAsync(() => link.LastCommandReportsReceived == 1, "o 1º relatório");

        device.Disconnect();

        var ex = await Assert.ThrowsAsync<HidException>(() => call);
        Assert.IsNotType<Bu86xShortReportException>(ex);
        Assert.Equal(1, link.LastCommandReportsReceived);

        // Uma chamada que falha antes de enviar o comando (cabo ainda ausente) volta a 0.
        await Assert.ThrowsAsync<HidException>(() => link.ExecuteAsync(0x8A, 4));
        Assert.Equal(0, link.LastCommandReportsReceived);
    }

    [Fact]
    public async Task ReportsReceivedBeforeADisposeAreCounted()
    {
        var device = new SimulatedHidDevice(Echo(4)) { InterReportDelay = TimeSpan.FromMilliseconds(300) };
        var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(5) });
        var call = link.ExecuteAsync(0x89, 4);
        await TestWait.UntilAsync(() => link.LastCommandReportsReceived == 1, "o 1º relatório");

        link.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => call);
        Assert.Equal(1, link.LastCommandReportsReceived);
    }

    [Fact]
    public async Task A_call_on_a_link_disposed_beforehand_does_not_keep_the_count_of_the_previous_exchange()
    {
        // O MeterConnection pega o link e só depois chama ExecuteAsync: um Close de outra thread no meio descarta o link
        // antes da entrada. A contagem do 89 anterior (4) faria o leitor da memória mandar um 8A, que relê o bloco
        // anterior como se fosse o seguinte.
        var device = new SimulatedHidDevice(Echo(4));
        var link = new Bu86xLink(device, FastOptions);
        await link.ExecuteAsync(0x89, 4);
        Assert.Equal(4, link.LastCommandReportsReceived);

        link.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => link.ExecuteAsync(0x89, 4));
        Assert.Equal(0, link.LastCommandReportsReceived);
        Assert.Single(device.Writes);
    }

    [Fact]
    public async Task FirstReportTimeoutOverridesOnlyTheWaitForTheFirstReport()
    {
        // Resposta que começa 300 ms depois do comando, como o 1º 66 depois da leitura da memória (≈ 2 s no medidor).
        using var device = new SimulatedHidDevice(Echo(3)) { ResponseLatency = TimeSpan.FromMilliseconds(300) };
        using var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromMilliseconds(100) });

        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3));
        await Task.Delay(600); // o resto da resposta interrompida chega e é descartado no próximo comando
        byte[] frame = await link.ExecuteAsync(0x66, 3, TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 0, 1, 2 }, new[] { frame[2], frame[11], frame[20] });
        Assert.Equal(3, link.LastCommandReportsReceived);

        // Os relatórios seguintes continuam com o ReportTimeout.
        device.InterReportDelay = TimeSpan.FromMilliseconds(300);
        var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, ex.ReportsReceived);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveFirstReportTimeoutIsRejected(int milliseconds)
    {
        using var link = new Bu86xLink(new SimulatedHidDevice(Echo(3)), FastOptions);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.ExecuteAsync(0x66, 3, TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal(27, (await link.ExecuteAsync(0x66, 3, Timeout.InfiniteTimeSpan)).Length);
    }

    [Fact]
    public async Task TimeoutWithoutAnyReportSaysTheMeterDidNotAnswer()
    {
        using var link = new Bu86xLink(new SimulatedHidDevice((_, _) => null), FastOptions);

        var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x89, 4));

        Assert.Equal(0, ex.ReportsReceived);
        Assert.Equal("Sem resposta do medidor ao comando 52 89.", ex.Message);
    }

    [Fact]
    public async Task ReportsOfAnInterruptedExchangeStillInFlightAreDrained()
    {
        // O responder cancela a chamada logo depois da escrita: os 3 relatórios do 88 chegam depois, a +30/+60/+90 ms.
        using var cts = new CancellationTokenSource();
        using var device = new SimulatedHidDevice((_, data) =>
        {
            if (data.Span[2] == 0x88)
                cts.Cancel();
            return [.. Enumerable.Range(0, 3).Select(i => new byte[] { data.Span[2], (byte)i, 0, 0, 0, 0, 0, 0 })];
        })
        {
            ResponseLatency = TimeSpan.FromMilliseconds(30),
            InterReportDelay = TimeSpan.FromMilliseconds(30),
        };
        using var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(2) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => link.ExecuteAsync(0x88, 3, cts.Token));
        Assert.Equal(3, device.PendingReportCount);
        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.Equal(new byte[] { 0x66, 0x66, 0x66 }, new[] { frame[1], frame[10], frame[19] });
        Assert.Equal(0, device.PendingReportCount);
    }

    [Fact]
    public async Task FirstCommandAfterOpeningDrainsLateReportsOfAPreviousHandle()
    {
        // Resto de uma resposta pedida por um handle anterior, que chega 40 ms depois da abertura.
        using var device = new SimulatedHidDevice(Echo(3));
        device.EnqueueReport([0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE], TimeSpan.FromMilliseconds(40));
        using var link = new Bu86xLink(device, FastOptions);

        byte[] frame = await link.ExecuteAsync(0x66, 3);

        Assert.DoesNotContain((byte)0xEE, frame);
        Assert.Equal(0, device.PendingReportCount);
    }

    [Fact]
    public async Task OnlyTheShortDrainRunsAfterACompleteExchange()
    {
        var recording = new RecordingHidDevice(new SimulatedHidDevice(Echo(3)));
        using var link = new Bu86xLink(recording, new Bu86xLinkOptions
        {
            ReportTimeout = TimeSpan.FromSeconds(1),
            DrainTimeout = TimeSpan.FromMilliseconds(5),
            RecoveryDrainTimeout = TimeSpan.FromMilliseconds(300),
        });

        await link.ExecuteAsync(0x66, 3); // 1º comando: descarte longo
        var sw = Stopwatch.StartNew();
        await link.ExecuteAsync(0x66, 3);

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"O 2º comando levou {sw.Elapsed}.");
        Assert.Equal([TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(5)], recording.ReadTimeouts.Where(t => t != TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DrainGivesUpOnADeviceThatNeverGoesQuiet()
    {
        using var link = new Bu86xLink(new ChattyHidDevice(), FastOptions);

        byte[] frame = await link.ExecuteAsync(0x66, 3).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(27, frame.Length);
    }

    [Fact]
    public async Task DisposeDuringACallFailsItWithObjectDisposedException()
    {
        var device = new SimulatedHidDevice((_, _) => null);
        var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(30) });
        var call = link.ExecuteAsync(0x66, 3);
        await TestWait.UntilAsync(() => device.Writes.Count == 1, "a escrita do comando");

        var sw = Stopwatch.StartNew();
        link.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => call);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"A chamada demorou {sw.Elapsed} para falhar.");
    }

    [Fact]
    public async Task ConcurrentDisposeClosesTheDeviceOnce()
    {
        var recording = new RecordingHidDevice(new SimulatedHidDevice(Echo(3)));
        var link = new Bu86xLink(recording, FastOptions);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(link.Dispose)));

        Assert.Equal(1, recording.DisposeCount);
    }

    [Fact]
    public async Task CancellationStopsWaitingForTheResponse()
    {
        using var device = new SimulatedHidDevice((_, _) => null);
        using var link = new Bu86xLink(device, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => link.ExecuteAsync(0x66, 3, cts.Token));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Cancelamento demorou {sw.Elapsed}.");
    }

    [Fact]
    public async Task ConcurrentCallsAreSerialized()
    {
        using var simulated = new SimulatedHidDevice(Echo(3))
        {
            ResponseLatency = TimeSpan.FromMilliseconds(3),
            InterReportDelay = TimeSpan.FromMilliseconds(1),
        };
        var recording = new RecordingHidDevice(simulated);
        using var link = new Bu86xLink(recording, new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromSeconds(5) });

        byte[] commands = [.. Enumerable.Range(0x60, 8).Select(i => (byte)i)];
        var frames = await Task.WhenAll(commands.Select(c => Task.Run(() => link.ExecuteAsync(c, 3))));

        for (int i = 0; i < commands.Length; i++)
            Assert.Equal(new[] { commands[i], commands[i], commands[i] }, new[] { frames[i][1], frames[i][10], frames[i][19] });
        Assert.Equal(1, recording.MaxConcurrentOperations);

        // Cada escrita é seguida pelos seus 3 relatórios, sem intercalação com outro comando.
        var events = recording.Events;
        Assert.Equal(commands.Length * 4, events.Count);
        for (int t = 0; t < events.Count; t += 4)
        {
            string cmd = events[t][1..];
            Assert.StartsWith("W", events[t]);
            Assert.All(events.Skip(t + 1).Take(3), e => Assert.Equal("R" + cmd, e));
        }
    }

    [Fact]
    public async Task DisposeClosesTheDevice()
    {
        var device = new SimulatedHidDevice(Echo(3));
        var link = new Bu86xLink(device, FastOptions);

        link.Dispose();
        link.Dispose();

        Assert.False(device.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => link.ExecuteAsync(0x66, 3));
    }

    [Fact]
    public async Task InvalidReportCountIsRejected()
    {
        using var link = new Bu86xLink(new SimulatedHidDevice(Echo(3)), FastOptions);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.ExecuteAsync(0x66, 0));
    }

    [Fact]
    public void FindDeviceUsesTheFirstMatchOrTheConfiguredPath()
    {
        var first = new SimulatedHidDevice(Echo(3), SimulatedHidDevice.DefaultInfo with { Path = "sim://a" });
        var second = new SimulatedHidDevice(Echo(3), SimulatedHidDevice.DefaultInfo with { Path = "sim://b" });
        var other = new SimulatedHidDevice(Echo(3), new HidDeviceInfo("sim://other", 0x046D, 0xC31C));
        var provider = new SimulatedHidDeviceProvider(other, first, second);

        Assert.Equal("sim://a", Bu86xLink.FindDevice(provider)?.Path);
        Assert.Equal("sim://b", Bu86xLink.FindDevice(provider, "sim://b")?.Path);
        Assert.Null(Bu86xLink.FindDevice(provider, "sim://other"));
        Assert.Null(Bu86xLink.FindDevice(new SimulatedHidDeviceProvider([])));
    }

    [Fact]
    public async Task OpenFindsAndOpensTheCable()
    {
        var provider = new SimulatedHidDeviceProvider();

        using (var link = Bu86xLink.Open(provider))
        {
            byte[] frame = await link.ExecuteAsync(0x66, 3);
            Assert.Equal(new byte[] { 0x52, 0x52, 0x52, 0x52 }, frame[19..23]);
        }

        var ex = Assert.Throws<HidException>(() => Bu86xLink.Open(new SimulatedHidDeviceProvider([])));
        Assert.Contains("não encontrado", ex.Message);
    }

    /// <summary>Dispositivo com um relatório sempre disponível (ex.: firmware defeituoso que não para de enviar).</summary>
    private sealed class ChattyHidDevice : IHidDevice
    {
        public HidDeviceInfo Info { get; } = SimulatedHidDevice.DefaultInfo;

        public int InputReportLength => 8;

        public int OutputReportLength => 3;

        public ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            buffer.Span[..8].Fill(0xAA);
            return ValueTask.FromResult(8);
        }

        public void Dispose()
        {
        }
    }
}
