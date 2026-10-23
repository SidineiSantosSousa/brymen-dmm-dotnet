using System.Globalization;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>
/// Conexão de baixo nível com o cabo BC-86X e o medidor (BM52x ou BM82x), que o <see cref="BrymenMeter"/> compõe: procura
/// e abre o cabo (<see cref="BrymenMeterOptions.DevicePath"/> ou o primeiro VID 0820h/PID 0001h), guarda o
/// <see cref="Bu86xLink"/> atual, detecta a família do medidor, aplica a política de timeout e de reenvio do tempo real e
/// lê a memória do datalogger com o <see cref="LoggerMemoryReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Família (§8.6 de <c>docs/protocol/bm52x-usb-protocol.md</c>): com <see cref="MeterFamilySelection.Auto"/>, a 1ª
/// consulta de tempo real é a detecção. Ela envia <c>82 66</c>, que o BM52x e o BM82x aceitam, e a família vem do Model ID
/// do quadro (<see cref="Family"/>); sem nenhum relatório, tenta o <c>52 66</c> de reserva. O quadro da detecção já é uma
/// leitura, sem consulta extra. Depois, o tempo real usa o código da família: <c>52 66</c> no BM52x (o comando validado
/// no BM525s) e <c>82 66</c> no BM82x. Um quadro válido com o Model ID da outra família troca a família (o medidor foi
/// trocado no cabo), e depois de <see cref="FailuresBeforeRedetection"/> consultas seguidas sem resposta a família é
/// detectada de novo (um BM525s trocado pelo ET-2517A não responde mais ao <c>52 66</c>). A família continua depois de
/// <see cref="Close"/> e <see cref="Reopen"/>. Com <see cref="BrymenMeterOptions.Family"/> fixa, não há detecção, e um
/// quadro da outra família vira <see cref="BrymenErrorCodes.InvalidFrame"/>. Para a leitura da memória, a família
/// detectada só vale sem consulta por <see cref="FamilyConfirmationWindow"/> depois do último quadro
/// (<see cref="IsFamilyConfirmed"/>), porque o medidor pode ter sido trocado no cabo.
/// </para>
/// <para>
/// Política do tempo real:
/// <list type="bullet">
/// <item>Espera longa (§8.5.4): o 1º <c>66</c> depois da criação da conexão e depois de qualquer comando de memória
/// (<c>88</c>, <c>89</c>, <c>8A</c>) tem o prazo de <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/> (3 s;
/// <see cref="BrymenMeterOptions.ReportTimeout"/>, se maior). No BM525s real, o <c>66</c> seguinte à leitura do último
/// bloco da memória leva ≈ 2,06 s, e na partida a memória pode ter sido lida por outro programa. Com a família BM52x
/// conhecida, ou depois de um comando de memória respondido (só o BM52x tem memória), é um envio só, que espera o prazo
/// inteiro pelo 1º relatório: um reenvio no meio do preparo da resposta poderia gerar uma resposta duplicada. Com a
/// família desconhecida (a detecção) ou BM82x, o pedido é reenviado a cada
/// <see cref="BrymenMeterOptions.RealTimeResendTimeout"/> até o prazo (ou até
/// <see cref="BrymenMeterOptions.RealTimeResendBudget"/>, se maior): o ET-2517A pode descartar justamente esse pedido, o
/// 1º depois do maior tempo ocioso. É uma espera longa só: o <c>66</c> que termina, com resposta ou sem ela, volta à
/// política normal. Um <c>66</c> cancelado não conta.</item>
/// <item>BM52x: um envio, que espera até <see cref="BrymenMeterOptions.ReportTimeout"/> por relatório. O BM525s nunca
/// descartou pedidos.</item>
/// <item>BM82x e detecção: o mesmo pedido é reenviado, sem fechar o cabo, a cada
/// <see cref="BrymenMeterOptions.RealTimeResendTimeout"/> (300 ms) sem o 1º relatório, até
/// <see cref="BrymenMeterOptions.RealTimeResendBudget"/> (2 s): o ET-2517A descarta parte dos pedidos, e esperar mais não
/// adianta (§8.5). Só o orçamento esgotado é falha; os reenvios ficam em <see cref="RealTimeStatistics"/>.</item>
/// </list>
/// </para>
/// <para>
/// Os comandos são serializados entre si. <see cref="Close"/> e <see cref="Dispose"/> podem ser chamados de qualquer
/// thread e interrompem o comando em andamento. Um comando abre o cabo se ele estiver fechado; uma falha de comunicação
/// não o fecha: quem chama decide se fecha (<see cref="Close"/>) e tenta de novo, como o original, que reabre o HID a
/// cada nova tentativa.
/// </para>
/// </remarks>
public sealed class MeterConnection : IDisposable
{
    /// <summary>Padrão de <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/>: 3 s.</summary>
    public static readonly TimeSpan DefaultRealTimeAfterMemoryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Padrão de <see cref="BrymenMeterOptions.RealTimeResendTimeout"/>: 300 ms.</summary>
    public static readonly TimeSpan DefaultRealTimeResendTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Padrão de <see cref="BrymenMeterOptions.RealTimeResendBudget"/>: 2 s.</summary>
    public static readonly TimeSpan DefaultRealTimeResendBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consultas de tempo real seguidas sem resposta depois das quais a família é detectada de novo (com
    /// <see cref="MeterFamilySelection.Auto"/>): 3, as mesmas do <see cref="BrymenErrorCodes.TransmissionTimeout"/> da leitura
    /// contínua (<see cref="BrymenMeter.MaxConsecutiveFailures"/>).
    /// </summary>
    public const int FailuresBeforeRedetection = BrymenMeter.MaxConsecutiveFailures;

    /// <summary>
    /// Por quanto tempo o último quadro de tempo real confirma a família detectada para a leitura da memória: 2 s
    /// (<see cref="IsFamilyConfirmed"/>). Com a leitura contínua rodando, a família está sempre confirmada, e um BM82x
    /// recusa a memória na hora; depois de mais tempo sem quadro, a conferência do modelo (uma consulta de tempo real)
    /// confirma a família antes, porque o medidor pode ter sido trocado no cabo.
    /// </summary>
    public static readonly TimeSpan FamilyConfirmationWindow = TimeSpan.FromSeconds(2);

    private readonly BrymenMeterOptions _options;
    private readonly Bu86xLinkOptions _linkOptions;
    private readonly Bu86xResendPolicy _resend;
    private readonly Bu86xResendPolicy _longWaitResend;
    private readonly MeterFamily? _configuredFamily;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Lock _openLock = new();

    // Protegidos por _sync.
    private Bu86xLink? _link;
    private HidDeviceInfo? _device;
    private bool _disposed;

    // Escritos só sob _gate.
    private bool _realTimeMayBeSlow = true;
    // Um comando de memória teve resposta desde o último 66 terminado: o medidor é um BM52x, e a espera longa não reenvia.
    private bool _memoryAnswered;
    private int _lastCommandReports;
    private int _consecutiveRealTimeFailures;
    // Código da família detectada (0 = desconhecida) e o instante (TimeProvider) do último quadro com ela; escritos só
    // sob _gate, lidos de qualquer thread.
    private int _detectedFamily;
    private long _familyTimestamp;

    // Contadores de RealTimeStatistics, atualizados com Interlocked.
    private long _queries;
    private long _requests;
    private long _resends;
    private long _lostRequests;
    private long _recoveredQueries;
    private long _failedQueries;

    /// <summary>Cria a conexão, sem abrir o cabo (o 1º comando, ou <see cref="Open"/>, o abre).</summary>
    /// <param name="options">Opções; padrão: <see cref="BrymenMeterOptions"/> com os valores padrão. A instância é copiada.</param>
    /// <exception cref="ArgumentException">Alguma opção está fora da faixa aceita.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Sem <see cref="BrymenMeterOptions.DeviceProvider"/>, num sistema sem transporte HID (só Linux e Windows têm).
    /// </exception>
    public MeterConnection(BrymenMeterOptions? options = null)
    {
        _options = (options ?? new BrymenMeterOptions()).ValidatedCopy();
        DeviceProvider = _options.DeviceProvider ?? HidDeviceProviders.CreateForCurrentPlatform();
        _linkOptions = _options.ToLinkOptions();
        _resend = new Bu86xResendPolicy(_options.RealTimeResendTimeout, _options.RealTimeResendBudget);
        _longWaitResend = new Bu86xResendPolicy(_options.RealTimeResendTimeout, Max(_options.RealTimeResendBudget, LongFirstReportTimeout));
        _configuredFamily = _options.Family == MeterFamilySelection.Auto ? null : (MeterFamily)_options.Family;
    }

    /// <summary>Transporte HID usado para procurar e abrir o cabo.</summary>
    public IHidDeviceProvider DeviceProvider { get; }

    /// <summary>Relógio das opções (<see cref="BrymenMeterOptions.TimeProvider"/>).</summary>
    public TimeProvider TimeProvider => _options.TimeProvider;

    /// <summary>O último cabo aberto; continua disponível depois de <see cref="Close"/>. <see langword="null"/> antes da 1ª abertura.</summary>
    public HidDeviceInfo? Device
    {
        get { lock (_sync) return _device; }
    }

    /// <summary>Se o cabo está aberto.</summary>
    public bool IsOpen
    {
        get { lock (_sync) return _link is not null; }
    }

    /// <summary>
    /// A família do medidor: a de <see cref="BrymenMeterOptions.Family"/>, quando fixa, ou a detectada pelo Model ID do
    /// último quadro de tempo real (veja as observações da classe). <see langword="null"/> antes da 1ª consulta de tempo
    /// real e depois de <see cref="FailuresBeforeRedetection"/> consultas seguidas sem resposta, até a próxima detecção.
    /// </summary>
    public MeterFamily? Family
    {
        get
        {
            if (_configuredFamily is { } configured)
                return configured;
            int code = Volatile.Read(ref _detectedFamily);
            return code == 0 ? null : (MeterFamily)code;
        }
    }

    /// <summary>
    /// Se a família vale sem consulta: fixa (<see cref="BrymenMeterOptions.Family"/>) ou detectada por um quadro de tempo
    /// real há menos de <see cref="FamilyConfirmationWindow"/>, pelo <see cref="TimeProvider"/>. Com a família conhecida e
    /// <see langword="false"/>, o medidor pode ter sido trocado no cabo desde o último quadro: a leitura da memória confere
    /// o modelo antes de recusar um BM82x.
    /// </summary>
    public bool IsFamilyConfirmed
    {
        get
        {
            if (_configuredFamily is not null)
                return true;
            if (Volatile.Read(ref _detectedFamily) == 0)
                return false;
            return _options.TimeProvider.GetElapsedTime(Interlocked.Read(ref _familyTimestamp)) < FamilyConfirmationWindow;
        }
    }

    /// <summary>
    /// Se o medidor pode ter datalogger: <see langword="false"/> só com a família conhecida e sem datalogger (BM82x), em
    /// que a leitura da memória falha com <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, sem nenhum comando de
    /// memória: na hora, com a família confirmada (<see cref="IsFamilyConfirmed"/>), ou depois da conferência do modelo.
    /// </summary>
    public bool SupportsDataLogger => Family is not { } family || family.HasDataLogger();

    /// <summary>Os contadores das consultas de tempo real desde a criação da conexão: pedidos, reenvios e perdas.</summary>
    public RealTimeStatistics RealTimeStatistics => new(
        Interlocked.Read(ref _queries),
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _resends),
        Interlocked.Read(ref _lostRequests),
        Interlocked.Read(ref _recoveredQueries),
        Interlocked.Read(ref _failedQueries));

    /// <summary>
    /// Se a próxima consulta de tempo real faz a espera longa, com o prazo de
    /// <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/> (a 1ª depois da criação da conexão ou de comandos de
    /// memória): um envio só com a família BM52x conhecida ou depois de um comando de memória respondido; com a família
    /// desconhecida ou BM82x, com reenvios até esse prazo (veja as observações da classe).
    /// </summary>
    public bool IsNextRealTimeSlow => Volatile.Read(ref _realTimeMayBeSlow);

    /// <summary>
    /// Relatórios recebidos depois da escrita do comando mais recente, em qualquer desfecho (veja
    /// <see cref="Bu86xLink.LastCommandReportsReceived"/>). Com ≥ 1, o medidor processou o comando.
    /// </summary>
    public int LastCommandReportsReceived => Volatile.Read(ref _lastCommandReports);

    /// <summary>Cabos BC-86X (VID 0820h/PID 0001h) conectados.</summary>
    /// <param name="provider">Transporte HID; padrão: <see cref="HidDeviceProviders.CreateForCurrentPlatform"/>.</param>
    /// <exception cref="PlatformNotSupportedException">Sem <paramref name="provider"/>, num sistema sem transporte HID.</exception>
    public static IReadOnlyList<HidDeviceInfo> FindDevices(IHidDeviceProvider? provider = null)
        => (provider ?? HidDeviceProviders.CreateForCurrentPlatform()).Enumerate(Bu86xLink.VendorId, Bu86xLink.ProductId);

    /// <summary>Abre o cabo, se ainda não estiver aberto, e devolve a sua identificação.</summary>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ou sem acesso (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    public HidDeviceInfo Open() => OpenLink().Device.Info;

    /// <summary>Fecha o cabo, se aberto, e o abre de novo (o original reabre o HID depois de cada falha).</summary>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ou sem acesso (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    public HidDeviceInfo Reopen()
    {
        Close();
        return Open();
    }

    /// <summary>
    /// Fecha o cabo. Um comando em andamento falha (com <see cref="ObjectDisposedException"/> do <see cref="Bu86xLink"/>);
    /// o próximo reabre o cabo. Pode ser chamado de qualquer thread.
    /// </summary>
    public void Close()
    {
        Bu86xLink? link;
        lock (_sync)
        {
            link = _link;
            _link = null;
        }
        DisposeLink(link);
    }

    /// <summary>
    /// Envia <paramref name="command"/> (o 3º byte de <c>00 52 cmd</c>) e devolve o quadro de
    /// <paramref name="reportCount"/> × 9 bytes, com os report IDs. Abre o cabo se preciso e aplica a espera longa do 1º
    /// <c>66</c> (veja as observações da classe), sem reenvio e sem detectar a família: para o tempo real com a família da
    /// conexão, use <see cref="ExecuteRealTimeAsync"/>.
    /// </summary>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou curto.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">
    /// O cabo foi fechado durante o comando (<see cref="Close"/>), ou a conexão foi descartada.
    /// </exception>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public Task<byte[]> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
        => ExecuteAsync(Bm52xCommands.Bm52xFamily, command, reportCount, cancellationToken);

    /// <summary>
    /// Como <see cref="ExecuteAsync(byte, int, CancellationToken)"/>, com o “Command 2” <paramref name="family"/>: envia
    /// <c>00 family command</c> (por exemplo, <c>82 66</c>).
    /// </summary>
    /// <param name="family">Código de família (o 2º byte do relatório de saída): <see cref="Bm52xCommands.Bm52xFamily"/> ou <see cref="Bm52xCommands.Bm82xFamily"/>.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real, 4 na memória.</param>
    /// <param name="cancellationToken">Cancela o comando.</param>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou curto.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O cabo foi fechado durante o comando, ou a conexão foi descartada.</exception>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<byte[]> ExecuteAsync(byte family, byte command, int reportCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(family, command, reportCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Como <see cref="ExecuteAsync(byte, int, CancellationToken)"/>, mas uma falha de comunicação (timeout, relatório
    /// curto, E/S, cabo fechado durante o comando) volta no resultado, com os relatórios recebidos, em vez de lançar.
    /// </summary>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public Task<MeterCommandResult> TryExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
        => TryExecuteAsync(Bm52xCommands.Bm52xFamily, command, reportCount, cancellationToken);

    /// <summary>
    /// Como <see cref="TryExecuteAsync(byte, int, CancellationToken)"/>, com o “Command 2” <paramref name="family"/>
    /// (<c>00 family command</c>).
    /// </summary>
    /// <param name="family">Código de família (o 2º byte do relatório de saída): <see cref="Bm52xCommands.Bm52xFamily"/> ou <see cref="Bm52xCommands.Bm82xFamily"/>.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real, 4 na memória.</param>
    /// <param name="cancellationToken">Cancela o comando.</param>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<MeterCommandResult> TryExecuteAsync(byte family, byte command, int reportCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryExecuteCoreAsync(family, command, reportCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Uma consulta de tempo real: o quadro de 27 bytes, com o código da família da conexão (<c>52 66</c> no BM52x,
    /// <c>82 66</c> no BM82x e na detecção) e a política de timeout e de reenvio (veja as observações da classe). O quadro
    /// atualiza <see cref="Family"/>, com <see cref="MeterFamilySelection.Auto"/>.
    /// </summary>
    /// <exception cref="Bu86xTimeoutException">A resposta não chegou inteira a tempo (no BM82x, dentro do orçamento dos reenvios).</exception>
    /// <exception cref="HidException">Falha de E/S, relatório curto ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O cabo foi fechado durante o comando, ou a conexão foi descartada.</exception>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<byte[]> ExecuteRealTimeAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteRealTimeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Uma consulta de tempo real (<see cref="ExecuteRealTimeAsync"/>) decodificada, com o instante do
    /// <see cref="TimeProvider"/>.
    /// </summary>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.CableNotFound"/> (cabo ausente), <see cref="BrymenErrorCodes.NoDataReceived"/> (sem
    /// resposta completa, falha de E/S ou cabo fechado durante o comando) ou <see cref="BrymenErrorCodes.InvalidFrame"/>
    /// (inclusive o Model ID da outra família, com <see cref="BrymenMeterOptions.Family"/> fixa).
    /// </exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<DmmReading> ReadRealTimeAsync(CancellationToken cancellationToken = default)
    {
        byte[] frame;
        try
        {
            frame = await ExecuteRealTimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex) && !IsDisposed)
        {
            throw new BrymenMeterException(BrymenErrorCodes.NoDataReceived, $"{MeterMessages.NoDataReceived} ({ex.Message})", ex);
        }
        return Decode(frame, _options.TimeProvider.GetUtcNow(), _configuredFamily);
    }

    /// <summary>
    /// Lê a memória do datalogger com o <see cref="LoggerMemoryReader"/> e as opções de
    /// <see cref="BrymenMeterOptions.LoggerRead"/>. A conexão fica ocupada até o fim da leitura: os outros comandos esperam.
    /// A conferência do modelo é uma consulta de tempo real com a família da conexão (a detecção, se ela ainda não é
    /// conhecida); os comandos de memória são sempre <c>52 88</c>/<c>52 89</c>/<c>52 8A</c>. Com uma família sem
    /// datalogger (BM82x) confirmada (<see cref="IsFamilyConfirmed"/>), ou só conhecida e sem
    /// <see cref="LoggerReadOptions.VerifyModel"/>, falha na hora, sem nenhum comando; conhecida há mais tempo, a
    /// conferência do modelo a confirma antes (o medidor pode ter sido trocado no cabo), sem nenhum comando de memória.
    /// </summary>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura (<see cref="IProgress{T}.Report"/> síncrono).</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, <see cref="BrymenErrorCodes.CableNotFound"/>,
    /// <see cref="BrymenErrorCodes.NoDataLogger"/>, <see cref="BrymenErrorCodes.TransmissionTimeout"/> ou
    /// <see cref="BrymenErrorCodes.InvalidFrame"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNoDataLogger();
            var reader = new LoggerMemoryReader(new GatedTransport(this), _options.LoggerRead, progress, _options.TimeProvider);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fecha o cabo; os comandos em andamento falham e os próximos lançam <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        Bu86xLink? link;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            link = _link;
            _link = null;
        }
        // O SemaphoreSlim não é descartado: sem AvailableWaitHandle não há recurso nativo, e descartá-lo faria o Release
        // de um comando em andamento lançar ObjectDisposedException.
        DisposeLink(link);
    }

    internal bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    /// <summary>Decodifica um quadro de tempo real; um quadro inválido vira <see cref="BrymenErrorCodes.InvalidFrame"/>.</summary>
    internal static DmmReading Decode(byte[] frame, DateTimeOffset timestamp) => Decode(frame, timestamp, null);

    /// <summary>
    /// Decodifica um quadro de tempo real e, com <paramref name="expectedFamily"/>, confere a família; um quadro inválido
    /// vira <see cref="BrymenErrorCodes.InvalidFrame"/>.
    /// </summary>
    internal static DmmReading Decode(byte[] frame, DateTimeOffset timestamp, MeterFamily? expectedFamily)
    {
        bool decoded = expectedFamily is { } family
            ? Bm52xRealtime.TryDecode(frame, timestamp, family, out var reading, out string? error)
            : Bm52xRealtime.TryDecode(frame, timestamp, out reading, out error);
        if (!decoded)
            throw new BrymenMeterException(BrymenErrorCodes.InvalidFrame, $"{MeterMessages.InvalidFrame} {error}");
        return reading!;
    }

    /// <summary>A leitura da memória de uma família sem datalogger: <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>.</summary>
    internal static BrymenMeterException NoDataLoggerFamily(MeterFamily family) => new(BrymenErrorCodes.DataLoggerNotSupported,
        string.Create(CultureInfo.InvariantCulture,
            $"{MeterMessages.DataLoggerNotSupported} O medidor é da família {family.Describe()}, Model ID {family.Code():X2}h."));

    /// <summary>
    /// Lança <see cref="BrymenErrorCodes.DataLoggerNotSupported"/> se a família não tiver datalogger e valer sem consulta:
    /// confirmada (<see cref="IsFamilyConfirmed"/>) ou, sem a conferência do modelo, só conhecida. Fora disso, a
    /// conferência do modelo decide.
    /// </summary>
    internal void ThrowIfNoDataLogger()
    {
        if (Family is { } family && !family.HasDataLogger() && (IsFamilyConfirmed || !_options.LoggerRead.VerifyModel))
            throw NoDataLoggerFamily(family);
    }

    /// <summary>Falhas de comunicação de um comando: timeout, relatório curto, E/S e cabo fechado durante o comando.</summary>
    internal static bool IsCommunicationFailure(Exception ex) => ex is TimeoutException or IOException or ObjectDisposedException;

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (IsDisposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    // Chamado sob _gate.
    private async Task<MeterCommandResult> TryExecuteCoreAsync(byte family, byte command, int reportCount, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await ExecuteCoreAsync(family, command, reportCount, cancellationToken).ConfigureAwait(false);
            return new MeterCommandResult(frame, null, reportCount);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex) && !IsDisposed)
        {
            // Relatórios que chegaram depois do comando, em qualquer falha (≥ 1: o medidor o processou).
            return new MeterCommandResult(null, ex, LastCommandReportsReceived);
        }
    }

    // Chamado sob _gate: um comando cru, sem detecção nem reenvio; só a espera longa do 1º 66, num envio só.
    private async Task<byte[]> ExecuteCoreAsync(byte family, byte command, int reportCount, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastCommandReports, 0);
        var link = OpenLink();
        try
        {
            if (command != Bm52xCommands.RealTime)
            {
                Volatile.Write(ref _realTimeMayBeSlow, true);
                return await link.ExecuteAsync(family, command, reportCount, cancellationToken).ConfigureAwait(false);
            }
            if (!_realTimeMayBeSlow)
                return await link.ExecuteAsync(family, command, reportCount, cancellationToken).ConfigureAwait(false);

            byte[] frame;
            try
            {
                frame = await link.ExecuteAsync(family, command, reportCount, LongFirstReportTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EndLongWait();
                throw;
            }
            EndLongWait();
            return frame;
        }
        finally
        {
            int reports = link.LastCommandReportsReceived;
            Volatile.Write(ref _lastCommandReports, reports);
            if (command != Bm52xCommands.RealTime && reports > 0)
                _memoryAnswered = true;
        }
    }

    // Chamado sob _gate: uma consulta de tempo real como em ReadLoggerImageAsync, com a falha no resultado.
    private async Task<MeterCommandResult> TryExecuteRealTimeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var frame = await ExecuteRealTimeCoreAsync(cancellationToken).ConfigureAwait(false);
            return new MeterCommandResult(frame, null, Bm52xCommands.RealTimeReportCount);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex) && !IsDisposed)
        {
            return new MeterCommandResult(null, ex, LastCommandReportsReceived);
        }
    }

    // Chamado sob _gate: a consulta de tempo real com a família da conexão (veja as observações da classe).
    private async Task<byte[]> ExecuteRealTimeCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastCommandReports, 0);
        var link = OpenLink();
        MeterFamily? family = Family;
        var query = new QueryCounters();
        try
        {
            byte[] frame;
            try
            {
                frame = await SendRealTimeAsync(link, family, Volatile.Read(ref _realTimeMayBeSlow), query, cancellationToken).ConfigureAwait(false);
            }
            catch (Bu86xTimeoutException ex) when (family is null && ex.ReportsReceived == 0)
            {
                // A detecção sem nenhum relatório do 82 66: o 52 66 de reserva, para um BM52x que ignore o 82 66 (§8.6).
                frame = await SendOnceAsync(link, Bm52xCommands.Bm52xFamily, _options.ReportTimeout, query, cancellationToken).ConfigureAwait(false);
            }
            OnRealTimeFrame(frame, query);
            return frame;
        }
        catch (OperationCanceledException)
        {
            // Cancelada: não conta como consulta, e a espera longa continua para a próxima.
            AddRequests(query);
            throw;
        }
        catch (Exception ex)
        {
            OnRealTimeFailure(ex, query);
            throw;
        }
        finally
        {
            Volatile.Write(ref _lastCommandReports, link.LastCommandReportsReceived);
        }
    }

    // Chamado sob _gate. BM52x (ou depois de memória respondida): um envio, com o prazo da espera longa ou o ReportTimeout;
    // BM82x e detecção: o pedido reenviado a cada RealTimeResendTimeout, até o prazo da espera longa ou o
    // RealTimeResendBudget (§8.5.3 e §8.5.4).
    private async Task<byte[]> SendRealTimeAsync(Bu86xLink link, MeterFamily? family, bool slow, QueryCounters query, CancellationToken cancellationToken)
    {
        byte code = family is { } known ? known.Code() : Bm52xCommands.Bm82xFamily;
        if (family == MeterFamily.Bm52x || (slow && _memoryAnswered))
        {
            var timeout = slow ? LongFirstReportTimeout : _options.ReportTimeout;
            return await SendOnceAsync(link, code, timeout, query, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var policy = slow ? _longWaitResend : _resend;
            return await link.ExecuteAsync(code, Bm52xCommands.RealTime, Bm52xCommands.RealTimeReportCount, policy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            query.Add(link.LastCommandAttempts, resendExchange: true);
        }
    }

    private static async Task<byte[]> SendOnceAsync(Bu86xLink link, byte code, TimeSpan firstReportTimeout, QueryCounters query, CancellationToken cancellationToken)
    {
        try
        {
            return await link.ExecuteAsync(code, Bm52xCommands.RealTime, Bm52xCommands.RealTimeReportCount, firstReportTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            query.Add(link.LastCommandAttempts, resendExchange: false);
        }
    }

    private TimeSpan LongFirstReportTimeout => Max(_options.ReportTimeout, _options.RealTimeAfterMemoryTimeout);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    // Chamado sob _gate: o 66 da espera longa terminou, com resposta ou sem ela.
    private void EndLongWait()
    {
        Volatile.Write(ref _realTimeMayBeSlow, false);
        _memoryAnswered = false;
    }

    // Chamado sob _gate: um quadro completo chegou. Com a família em Auto, o Model ID detecta (ou troca) a família.
    private void OnRealTimeFrame(byte[] frame, QueryCounters query)
    {
        EndLongWait();
        if (Bm52xRealtime.TryGetFamily(frame, out var family))
        {
            _consecutiveRealTimeFailures = 0;
            if (_configuredFamily is null)
            {
                Interlocked.Exchange(ref _familyTimestamp, _options.TimeProvider.GetTimestamp());
                Volatile.Write(ref _detectedFamily, (int)family);
            }
        }
        AddRequests(query);
        Interlocked.Increment(ref _queries);
        Interlocked.Add(ref _lostRequests, Math.Max(query.Requests - 1, 0));
        if (query.LastExchangeResends > 0)
            Interlocked.Increment(ref _recoveredQueries);
    }

    // Chamado sob _gate: a consulta falhou. Depois de FailuresBeforeRedetection seguidas, a família é detectada de novo.
    private void OnRealTimeFailure(Exception ex, QueryCounters query)
    {
        EndLongWait();
        AddRequests(query);
        if (!IsCommunicationFailure(ex) || IsDisposed)
            return;
        Interlocked.Increment(ref _queries);
        Interlocked.Increment(ref _failedQueries);
        Interlocked.Add(ref _lostRequests, query.Requests);
        if (++_consecutiveRealTimeFailures < FailuresBeforeRedetection)
            return;
        _consecutiveRealTimeFailures = 0;
        if (_configuredFamily is null)
            Volatile.Write(ref _detectedFamily, 0);
    }

    private void AddRequests(QueryCounters query)
    {
        Interlocked.Add(ref _requests, query.Requests);
        Interlocked.Add(ref _resends, query.Resends);
    }

    // As aberturas são serializadas por _openLock: o SimulatedHidDeviceProvider devolve a mesma instância a cada Open, e
    // descartar o link de uma abertura concorrente perdida fecharia o da vencedora.
    private Bu86xLink OpenLink()
    {
        lock (_openLock)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_link is not null)
                    return _link;
            }

            var info = FindDevice();
            IHidDevice device;
            try
            {
                device = DeviceProvider.Open(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw new BrymenMeterException(BrymenErrorCodes.CableNotFound, $"{MeterMessages.CableNotFound} {info.Path}: {ex.Message}", ex);
            }

            var link = new Bu86xLink(device, _linkOptions);
            lock (_sync)
            {
                if (!_disposed)
                {
                    _link = link;
                    _device = device.Info;
                    return link;
                }
            }
            DisposeLink(link);
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    // Sem o cabo, E9015. Uma falha da procura (ex.: sem permissão no DevicePath, que é aberto para conferir o VID/PID)
    // vai na mensagem, com a causa.
    private HidDeviceInfo FindDevice()
    {
        HidDeviceInfo? info;
        try
        {
            info = Bu86xLink.FindDevice(DeviceProvider, _options.DevicePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new BrymenMeterException(BrymenErrorCodes.CableNotFound, $"{MeterMessages.CableNotFound} {ex.Message}", ex);
        }
        if (info is not null)
            return info;
        string message = _options.DevicePath is { } path
            ? $"{MeterMessages.CableNotFound} Nenhum BC-86X (VID 0820, PID 0001) em {path}."
            : $"{MeterMessages.CableNotFound} Nenhum BC-86X (VID 0820, PID 0001) conectado.";
        throw new BrymenMeterException(BrymenErrorCodes.CableNotFound, message);
    }

    private static void DisposeLink(Bu86xLink? link)
    {
        if (link is null)
            return;
        try
        {
            link.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // O cabo já foi removido: não há o que fechar.
        }
    }

    /// <summary>Os pedidos de uma consulta de tempo real, para <see cref="RealTimeStatistics"/>.</summary>
    private sealed class QueryCounters
    {
        /// <summary>Envios na consulta, inclusive o 52 66 de reserva da detecção.</summary>
        public int Requests { get; private set; }

        /// <summary>Reenvios na consulta.</summary>
        public int Resends { get; private set; }

        /// <summary>Reenvios da última troca: com o quadro, mais que 0 = recuperado por reenvio.</summary>
        public int LastExchangeResends { get; private set; }

        public void Add(int attempts, bool resendExchange)
        {
            Requests += attempts;
            LastExchangeResends = resendExchange ? Math.Max(attempts - 1, 0) : 0;
            Resends += LastExchangeResends;
        }
    }

    /// <summary>
    /// Transporte do leitor da memória dentro de <see cref="ReadLoggerImageAsync"/>, que já tem o _gate: a conferência do
    /// modelo é uma consulta de tempo real com a família da conexão, e os comandos de memória são sempre da família 52h.
    /// </summary>
    private sealed class GatedTransport(MeterConnection connection) : ILoggerMemoryTransport
    {
        public Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken)
            => command == Bm52xCommands.RealTime && reportCount == Bm52xCommands.RealTimeReportCount
                ? connection.TryExecuteRealTimeCoreAsync(cancellationToken)
                : connection.TryExecuteCoreAsync(Bm52xCommands.Bm52xFamily, command, reportCount, cancellationToken);

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            connection.Close();
            return Task.CompletedTask;
        }
    }
}
