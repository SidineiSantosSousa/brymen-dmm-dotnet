using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Tests;

/// <summary>
/// O algoritmo de leitura da memória (movido da API), direto sobre o medidor simulado: checksum, espera depois do
/// <c>88</c>, recuperação do <c>88</c> com <c>88</c>, <c>8A</c> só depois de um <c>89</c> processado e limites.
/// </summary>
public sealed class LoggerMemoryReaderTests
{
    private static readonly string[] Dump = ["66", "88", .. Enumerable.Repeat("89", 94)];

    private static SimulatedBm52xMeter NewMeter(TimeProvider? time = null) => SimulatedBm52xMeter.FromCaptures(time);

    private static Task<LoggerMemoryImage> ReadReferenceAsync()
        => new LoggerMemoryReader(new ResponderTransport(NewMeter())).ReadAsync();

    [Fact]
    public async Task Reads_the_dump_without_faults()
    {
        var transport = new ResponderTransport(NewMeter());
        var progress = new ProgressRecorder();

        var result = await new LoggerMemoryReader(transport, progress: progress).ReadAsync();

        Assert.Equal(2272, result.Header.TotalBytes);
        Assert.Equal(95, result.BlockCount);
        Assert.Equal(95 * 24, result.Image.Length);
        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), result.ToArray());
        Assert.Equal(0, result.Retries);
        Assert.Equal(0, result.Restarts);
        Assert.Empty(result.Warnings);
        Assert.Equal(375, Assert.Single(result.Decode().Sessions).Samples.Count);
        Assert.Equal(Dump, transport.Commands);
        Assert.Equal(95, progress.Items.Count);
        Assert.Equal(new LoggerReadProgress(95, 95, 2272, 0, 0), progress.Items[^1]);
        Assert.Equal(100, progress.Items[^1].Percent);
    }

    [Theory]
    [InlineData(nameof(SimulatedFault.CorruptChecksum), true)]
    [InlineData(nameof(SimulatedFault.Truncate), true)]
    [InlineData(nameof(SimulatedFault.DropAfterProcessing), false)]
    [InlineData(nameof(SimulatedFault.Drop), false)]
    public async Task Uses_8A_only_when_the_89_was_certainly_processed(string faultName, bool expect8A)
    {
        var fault = Enum.Parse<SimulatedFault>(faultName);
        var meter = NewMeter();
        int nexts = 0;
        meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryNext && ++nexts == 40 ? fault : SimulatedFault.None;
        var transport = new ResponderTransport(meter);
        var reference = await ReadReferenceAsync();

        var result = await new LoggerMemoryReader(transport).ReadAsync();

        Assert.Equal(reference.ToArray(), result.ToArray());
        Assert.Equal(expect8A, transport.Commands.Contains("8A"));
        Assert.Equal(expect8A ? 0 : 1, result.Restarts);
        Assert.Equal(expect8A ? 1 : 2, transport.Commands.Count(c => c == "88"));
    }

    [Fact]
    public async Task Waits_200_ms_after_the_88_before_the_next_command()
    {
        var time = new FakeTimeProvider();
        var transport = new ResponderTransport(NewMeter(time));

        var read = new LoggerMemoryReader(transport, timeProvider: time).ReadAsync();

        Assert.Equal(["66", "88"], transport.Commands);
        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal(["66", "88"], transport.Commands);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(200), new LoggerReadOptions().HeadSettleDelay);
        Assert.Equal(95 * 24, result.Image.Length);
        Assert.Equal(Dump, transport.Commands);
    }

    [Theory]
    [InlineData(nameof(SimulatedFault.CorruptChecksum))]
    [InlineData(nameof(SimulatedFault.Truncate))]
    [InlineData(nameof(SimulatedFault.DropAfterProcessing))]
    [InlineData(nameof(SimulatedFault.Drop))]
    public async Task Head_failure_is_recovered_with_another_88_never_with_8A(string faultName)
    {
        // Simulador com o comportamento do medidor real: um 8A logo depois do 88 avança como um 89.
        var fault = Enum.Parse<SimulatedFault>(faultName);
        var time = new FakeTimeProvider();
        var meter = NewMeter(time);
        Assert.Equal(TimeSpan.FromMilliseconds(200), meter.CurrentAfterHeadWindow);
        int heads = 0;
        meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryHead && ++heads == 1 ? fault : SimulatedFault.None;
        var transport = new ResponderTransport(meter);
        var reference = await ReadReferenceAsync();

        var result = await TestWait.PumpAsync(time, new LoggerMemoryReader(transport, timeProvider: time).ReadAsync(), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(5));

        Assert.Equal(reference.ToArray(), result.ToArray());
        Assert.Equal(["66", "88", "88", .. Enumerable.Repeat("89", 94)], transport.Commands);
        Assert.Equal(1, result.Retries);
        Assert.Equal(0, result.Restarts);
    }

    [Fact]
    public async Task First_89_failure_after_the_88_is_recovered_with_8A_after_the_settle_delay()
    {
        var time = new FakeTimeProvider();
        var meter = NewMeter(time);
        int nexts = 0;
        meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryNext && ++nexts == 1 ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
        var transport = new ResponderTransport(meter);
        var reference = await ReadReferenceAsync();

        var result = await TestWait.PumpAsync(time, new LoggerMemoryReader(transport, timeProvider: time).ReadAsync(), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(5));

        Assert.Equal(reference.ToArray(), result.ToArray());
        Assert.Equal(["66", "88", "89", "8A", .. Enumerable.Repeat("89", 93)], transport.Commands);
    }

    [Fact]
    public async Task Exhausted_8A_count_three_retries_and_restart_from_the_88_once()
    {
        // O 89 nº 40 e os três 8A seguintes chegam com checksum errado: o bloco se esgota e a leitura recomeça do 88.
        var meter = NewMeter();
        int nexts = 0, currents = 0;
        meter.FaultInjector = (command, _) => command switch
        {
            Bm52xCommands.MemoryNext when ++nexts == 40 => SimulatedFault.CorruptChecksum,
            Bm52xCommands.MemoryCurrent when ++currents <= 3 => SimulatedFault.CorruptChecksum,
            _ => SimulatedFault.None,
        };
        var transport = new ResponderTransport(meter);
        var reference = await ReadReferenceAsync();

        var result = await new LoggerMemoryReader(transport).ReadAsync();

        Assert.Equal(reference.ToArray(), result.ToArray());
        Assert.Equal(3, transport.Commands.Count(c => c == "8A"));
        Assert.Equal(new LoggerReadOptions().MaxBlockAttempts - 1, result.Retries);
        Assert.Equal(1, result.Restarts);
        Assert.Contains(result.Warnings, w => w.Contains("Recomeço 1", StringComparison.Ordinal));
        // Um reset antes de cada 8A e um no recomeço.
        Assert.Equal(4, transport.Resets);
    }

    [Fact]
    public async Task Gives_up_with_E9016_after_the_restart_limit()
    {
        var meter = NewMeter();
        meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryNext ? SimulatedFault.Drop : SimulatedFault.None;
        var transport = new ResponderTransport(meter);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(transport).ReadAsync());

        Assert.Equal(BrymenErrorCodes.TransmissionTimeout, ex.Code);
        Assert.Equal(1 + new LoggerReadOptions().MaxRestarts, transport.Commands.Count(c => c == "88"));
    }

    [Fact]
    public async Task Gives_up_with_E9016_when_every_88_fails()
    {
        var meter = NewMeter();
        meter.FaultInjector = (command, _) => command == Bm52xCommands.MemoryHead ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
        var transport = new ResponderTransport(meter);
        var options = new LoggerReadOptions { HeadSettleDelay = TimeSpan.Zero };

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(transport, options).ReadAsync());

        Assert.Equal(BrymenErrorCodes.TransmissionTimeout, ex.Code);
        Assert.Contains("checksum", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["66", "88", "88", "88"], transport.Commands);
    }

    [Fact]
    public async Task Model_check_refuses_a_meter_without_the_52h_model_id()
    {
        var meter = NewMeter();
        meter.FaultInjector = (command, _) => command == Bm52xCommands.RealTime ? SimulatedFault.CorruptModel : SimulatedFault.None;
        var transport = new ResponderTransport(meter);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(transport).ReadAsync());

        Assert.Equal(BrymenErrorCodes.NoDataLogger, ex.Code);
        Assert.Equal(["66"], transport.Commands);
    }

    [Fact]
    public async Task Model_check_retries_once_and_then_answers_E9017()
    {
        var meter = NewMeter();
        meter.IsPoweredOn = false;
        var transport = new ResponderTransport(meter);

        var ex = await Assert.ThrowsAsync<BrymenMeterException>(() => new LoggerMemoryReader(transport).ReadAsync());

        Assert.Equal(BrymenErrorCodes.NoDataLogger, ex.Code);
        Assert.Equal(["66", "66"], transport.Commands);
        Assert.Equal(2, transport.Resets);
    }

    [Fact]
    public async Task Model_check_can_be_disabled()
    {
        var transport = new ResponderTransport(NewMeter());

        await new LoggerMemoryReader(transport, new LoggerReadOptions { VerifyModel = false }).ReadAsync();

        Assert.Equal(Dump[1..], transport.Commands);
    }

    [Fact]
    public async Task A_reader_reads_only_once_and_validates_its_options()
    {
        var reader = new LoggerMemoryReader(new ResponderTransport(NewMeter()));
        await reader.ReadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerMemoryReader(new ResponderTransport(NewMeter()), new LoggerReadOptions { MaxBlockAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerMemoryReader(new ResponderTransport(NewMeter()), new LoggerReadOptions { HeadSettleDelay = TimeSpan.FromMilliseconds(-1) }));
    }

    [Fact]
    public void Image_exposes_the_blocks()
    {
        byte[] raw = SimulatedBm52xMeter.LoadDefaultMemoryImage();
        var header = new LoggerHeader(raw[0], raw[1] | raw[2] << 8 | raw[3] << 16, raw[4] | raw[5] << 8);
        var image = new LoggerMemoryImage(header, raw, retries: 2, restarts: 1, warnings: ["aviso"]);
        raw[0] ^= 0xFF;

        Assert.Equal(95, image.BlockCount);
        Assert.Equal(95, image.Blocks.Count);
        Assert.NotEqual(raw[0], image.Image.Span[0]); // cópia independente
        Assert.Equal(image.Image.Slice(24 * 94, 24).ToArray(), image.GetBlock(94).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetBlock(95));
        Assert.Throws<ArgumentException>(() => new LoggerMemoryImage(header, new byte[25]));
        Assert.Equal((2, 1), (image.Retries, image.Restarts));
        Assert.Equal(["aviso"], image.Warnings);
        Assert.Equal(375, Assert.Single(image.Decode().Sessions).Samples.Count);
    }
}
