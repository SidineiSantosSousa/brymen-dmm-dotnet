using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Polling × importação × link/unlink, assinantes SSE e descarte do host com uma importação em andamento.</summary>
public sealed class ConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
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
    public async Task Link_toggling_during_an_import_neither_breaks_it_nor_polls_in_between()
    {
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            if (command == Bm52xCommands.MemoryNext)
                Thread.Sleep(3);
            return SimulatedFault.None;
        };

        string id = await StartImportAsync();
        for (int i = 0; i < 8; i++)
        {
            var off = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
            Assert.True(off.GetProperty("importing").GetBoolean() || off.GetProperty("state").GetString() == "unlinked");
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        }
        var job = await WaitForJobAsync(id);
        factory.Responder.FaultInjector = null;

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        byte[] sequence;
        lock (commands)
            sequence = [.. commands];
        int head = Array.IndexOf(sequence, Bm52xCommands.MemoryHead);
        int lastNext = Array.LastIndexOf(sequence, Bm52xCommands.MemoryNext);
        Assert.DoesNotContain(Bm52xCommands.RealTime, sequence[head..(lastNext + 1)]);

        // Link ativo no fim: o polling volta sozinho.
        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.False(meter.GetProperty("importing").GetBoolean());
        long count = meter.GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > count);
    }

    [Fact]
    public async Task Import_with_the_link_disabled_closes_the_cable_and_does_not_resume_polling()
    {
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            return SimulatedFault.None;
        };

        var job = await WaitForJobAsync(await StartImportAsync());
        await Task.Delay(300);
        factory.Responder.FaultInjector = null;

        Assert.Equal("completed", job.GetProperty("state").GetString());
        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.Equal("unlinked", meter.GetProperty("state").GetString());
        Assert.False(meter.GetProperty("deviceOpen").GetBoolean());
        Assert.False(meter.GetProperty("importing").GetBoolean());
        lock (commands)
        {
            int lastNext = commands.LastIndexOf(Bm52xCommands.MemoryNext);
            Assert.DoesNotContain(Bm52xCommands.RealTime, commands.Skip(lastNext));
        }
    }

    [Fact]
    public async Task Latest_reading_and_manual_samples_are_refused_while_polling_is_paused()
    {
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "manual" });
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        factory.Responder.FaultInjector = (command, _) =>
        {
            if (command == Bm52xCommands.MemoryNext)
                Thread.Sleep(20);
            return SimulatedFault.None;
        };
        try
        {
            string id = await StartImportAsync();

            using (var latest = await _client.GetAsync("/api/v1/readings/latest"))
                await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.Conflict, "CONFLICT");
            using (var manual = await _client.PostAsync("/api/v1/acquisition/samples", null))
                await TestHelpers.AssertProblemAsync(manual, HttpStatusCode.Conflict, "CONFLICT");
            // A última leitura também some do broadcaster: um assinante novo do SSE não recebe dado velho.
            var broadcaster = factory.Services.GetRequiredService<ReadingBroadcaster>();
            await TestHelpers.EventuallyAsync(() => Task.FromResult(broadcaster.Latest is null ? "ok" : null), "a leitura descartada");

            using var cancel = await _client.DeleteAsync($"/api/v1/logger/imports/{id}");
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
            Assert.Equal("canceled", (await WaitForJobAsync(id)).GetProperty("state").GetString());
        }
        finally
        {
            factory.Responder.FaultInjector = null;
            await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed" });
        }
        Assert.Equal(0, (await _client.GetJsonAsync("/api/v1/acquisition")).GetProperty("sampleCount").GetInt32());
    }

    [Fact]
    public async Task Concurrent_partial_updates_keep_every_field_that_was_sent()
    {
        // Comparador: cada PUT muda um campo; nenhum pode restaurar o campo que o outro mudou.
        for (int i = 1; i <= 10; i++)
        {
            double high = 1000 + i, low = -i;
            await Task.WhenAll(
                _client.SendJsonAsync(HttpMethod.Put, "/api/v1/comparator", new { high }),
                _client.SendJsonAsync(HttpMethod.Put, "/api/v1/comparator", new { low }));
            var comparator = await _client.GetJsonAsync("/api/v1/comparator");
            Assert.Equal(high, comparator.GetProperty("high").GetDouble());
            Assert.Equal(low, comparator.GetProperty("low").GetDouble());
        }

        // Aquisição: o modo de um PUT e o intervalo do outro.
        try
        {
            for (int i = 2; i <= 11; i++)
            {
                await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed" });
                var results = await Task.WhenAll(
                    _client.PutAsJsonAsync("/api/v1/acquisition/settings", new { intervalSeconds = i }),
                    _client.PutAsJsonAsync("/api/v1/acquisition/settings", new { mode = "interval" }));
                foreach (var response in results)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    response.Dispose();
                }
                var settings = await _client.GetJsonAsync("/api/v1/acquisition/settings");
                Assert.Equal("interval", settings.GetProperty("mode").GetString());
                Assert.Equal(i, settings.GetProperty("intervalSeconds").GetInt32());
            }
        }
        finally
        {
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed", intervalSeconds = 1 });
        }
    }

    [Fact]
    public async Task Sse_subscriber_is_removed_when_the_client_disconnects()
    {
        var broadcaster = factory.Services.GetRequiredService<ReadingBroadcaster>();
        // O encaminhador do SignalR é um assinante permanente.
        const int baseline = 1;
        await TestHelpers.EventuallyAsync(() => Task.FromResult(broadcaster.SubscriberCount == baseline ? "ok" : null), "só o encaminhador do SignalR antes do teste");

        for (int i = 0; i < 3; i++)
        {
            using (var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout))
            using (var response = await _client.GetAsync("/api/v1/readings/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token))
            await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
            using (var reader = new StreamReader(stream))
            {
                while (await reader.ReadLineAsync(cts.Token) is { } line && !line.StartsWith("data:", StringComparison.Ordinal))
                {
                }
                Assert.Equal(baseline + 1, broadcaster.SubscriberCount);
            }
            await TestHelpers.EventuallyAsync(() => Task.FromResult(broadcaster.SubscriberCount == baseline ? "ok" : null), "o assinante removido após a desconexão");
        }
    }

    [Fact]
    public async Task Disposing_the_host_during_an_import_cancels_it_promptly()
    {
        var local = new ApiFactory();
        var client = local.CreateClient();
        await TestHelpers.WaitForReadingAsync(client);
        local.Responder.FaultInjector = (command, _) =>
        {
            if (command == Bm52xCommands.MemoryNext)
                Thread.Sleep(50);
            return SimulatedFault.None;
        };
        using (var post = await client.PostAsync("/api/v1/logger/imports", null))
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var watch = Stopwatch.StartNew();
        await local.DisposeAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Descarte levou {watch.Elapsed}.");
    }

    private async Task<string> StartImportAsync()
    {
        using var post = await _client.PostAsync("/api/v1/logger/imports", null);
        var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
        return created.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitForJobAsync(string id)
    {
        object job = await TestHelpers.EventuallyAsync(async () =>
        {
            var j = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}");
            return j.GetProperty("state").GetString() != "running" ? (object)j : null;
        }, $"o fim da importação {id}", TimeSpan.FromSeconds(60));
        return (JsonElement)job;
    }
}
