using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// O medidor simulado com as capturas completas (<see cref="SimulatedBm52xMeter.FromCaptures"/>), a injeção de falhas e o
/// <c>8A</c> que avança como um <c>89</c> logo depois do <c>88</c> (testes que vieram do CaptureResponder da API).
/// </summary>
public sealed class SimulatedBm52xMeterFaultTests
{
    private static readonly byte[] Rtd = [0x00, 0x52, 0x66];
    private static readonly byte[] Head = [0x00, 0x52, 0x88];
    private static readonly byte[] Next = [0x00, 0x52, 0x89];
    private static readonly byte[] Current = [0x00, 0x52, 0x8A];

    [Fact]
    public void Real_time_cycles_through_all_captured_frames()
    {
        var meter = SimulatedBm52xMeter.FromCaptures();
        var frames = Enumerable.Range(0, 28).Select(_ => Join(meter.Respond(0, Rtd)!)).ToList();

        Assert.Equal(14, SimulatedBm52xMeter.CapturedRealTimeFrames.Count);
        Assert.All(frames, f => Assert.Equal(27, f.Length));
        Assert.Equal("00001000BFCBEFA80000BFAFAFAF00012006005252525211000000", Convert.ToHexString(frames[0]));
        Assert.Equal("00001000BFA06D6F0000BFAFAFAF00012006005252525210000000", Convert.ToHexString(frames[1]));
        Assert.Equal(SimulatedBm52xMeter.DefaultRealTimeFrames, SimulatedBm52xMeter.CapturedRealTimeFrames.Take(3));
        Assert.Equal(frames[0], frames[14]);
        Assert.Equal(frames[13], frames[27]);
    }

    [Fact]
    public void Memory_pointer_follows_88_89_8A_and_stops_after_the_last_block()
    {
        var meter = SimulatedBm52xMeter.FromCaptures();
        Assert.Equal(95, meter.MemoryBlockCount);

        var head = Join(meter.Respond(0, Head)!);
        Assert.Equal("0001E008000100EEA0001804A05A09A001000080CA0800000D0000009705000000000000", Convert.ToHexString(head));

        byte[] last = [];
        for (int i = 0; i < 94; i++)
            last = Join(meter.Respond(0, Next)!);
        Assert.Equal("00000000060000000000000600000000EEC000000000000000000000BA01000000000000", Convert.ToHexString(last));
        Assert.Equal(last, Join(meter.Respond(0, Current)!));
        Assert.Null(meter.Respond(0, Next));
        Assert.Equal(95 * 24, meter.MemoryPointer);

        Assert.Equal(head, Join(meter.Respond(0, Head)!));
        Assert.Null(meter.Respond(0, new byte[] { 0x00, 0x82, 0x88 }));
    }

    [Fact]
    public void Faults_are_injected_per_command_with_the_sequence_number()
    {
        var meter = SimulatedBm52xMeter.FromCaptures();
        var seen = new List<(byte Command, long Sequence)>();
        SimulatedFault next = SimulatedFault.None;
        meter.FaultInjector = (command, sequence) =>
        {
            seen.Add((command, sequence));
            return next;
        };
        meter.Respond(0, Head);

        next = SimulatedFault.Drop;
        Assert.Null(meter.Respond(0, Next));
        Assert.Equal(24, meter.MemoryPointer);

        next = SimulatedFault.DropAfterProcessing;
        Assert.Null(meter.Respond(0, Next));
        Assert.Equal(48, meter.MemoryPointer);

        next = SimulatedFault.CorruptChecksum;
        var corrupt = Join(meter.Respond(0, Current)!);
        Assert.NotEqual(corrupt[28] | corrupt[29] << 8, Sum(corrupt));

        next = SimulatedFault.Truncate;
        Assert.Equal(2, meter.Respond(0, Next)!.Count);

        next = SimulatedFault.CorruptModel;
        var rtd = Join(meter.Respond(0, Rtd)!);
        Assert.Equal("00000000", Convert.ToHexString(rtd, 19, 4));

        Assert.Equal([(0x88, 1L), (0x89, 2L), (0x89, 3L), (0x8A, 4L), (0x89, 5L), (0x66, 6L)], seen.Select(s => ((int)s.Command, s.Sequence)));
        Assert.Equal(6, meter.CommandCount);
    }

    [Fact]
    public void Current_right_after_head_advances_like_next_inside_the_window()
    {
        var time = new FakeTimeProvider();
        var meter = SimulatedBm52xMeter.FromCaptures(time);
        Assert.Equal(TimeSpan.FromMilliseconds(200), meter.CurrentAfterHeadWindow);
        var reference = new SimulatedBm52xMeter(memoryImage: SimulatedBm52xMeter.LoadDefaultMemoryImage());
        var head = Join(reference.Respond(0, Head)!);
        var block1 = Join(reference.Respond(0, Next)!);

        // 8A 15 ms depois do 88: o medidor real avança como um 89 e devolve o bloco 1.
        Assert.Equal(head, Join(meter.Respond(0, Head)!));
        time.Advance(TimeSpan.FromMilliseconds(15));
        Assert.Equal(block1, Join(meter.Respond(0, Current)!));
        Assert.Equal(48, meter.MemoryPointer);

        // Passada a janela, o 8A volta a reler o bloco atual (P - 24).
        Assert.Equal(head, Join(meter.Respond(0, Head)!));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(head, Join(meter.Respond(0, Current)!));
        Assert.Equal(24, meter.MemoryPointer);

        // 89 logo depois do 88 e 8A logo depois do 89 funcionam.
        Assert.Equal(head, Join(meter.Respond(0, Head)!));
        Assert.Equal(block1, Join(meter.Respond(0, Next)!));
        Assert.Equal(block1, Join(meter.Respond(0, Current)!));

        // Sem a janela (padrão do construtor), o 8A logo depois do 88 relê o cabeçalho.
        var plain = new SimulatedBm52xMeter(timeProvider: time);
        Assert.Equal(TimeSpan.Zero, plain.CurrentAfterHeadWindow);
        Assert.Equal(head, Join(plain.Respond(0, Head)!));
        Assert.Equal(head, Join(plain.Respond(0, Current)!));
    }

    [Fact]
    public async Task Simulated_device_latency_follows_its_time_provider()
    {
        var time = new FakeTimeProvider();
        var device = SimulatedHidDevice.CreateBm52x(SimulatedBm52xMeter.FromCaptures(time), timeProvider: time);
        device.ResponseLatency = TimeSpan.FromSeconds(2);
        var buffer = new byte[8];

        await device.WriteReportAsync(0, Rtd);
        var read = device.ReadReportAsync(buffer, TimeSpan.FromSeconds(5)).AsTask();
        await Task.Delay(50);
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(8, await read.WaitAsync(TimeSpan.FromSeconds(10)));
        // Os outros 2 relatórios da resposta já chegaram; o seguinte não vem, e o timeout segue o relógio falso.
        Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
        Assert.Equal(8, await device.ReadReportAsync(buffer, TimeSpan.Zero));
        var timeout = device.ReadReportAsync(buffer, TimeSpan.FromSeconds(1)).AsTask();
        await Task.Delay(50);
        Assert.False(timeout.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => timeout.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static int Sum(byte[] frame)
    {
        int sum = 0;
        for (int i = 0; i < 27; i++)
            sum += frame[i];
        return (ushort)sum;
    }

    private static byte[] Join(IReadOnlyList<byte[]> reports) => [.. reports.SelectMany(r => r.Prepend((byte)0x00))];
}
