using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Errors;

namespace Brymen.Dmm.Api.Meter;

/// <summary>
/// Polling do tempo real: com o link ativo, faz uma consulta de tempo real a cada <c>Meter:PollInterval</c> pela
/// <see cref="MeterSession"/> (com a detecção da família e a política de reenvio do <see cref="MeterConnection"/> da
/// biblioteca: <c>82 66</c> na detecção, depois <c>52 66</c> no BM52x e <c>82 66</c> no BM82x), aplica o comparador e
/// publica no <see cref="ReadingBroadcaster"/> e no gravador.
/// Como o original (relatório de RE, §6.5) e a leitura contínua do <see cref="BrymenMeter"/>, as duas primeiras falhas
/// seguidas só reabrem o cabo (W7012); a terceira vira E9016 e a reconexão espera <c>Meter:ReconnectDelay</c>. Uma falha
/// é uma consulta inteira sem resposta: os pedidos que o ET-2517A descarta e que um reenvio recupera ficam dentro da
/// consulta (só nas estatísticas do tempo real), devolvem uma leitura e zeram a contagem; no BM82x, o W7012 só sai com o
/// <c>Meter:RealTimeResendBudget</c> esgotado. Cabo ausente é E9015. Quadro inválido (W8011) também conta como falha, mas
/// não fecha o cabo: na 3ª seguida, a última leitura deixa de ser servida como atual. O estado <c>reconnecting</c>, a
/// última leitura e as mensagens que avisam da nova tentativa automática são da API.
/// </summary>
internal sealed class MeterPollingService(
    MeterSession session,
    ReadingBroadcaster broadcaster,
    ComparatorService comparator,
    Recorder recorder,
    TimeProvider time,
    ILogger<MeterPollingService> logger) : BackgroundService
{
    /// <summary>Falhas seguidas toleradas antes de declarar E9016 (o original avisa na 3ª), as mesmas do <see cref="BrymenMeter"/>.</summary>
    public const int MaxConsecutiveFailures = BrymenMeter.MaxConsecutiveFailures;

    private static readonly TimeSpan ReopenDelay = TimeSpan.FromMilliseconds(10);

    private int _consecutiveFailures;
    private string? _lastLoggedCode;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Libera a partida do host antes do primeiro acesso ao cabo.
        await Task.Yield();
        if (session.Options.AutoLink)
            session.EnableAutoLink();

        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = session.Changed;
            if (!session.IsPollingActive)
            {
                _consecutiveFailures = 0;
                // Sem polling (link desativado ou importação em andamento) não há leitura atual: a última envelheceria
                // em /readings/latest e entraria como amostra nova no gravador.
                broadcaster.ClearLatest();
                await changed.WaitAsync(stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            long started = time.GetTimestamp();
            TimeSpan wait = await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            if (wait == TimeSpan.Zero)
                wait = session.Options.PollInterval - time.GetElapsedTime(started);
            if (wait > TimeSpan.Zero)
                await WaitAsync(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Faz uma consulta; devolve a espera extra antes da próxima (zero = cadência normal).</summary>
    private async Task<TimeSpan> PollOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await session.PollRealTimeAsync(stoppingToken).ConfigureAwait(false) is not { } reading)
                return TimeSpan.Zero;

            long monotonic = time.GetTimestamp();
            _consecutiveFailures = 0;
            session.ReportSuccess();
            _lastLoggedCode = null;
            var live = broadcaster.Publish(reading, comparator.Evaluate(reading), monotonic);
            recorder.OnReading(live);
            return TimeSpan.Zero;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }
        catch (BrymenMeterException ex) when (ex.Code == ErrorCodes.CableNotFound)
        {
            _consecutiveFailures = 0;
            broadcaster.ClearLatest();
            Fault(ex.ToApiError(), reconnecting: true);
            return Positive(session.Options.ReconnectDelay);
        }
        catch (BrymenMeterException ex) when (ex.Code == ErrorCodes.InvalidFrame)
        {
            if (++_consecutiveFailures < MaxConsecutiveFailures)
            {
                Fault(ex.ToApiError(), reconnecting: false);
                return TimeSpan.Zero;
            }
            // 3ª falha seguida: a última leitura boa já não representa o display e deixa de ser servida como atual.
            _consecutiveFailures = 0;
            broadcaster.ClearLatest();
            Fault(ex.ToApiError(), reconnecting: true);
            return Positive(session.Options.ReconnectDelay);
        }
        catch (BrymenMeterException ex) when (ex.Code == ErrorCodes.NoDataReceived)
        {
            // A causa (timeout, relatório curto, E/S) é a falha de comunicação que a biblioteca envolveu no W7012; a
            // mensagem da API avisa que a nova tentativa é automática.
            string cause = ex.InnerException?.Message ?? ex.Message;
            if (++_consecutiveFailures < MaxConsecutiveFailures)
            {
                // Reabertura silenciosa: a sessão já fechou o cabo e a próxima consulta o reabre.
                Fault(new ApiError(ErrorCodes.NoDataReceived, $"{Messages.NoDataReceived} ({cause})"), reconnecting: false);
                return ReopenDelay;
            }
            _consecutiveFailures = 0;
            broadcaster.ClearLatest();
            Fault(new ApiError(ErrorCodes.TransmissionTimeout, $"{Messages.TransmissionTimeout} ({cause})"), reconnecting: true);
            return Positive(session.Options.ReconnectDelay);
        }
        catch (Exception ex)
        {
            // Falha inesperada (um defeito): registra e segue tentando, sem derrubar o host.
            if (_lastLoggedCode != ErrorCodes.Internal)
                logger.LogError(ex, "Falha inesperada no polling do medidor.");
            _lastLoggedCode = ErrorCodes.Internal;
            session.ReportFault(new ApiError(ErrorCodes.Internal, $"Falha inesperada no polling: {ex.Message}"), reconnecting: true);
            return Positive(session.Options.ReconnectDelay);
        }
    }

    private void Fault(ApiError error, bool reconnecting)
    {
        // Evita repetir no log o mesmo erro a cada consulta.
        if (_lastLoggedCode != error.Code)
            logger.LogWarning("Medidor: [{Code}] {Message}", error.Code, error.Message);
        _lastLoggedCode = error.Code;
        session.ReportFault(error, reconnecting);
    }

    // A espera é interrompida por mudanças de estado (ex.: link desativado ou reativado pela API).
    private async Task WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        var changed = session.Changed;
        // Cancela o timer quando a mudança de estado chega antes; senão ele ficaria vivo até o fim do delay.
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delayTask = Task.Delay(delay, time, delayCts.Token);
        await Task.WhenAny(delayTask, changed).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
    }

    private static TimeSpan Positive(TimeSpan delay) => delay > TimeSpan.Zero ? delay : ReopenDelay;
}
