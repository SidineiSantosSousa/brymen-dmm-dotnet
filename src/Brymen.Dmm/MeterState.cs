namespace Brymen.Dmm;

/// <summary>Estado da comunicação de um <see cref="BrymenMeter"/> com o cabo.</summary>
public enum MeterState
{
    /// <summary>
    /// Nenhum medidor aberto. Só aparece no <c>IBrymenMeterAccessor</c> do pacote Brymen.Dmm.Extensions.Hosting: antes da
    /// 1ª abertura e com o serviço parado. Um <see cref="BrymenMeter"/> nunca fica neste estado:
    /// <see cref="BrymenMeter.OpenAsync"/> o devolve em <see cref="Connected"/>, e o descarte o leva a
    /// <see cref="Disposed"/>.
    /// </summary>
    Closed,

    /// <summary>Abrindo ou reabrindo o cabo (depois de uma falha ou na reconexão automática).</summary>
    Connecting,

    /// <summary>Cabo aberto, e a última operação terminou bem.</summary>
    Connected,

    /// <summary>
    /// A última operação falhou (veja <see cref="BrymenMeter.LastError"/>) e o cabo foi fechado; a próxima operação, ou a
    /// reconexão automática da leitura contínua, o reabre.
    /// </summary>
    Faulted,

    /// <summary>O medidor foi descartado; nenhuma operação é aceita.</summary>
    Disposed,
}

/// <summary>Dados do evento <see cref="BrymenMeter.StateChanged"/>.</summary>
public sealed class MeterStateChangedEventArgs : EventArgs
{
    /// <summary>Cria os dados da mudança de <paramref name="previous"/> para <paramref name="current"/>.</summary>
    /// <param name="previous">Estado anterior.</param>
    /// <param name="current">Estado novo.</param>
    public MeterStateChangedEventArgs(MeterState previous, MeterState current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>Estado anterior.</summary>
    public MeterState Previous { get; }

    /// <summary>Estado novo.</summary>
    public MeterState Current { get; }
}
