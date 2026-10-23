using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace Brymen.Dmm.Api.Tests.Infrastructure;

/// <summary>
/// Cliente SignalR do <c>/hubs/meter</c> ligado ao <see cref="TestServer"/> (LongPolling pelo
/// <c>CreateHandler</c> ou WebSockets pelo <c>CreateWebSocketClient</c>), que guarda os eventos recebidos em canais.
/// </summary>
internal sealed class HubClient : IAsyncDisposable
{
    private readonly Channel<JsonElement> _readings = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _statuses = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _samples = Channel.CreateUnbounded<JsonElement>();

    private HubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<JsonElement>("reading", e => _readings.Writer.TryWrite(e));
        connection.On<JsonElement>("status", e => _statuses.Writer.TryWrite(e));
        connection.On<JsonElement>("acquisitionSample", e => _samples.Writer.TryWrite(e));
    }

    public HubConnection Connection { get; }

    public ChannelReader<JsonElement> Readings => _readings.Reader;

    public ChannelReader<JsonElement> Statuses => _statuses.Reader;

    public ChannelReader<JsonElement> Samples => _samples.Reader;

    public static async Task<HubClient> ConnectAsync(TestServer server, HttpTransportType transport = HttpTransportType.LongPolling)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/meter"), options =>
            {
                options.Transports = transport;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (transport == HttpTransportType.WebSockets)
                {
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = (context, cancellationToken) =>
                        new ValueTask<System.Net.WebSockets.WebSocket>(server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken));
                }
            })
            .Build();
        var client = new HubClient(connection);
        using var cts = new CancellationTokenSource(TestHelpers.DefaultTimeout);
        await connection.StartAsync(cts.Token);
        // O servidor responde ao handshake antes de pôr a conexão em Clients.All; uma invocação respondida garante que
        // ela já está lá e recebe todo evento publicado daqui em diante.
        await connection.InvokeAsync<JsonElement>("GetStatus", cts.Token);
        return client;
    }

    /// <summary>Lê eventos do canal até <paramref name="count"/> deles satisfazerem <paramref name="predicate"/> (os outros são descartados).</summary>
    public static async Task<List<JsonElement>> TakeAsync(ChannelReader<JsonElement> reader, int count, Func<JsonElement, bool>? predicate = null, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TestHelpers.DefaultTimeout);
        var items = new List<JsonElement>(count);
        try
        {
            while (items.Count < count)
            {
                var item = await reader.ReadAsync(cts.Token);
                if (predicate is null || predicate(item))
                    items.Add(item);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Tempo esgotado: {items.Count} de {count} eventos recebidos.");
        }
        return items;
    }

    /// <summary>Descarta os eventos já recebidos.</summary>
    public static void Drain(ChannelReader<JsonElement> reader)
    {
        while (reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
