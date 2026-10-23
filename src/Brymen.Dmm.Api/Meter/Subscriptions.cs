using System.Threading.Channels;

namespace Brymen.Dmm.Api.Meter;

/// <summary>
/// Assinatura de um fluxo publicado (leituras, amostras): canal limitado que descarta o item mais antigo quando o
/// leitor atrasa, para nunca bloquear quem publica, e conta os descartes para que o leitor os registre em log.
/// O descarte remove a assinatura e completa o canal.
/// </summary>
internal sealed class Subscription<T> : IDisposable
{
    private readonly Channel<T> _channel;
    private readonly Action<Subscription<T>> _unsubscribe;
    private long _dropped;
    private int _disposed;

    internal Subscription(int capacity, Action<Subscription<T>> unsubscribe)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _unsubscribe = unsubscribe;
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Itens publicados depois da assinatura, em ordem; completa no descarte ou no encerramento do serviço.</summary>
    public ChannelReader<T> Reader => _channel.Reader;

    /// <summary>Itens descartados por falta de espaço desde a última chamada (e zera a contagem).</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    internal void Write(T item) => _channel.Writer.TryWrite(item);

    internal void Complete() => _channel.Writer.TryComplete();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _unsubscribe(this);
        Complete();
    }
}

/// <summary>
/// Lista de assinantes de um fluxo. <see cref="Publish"/> só faz <c>TryWrite</c> em canais que descartam o mais
/// antigo, então nunca bloqueia. Depois de <see cref="Complete"/> (encerramento do serviço), as assinaturas
/// existentes terminam e as novas já nascem completas.
/// </summary>
internal sealed class SubscriberList<T>
{
    private readonly Lock _lock = new();
    private Subscription<T>[] _subscribers = [];
    private bool _completed;

    public int Count
    {
        get { lock (_lock) return _subscribers.Length; }
    }

    public Subscription<T> Subscribe(int capacity)
    {
        var subscription = new Subscription<T>(capacity, Remove);
        lock (_lock)
        {
            if (!_completed)
            {
                _subscribers = [.. _subscribers, subscription];
                return subscription;
            }
        }
        subscription.Complete();
        return subscription;
    }

    /// <summary>Entrega o item a todos os assinantes; quem publica de várias threads deve serializar as chamadas para manter a ordem.</summary>
    public void Publish(T item)
    {
        Subscription<T>[] targets;
        lock (_lock)
            targets = _subscribers;
        foreach (var subscription in targets)
            subscription.Write(item);
    }

    /// <summary>Completa todas as assinaturas e recusa as próximas (chamado no <c>ApplicationStopping</c>).</summary>
    public void Complete()
    {
        Subscription<T>[] targets;
        lock (_lock)
        {
            _completed = true;
            targets = _subscribers;
            _subscribers = [];
        }
        foreach (var subscription in targets)
            subscription.Complete();
    }

    private void Remove(Subscription<T> subscription)
    {
        lock (_lock)
        {
            int index = Array.IndexOf(_subscribers, subscription);
            if (index >= 0)
                _subscribers = [.. _subscribers.AsSpan(0, index), .. _subscribers.AsSpan(index + 1)];
        }
    }
}
