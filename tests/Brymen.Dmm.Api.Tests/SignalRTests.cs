using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Hubs;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Hub <c>/hubs/meter</c>: eventos reading/status/acquisitionSample e métodos GetLatest/GetStatus, pelo TestServer.</summary>
public sealed class SignalRTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private ReadingBroadcaster Broadcaster => factory.Services.GetRequiredService<ReadingBroadcaster>();

    public async Task InitializeAsync()
    {
        factory.Responder.FaultInjector = null;
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        await TestHelpers.WaitForReadingAsync(_client);
    }

    public async Task DisposeAsync()
    {
        factory.Responder.FaultInjector = null;
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
    }

    [Theory]
    [InlineData(HttpTransportType.LongPolling)]
    [InlineData(HttpTransportType.WebSockets)]
    public async Task Every_published_reading_reaches_the_client_once_and_in_order(HttpTransportType transport)
    {
        // Assinante de controle do broadcaster, com folga para não descartar nada durante o teste.
        using var control = Broadcaster.Subscribe(capacity: 100_000);
        await using var hub = await HubClient.ConnectAsync(factory.Server, transport);

        var received = await HubClient.TakeAsync(hub.Readings, 40);
        await hub.Connection.StopAsync();

        var published = new List<LiveReading>();
        while (control.Reader.TryRead(out var live))
            published.Add(live);
        Assert.Equal(0, control.TakeDropped());

        // Uma vez cada e em ordem: números consecutivos, sem repetição nem buraco.
        long[] sequences = [.. received.Select(r => r.GetProperty("sequence").GetInt64())];
        Assert.Equal(Enumerable.Range(0, sequences.Length).Select(i => sequences[0] + i), sequences);

        // As mesmas leituras que o controle viu, com os mesmos instantes; o controle assinou antes do cliente.
        var bySequence = published.ToDictionary(p => p.Sequence);
        Assert.True(published[0].Sequence <= sequences[0], $"controle começou em {published[0].Sequence}, cliente em {sequences[0]}");
        Assert.Equal(published.Count(p => p.Sequence >= sequences[0] && p.Sequence <= sequences[^1]), received.Count);
        var http = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        foreach (var reading in received)
        {
            var live = bySequence[reading.GetProperty("sequence").GetInt64()];
            Assert.Equal(live.Reading.Timestamp, reading.GetProperty("timestamp").GetDateTimeOffset());
            // O payload é exatamente o JSON de GET /api/v1/readings/latest para essa leitura.
            var rest = JsonSerializer.SerializeToElement(DtoMapper.ToDto(live), http.GetTypeInfo(typeof(ReadingDto)));
            Assert.True(JsonElement.DeepEquals(rest, reading), $"payload diferente do REST: {reading}");
        }
    }

    [Fact]
    public async Task GetLatest_and_GetStatus_answer_like_the_REST_endpoints()
    {
        await using var hub = await HubClient.ConnectAsync(factory.Server);

        var latest = await hub.Connection.InvokeAsync<JsonElement>("GetLatest");
        var status = await hub.Connection.InvokeAsync<JsonElement>("GetStatus");

        var rest = await TestHelpers.WaitForReadingAsync(_client);
        Assert.Equal(JsonValueKind.Object, latest.ValueKind);
        Assert.True(latest.GetProperty("sequence").GetInt64() > 0);
        Assert.Equal(Names(rest), Names(latest));
        Assert.Equal(54, latest.GetProperty("rawHex").GetString()!.Length);
        Assert.Equal("V", latest.GetProperty("main").GetProperty("unit").GetString());

        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.Equal(Names(meter), Names(status));
        Assert.True(status.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal("connected", status.GetProperty("state").GetString());
        Assert.Equal("simulated", status.GetProperty("transport").GetString());
        Assert.Equal("0x0820", status.GetProperty("device").GetProperty("vendorId").GetString());
    }

    [Fact]
    public async Task Status_events_follow_unlink_and_link()
    {
        await using var hub = await HubClient.ConnectAsync(factory.Server);
        HubClient.Drain(hub.Statuses);

        var off = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        var unlinked = (await HubClient.TakeAsync(hub.Statuses, 1, s => s.GetProperty("state").GetString() == "unlinked"))[0];
        Assert.False(unlinked.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal(Names(off), Names(unlinked));
        Assert.Equal(JsonValueKind.Null, (await hub.Connection.InvokeAsync<JsonElement>("GetLatest")).ValueKind);
        Assert.Equal("unlinked", (await hub.Connection.InvokeAsync<JsonElement>("GetStatus")).GetProperty("state").GetString());

        // Sem link não há leitura nova.
        HubClient.Drain(hub.Readings);
        await Task.Delay(200);
        Assert.False(hub.Readings.TryRead(out _));

        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        var linked = (await HubClient.TakeAsync(hub.Statuses, 1, s => s.GetProperty("state").GetString() == "connected"))[0];
        Assert.True(linked.GetProperty("linkEnabled").GetBoolean());
        Assert.True(linked.GetProperty("deviceOpen").GetBoolean());
        await HubClient.TakeAsync(hub.Readings, 3);
    }

    [Fact]
    public async Task Status_event_carries_the_error_code()
    {
        await using var hub = await HubClient.ConnectAsync(factory.Server);
        HubClient.Drain(hub.Statuses);
        bool silent = true;
        factory.Responder.FaultInjector = (command, _) =>
            Volatile.Read(ref silent) && command == Protocol.Bm52xCommands.RealTime ? Hid.SimulatedFault.Drop : Hid.SimulatedFault.None;
        try
        {
            var error = (await HubClient.TakeAsync(hub.Statuses, 1, s =>
                s.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "E9016"))[0];
            Assert.Equal("reconnecting", error.GetProperty("state").GetString());
        }
        finally
        {
            Volatile.Write(ref silent, false);
        }
        await HubClient.TakeAsync(hub.Statuses, 1, s => s.GetProperty("state").GetString() == "connected");
    }

    [Fact]
    public async Task Status_events_show_the_cable_closed_and_reopened_after_a_failure()
    {
        await using var hub = await HubClient.ConnectAsync(factory.Server);
        HubClient.Drain(hub.Statuses);
        int dropped = 0;
        factory.Responder.FaultInjector = (command, _) =>
            command == Protocol.Bm52xCommands.RealTime && Interlocked.Increment(ref dropped) == 1 ? Hid.SimulatedFault.Drop : Hid.SimulatedFault.None;

        // 1ª falha: W7012, o cabo é fechado e reaberto na consulta seguinte, sem sair de "connected".
        var closed = (await HubClient.TakeAsync(hub.Statuses, 1, s => !s.GetProperty("deviceOpen").GetBoolean()))[0];
        Assert.Equal("W7012", closed.GetProperty("lastError").GetProperty("code").GetString());
        var reopened = (await HubClient.TakeAsync(hub.Statuses, 1, s => s.GetProperty("deviceOpen").GetBoolean()))[0];
        Assert.Equal("connected", reopened.GetProperty("state").GetString());
        await HubClient.TakeAsync(hub.Readings, 2);
    }

    [Fact]
    public async Task Acquisition_samples_are_sent_while_recording_in_full_speed()
    {
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        await _client.SendJsonAsync(HttpMethod.Delete, "/api/v1/acquisition/samples", expected: HttpStatusCode.NoContent);
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed" });
        await using var hub = await HubClient.ConnectAsync(factory.Server);

        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        var samples = await HubClient.TakeAsync(hub.Samples, 8);
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");

        int[] items = [.. samples.Select(s => s.GetProperty("item").GetInt32())];
        Assert.Equal(Enumerable.Range(1, items.Length), items);
        var page = await _client.GetJsonAsync("/api/v1/acquisition/samples?offset=0&limit=8");
        var stored = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(8, stored.Count);
        for (int i = 0; i < 8; i++)
            Assert.True(JsonElement.DeepEquals(stored[i], samples[i]), $"amostra {i + 1} diferente do REST: {samples[i]}");

        // Parado, nada mais chega.
        await Task.Delay(200);
        HubClient.Drain(hub.Samples);
        await Task.Delay(200);
        Assert.False(hub.Samples.TryRead(out _));
        await _client.SendJsonAsync(HttpMethod.Delete, "/api/v1/acquisition/samples", expected: HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Simultaneous_clients_receive_the_same_readings()
    {
        await using var a = await HubClient.ConnectAsync(factory.Server);
        await using var b = await HubClient.ConnectAsync(factory.Server);
        await using var c = await HubClient.ConnectAsync(factory.Server, HttpTransportType.WebSockets);

        // Depois que os três estão conectados, a próxima leitura publicada é o ponto de partida comum.
        long start = Broadcaster.Count + 1;
        var lists = await Task.WhenAll(
            HubClient.TakeAsync(a.Readings, 15, r => r.GetProperty("sequence").GetInt64() >= start),
            HubClient.TakeAsync(b.Readings, 15, r => r.GetProperty("sequence").GetInt64() >= start),
            HubClient.TakeAsync(c.Readings, 15, r => r.GetProperty("sequence").GetInt64() >= start));

        long[] expected = [.. Enumerable.Range(0, 15).Select(i => start + i)];
        foreach (var list in lists)
        {
            Assert.Equal(expected, list.Select(r => r.GetProperty("sequence").GetInt64()));
            for (int i = 0; i < list.Count; i++)
                Assert.True(JsonElement.DeepEquals(lists[0][i], list[i]));
        }
    }

    [Fact]
    public async Task Disconnected_clients_do_not_leak()
    {
        var connections = factory.Services.GetRequiredService<MeterHubConnections>();
        int baseline = connections.Count;
        int subscribers = Broadcaster.SubscriberCount;

        var clients = new List<HubClient>();
        for (int i = 0; i < 5; i++)
            clients.Add(await HubClient.ConnectAsync(factory.Server, i % 2 == 0 ? HttpTransportType.LongPolling : HttpTransportType.WebSockets));
        await TestHelpers.EventuallyAsync(() => Task.FromResult(connections.Count == baseline + 5 ? "ok" : null), "5 conexões abertas");
        await Task.WhenAll(clients.Select(c => HubClient.TakeAsync(c.Readings, 2)));
        // Os clientes não assinam o broadcaster: o encaminhador é o único assinante, qualquer que seja o número de conexões.
        Assert.Equal(subscribers, Broadcaster.SubscriberCount);

        await clients[0].Connection.StopAsync();
        await clients[1].DisposeAsync();
        await TestHelpers.EventuallyAsync(() => Task.FromResult(connections.Count == baseline + 3 ? "ok" : null), "2 conexões fechadas");
        foreach (var client in clients.Skip(2))
            await client.DisposeAsync();
        await TestHelpers.EventuallyAsync(() => Task.FromResult(connections.Count == baseline ? "ok" : null), "todas as conexões fechadas");
        Assert.Equal(subscribers, Broadcaster.SubscriberCount);

        // O encaminhamento segue normal para quem chega depois.
        await using var late = await HubClient.ConnectAsync(factory.Server);
        await HubClient.TakeAsync(late.Readings, 3);
        Assert.Equal(baseline + 1, connections.Count);
    }

    [Fact]
    public void Hub_protocol_resolves_types_only_from_the_generated_context()
    {
        var options = factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        Assert.Same(ApiJsonContext.Default, options.TypeInfoResolverChain[0]);
        Assert.NotNull(options.GetTypeInfo(typeof(ReadingDto)));
        Assert.NotNull(options.GetTypeInfo(typeof(MeterStatusDto)));
        Assert.NotNull(options.GetTypeInfo(typeof(AcquisitionSampleDto)));
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
}

/// <summary>Configuração do SignalR e CORS para clientes browser de outra origem.</summary>
public sealed class CorsFactory : ApiFactory
{
    public const string AllowedOrigin = "http://painel.test:8080";

    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        .. base.Settings,
        new("Cors:AllowedOrigins:0", AllowedOrigin + "/"),
        new("Cors:AllowedOrigins:1", "https://outro.test"),
        new("SignalR:EnableDetailedErrors", "true"),
        new("SignalR:KeepAliveInterval", "00:00:05"),
    ];
}

public sealed class SignalRConfigurationTests(CorsFactory factory) : IClassFixture<CorsFactory>
{
    [Fact]
    public void SignalR_section_configures_the_hub()
    {
        // As opções globais valem para o MeterHub: as de HubOptions<MeterHub> não são alteradas.
        var options = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;

        Assert.True(options.EnableDetailedErrors);
        Assert.Equal(TimeSpan.FromSeconds(5), options.KeepAliveInterval);
    }

    [Fact]
    public async Task Preflight_of_the_negotiate_allows_the_configured_origin_with_credentials()
    {
        var client = factory.CreateClient();

        using var allowed = await client.SendAsync(Preflight(CorsFactory.AllowedOrigin));
        using var denied = await client.SendAsync(Preflight("http://intruso.test"));

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(CorsFactory.AllowedOrigin, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        // Requisição simples da REST também recebe os cabeçalhos.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meter");
        request.Headers.Add("Origin", "https://outro.test");
        using var response = await client.SendAsync(request);
        Assert.Equal("https://outro.test", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Browser_client_of_another_origin_connects()
    {
        // O cliente .NET manda Origin quando configurado; o servidor responde com a política de CORS.
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/meter"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Origin"] = CorsFactory.AllowedOrigin;
            })
            .Build();
        await using (connection)
        {
            await connection.StartAsync();
            var status = await connection.InvokeAsync<JsonElement>("GetStatus");
            Assert.Equal("simulated", status.GetProperty("transport").GetString());
        }
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/hubs/meter/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-requested-with,x-signalr-user-agent");
        return request;
    }
}

public sealed class CorsDisabledTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Without_allowed_origins_no_cors_header_is_sent()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meter");
        request.Headers.Add("Origin", "http://painel.test");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var hub = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.False(hub.EnableDetailedErrors);
        Assert.Equal(TimeSpan.FromSeconds(15), hub.KeepAliveInterval);
    }
}

/// <summary>Encerramento: SSE e SignalR terminam no <c>ApplicationStopping</c>, sem esperar o ShutdownTimeout.</summary>
public sealed class StreamShutdownTests
{
    [Fact]
    public async Task ApplicationStopping_ends_sse_streams_signalr_connections_and_the_forwarder()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await TestHelpers.WaitForReadingAsync(client);
        await using var hub = await HubClient.ConnectAsync(factory.Server);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        using var response = await client.GetAsync("/api/v1/readings/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cts.Token) is { } line && !line.StartsWith("data:", StringComparison.Ordinal))
        {
        }

        var broadcaster = factory.Services.GetRequiredService<ReadingBroadcaster>();
        var watch = Stopwatch.StartNew();
        // No WebApplicationFactory, o StopApplication também para e descarta o host.
        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        // O SSE termina normalmente (fim do corpo), sem esperar o desligamento do Kestrel.
        while (await reader.ReadLineAsync(cts.Token) is not null)
        {
        }
        await closed.Task.WaitAsync(cts.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Encerramento levou {watch.Elapsed}.");
        Assert.Equal(0, broadcaster.SubscriberCount);
    }
}
