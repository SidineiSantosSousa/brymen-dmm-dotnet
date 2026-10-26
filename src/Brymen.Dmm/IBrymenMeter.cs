using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>
/// As operações de um multímetro aberto, sem o ciclo de vida: sem abertura, sem descarte e sem o laço de
/// <see cref="BrymenMeter.Start"/>. É o que recebe quem usa o medidor sem ser o dono dele, como no
/// <c>IBrymenMeterAccessor</c> do pacote Brymen.Dmm.Extensions.Hosting, e o que um teste pode simular.
/// <see cref="BrymenMeter"/> implementa a interface.
/// </summary>
/// <remarks>
/// As operações seguem as regras do <see cref="BrymenMeter"/>: são thread-safe, passam uma de cada vez pelo cabo e
/// nunca fazem a E/S na thread de quem chama. Os membros novos têm implementação padrão (<see cref="Family"/>,
/// <see cref="SupportsDataLogger"/>, <see cref="ModelDescription"/> e <see cref="RealTimeStatistics"/>, na 0.2.0), para não
/// quebrar quem implementa a interface.
/// </remarks>
public interface IBrymenMeter
{
    /// <summary>Cabo aberto (atualizado a cada reabertura).</summary>
    HidDeviceInfo Device { get; }

    /// <summary>Estado atual da comunicação.</summary>
    MeterState State { get; }

    /// <summary>Última falha registrada (<see langword="null"/> se nenhuma); continua depois da recuperação.</summary>
    BrymenMeterError? LastError { get; }

    /// <summary>
    /// A família do medidor (veja <see cref="BrymenMeter.Family"/>): a configurada, ou a detectada pela 1ª leitura;
    /// <see langword="null"/> antes dela. A implementação padrão devolve <see langword="null"/>.
    /// </summary>
    MeterFamily? Family => null;

    /// <summary>
    /// Se o medidor pode ter datalogger: <see langword="false"/> só com uma família conhecida sem datalogger (BM82x), em que a
    /// leitura da memória falha com <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, sem nenhum comando de memória. A
    /// implementação padrão usa <see cref="Family"/>.
    /// </summary>
    bool SupportsDataLogger => Family is not { } family || family.HasDataLogger();

    /// <summary>
    /// Descrição da família do medidor, como <c>"Minipa ET-2615A (Brymen BM525s, BM52x)"</c> ou <c>"Minipa ET-2517A (Brymen BM827s, BM82x)"</c>
    /// (<see cref="MeterFamilies.Describe"/>); <see langword="null"/> com a família desconhecida. A implementação padrão usa
    /// <see cref="Family"/>.
    /// </summary>
    string? ModelDescription => Family?.Describe();

    /// <summary>
    /// Os contadores das consultas de tempo real: pedidos, reenvios e perdas (veja <see cref="BrymenMeter.RealTimeStatistics"/>).
    /// A implementação padrão devolve zeros.
    /// </summary>
    RealTimeStatistics RealTimeStatistics => default;

    /// <summary>Uma consulta de tempo real decodificada, sem nova tentativa (veja <see cref="BrymenMeter.ReadAsync"/>).</summary>
    /// <param name="cancellationToken">Cancela a consulta.</param>
    Task<DmmReading> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Lê e decodifica a memória do datalogger (veja <see cref="BrymenMeter.ReadLoggerMemoryAsync"/>).</summary>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura.</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    Task<LoggerMemory> ReadLoggerMemoryAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Lê a imagem bruta da memória do datalogger (veja <see cref="BrymenMeter.ReadLoggerImageAsync"/>).</summary>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura.</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default);
}
