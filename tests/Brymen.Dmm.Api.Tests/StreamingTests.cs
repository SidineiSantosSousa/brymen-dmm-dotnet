using System.Diagnostics;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Medidor lento para responder (ReportTimeout de 5 s) e heartbeat do SSE a cada 100 ms.</summary>
public sealed class SlowMeterFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        .. base.Settings.Where(s => s.Key != "Meter:ReportTimeout"),
        new("Meter:ReportTimeout", "00:00:05"),
        new("Sse:HeartbeatInterval", "00:00:00.100"),
    ];
}

/// <summary>Heartbeat do SSE e unlink que cancela a consulta em andamento.</summary>
public sealed class StreamingTests(SlowMeterFactory factory) : IClassFixture<SlowMeterFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Responder.FaultInjector = null;
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        await TestHelpers.WaitForReadingAsync(_client);
    }

    public async Task DisposeAsync()
    {
        factory.Responder.FaultInjector = null;
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
    }

    [Fact]
    public async Task Sse_sends_heartbeats_even_without_readings()
    {
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });

        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var response = await _client.GetAsync("/api/v1/readings/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (lines.Count(l => l == "event: heartbeat") < 3 && await reader.ReadLineAsync(cts.Token) is { } line)
            lines.Add(line);

        // Cada heartbeat é um evento com o campo data vazio, que o EventSource do browser não despacha.
        int first = lines.IndexOf("event: heartbeat");
        Assert.Equal("data: ", lines[first + 1]);
        Assert.Equal("", lines[first + 2]);
        Assert.DoesNotContain("event: reading", lines);
    }

    [Fact]
    public async Task Sse_helper_ignores_heartbeats_between_readings()
    {
        var events = await TestHelpers.ReadEventsAsync(_client, "/api/v1/readings/stream", e => e.Count >= 8);

        Assert.All(events, e => Assert.Equal("reading", e.Event));
    }

    [Fact]
    public async Task Unlink_cancels_the_poll_in_progress_instead_of_waiting_for_the_timeout()
    {
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Responder.FaultInjector = (command, _) =>
        {
            if (command != Bm52xCommands.RealTime)
                return SimulatedFault.None;
            dropped.TrySetResult();
            return SimulatedFault.Drop;
        };
        await dropped.Task.WaitAsync(TestHelpers.DefaultTimeout);
        // A consulta agora espera o 1º relatório por até 5 s.
        await Task.Delay(100);

        var watch = Stopwatch.StartNew();
        var meter = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"O unlink levou {watch.Elapsed}.");
        Assert.Equal("unlinked", meter.GetProperty("state").GetString());
        Assert.False(meter.GetProperty("deviceOpen").GetBoolean());
        // O cancelamento não é falha do medidor.
        var status = await _client.GetJsonAsync("/api/v1/meter");
        Assert.NotEqual("INTERNAL", status.GetProperty("lastError") is { ValueKind: System.Text.Json.JsonValueKind.Object } e ? e.GetProperty("code").GetString() : null);

        factory.Responder.FaultInjector = null;
        var linked = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        Assert.Equal("connected", linked.GetProperty("state").GetString());
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > linked.GetProperty("readingCount").GetInt64());
    }
}
