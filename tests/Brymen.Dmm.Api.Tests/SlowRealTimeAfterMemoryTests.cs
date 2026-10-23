using System.Net;
using System.Text.Json;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Brymen.Dmm.Api.Tests;

/// <summary>
/// Medidor simulado com o atraso do BM525s real: o 1º <c>66</c> depois de comandos de memória (e na partida, quando a
/// memória pode ter sido lida por outro programa) só começa a ser respondido depois de <see cref="SlowRealTimeMeter.Delay"/>,
/// maior que o <c>Meter:ReportTimeout</c> dos testes (250 ms). No medidor real são ≈ 2,06 s depois do último bloco.
/// </summary>
public sealed class SlowRealTimeFactory : ApiFactory
{
    internal SlowRealTimeMeter Meter { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IHidDeviceProvider>(sp => new SlowRealTimeMeter.Provider(
            MeterTransportFactory.CreateSimulated(sp.GetRequiredService<SimulatedBm52xMeter>(), TimeSpan.FromMilliseconds(2)), Meter)));
}

/// <summary>Estado do medidor (sobrevive às reaberturas do cabo) e o decorador do dispositivo.</summary>
internal sealed class SlowRealTimeMeter
{
    private readonly Lock _lock = new();
    private bool _afterMemory = true;
    private int _delayed;

    public TimeSpan Delay { get; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Quantos <c>66</c> foram atrasados.</summary>
    public int DelayedCount
    {
        get { lock (_lock) return _delayed; }
    }

    private bool OnCommand(byte command)
    {
        lock (_lock)
        {
            bool slow = command == Bm52xCommands.RealTime && _afterMemory;
            _afterMemory = command is Bm52xCommands.MemoryHead or Bm52xCommands.MemoryNext or Bm52xCommands.MemoryCurrent;
            if (slow)
                _delayed++;
            return slow;
        }
    }

    internal sealed class Provider(IHidDeviceProvider inner, SlowRealTimeMeter meter) : IHidDeviceProvider
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => inner.Enumerate(vendorId, productId);

        public IHidDevice Open(HidDeviceInfo device) => new Device(inner.Open(device), meter);
    }

    // O comando atrasado é entregue ao medidor simulado depois de Delay: a escrita volta na hora, como no cabo real, e a
    // espera conta no timeout do 1º relatório.
    private sealed class Device(IHidDevice inner, SlowRealTimeMeter meter) : IHidDevice
    {
        public HidDeviceInfo Info => inner.Info;

        public int InputReportLength => inner.InputReportLength;

        public int OutputReportLength => inner.OutputReportLength;

        public ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (!meter.OnCommand(data.Span[2]))
                return inner.WriteReportAsync(reportId, data, cancellationToken);
            byte[] copy = data.ToArray();
            _ = Task.Run(async () =>
            {
                await Task.Delay(meter.Delay);
                try
                {
                    await inner.WriteReportAsync(reportId, copy);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HidException)
                {
                    // O cabo foi fechado no meio: a resposta se perde, como no hardware.
                }
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.ReadReportAsync(buffer, timeout, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}

public sealed class SlowRealTimeAfterMemoryTests(SlowRealTimeFactory factory) : IClassFixture<SlowRealTimeFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task First_poll_after_startup_waits_for_the_slow_answer_without_W7012()
    {
        await TestHelpers.WaitForReadingAsync(_client);

        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.True(factory.Meter.DelayedCount >= 1);
        Assert.Equal(JsonValueKind.Null, meter.GetProperty("lastError").ValueKind);
        Assert.Equal("connected", meter.GetProperty("state").GetString());
    }

    [Fact]
    public async Task First_poll_after_an_import_waits_for_the_slow_answer_without_W7012()
    {
        await TestHelpers.WaitForReadingAsync(_client);
        int delayedBefore = factory.Meter.DelayedCount;
        var started = DateTimeOffset.UtcNow;

        using var post = await _client.PostAsync("/api/v1/logger/imports", null);
        var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
        string id = created.GetProperty("id").GetString()!;
        var job = await TestHelpers.EventuallyAsync(async () =>
        {
            var j = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}");
            return j.GetProperty("state").GetString() == "running" ? null : (object)j;
        }, "o fim da importação");
        Assert.Equal("completed", ((JsonElement)job).GetProperty("state").GetString());
        long lastSequence = (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("readingCount").GetInt64();

        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > lastSequence);

        Assert.Equal(delayedBefore + 1, factory.Meter.DelayedCount);
        var error = (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("lastError");
        Assert.True(error.ValueKind == JsonValueKind.Null || error.GetProperty("timestamp").GetDateTimeOffset() < started,
            $"Erro depois da importação: {error}");
    }
}
