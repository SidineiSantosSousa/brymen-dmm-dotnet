using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Extensions.Hosting;

/// <summary>
/// Acesso ao multímetro aberto pelo serviço hospedado de
/// <see cref="BrymenMeterServiceCollectionExtensions.AddBrymenMeter(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{BrymenMeterOptions}?)"/>:
/// as operações avulsas no medidor atual (<see cref="Meter"/>, ou <see langword="null"/>), as leituras do laço do
/// serviço (<see cref="ReadingsAsync"/> e <see cref="ReadingReceived"/>), a última leitura, a última falha e os eventos
/// do medidor, repassados. Registrado como singleton (ou como serviço com chave, no registro nomeado); thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// O serviço é o dono do <see cref="BrymenMeter"/>: ele o abre na partida do host, inicia a leitura contínua
/// (<see cref="BrymenMeter.Start"/>) e o descarta na parada. Sem o cabo, ele tenta de novo a cada
/// <see cref="BrymenMeterOptions.ReconnectDelay"/> (com <see cref="BrymenMeterOptions.AutoReconnect"/>); depois de
/// aberto, o próprio medidor reabre o cabo depois de uma falha, sem trocar de instância. Quem usa o acessor recebe só as
/// operações (<see cref="IBrymenMeter"/>), sem o descarte nem o laço, e não tem como desligar o serviço por engano.
/// </para>
/// <para>
/// A abertura acontece no <c>StartingAsync</c> do serviço (<see cref="Microsoft.Extensions.Hosting.IHostedLifecycleService"/>),
/// antes do <c>StartAsync</c> de qualquer serviço, e o laço de leitura começa no <c>StartedAsync</c>, depois de todos:
/// um serviço pode esperar o medidor (<see cref="WaitForMeterAsync"/>) e assinar os eventos no próprio
/// <c>StartAsync</c>, em qualquer ordem de registro, sem perder a 1ª leitura.
/// </para>
/// <para>
/// Os eventos têm como <c>sender</c> este acessor e são disparados na thread de quem os causou (a do laço de leitura do
/// medidor, no caso de <see cref="ReadingReceived"/>), fora dos locks internos. Cada handler é chamado à parte: a exceção
/// de um handler de <see cref="ReadingReceived"/> ou de <see cref="StateChanged"/> não interrompe a leitura e vira um
/// <see cref="ErrorOccurred"/> com o código <see cref="BrymenErrorCodes.EventHandlerFailed"/>; a de um handler de
/// <see cref="ErrorOccurred"/> só é registrada no log. Um handler lento atrasa a próxima consulta ao medidor.
/// </para>
/// <para>
/// Os membros novos têm implementação padrão (<see cref="Family"/>, na 0.2.0), para não quebrar quem implementa ou
/// simula a interface num teste.
/// </para>
/// </remarks>
public interface IBrymenMeterAccessor
{
    /// <summary>
    /// As operações avulsas do medidor aberto pelo serviço, como <see cref="IBrymenMeter.ReadAsync"/> e
    /// <see cref="IBrymenMeter.ReadLoggerMemoryAsync"/> (que pausa a leitura contínua durante a importação), ou
    /// <see langword="null"/> antes da 1ª abertura (cabo ausente, por exemplo) e depois da parada do host. A instância não
    /// é a do <see cref="BrymenMeter"/>: ela não descarta nem para o medidor. Depois da parada do host, as operações dela
    /// lançam <see cref="ObjectDisposedException"/>, e o <see cref="IBrymenMeter.State"/> dela vai a
    /// <see cref="MeterState.Disposed"/>.
    /// </summary>
    IBrymenMeter? Meter { get; }

    /// <summary>
    /// Estado da comunicação: o do medidor, quando aberto; senão, <see cref="MeterState.Closed"/> (antes da partida ou com
    /// o serviço parado), <see cref="MeterState.Connecting"/> (abrindo) ou <see cref="MeterState.Faulted"/> (a abertura
    /// falhou; veja <see cref="LastError"/>). Nunca é <see cref="MeterState.Disposed"/>.
    /// </summary>
    MeterState State { get; }

    /// <summary>
    /// A última leitura do laço do serviço (a de <see cref="ReadingReceived"/>), ou <see langword="null"/> se nenhuma
    /// chegou. Continua disponível depois de uma falha e da parada; o instante está em <see cref="DmmReading.Timestamp"/>.
    /// As leituras avulsas de <see cref="IBrymenMeter.ReadAsync"/> não entram aqui.
    /// </summary>
    DmmReading? LastReading { get; }

    /// <summary>
    /// A última falha (da abertura ou do medidor), ou <see langword="null"/> se nenhuma. Continua depois da recuperação: o
    /// <see cref="State"/> diz se a comunicação está boa agora. As falhas de handlers
    /// (<see cref="BrymenErrorCodes.EventHandlerFailed"/>) não entram aqui.
    /// </summary>
    BrymenMeterError? LastError { get; }

    /// <summary>
    /// A família do medidor (<see cref="BrymenMeter.Family"/>, repassada): BM52x ou BM82x, detectada pela 1ª leitura do
    /// serviço, ou a configurada em <see cref="BrymenMeterOptions.Family"/>. Sem medidor aberto (ou antes da detecção), é a
    /// da última leitura (<see cref="DmmReading.Family"/>), ou <see langword="null"/> se nenhuma chegou. As operações do
    /// medidor, inclusive <see cref="IBrymenMeter.SupportsDataLogger"/> e <see cref="IBrymenMeter.RealTimeStatistics"/>, ficam
    /// em <see cref="Meter"/>. A implementação padrão devolve <c>Meter?.Family</c>.
    /// </summary>
    MeterFamily? Family => Meter?.Family;

    /// <summary>Cada leitura do laço do serviço (<see cref="BrymenMeter.ReadingReceived"/>, repassado).</summary>
    event EventHandler<DmmReading>? ReadingReceived;

    /// <summary>
    /// Mudança de <see cref="State"/>: as do medidor, repassadas, e as do serviço (<see cref="MeterState.Connecting"/>
    /// e <see cref="MeterState.Faulted"/> nas tentativas de abertura, <see cref="MeterState.Connected"/> quando
    /// <see cref="Meter"/> fica disponível e <see cref="MeterState.Closed"/> na parada).
    /// </summary>
    event EventHandler<MeterStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Falha da abertura ou do medidor (<see cref="BrymenMeter.ErrorOccurred"/>, repassado), a mesma que fica em
    /// <see cref="LastError"/>; também a exceção de um handler (<see cref="BrymenErrorCodes.EventHandlerFailed"/>). Com a
    /// reconexão automática, as falhas não interrompem a leitura: este evento é a forma de saber delas.
    /// </summary>
    event EventHandler<BrymenMeterError>? ErrorOccurred;

    /// <summary>
    /// Espera o medidor ficar disponível (<see cref="Meter"/> diferente de <see langword="null"/>) e devolve as operações
    /// dele; já volta concluída se ele estiver aberto. Antes da partida do host e enquanto o serviço tenta abrir o cabo, a
    /// espera continua.
    /// </summary>
    /// <remarks>
    /// A espera termina com falha quando o medidor não vai mais aparecer: o serviço desistiu da abertura (sem
    /// <see cref="BrymenMeterOptions.AutoReconnect"/>), parou, não conseguiu partir (com a exceção da partida, como o
    /// <see cref="ArgumentException"/> de uma opção inválida) ou o contêiner foi descartado. Uma chamada depois disso
    /// falha na hora, até uma nova partida do serviço.
    /// </remarks>
    /// <param name="cancellationToken">Cancela a espera.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    /// <exception cref="BrymenMeterException">
    /// O serviço desistiu: sem <see cref="BrymenMeterOptions.AutoReconnect"/>, a abertura da partida falhou (a falha dela,
    /// normalmente <see cref="BrymenErrorCodes.CableNotFound"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">O serviço parou (parada do host) ou não está rodando.</exception>
    /// <exception cref="ObjectDisposedException">O contêiner de serviços foi descartado.</exception>
    Task<IBrymenMeter> WaitForMeterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// As leituras do laço do serviço, as mesmas de <see cref="ReadingReceived"/>, como uma enumeração assíncrona. Não faz
    /// consultas próprias: várias enumerações ao mesmo tempo dividem o mesmo laço, sem tráfego a mais no cabo (ao
    /// contrário de <see cref="BrymenMeter.ReadContinuouslyAsync"/>, que faz as suas).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A inscrição acontece no início da enumeração (o 1º <c>MoveNextAsync</c> do <c>await foreach</c>), e só as leituras
    /// seguintes chegam. Antes da partida do host e com o cabo ausente, a enumeração espera; ela continua através das
    /// falhas e reconexões do medidor.
    /// </para>
    /// <para>
    /// Cada enumeração guarda até 64 leituras ainda não consumidas; com um consumidor mais lento que o laço, as mais
    /// antigas são descartadas. A enumeração termina normalmente na parada do serviço e no descarte do contêiner, e com
    /// <see cref="BrymenMeterException"/> se o serviço desistir da abertura (sem
    /// <see cref="BrymenMeterOptions.AutoReconnect"/>). Sem <see cref="BrymenMeterOptions.AutoReconnect"/>, uma falha
    /// persistente do medidor já aberto termina o laço, e a enumeração fica sem leituras até a parada: use o
    /// <paramref name="cancellationToken"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Termina a enumeração, com <see cref="OperationCanceledException"/>.</param>
    IAsyncEnumerable<DmmReading> ReadingsAsync(CancellationToken cancellationToken = default);
}
