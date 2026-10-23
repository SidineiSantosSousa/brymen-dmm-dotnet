using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>Família do medidor nas opções (<see cref="BrymenMeterOptions.Family"/>): detectada ou fixa.</summary>
public enum MeterFamilySelection
{
    /// <summary>
    /// Detecta a família pelo Model ID do 1º quadro de tempo real depois da abertura (§8.6 da especificação): o pedido é o
    /// <c>82 66</c>, que as duas famílias aceitam, com o <c>52 66</c> como reserva. Detecta de novo quando chega um quadro
    /// válido com o Model ID da outra família e depois de 3 consultas seguidas sem resposta (a troca do medidor no cabo).
    /// </summary>
    Auto = 0,

    /// <summary>Sempre BM52x (<see cref="MeterFamily.Bm52x"/>, BM525s / Minipa ET-2615A): tempo real com <c>52 66</c>.</summary>
    Bm52x = MeterFamily.Bm52x,

    /// <summary>
    /// Sempre BM82x (<see cref="MeterFamily.Bm82x"/>, Minipa ET-2517A): tempo real com <c>82 66</c> e reenvios, e a leitura da
    /// memória falha na hora com <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>.
    /// </summary>
    Bm82x = MeterFamily.Bm82x,
}
