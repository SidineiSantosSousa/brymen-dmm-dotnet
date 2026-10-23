namespace Brymen.Dmm.Api.Meter;

/// <summary>
/// Registra em log os itens descartados por backpressure numa <see cref="Subscription{T}"/>: o primeiro descarte na
/// hora e, enquanto eles continuarem, um total a cada <see cref="Interval"/>, para não inundar o journal. Não é
/// thread-safe: pertence ao laço que lê a assinatura.
/// </summary>
internal sealed class DropReporter(ILogger logger, string stream)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private long _pending;
    private long _total;
    private long? _lastLog;

    /// <summary>Total descartado desde a criação.</summary>
    public long Total => _total;

    /// <summary>Recolhe os descartes da assinatura e registra se for a hora.</summary>
    public void Collect<T>(Subscription<T> subscription)
    {
        long dropped = subscription.TakeDropped();
        if (dropped == 0 && _pending == 0)
            return;
        _pending += dropped;
        _total += dropped;
        long now = Environment.TickCount64;
        if (_lastLog is { } last && now - last < (long)Interval.TotalMilliseconds)
            return;
        Flush();
        _lastLog = now;
    }

    /// <summary>Registra o que ainda não foi registrado (fim do fluxo).</summary>
    public void Flush()
    {
        if (_pending == 0)
            return;
        logger.LogWarning("{Stream}: {Count} item(ns) descartado(s) porque o consumidor não acompanhou o ritmo (total {Total}).", stream, _pending, _total);
        _pending = 0;
    }
}
