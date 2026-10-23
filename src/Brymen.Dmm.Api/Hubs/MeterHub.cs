using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Meter;
using Microsoft.AspNetCore.SignalR;

namespace Brymen.Dmm.Api.Hubs;

/// <summary>
/// Hub SignalR do medidor em <c>/hubs/meter</c>, com o protocolo JSON e os tipos do <see cref="ApiJsonContext"/>.
/// É um hub não tipado (<see cref="Hub"/>, e não <c>Hub&lt;T&gt;</c>): hubs fortemente tipados não são compatíveis com
/// trimming/Native AOT. Os eventos servidor → cliente (<see cref="ReadingEvent"/>, <see cref="StatusEvent"/> e
/// <see cref="AcquisitionSampleEvent"/>) saem do <see cref="SignalRForwarder"/>; os métodos abaixo atendem o cliente.
/// </summary>
internal sealed class MeterHub(MeterSession session, ReadingBroadcaster broadcaster, MeterHubConnections connections) : Hub
{
    public const string Route = "/hubs/meter";

    /// <summary>Cada leitura nova, com o DTO de <c>GET /api/v1/readings/latest</c>.</summary>
    public const string ReadingEvent = "reading";

    /// <summary>Mudança do link, do cabo ou da família detectada (objeto <c>model</c>), com o DTO de <c>GET /api/v1/meter</c>.</summary>
    public const string StatusEvent = "status";

    /// <summary>Cada amostra gravada, com o DTO de <c>GET /api/v1/acquisition/samples</c>.</summary>
    public const string AcquisitionSampleEvent = "acquisitionSample";

    /// <summary>A leitura atual, como em <c>GET /api/v1/readings/latest</c>; null quando aquele endpoint responderia erro.</summary>
    public ReadingDto? GetLatest()
        => broadcaster.GetCurrent(session.GetSnapshot()) is { } live ? DtoMapper.ToDto(live) : null;

    /// <summary>Estado do link e do cabo, como em <c>GET /api/v1/meter</c>.</summary>
    public MeterStatusDto GetStatus() => DtoMapper.ToDto(session.GetSnapshot(), session, broadcaster);

    public override Task OnConnectedAsync()
    {
        connections.Connected();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Disconnected();
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Conexões abertas no <see cref="MeterHub"/> (diagnóstico: um cliente que cai não pode ficar registrado).</summary>
internal sealed class MeterHubConnections
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Connected() => Interlocked.Increment(ref _count);

    public void Disconnected() => Interlocked.Decrement(ref _count);
}
