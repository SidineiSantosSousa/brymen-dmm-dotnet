using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>O BM82x simulado (Minipa ET-2517A): só o <c>82 66</c>, sem memória, e o descarte de pedidos medido (§8).</summary>
public class SimulatedBm82xMeterTests
{
    private static readonly byte[] Rtd82 = [0x00, 0x82, 0x66];
    private static readonly byte[] Rtd52 = [0x00, 0x52, 0x66];

    private static byte[] Join(IReadOnlyList<byte[]> reports) => [.. reports.SelectMany(r => r.Prepend((byte)0x00))];

    [Fact]
    public void The_BM82x_answers_only_82_66_and_has_no_memory()
    {
        var meter = SimulatedBm52xMeter.CreateBm82x();

        Assert.Equal(0x82, meter.FamilyCode);
        Assert.False(meter.HasDataLogger);
        Assert.Null(meter.Respond(0, Rtd52));
        Assert.Null(meter.Respond(0, new byte[] { 0x00, 0x52, 0x88 }));
        Assert.Equal(SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames[0], Join(meter.Respond(0, Rtd82)!));
        Assert.Null(meter.Respond(0, new byte[] { 0x00, 0x82, 0x88 }));
        Assert.Null(meter.Respond(0, new byte[] { 0x00, 0x82, 0x89 }));
        Assert.Null(meter.Respond(0, new byte[] { 0x00, 0x82, 0x8A }));
        Assert.Null(meter.Respond(1, Rtd82));

        // Só os comandos da família 82h contam.
        Assert.Equal(4, meter.CommandCount);
        Assert.Equal(0, meter.MemoryBlockCount);
        Assert.Equal(0, meter.DroppedRequestCount);
        Assert.Throws<InvalidOperationException>(() => meter.SetMemoryImage([1, 2, 3]));
    }

    [Fact]
    public void The_BM525s_answers_82_66_with_the_same_frames_as_52_66()
    {
        var meter = SimulatedBm52xMeter.FromCaptures();
        var frames = SimulatedBm52xMeter.CapturedRealTimeFrames;

        Assert.Equal(0x52, meter.FamilyCode);
        Assert.True(meter.HasDataLogger);
        Assert.Equal(frames[0], Join(meter.Respond(0, Rtd82)!));
        Assert.Equal(frames[1], Join(meter.Respond(0, Rtd52)!));
        Assert.Equal(frames[2], Join(meter.Respond(0, Rtd82)!));
        Assert.Equal(3, meter.CommandCount);
    }

    [Fact]
    public void The_captured_ET2517A_frames_are_the_43_frames_of_the_sweep()
    {
        var frames = SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames;

        Assert.Equal(43, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.Equal(27, f.Length);
            Assert.Equal(new byte[] { 0x82, 0x82, 0x82, 0x82 }, f[19..23]);
            Assert.Equal(0, f[0] | f[9] | f[18]);
        });
        Assert.Equal("00001000BFAFA0E90000BFAFAFAF00012006008282828210000000", Convert.ToHexString(frames[0]));
        Assert.Equal("00001000AFBFAFEF0000BFAFAFAF00012006008282828200000000", Convert.ToHexString(frames[^1]));
        Assert.Equal(30, frames.Select(Convert.ToHexString).Distinct().Count());
    }

    [Fact]
    public async Task The_frames_rotate_in_the_order_of_the_sweep_through_the_link()
    {
        var meter = SimulatedBm52xMeter.CreateBm82x();
        using var link = new Bu86xLink(SimulatedHidDevice.CreateBm52x(meter), new Bu86xLinkOptions { ReportTimeout = TimeSpan.FromMilliseconds(100) });
        var frames = SimulatedBm52xMeter.CapturedEt2517aRealTimeFrames;

        for (int i = 0; i < frames.Count + 2; i++)
            Assert.Equal(frames[i % frames.Count], await link.ExecuteAsync(0x82, 0x66, 3));
        await Assert.ThrowsAsync<Bu86xTimeoutException>(() => link.ExecuteAsync(0x66, 3));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(50, 0.025)]
    [InlineData(100, 0.05)]
    [InlineData(150, 0.1625)]
    [InlineData(200, 0.275)]
    [InlineData(300, 0.275)]
    [InlineData(400, 0.375)]
    [InlineData(500, 0.475)]
    [InlineData(1000, 0.6)]
    [InlineData(5000, 0.6)]
    public void The_drop_probability_follows_the_measured_curve(int milliseconds, double probability)
        => Assert.Equal(probability, SimulatedBm52xMeter.Et2517aDropProbability(TimeSpan.FromMilliseconds(milliseconds)), 9);

    [Fact]
    public void The_drop_probability_never_decreases_with_the_idle_time()
    {
        var values = Enumerable.Range(0, 300).Select(i => SimulatedBm52xMeter.Et2517aDropProbability(TimeSpan.FromMilliseconds(i * 10))).ToList();

        Assert.All(values.Zip(values.Skip(1)), p => Assert.True(p.Second >= p.First));
        Assert.Equal(0, SimulatedBm52xMeter.Et2517aDropProbability(TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void The_dropper_is_deterministic_and_caps_the_consecutive_drops()
    {
        var idle = TimeSpan.FromMilliseconds(500);
        static bool[] Draw(Func<TimeSpan, int, bool> dropper, TimeSpan idle) => [.. Enumerable.Range(0, 200).Select(_ => dropper(idle, 0))];

        var first = Draw(SimulatedBm52xMeter.CreateEt2517aRequestDropper(seed: 7), idle);
        var again = Draw(SimulatedBm52xMeter.CreateEt2517aRequestDropper(seed: 7), idle);
        var other = Draw(SimulatedBm52xMeter.CreateEt2517aRequestDropper(seed: 8), idle);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.InRange(first.Count(d => d), 60, 130); // 47,5 % de 200

        var dropper = SimulatedBm52xMeter.CreateEt2517aRequestDropper();
        Assert.All(Enumerable.Range(0, 100), _ => Assert.False(dropper(TimeSpan.FromSeconds(5), 3)));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.False(dropper(TimeSpan.Zero, 0)));
        var never = SimulatedBm52xMeter.CreateEt2517aRequestDropper(maxConsecutiveDrops: 0);
        Assert.All(Enumerable.Range(0, 100), _ => Assert.False(never(TimeSpan.FromSeconds(5), 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedBm52xMeter.CreateEt2517aRequestDropper(maxConsecutiveDrops: -1));
    }

    [Fact]
    public void The_simulated_ET2517A_loses_more_requests_the_longer_it_is_idle()
    {
        // Como o rate.cs (§8.5.1): 400 consultas por intervalo; um pedido perdido é reenviado na hora, até ser atendido.
        var rates = new Dictionary<int, double>();
        foreach (int interval in new[] { 0, 100, 200, 500, 1000 })
        {
            var time = new FakeTimeProvider();
            var meter = SimulatedBm52xMeter.FromEt2517aCaptures(time);
            int lost = 0;
            for (int i = 0; i < 400; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(interval));
                if (meter.Respond(0, Rtd82) is not null)
                    continue;
                lost++;
                int resends = 0;
                while (meter.Respond(0, Rtd82) is null)
                    Assert.True(++resends < 3, "mais de 3 perdas seguidas");
            }
            Assert.True(meter.DroppedRequestCount >= lost);
            rates[interval] = lost / 400.0;
        }

        Assert.Equal(0, rates[0]);
        Assert.InRange(rates[100], 0.01, 0.10);   // medido: 2/40
        Assert.InRange(rates[200], 0.18, 0.37);   // medido: 11/40
        Assert.InRange(rates[500], 0.37, 0.58);   // medido: 19/40
        Assert.InRange(rates[1000], 0.48, 0.72);  // medido: 24/40 na 1ª tentativa (rate2.cs)
        Assert.True(rates[0] < rates[100] && rates[100] < rates[200] && rates[200] < rates[500]);
    }

    [Fact]
    public void The_dropper_sees_the_time_since_the_last_answer_and_the_drops_since_then()
    {
        var time = new FakeTimeProvider();
        var meter = SimulatedBm52xMeter.FromCaptures(time);
        var seen = new List<(TimeSpan Idle, int Drops)>();
        // Injetável também no BM52x: os 2 primeiros pedidos de cada resposta se perdem.
        meter.RequestDropper = (idle, drops) =>
        {
            seen.Add((idle, drops));
            return drops < 2;
        };

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(meter.Respond(0, Rtd52));
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Null(meter.Respond(0, Rtd82));
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(meter.Respond(0, Rtd52));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Null(meter.Respond(0, Rtd52));

        Assert.Equal(
            [(TimeSpan.FromMilliseconds(100), 0), (TimeSpan.FromMilliseconds(400), 1), (TimeSpan.FromMilliseconds(700), 2), (TimeSpan.FromMilliseconds(200), 0)],
            seen);
        Assert.Equal(3, meter.DroppedRequestCount);
        Assert.Equal(4, meter.CommandCount);

        // Medidor desligado e falha injetada: nenhum sorteio, e nada conta como descarte do modelo.
        meter.IsPoweredOn = false;
        Assert.Null(meter.Respond(0, Rtd52));
        meter.IsPoweredOn = true;
        meter.FaultInjector = (_, _) => SimulatedFault.Drop;
        Assert.Null(meter.Respond(0, Rtd52));
        Assert.Equal(4, seen.Count);
        Assert.Equal(3, meter.DroppedRequestCount);
    }
}
