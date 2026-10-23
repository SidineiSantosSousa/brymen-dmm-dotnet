using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Meter;

/// <summary>Leitura publicada pelo polling, já com o resultado do comparador.</summary>
/// <param name="MonotonicTimestamp">
/// <see cref="TimeProvider.GetTimestamp"/> do instante da leitura. O tempo decorrido das amostras sai daqui, não do
/// relógio de parede: um Raspberry Pi sem RTC acerta o relógio pelo NTP depois do boot, com saltos de horas ou meses.
/// </param>
internal sealed record LiveReading(long Sequence, DmmReading Reading, ComparatorResult? Comparison, long MonotonicTimestamp);

/// <summary>
/// Guarda a última leitura e a distribui aos assinantes (SSE, SignalR) por canais limitados que descartam a mais
/// antiga, sem nunca bloquear o polling. No encerramento do serviço (<see cref="Complete"/>), os fluxos terminam.
/// </summary>
internal sealed class ReadingBroadcaster
{
    public const int SubscriberCapacity = 64;

    private readonly Lock _lock = new();
    private readonly SubscriberList<LiveReading> _subscribers = new();
    private LiveReading? _latest;
    private long _sequence;

    public LiveReading? Latest
    {
        get { lock (_lock) return _latest; }
    }

    /// <summary>Total de leituras publicadas desde o início do serviço.</summary>
    public long Count
    {
        get { lock (_lock) return _sequence; }
    }

    public int SubscriberCount => _subscribers.Count;

    public LiveReading Publish(DmmReading reading, ComparatorResult? comparison, long monotonicTimestamp)
    {
        // A entrega fica sob o lock: o número sequencial e a ordem nos canais andam juntos, mesmo com publicações
        // concorrentes. TryWrite em canal que descarta o mais antigo não bloqueia.
        lock (_lock)
        {
            var live = new LiveReading(++_sequence, reading, comparison, monotonicTimestamp);
            _latest = live;
            _subscribers.Publish(live);
            return live;
        }
    }

    /// <summary>
    /// A leitura atual: link ativo, sem importação, cabo conectado e alguma leitura publicada; senão null. É a regra de
    /// <c>GET /api/v1/readings/latest</c> e do método <c>GetLatest</c> do hub.
    /// </summary>
    public LiveReading? GetCurrent(MeterSnapshot meter)
        => meter is { LinkEnabled: true, Importing: false, State: MeterConnectionState.Connected } ? Latest : null;

    /// <summary>Esquece a última leitura (link desativado ou cabo perdido), para não servir dado velho.</summary>
    public void ClearLatest()
    {
        lock (_lock)
            _latest = null;
    }

    /// <summary>
    /// Assina as próximas leituras (sem a última conhecida). A assinatura vale a partir do retorno: nenhuma leitura
    /// publicada depois dele se perde sem aparecer em <see cref="Subscription{T}.TakeDropped"/>.
    /// </summary>
    public Subscription<LiveReading> Subscribe(int capacity = SubscriberCapacity)
    {
        lock (_lock)
            return _subscribers.Subscribe(capacity);
    }

    /// <summary>Assina e devolve também a última leitura, lida atomicamente com a assinatura (null se não houver).</summary>
    public Subscription<LiveReading> Subscribe(out LiveReading? latest, int capacity = SubscriberCapacity)
    {
        lock (_lock)
        {
            latest = _latest;
            return _subscribers.Subscribe(capacity);
        }
    }

    /// <summary>Encerra todos os fluxos e recusa novas assinaturas (<c>ApplicationStopping</c>).</summary>
    public void Complete() => _subscribers.Complete();
}
