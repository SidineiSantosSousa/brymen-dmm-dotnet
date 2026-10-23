namespace Brymen.Dmm.Protocol;

/// <summary>
/// Família do medidor no cabo BC-86X: o código do “Command 2” do pedido de tempo real (<c>00 | 00 cc 66</c>) e o Model ID
/// que volta nos bytes 20–23 do quadro (§2.3 e §8 de <c>docs/protocol/bm52x-usb-protocol.md</c>). As duas famílias usam o
/// mesmo mapa de LCD; o Model ID identifica a família, e não o modelo exato.
/// </summary>
public enum MeterFamily : byte
{
    /// <summary>
    /// BM52x (Model ID <c>52h</c>): BM525s (= Minipa ET-2615A) e BM521s, com datalogger. Responde a <c>52 66</c> e a
    /// <c>82 66</c>, com o mesmo quadro, e lê a memória com <c>52 88</c>/<c>52 89</c>/<c>52 8A</c>.
    /// </summary>
    Bm52x = 0x52,

    /// <summary>
    /// BM82x (Model ID <c>82h</c>): BM821s, BM822s, BM827s (= Minipa ET-2517A) e BM829s, sem datalogger. Só responde a
    /// <c>82 66</c>, e descarta parte dos pedidos de tempo real (§8.5).
    /// </summary>
    Bm82x = 0x82,
}

/// <summary>Informações das famílias de <see cref="MeterFamily"/>.</summary>
public static class MeterFamilies
{
    /// <summary>A família de um Model ID (byte 20 a 23 do quadro de tempo real), se ele for conhecido.</summary>
    /// <param name="modelId">Model ID: <c>52h</c> ou <c>82h</c>.</param>
    /// <param name="family">A família, quando o retorno é <see langword="true"/>.</param>
    public static bool TryFromModelId(byte modelId, out MeterFamily family)
    {
        switch (modelId)
        {
            case (byte)MeterFamily.Bm52x:
                family = MeterFamily.Bm52x;
                return true;
            case (byte)MeterFamily.Bm82x:
                family = MeterFamily.Bm82x;
                return true;
            default:
                family = default;
                return false;
        }
    }

    /// <summary>O código do “Command 2” e do Model ID da família (<c>52h</c> ou <c>82h</c>).</summary>
    public static byte Code(this MeterFamily family) => (byte)family;

    /// <summary>Se a família tem datalogger: só a BM52x (BM525s e BM521s).</summary>
    public static bool HasDataLogger(this MeterFamily family) => family == MeterFamily.Bm52x;

    /// <summary>Nome curto da família: <c>"BM52x"</c> ou <c>"BM82x"</c>.</summary>
    public static string Name(this MeterFamily family) => family switch
    {
        MeterFamily.Bm52x => "BM52x",
        MeterFamily.Bm82x => "BM82x",
        _ => "desconhecida",
    };

    /// <summary>
    /// Descrição da família com os modelos validados no hardware: <c>"BM52x (BM525s / Minipa ET-2615A)"</c> e
    /// <c>"BM82x (Minipa ET-2517A)"</c>. O Model ID não distingue os modelos de uma família.
    /// </summary>
    public static string Describe(this MeterFamily family) => family switch
    {
        MeterFamily.Bm52x => "BM52x (BM525s / Minipa ET-2615A)",
        MeterFamily.Bm82x => "BM82x (Minipa ET-2517A)",
        _ => "família desconhecida",
    };
}
