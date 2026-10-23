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
/// nunca fazem a E/S na thread de quem chama. Os membros que vierem em versões futuras terão implementação padrão, para
/// não quebrar quem implementa a interface.
/// </remarks>
public interface IBrymenMeter
{
    /// <summary>Cabo aberto (atualizado a cada reabertura).</summary>
    HidDeviceInfo Device { get; }

    /// <summary>Estado atual da comunicação.</summary>
    MeterState State { get; }

    /// <summary>Última falha registrada (<see langword="null"/> se nenhuma); continua depois da recuperação.</summary>
    BrymenMeterError? LastError { get; }

    /// <summary>Um <c>52 66</c> decodificado, sem nova tentativa (veja <see cref="BrymenMeter.ReadAsync"/>).</summary>
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
