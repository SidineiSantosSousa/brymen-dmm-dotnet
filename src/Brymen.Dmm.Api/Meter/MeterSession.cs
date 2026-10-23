using System.Globalization;
using System.Text.Json.Serialization;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Meter;

/// <summary>Estado da comunicação com o cabo.</summary>
public enum MeterConnectionState
{
    /// <summary>Link desativado (equivale ao botão Link solto do original).</summary>
    [JsonStringEnumMemberName("unlinked")] Unlinked,

    /// <summary>Link ativado, aguardando a primeira abertura do cabo.</summary>
    [JsonStringEnumMemberName("connecting")] Connecting,

    /// <summary>Cabo aberto e respondendo.</summary>
    [JsonStringEnumMemberName("connected")] Connected,

    /// <summary>Link ativado, mas a última tentativa falhou; nova tentativa após <c>Meter:ReconnectDelay</c>.</summary>
    [JsonStringEnumMemberName("reconnecting")] Reconnecting,
}

/// <summary>Erro registrado pela sessão, com o instante em que ocorreu.</summary>
internal sealed record MeterError(string Code, string Message, DateTimeOffset Timestamp)
{
    public ApiError ToApiError() => new(Code, Message);
}

/// <summary>Fotografia do estado da sessão.</summary>
/// <param name="LastError">Último erro registrado (histórico de <c>GET /api/v1/meter</c>); continua depois da recuperação.</param>
/// <param name="LinkFailure">
/// Por que o link está desativado, quando foi o Link que falhou (E9015, cabo ausente); null com o link ativo ou
/// desativado pelo usuário.
/// </param>
/// <param name="CurrentFault">
/// Falha ainda não superada por uma leitura boa do polling: explica a falta de leitura atual. Uma falha antiga, de antes
/// da última leitura boa, fica só em <paramref name="LastError"/>.
/// </param>
/// <param name="Family">
/// Família do medidor (<see cref="MeterConnection.Family"/>): a de <c>Meter:Family</c>, quando fixa, ou a detectada pelo
/// Model ID do último quadro de tempo real; null antes da 1ª detecção e depois de 3 consultas seguidas sem resposta.
/// </param>
internal sealed record MeterSnapshot(
    bool LinkEnabled,
    MeterConnectionState State,
    HidDeviceInfo? Device,
    bool DeviceOpen,
    MeterError? LastError,
    bool Importing,
    MeterError? LinkFailure = null,
    MeterError? CurrentFault = null,
    MeterFamily? Family = null);

/// <summary>
/// Estado do link com o medidor (o botão Link do original) sobre os blocos da biblioteca Brymen.Dmm:
/// <list type="bullet">
///   <item><description>
///     <see cref="MeterConnection"/>: procura e abre o cabo (<c>Meter:DevicePath</c> ou o primeiro VID 0820/PID 0001,
///     E9015 sem ele), detecta a família do medidor (BM52x ou BM82x, <c>Meter:Family</c>), aplica a política de timeout
///     do 1º <c>66</c> lento e a de reenvio do BM82x (<c>Meter:RealTimeResendTimeout</c> e
///     <c>Meter:RealTimeResendBudget</c>: os pedidos que o ET-2517A descarta são reenviados sem fechar o cabo, e só o
///     orçamento esgotado é W7012) e decodifica o tempo real (W7012, W8011);
///   </description></item>
///   <item><description>
///     <see cref="LoggerMemoryReader"/>: a leitura robusta da memória, sobre o <see cref="MeterExclusiveAccess"/>, com a
///     conferência do modelo pelo tempo real da família da conexão (E9014 num BM82x, que não tem datalogger).
///   </description></item>
/// </list>
/// A sessão guarda o que é da API: o link ativo ou não, a pausa do polling durante a importação do datalogger, o
/// último erro e a falha atual, e o sinal de mudança de estado (evento <c>status</c> do SignalR), que também avisa
/// quando a família muda.
/// </summary>
internal sealed class MeterSession : IDisposable
{
    /// <summary>
    /// Espera pelo 1º relatório do primeiro <c>52 66</c> depois de comandos de memória e na partida do serviço. No
    /// BM525s real, o <c>66</c> seguinte à leitura do último bloco da memória (o <c>89</c> que alcança o TotalBytes) só
    /// começa a ser respondido ≈ 2,06 s depois de enviado, mesmo com uma pausa de até 1 s antes dele (6 de 6 no teste
    /// de 2026-10-22); com o <c>Meter:ReportTimeout</c> padrão de 1 s, o polling acusaria W7012 depois de toda
    /// importação. Na partida, a memória pode ter sido lida por outro programa (o software original, por exemplo). A
    /// política fica no <see cref="MeterConnection"/>.
    /// </summary>
    public static readonly TimeSpan RealTimeAfterMemoryTimeout = MeterConnection.DefaultRealTimeAfterMemoryTimeout;

    private readonly MeterConnection _connection;
    private readonly MeterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MeterSession> _logger;

    // O MeterConnection serializa cada comando; _io serializa as sequências da sessão: a consulta do polling (link e
    // importação conferidos antes do comando), o link e o unlink (que espera a consulta em curso e fecha o cabo) e cada
    // comando da importação com as suas reaberturas. _sync protege o estado lido pela API.
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Lock _sync = new();
    private TaskCompletionSource _changed = NewSignal();

    private bool _linkEnabled;
    private bool _exclusive;
    private MeterConnectionState _state = MeterConnectionState.Unlinked;
    private MeterError? _lastError;
    private MeterError? _linkFailure;
    private MeterError? _currentFault;
    // Consulta de tempo real em andamento (protegido por _sync): o unlink a cancela em vez de esperar o timeout ou o
    // orçamento dos reenvios.
    private CancellationTokenSource? _pollCancellation;
    // A família anunciada (em log e no evento "status"); escrita só sob _io.
    private MeterFamily? _announcedFamily;
    private volatile bool _disposed;

    public MeterSession(IHidDeviceProvider provider, IOptions<MeterOptions> options, TimeProvider time, ILogger<MeterSession> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        IsSimulated = provider is SimulatedHidDeviceProvider;
        // Só as opções do cabo e do medidor: o intervalo do polling e a reconexão são do MeterPollingService.
        _connection = new MeterConnection(new BrymenMeterOptions
        {
            DevicePath = _options.DevicePath,
            Family = _options.Family,
            ReportTimeout = _options.ReportTimeout,
            RealTimeResendTimeout = _options.RealTimeResendTimeout,
            RealTimeResendBudget = _options.RealTimeResendBudget,
            DeviceProvider = provider,
            TimeProvider = time,
        });
        _announcedFamily = _connection.Family;
    }

    public bool IsSimulated { get; }

    public string TransportName => IsSimulated ? "simulated" : "hid";

    public MeterOptions Options => _options;

    /// <summary>
    /// Família do medidor (<see cref="MeterConnection.Family"/>): a de <c>Meter:Family</c>, quando fixa, ou a detectada
    /// pelo Model ID do último quadro de tempo real; null antes da 1ª detecção.
    /// </summary>
    public MeterFamily? Family => _connection.Family;

    /// <summary>Se o medidor pode ter datalogger: false só com a família conhecida e sem datalogger (BM82x).</summary>
    public bool SupportsDataLogger => _connection.SupportsDataLogger;

    /// <summary>
    /// Se a família vale sem consulta (<see cref="MeterConnection.IsFamilyConfirmed"/>): a de <c>Meter:Family</c>, quando
    /// fixa, ou a detectada por um quadro de tempo real há menos de <see cref="MeterConnection.FamilyConfirmationWindow"/>
    /// (o polling rodando). Fora disso, o medidor pode ter sido trocado no cabo, e a importação a confirma antes.
    /// </summary>
    public bool IsFamilyConfirmed => _connection.IsFamilyConfirmed;

    /// <summary>Contadores do tempo real desde a partida: consultas, pedidos, reenvios e perdas (<see cref="MeterConnection.RealTimeStatistics"/>).</summary>
    public RealTimeStatistics RealTimeStatistics => _connection.RealTimeStatistics;

    /// <summary>
    /// O erro E9014 quando a família conhecida não tem datalogger (BM82x): a importação é recusada sem nenhum comando de
    /// memória; null quando o medidor pode ter datalogger (BM52x ou família ainda desconhecida).
    /// </summary>
    public ApiError? DataLoggerNotSupportedError()
    {
        if (Family is not { } family || family.HasDataLogger())
            return null;
        return new ApiError(ErrorCodes.DataLoggerNotSupported, string.Create(CultureInfo.InvariantCulture,
            $"{Messages.DataLoggerNotSupported} Medidor: {family.Describe()}, Model ID {family.Code():X2}h."));
    }

    /// <summary>Completa na próxima mudança de estado (link, importação, falha, reconexão).</summary>
    public Task Changed
    {
        get { lock (_sync) return _changed.Task; }
    }

    /// <summary>O polling deve rodar: link ativo e nenhuma importação em andamento.</summary>
    public bool IsPollingActive
    {
        get { lock (_sync) return _linkEnabled && !_exclusive; }
    }

    /// <summary>Fotografia do estado; o cabo (aberto ou não, e o último aberto) vem do <see cref="MeterConnection"/>.</summary>
    public MeterSnapshot GetSnapshot()
    {
        lock (_sync)
            return new MeterSnapshot(_linkEnabled, _state, _connection.Device, _connection.IsOpen, _lastError, _exclusive, _linkFailure, _currentFault, _connection.Family);
    }

    /// <summary>Liga o link sem abrir o cabo agora; o polling abre e reconecta sozinho (<c>Meter:AutoLink</c>).</summary>
    public void EnableAutoLink()
    {
        lock (_sync)
        {
            _linkEnabled = true;
            _state = MeterConnectionState.Connecting;
        }
        Signal();
    }

    /// <summary>
    /// Ativa ou desativa o link. Ao ativar, abre o cabo na hora; se ele não for encontrado, o link fica desativado
    /// (como o botão Link do original) e o erro E9015 é devolvido. Ao desativar, cancela a consulta em andamento; o
    /// <paramref name="cancellationToken"/> só vale para a ativação.
    /// </summary>
    public async Task<ApiError?> SetLinkAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            // Antes de esperar o _io: a consulta em curso vê o link desativado (ou é cancelada) e libera o cabo na hora.
            CancellationTokenSource? poll;
            lock (_sync)
            {
                _linkEnabled = false;
                poll = _pollCancellation;
            }
            try
            {
                poll?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A consulta terminou entre a leitura do campo e o cancelamento.
            }
        }

        // O unlink não é cancelável: o link já foi desativado acima, e um cancelamento aqui (cliente que desiste
        // enquanto a importação usa o cabo) deixaria linkEnabled=false com o estado antigo e sem o evento "status". A
        // espera é curta: a consulta em curso foi cancelada, e cada comando de memória dura no máximo alguns segundos.
        await _io.WaitAsync(enabled ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                bool exclusive;
                lock (_sync)
                {
                    _linkEnabled = false;
                    _state = MeterConnectionState.Unlinked;
                    // Desativado pelo usuário: GET /readings/latest responde E9002, e não uma falha antiga.
                    _linkFailure = null;
                    _currentFault = null;
                    exclusive = _exclusive;
                }
                // Durante a importação o cabo continua aberto; ele é fechado quando ela termina.
                if (!exclusive)
                    _connection.Close();
                return null;
            }

            try
            {
                OpenCore();
            }
            catch (BrymenMeterException ex)
            {
                lock (_sync)
                {
                    _linkEnabled = false;
                    _state = MeterConnectionState.Unlinked;
                    _lastError = new MeterError(ex.Code, ex.Message, _time.GetUtcNow());
                    _linkFailure = _lastError;
                    _currentFault = null;
                }
                return ex.ToApiError();
            }

            lock (_sync)
            {
                _linkEnabled = true;
                _state = MeterConnectionState.Connected;
                _lastError = null;
                _linkFailure = null;
                _currentFault = null;
            }
            return null;
        }
        finally
        {
            _io.Release();
            Signal();
        }
    }

    /// <summary>
    /// Uma consulta de tempo real do polling (<c>82 66</c> na detecção da família, depois <c>52 66</c> no BM52x ou
    /// <c>82 66</c> no BM82x), decodificada pelo <see cref="MeterConnection"/>. No BM82x, os pedidos sem resposta são
    /// reenviados dentro da consulta, sem fechar o cabo: uma perda recuperada não é falha. Devolve null quando o polling
    /// não deve rodar agora (link desativado ou importação em andamento), inclusive quando o link é desativado durante a
    /// consulta, que então é cancelada.
    /// </summary>
    /// <exception cref="BrymenMeterException">
    /// E9015 (cabo ausente), W7012 (sem resposta completa, no BM82x depois de esgotado o orçamento dos reenvios, ou falha
    /// de E/S; o cabo é fechado, e a próxima consulta o reabre) ou W8011 (quadro inválido; o cabo continua aberto).
    /// </exception>
    public async Task<DmmReading?> PollRealTimeAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Rechecado sob _io: uma importação pode ter começado enquanto esta consulta esperava. A consulta fica
            // registrada no mesmo lock em que o link é conferido, para o unlink sempre a encontrar.
            lock (_sync)
            {
                if (!_linkEnabled || _exclusive)
                    return null;
                _pollCancellation = poll;
            }
            OpenAndSignal();
            try
            {
                return await _connection.ReadRealTimeAsync(poll.Token).ConfigureAwait(false);
            }
            catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.NoDataReceived)
            {
                _connection.Close();
                throw;
            }
            catch (OperationCanceledException) when (poll.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Unlink durante a consulta: a resposta interrompida ainda pode chegar, e o cabo é fechado de qualquer forma.
                _connection.Close();
                return null;
            }
            finally
            {
                AnnounceFamilyChange();
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pollCancellation, poll))
                    _pollCancellation = null;
            }
            _io.Release();
        }
    }

    /// <summary>Registra uma leitura bem-sucedida do polling; as falhas anteriores ficam só no histórico (<c>LastError</c>).</summary>
    public void ReportSuccess()
    {
        bool changed;
        lock (_sync)
        {
            _currentFault = null;
            changed = _linkEnabled && _state != MeterConnectionState.Connected;
            if (changed)
                _state = MeterConnectionState.Connected;
        }
        if (changed)
            Signal();
    }

    /// <summary>Registra uma falha; com <paramref name="reconnecting"/>, o estado passa a <see cref="MeterConnectionState.Reconnecting"/>.</summary>
    public void ReportFault(ApiError error, bool reconnecting)
    {
        lock (_sync)
        {
            _lastError = new MeterError(error.Code, error.Message, _time.GetUtcNow());
            _currentFault = _lastError;
            if (reconnecting && _linkEnabled)
                _state = MeterConnectionState.Reconnecting;
        }
        Signal();
    }

    /// <summary>
    /// Acesso exclusivo ao cabo para a importação do datalogger: pausa o polling, espera a consulta em curso e abre
    /// o cabo (mesmo com o link desativado).
    /// </summary>
    /// <exception cref="ApiException">Já existe uma importação em andamento (CONFLICT).</exception>
    /// <exception cref="BrymenMeterException">O cabo não foi encontrado (E9015); a falha fica registrada.</exception>
    public async Task<MeterExclusiveAccess> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_exclusive)
                throw new ApiException(ErrorCodes.Conflict, "Já existe uma importação do datalogger em andamento.");
            _exclusive = true;
        }
        Signal();
        try
        {
            await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                OpenAndSignal();
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception ex)
        {
            if (ex is BrymenMeterException meter)
                ReportFault(meter.ToApiError(), reconnecting: false);
            await ReleaseExclusiveAsync().ConfigureAwait(false);
            throw;
        }
        return new MeterExclusiveAccess(this);
    }

    /// <summary>Lê a memória com o <see cref="LoggerMemoryReader"/> da biblioteca, sobre o acesso exclusivo.</summary>
    internal Task<LoggerMemoryImage> ReadLoggerImageAsync(MeterExclusiveAccess access, IProgress<LoggerReadProgress>? progress, CancellationToken cancellationToken)
        => new LoggerMemoryReader(access, progress: progress, timeProvider: _time).ReadAsync(cancellationToken);

    /// <summary>
    /// Um comando da importação; uma falha de comunicação volta no resultado, com os relatórios recebidos. A consulta de
    /// tempo real (<c>66</c>, 3 relatórios: a conferência do modelo do <see cref="LoggerMemoryReader"/>) segue a família da
    /// conexão, como a do <see cref="MeterConnection.ReadLoggerImageAsync"/>: <c>82 66</c> com reenvios na detecção e no
    /// BM82x, que não responde ao <c>52 66</c>, e <c>52 66</c> no BM52x. Os comandos de memória são sempre <c>52 88</c>,
    /// <c>52 89</c> e <c>52 8A</c>.
    /// </summary>
    internal async Task<MeterCommandResult> ExecuteExclusiveAsync(byte command, int reportCount, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OpenAndSignal();
            if (command != Bm52xCommands.RealTime || reportCount != Bm52xCommands.RealTimeReportCount)
                return await _connection.TryExecuteAsync(command, reportCount, cancellationToken).ConfigureAwait(false);
            try
            {
                return await TryExecuteRealTimeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                AnnounceFamilyChange();
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// Detecção (ou confirmação) da família no início da importação, com o acesso exclusivo: uma consulta de tempo real,
    /// se a família não for fixa nem tiver sido confirmada há pouco (<see cref="IsFamilyConfirmed"/>; a consulta do
    /// polling que o acesso exclusivo esperou pode tê-la confirmado). Com a família desconhecida, ou depois de um comando
    /// de memória, é a espera longa do 1º <c>66</c>, com os reenvios até 3 s, que cobrem um pedido descartado pelo
    /// ET-2517A. Uma falha de comunicação não é erro aqui: a família continua a conhecida (ou desconhecida), e a
    /// conferência do modelo da importação decide.
    /// </summary>
    /// <returns>A família, ou null se ela continua desconhecida (o medidor não respondeu).</returns>
    /// <exception cref="BrymenMeterException">O cabo não foi encontrado ao reabri-lo (E9015); a falha fica registrada.</exception>
    internal async Task<MeterFamily?> DetectFamilyExclusiveAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connection.IsFamilyConfirmed)
            {
                try
                {
                    OpenAndSignal();
                }
                catch (BrymenMeterException ex)
                {
                    ReportFault(ex.ToApiError(), reconnecting: false);
                    throw;
                }
                await TryExecuteRealTimeCoreAsync(cancellationToken).ConfigureAwait(false);
                AnnounceFamilyChange();
            }
            return _connection.Family;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Fecha o cabo; o próximo comando o reabre (o original reabre o HID a cada nova tentativa).</summary>
    internal async Task ResetDeviceAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection.Close();
        }
        finally
        {
            _io.Release();
        }
        Signal();
    }

    internal async Task ReleaseExclusiveAsync()
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            bool linkEnabled;
            lock (_sync)
            {
                _exclusive = false;
                linkEnabled = _linkEnabled;
            }
            if (!linkEnabled)
                _connection.Close();
        }
        finally
        {
            _io.Release();
        }
        Signal();
    }

    // Chamado sob _io: a consulta de tempo real da família da conexão, com a falha de comunicação no resultado (como o
    // TryExecuteAsync). O cabo fechado no meio (unlink, ResetDevice) também é falha de comunicação, menos no descarte da
    // sessão.
    private async Task<MeterCommandResult> TryExecuteRealTimeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] frame = await _connection.ExecuteRealTimeAsync(cancellationToken).ConfigureAwait(false);
            return new MeterCommandResult(frame, null, Bm52xCommands.RealTimeReportCount);
        }
        catch (Exception ex) when ((ex is TimeoutException or IOException or ObjectDisposedException) && !_disposed)
        {
            return new MeterCommandResult(null, ex, _connection.LastCommandReportsReceived);
        }
    }

    // Chamado sob _io (ou na construção): registra em log e avisa no evento "status" quando a família muda (detecção, troca
    // do medidor no cabo ou nova detecção depois de 3 consultas seguidas sem resposta).
    private void AnnounceFamilyChange()
    {
        var family = _connection.Family;
        if (family == _announcedFamily)
            return;
        _announcedFamily = family;
        if (family is { } known)
        {
            _logger.LogInformation("Família do multímetro: {Family}, {DataLogger}.", known.Describe(),
                known.HasDataLogger() ? "com datalogger" : "sem datalogger");
        }
        else
        {
            _logger.LogInformation("Família do multímetro desconhecida: será detectada de novo na próxima consulta.");
        }
        Signal();
    }

    // Chamado sob _io. Uma reabertura (depois de falha ou de ResetDevice) também é mudança de estado: o evento
    // "status" do SignalR mostra o cabo aberto de novo. O SetLinkAsync não usa este caminho, para não anunciar o cabo
    // aberto com o link ainda desativado; ele avisa no fim.
    private void OpenAndSignal()
    {
        if (OpenCore())
            Signal();
    }

    // Chamado sob _io; true se o cabo foi aberto agora. Sem o cabo, o MeterConnection lança E9015; uma falha da procura
    // (ex.: sem permissão no Meter:DevicePath, que é aberto para conferir o VID/PID) vai na mensagem e, com a causa, no
    // log de depuração. O polling registra o erro em log uma vez.
    private bool OpenCore()
    {
        if (_connection.IsOpen)
            return false;

        HidDeviceInfo info;
        try
        {
            info = _connection.Open();
        }
        catch (BrymenMeterException ex) when (ex.InnerException is { } cause)
        {
            _logger.LogDebug(cause, "Falha ao procurar ou abrir o cabo BC-86X.");
            throw;
        }
        _logger.LogInformation("Cabo BC-86X aberto: {Path}", info.Path);
        return true;
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_sync)
        {
            previous = _changed;
            _changed = NewSignal();
        }
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _disposed = true;
        _connection.Dispose();
        _io.Dispose();
    }
}

/// <summary>
/// Acesso exclusivo ao cabo, obtido com <see cref="MeterSession.AcquireExclusiveAsync"/>; o polling fica pausado até o
/// descarte. É o transporte do <see cref="LoggerMemoryReader"/> da biblioteca (<see cref="ILoggerMemoryTransport"/>):
/// cada comando reabre o cabo se preciso, e as reaberturas aparecem no evento <c>status</c>.
/// </summary>
internal sealed class MeterExclusiveAccess(MeterSession session) : ILoggerMemoryTransport, IAsyncDisposable
{
    private int _disposed;

    /// <summary>
    /// Lê a imagem da memória do datalogger: confere o modelo (E9017, ou E9014 num BM82x), envia o <c>88</c>, espera
    /// 200 ms e lê os <c>89</c>, com checksum, <c>8A</c> e recomeços (veja <see cref="LoggerMemoryReader"/>).
    /// </summary>
    /// <exception cref="BrymenMeterException">E9014, E9015, E9016, E9017 ou W8011.</exception>
    public Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress, CancellationToken cancellationToken)
        => session.ReadLoggerImageAsync(this, progress, cancellationToken);

    /// <summary>Envia o comando e devolve o quadro, ou a falha (timeout/E/S) e quantos relatórios chegaram antes dela.</summary>
    /// <exception cref="BrymenMeterException">O cabo não foi encontrado ao reabri-lo (E9015).</exception>
    public Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken)
        => session.ExecuteExclusiveAsync(command, reportCount, cancellationToken);

    /// <summary>Fecha o cabo; o próximo comando o reabre.</summary>
    public Task ResetAsync(CancellationToken cancellationToken) => session.ResetDeviceAsync(cancellationToken);

    /// <summary>
    /// Detecta (ou confirma) a família, se ela não estiver confirmada, antes de criar o job da importação (veja
    /// <see cref="MeterSession.DetectFamilyExclusiveAsync"/>); devolve a família, ou null se ela continua desconhecida.
    /// </summary>
    /// <exception cref="BrymenMeterException">O cabo não foi encontrado ao reabri-lo (E9015).</exception>
    public Task<MeterFamily?> DetectFamilyAsync(CancellationToken cancellationToken) => session.DetectFamilyExclusiveAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await session.ReleaseExclusiveAsync().ConfigureAwait(false);
    }
}
