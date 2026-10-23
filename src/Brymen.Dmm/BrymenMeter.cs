using System.Runtime.CompilerServices;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>
/// Multímetro Brymen BM52x (BM525s = Minipa ET-2615A) ou BM82x (Minipa ET-2517A) pelo cabo óptico USB BC-86X (kit BU-86X):
/// leitura do tempo real avulsa (<see cref="ReadAsync"/>), contínua (<see cref="ReadContinuouslyAsync"/>) ou por eventos
/// (<see cref="Start"/>), e importação da memória do datalogger do BM52x (<see cref="ReadLoggerMemoryAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Abra com <see cref="OpenAsync"/> (cabo real) ou <see cref="OpenSimulatedAsync"/> (medidor simulado com as capturas
/// reais). A classe é thread-safe, e todas as operações no cabo são serializadas: uma importação da memória pausa a
/// leitura contínua, que é retomada no fim dela. Uma falha de comunicação fecha o cabo, e a operação seguinte o reabre.
/// </para>
/// <para>
/// A família do medidor (<see cref="Family"/>) é detectada pela 1ª leitura depois da abertura, sem consulta extra
/// (<see cref="BrymenMeterOptions.Family"/>, §8.6 da especificação), e o tempo real usa o código dela: <c>52 66</c> no
/// BM52x e <c>82 66</c> no BM82x. O BM82x descarta parte dos pedidos de tempo real: cada consulta reenvia o pedido sem
/// fechar o cabo (<see cref="BrymenMeterOptions.RealTimeResendTimeout"/>, <see cref="BrymenMeterOptions.RealTimeResendBudget"/>),
/// e as perdas recuperadas não são falhas: elas só aparecem em <see cref="RealTimeStatistics"/>.
/// </para>
/// <para>
/// As operações assíncronas nunca fazem a E/S na thread de quem chama: elas passam para o pool antes do 1º comando e
/// voltam com a tarefa em andamento, mesmo com um transporte que bloqueia (o <c>poll()</c> do hidraw, um
/// <see cref="IHidDevice"/> próprio). Uma interface gráfica pode chamá-las da thread dela.
/// </para>
/// <para>
/// Os eventos são disparados fora dos locks internos, na thread da operação que os causou (a do laço de
/// <see cref="Start"/>, no caso de <see cref="ReadingReceived"/>). Uma exceção de um handler não interrompe nada: ela é
/// anunciada em <see cref="ErrorOccurred"/> com o código <see cref="BrymenErrorCodes.EventHandlerFailed"/>. Com operações
/// concorrentes, os eventos de operações diferentes podem chegar fora de ordem; <see cref="State"/> é sempre o atual.
/// Não espere de forma bloqueante (<c>Wait()</c>, <c>.Result</c>) por uma operação do medidor dentro de um handler.
/// </para>
/// </remarks>
public sealed class BrymenMeter : IBrymenMeter, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Falhas seguidas toleradas na leitura contínua antes de declarar <see cref="BrymenErrorCodes.TransmissionTimeout"/>
    /// (o original avisa na 3ª). As anteriores só reabrem o cabo, depois de 10 ms.
    /// </summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Latência de cada resposta do cabo simulado de <see cref="OpenSimulatedAsync"/> (a do transporte Simulated da API):
    /// <see cref="SimulatedHidDevice.TypicalResponseLatency"/>, 20 ms.
    /// </summary>
    public static readonly TimeSpan SimulatedLatency = SimulatedHidDevice.TypicalResponseLatency;

    private static readonly TimeSpan ReopenDelay = TimeSpan.FromMilliseconds(10);

    // Handlers sendo chamados nesta thread: StopAsync e DisposeAsync chamados de dentro de um deles não esperam o laço
    // de Start, que só continua quando o handler voltar.
    [ThreadStatic]
    private static BrymenMeter? t_raising;

    private readonly MeterConnection _connection;
    private readonly BrymenMeterOptions _options;
    private readonly TimeProvider _time;
    // Serializa toda operação no cabo. Não é descartado: o Release de uma operação interrompida pelo Dispose lançaria.
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();

    // Protegidos por _sync.
    private MeterState _state = MeterState.Closed;
    private HidDeviceInfo _device;
    private BrymenMeterError? _lastError;
    private bool _disposed;
    private Task? _loop;
    private CancellationTokenSource? _loopCts;

    private BrymenMeter(MeterConnection connection, BrymenMeterOptions options, HidDeviceInfo device)
    {
        _connection = connection;
        _options = options;
        _time = options.TimeProvider;
        _device = device;
        _state = MeterState.Connected;
    }

    /// <summary>
    /// Leitura de <see cref="Start"/>: cada <c>52 66</c> decodificado pelo laço de eventos, na thread do laço. Um handler
    /// lento atrasa a próxima consulta. As leituras de <see cref="ReadAsync"/> e <see cref="ReadContinuouslyAsync"/> não
    /// passam por aqui.
    /// </summary>
    public event EventHandler<DmmReading>? ReadingReceived;

    /// <summary>Mudança de <see cref="State"/>, causada por qualquer operação.</summary>
    public event EventHandler<MeterStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Falha de qualquer operação, a mesma que fica em <see cref="LastError"/>; também a exceção de um handler de evento
    /// (<see cref="BrymenErrorCodes.EventHandlerFailed"/>). Na leitura contínua com reconexão automática, é a única forma de
    /// saber das falhas, que não interrompem a leitura. Exceções lançadas pelos handlers deste evento são ignoradas.
    /// </summary>
    public event EventHandler<BrymenMeterError>? ErrorOccurred;

    /// <summary>Cabo aberto (atualizado a cada reabertura).</summary>
    public HidDeviceInfo Device
    {
        get { lock (_sync) return _device; }
    }

    /// <summary>Estado atual da comunicação.</summary>
    public MeterState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>
    /// Última falha registrada (<see langword="null"/> se nenhuma). Continua depois da recuperação: o
    /// <see cref="State"/> diz se a comunicação está boa agora.
    /// </summary>
    public BrymenMeterError? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    /// <summary>Se o laço de eventos de <see cref="Start"/> está rodando.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _loop is { IsCompleted: false } && _loopCts is { IsCancellationRequested: false };
        }
    }

    /// <summary>
    /// A família do medidor: a de <see cref="BrymenMeterOptions.Family"/>, quando fixa, ou a detectada pelo Model ID da
    /// leitura mais recente (<see cref="MeterConnection.Family"/>). Com <see cref="MeterFamilySelection.Auto"/>, é
    /// <see langword="null"/> logo depois da abertura, até a 1ª leitura (a detecção não faz consulta extra), e depois de 3
    /// consultas seguidas sem resposta, até a detecção seguinte.
    /// </summary>
    public MeterFamily? Family => _connection.Family;

    /// <summary>
    /// Se o medidor pode ter datalogger: <see langword="false"/> só com uma família conhecida sem datalogger (BM82x), em que
    /// <see cref="ReadLoggerMemoryAsync"/> e <see cref="ReadLoggerImageAsync"/> falham com
    /// <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, sem nenhum comando de memória (na hora, com a família
    /// confirmada há pouco; senão, depois da conferência do modelo).
    /// </summary>
    public bool SupportsDataLogger => _connection.SupportsDataLogger;

    /// <summary>
    /// Descrição da família, com os modelos validados: <c>"BM52x (BM525s / Minipa ET-2615A)"</c> ou
    /// <c>"BM82x (Minipa ET-2517A)"</c> (<see cref="MeterFamilies.Describe"/>); <see langword="null"/> com
    /// <see cref="Family"/> desconhecida. O Model ID identifica a família, e não o modelo exato.
    /// </summary>
    public string? ModelDescription => Family?.Describe();

    /// <summary>
    /// Os contadores das consultas de tempo real desde a abertura (<see cref="MeterConnection.RealTimeStatistics"/>):
    /// consultas, pedidos, reenvios, pedidos perdidos, consultas recuperadas por reenvio e consultas sem resposta.
    /// </summary>
    public RealTimeStatistics RealTimeStatistics => _connection.RealTimeStatistics;

    /// <summary>
    /// Procura o cabo (<see cref="BrymenMeterOptions.DevicePath"/> ou o primeiro VID 0820h/PID 0001h) e o abre. A
    /// procura e a abertura são síncronas (a enumeração HID do sistema), e a tarefa já volta concluída.
    /// </summary>
    /// <param name="options">Opções; padrão: <see cref="BrymenMeterOptions"/> com os valores padrão. A instância é copiada.</param>
    /// <param name="cancellationToken">Cancela antes da abertura.</param>
    /// <returns>O medidor aberto, em <see cref="MeterState.Connected"/>.</returns>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ou sem acesso (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ArgumentException">Alguma opção está fora da faixa aceita.</exception>
    /// <exception cref="PlatformNotSupportedException">Sem <see cref="BrymenMeterOptions.DeviceProvider"/>, num sistema que não é Linux nem Windows.</exception>
    public static Task<BrymenMeter> OpenAsync(BrymenMeterOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<BrymenMeter>(cancellationToken);
        try
        {
            return Task.FromResult(Open(options));
        }
        catch (Exception ex)
        {
            return Task.FromException<BrymenMeter>(ex);
        }
    }

    /// <summary>
    /// Abre um medidor simulado, sem hardware, para demonstrações e testes: um BC-86X simulado
    /// (<see cref="SimulatedHidDevice.CreateTypicalBm52x"/>, com <see cref="SimulatedLatency"/> por resposta) ligado a
    /// <paramref name="meter"/>. É o mesmo cabo do <see cref="SimulatedHidDeviceProvider()"/> sem argumentos, que pode ir em
    /// <see cref="BrymenMeterOptions.DeviceProvider"/> (no Generic Host, por exemplo).
    /// </summary>
    /// <param name="meter">
    /// Medidor simulado; padrão: <see cref="SimulatedBm52xMeter.FromCaptures"/>, com as capturas reais do BM525s (14
    /// leituras em ACV e a memória com 1 sessão de 375 amostras). Use <see cref="SimulatedBm52xMeter.FaultInjector"/> para
    /// injetar falhas e <see cref="SimulatedBm52xMeter.IsPoweredOn"/> para desligá-lo.
    /// </param>
    /// <param name="options">Opções; <see cref="BrymenMeterOptions.DeviceProvider"/> e <see cref="BrymenMeterOptions.DevicePath"/> são ignorados.</param>
    /// <param name="cancellationToken">Cancela antes da abertura.</param>
    /// <exception cref="ArgumentException">Alguma opção está fora da faixa aceita.</exception>
    public static Task<BrymenMeter> OpenSimulatedAsync(SimulatedBm52xMeter? meter = null, BrymenMeterOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<BrymenMeter>(cancellationToken);
        try
        {
            var copy = (options ?? new BrymenMeterOptions()).ValidatedCopy();
            // O mesmo cabo do new SimulatedHidDeviceProvider() sem argumentos: as capturas reais e a latência típica.
            var device = SimulatedHidDevice.CreateTypicalBm52x(meter, copy.TimeProvider);
            copy.DeviceProvider = new SimulatedHidDeviceProvider([device]);
            copy.DevicePath = null;
            return Task.FromResult(Open(copy));
        }
        catch (Exception ex)
        {
            return Task.FromException<BrymenMeter>(ex);
        }
    }

    /// <summary>Cabos BC-86X (VID 0820h/PID 0001h) conectados, na ordem da enumeração.</summary>
    /// <param name="provider">Transporte HID; padrão: <see cref="HidDeviceProviders.CreateForCurrentPlatform"/>.</param>
    /// <exception cref="PlatformNotSupportedException">Sem <paramref name="provider"/>, num sistema que não é Linux nem Windows.</exception>
    public static IReadOnlyList<HidDeviceInfo> FindDevices(IHidDeviceProvider? provider = null) => MeterConnection.FindDevices(provider);

    /// <summary>
    /// Uma consulta de tempo real decodificada (<c>52 66</c> no BM52x, <c>82 66</c> no BM82x e na detecção da família). A
    /// 1ª depois da abertura ou de uma leitura da memória tem o prazo de <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/>
    /// (3 s): um envio só com a família BM52x conhecida, e com os reenvios a cada
    /// <see cref="BrymenMeterOptions.RealTimeResendTimeout"/> na detecção e no BM82x. As demais esperam até
    /// <see cref="BrymenMeterOptions.ReportTimeout"/> no BM52x, e reenviam até <see cref="BrymenMeterOptions.RealTimeResendBudget"/>
    /// no BM82x.
    /// </summary>
    /// <remarks>
    /// Uma consulta só, sem nova tentativa além dos reenvios do BM82x. No BM525s real, o 1º comando depois de muito tempo
    /// sem comunicação já ficou sem resposta uma vez, com os seguintes normais: trate
    /// <see cref="BrymenErrorCodes.NoDataReceived"/> com uma nova tentativa, ou use <see cref="ReadContinuouslyAsync"/>, que
    /// tolera 2 falhas seguidas.
    /// </remarks>
    /// <param name="cancellationToken">Cancela a consulta.</param>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.CableNotFound"/> (cabo ausente ao reabrir), <see cref="BrymenErrorCodes.NoDataReceived"/>
    /// (sem resposta completa ou falha de E/S), <see cref="BrymenErrorCodes.InvalidFrame"/> ou
    /// <see cref="BrymenErrorCodes.Internal"/> (uma exceção fora do contrato, como a de um <see cref="IHidDeviceProvider"/>
    /// próprio, com a original em <see cref="Exception.InnerException"/>). A falha também vai para <see cref="LastError"/> e
    /// <see cref="ErrorOccurred"/>, e o estado fica em <see cref="MeterState.Faulted"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">O medidor foi descartado, inclusive durante a consulta.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<DmmReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var notes = new Notifications();
        try
        {
            await new ThreadPoolHop();
            await _io.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                return await ReadCoreAsync(notes, operation.Token).ConfigureAwait(false);
            }
            catch (BrymenMeterException ex)
            {
                RecordFailure(ex, notes);
                throw;
            }
            catch (Exception ex) when (IsUnexpected(ex))
            {
                throw RecordUnexpectedFailure(ex, "na leitura do medidor", notes);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception ex) when (IsDisposalFailure(ex, cancellationToken))
        {
            throw Disposed(ex);
        }
        finally
        {
            Raise(notes);
        }
    }

    /// <summary>
    /// Leituras contínuas: um <c>52 66</c> a cada <paramref name="interval"/>, contado do início de uma consulta ao da
    /// seguinte. A enumeração termina quando o medidor é descartado; o cancelamento de
    /// <paramref name="cancellationToken"/> a termina com <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Como no original, as 2 primeiras falhas seguidas (<see cref="BrymenErrorCodes.NoDataReceived"/> ou
    /// <see cref="BrymenErrorCodes.InvalidFrame"/>) só reabrem o cabo, depois de 10 ms; a 3ª (<see cref="MaxConsecutiveFailures"/>)
    /// vira <see cref="BrymenErrorCodes.TransmissionTimeout"/> (ou continua <see cref="BrymenErrorCodes.InvalidFrame"/>). Ela,
    /// ou o cabo ausente (<see cref="BrymenErrorCodes.CableNotFound"/>), termina a enumeração com
    /// <see cref="BrymenMeterException"/>; com <see cref="BrymenMeterOptions.AutoReconnect"/>, o cabo é reaberto depois de
    /// <see cref="BrymenMeterOptions.ReconnectDelay"/> e as leituras continuam. Toda falha vai para <see cref="LastError"/>
    /// e <see cref="ErrorOccurred"/>.
    /// </para>
    /// <para>
    /// Cada enumeração faz as suas próprias consultas, independentes do laço de <see cref="Start"/> e das outras
    /// enumerações: duas ao mesmo tempo dobram o tráfego no cabo. Para vários consumidores das mesmas leituras, use
    /// <see cref="Start"/> e <see cref="ReadingReceived"/> (no Generic Host, <c>IBrymenMeterAccessor.ReadingsAsync</c>).
    /// </para>
    /// </remarks>
    /// <param name="interval">Intervalo entre consultas; padrão: <see cref="BrymenMeterOptions.PollInterval"/>.</param>
    /// <param name="cancellationToken">Cancela a enumeração (com <see cref="OperationCanceledException"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> é negativo.</exception>
    /// <exception cref="ObjectDisposedException">O medidor já foi descartado.</exception>
    public IAsyncEnumerable<DmmReading> ReadContinuouslyAsync(TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        TimeSpan period = interval ?? _options.PollInterval;
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero, nameof(interval));
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return ReadLoopAsync(period, cancellationToken);
    }

    /// <summary>
    /// Inicia a leitura contínua em segundo plano, a cada <see cref="BrymenMeterOptions.PollInterval"/>, com as leituras em
    /// <see cref="ReadingReceived"/> e as falhas em <see cref="ErrorOccurred"/> (política de
    /// <see cref="ReadContinuouslyAsync"/>). Sem <see cref="BrymenMeterOptions.AutoReconnect"/>, uma falha persistente
    /// termina o laço (<see cref="IsRunning"/> volta a <see langword="false"/>). Chamar com o laço já rodando não faz nada.
    /// </summary>
    /// <exception cref="ObjectDisposedException">O medidor foi descartado.</exception>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is { IsCompleted: false } && _loopCts is { IsCancellationRequested: false })
                return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _loopCts = cts;
            _loop = Task.Run(() => RunLoopAsync(cts), CancellationToken.None);
        }
    }

    /// <summary>
    /// Para o laço de <see cref="Start"/>, cancelando a consulta em andamento, e espera ele terminar (exceto quando chamado
    /// de dentro de um handler de evento deste medidor: aí o laço termina quando o handler voltar).
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            loop = _loop;
            cts = _loopCts;
        }
        if (cts is null)
            return;
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // O laço já terminou e descartou o seu CancellationTokenSource.
        }
        if (loop is not null && !ReferenceEquals(t_raising, this))
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Lê e decodifica a memória do datalogger (sessões e amostras); só a família BM52x tem datalogger.</summary>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura (<see cref="IProgress{T}.Report"/> síncrono).</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    /// <exception cref="BrymenMeterException">Como em <see cref="ReadLoggerImageAsync"/>; e <see cref="BrymenErrorCodes.InvalidFrame"/> se a imagem não puder ser decodificada.</exception>
    /// <exception cref="ObjectDisposedException">O medidor foi descartado, inclusive durante a leitura.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<LoggerMemory> ReadLoggerMemoryAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var image = await ReadLoggerImageAsync(progress, cancellationToken).ConfigureAwait(false);
        try
        {
            return image.Decode();
        }
        catch (Bm52xFrameException ex)
        {
            throw new BrymenMeterException(BrymenErrorCodes.InvalidFrame, $"Memória do datalogger ilegível: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lê a imagem bruta da memória do datalogger, com o algoritmo robusto de <see cref="LoggerMemoryReader"/>: confere o
    /// modelo (<see cref="LoggerReadOptions.VerifyModel"/>), envia o <c>88</c>, espera 200 ms e envia os <c>89</c>, com
    /// checksum, recuperação do <c>88</c> com outro <c>88</c>, <c>8A</c> só depois de um <c>89</c> processado e limite de
    /// tentativas. A leitura contínua (<see cref="Start"/>, <see cref="ReadContinuouslyAsync"/>) fica pausada até o fim, e
    /// a 1ª consulta depois dela espera até <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Numa família sem datalogger (BM82x, <see cref="SupportsDataLogger"/> = <see langword="false"/>), falha com
    /// <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, sem nenhum comando de memória: na hora, com a família fixa ou
    /// confirmada por um quadro de tempo real há menos de <see cref="MeterConnection.FamilyConfirmationWindow"/> (a leitura
    /// contínua rodando, por exemplo); com a família desconhecida, ou conhecida há mais tempo (o medidor pode ter sido
    /// trocado no cabo), depois da conferência do modelo, uma consulta de tempo real que a detecta. Não é falha de
    /// comunicação: o cabo continua como está, e <see cref="State"/>, <see cref="LastError"/> e <see cref="ErrorOccurred"/>
    /// não mudam.
    /// </remarks>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura (<see cref="IProgress{T}.Report"/> síncrono).</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>, <see cref="BrymenErrorCodes.CableNotFound"/>, <see cref="BrymenErrorCodes.NoDataLogger"/>,
    /// <see cref="BrymenErrorCodes.TransmissionTimeout"/>, <see cref="BrymenErrorCodes.InvalidFrame"/> ou
    /// <see cref="BrymenErrorCodes.Internal"/> (uma exceção fora do contrato, como a de um <see cref="IHidDeviceProvider"/>
    /// próprio ou de <paramref name="progress"/>); a falha também vai para <see cref="LastError"/> e
    /// <see cref="ErrorOccurred"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">O medidor foi descartado, inclusive durante a leitura.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        var notes = new Notifications();
        try
        {
            await new ThreadPoolHop();
            // A família sem datalogger fixa ou confirmada há pouco: na hora, sem esperar o cabo nem mudar o estado.
            _connection.ThrowIfNoDataLogger();
            await _io.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                _connection.ThrowIfNoDataLogger();
                EnsureOpen(notes);
                var image = await _connection.ReadLoggerImageAsync(progress, operation.Token).ConfigureAwait(false);
                SetState(MeterState.Connected, notes);
                return image;
            }
            catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.DataLoggerNotSupported)
            {
                // Não é falha de comunicação: se a conferência do modelo o detectou, o medidor respondeu.
                if (_connection.IsOpen)
                    SetState(MeterState.Connected, notes);
                throw;
            }
            catch (BrymenMeterException ex)
            {
                _connection.Close();
                RecordFailure(ex, notes);
                throw;
            }
            catch (Exception ex) when (IsUnexpected(ex))
            {
                throw RecordUnexpectedFailure(ex, "na leitura da memória", notes);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception ex) when (IsDisposalFailure(ex, cancellationToken))
        {
            throw Disposed(ex);
        }
        finally
        {
            Raise(notes);
        }
    }

    /// <summary>
    /// Fecha o cabo e encerra as operações: as em andamento falham com <see cref="ObjectDisposedException"/> e as
    /// enumerações de <see cref="ReadContinuouslyAsync"/> terminam. Não espera o laço de <see cref="Start"/> (veja
    /// <see cref="DisposeAsync"/>), que termina logo depois sem disparar mais eventos.
    /// </summary>
    public void Dispose()
    {
        Notifications notes;
        lock (_sync)
        {
            if (_disposed)
                return;
            notes = new Notifications();
            SetStateCore(MeterState.Disposed, notes);
            _disposed = true;
        }
        _disposeCts.Cancel();
        _connection.Dispose();
        Raise(notes);
    }

    /// <summary>Como <see cref="Dispose"/>, e espera o laço de <see cref="Start"/> terminar (exceto quando chamado de dentro de um handler de evento deste medidor).</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_sync)
            loop = _loop;
        Dispose();
        if (loop is not null && !ReferenceEquals(t_raising, this))
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static BrymenMeter Open(BrymenMeterOptions? options)
    {
        var copy = (options ?? new BrymenMeterOptions()).ValidatedCopy();
        var connection = new MeterConnection(copy);
        try
        {
            var device = connection.Open();
            return new BrymenMeter(connection, copy, device);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    private async IAsyncEnumerable<DmmReading> ReadLoopAsync(TimeSpan period, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var operation = BeginOperation(cancellationToken);
        var token = operation.Token;
        int failures = 0;
        while (true)
        {
            long started = _time.GetTimestamp();
            PollOutcome outcome;
            try
            {
                outcome = await PollOnceAsync(failures, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDisposalFailure(ex, cancellationToken))
            {
                yield break;
            }

            TimeSpan wait;
            if (outcome.Reading is { } reading)
            {
                failures = 0;
                yield return reading;
                wait = period - _time.GetElapsedTime(started);
            }
            else if (outcome.Persistent is { } error)
            {
                failures = 0;
                if (!_options.AutoReconnect)
                    throw error;
                wait = _options.ReconnectDelay > TimeSpan.Zero ? _options.ReconnectDelay : ReopenDelay;
            }
            else
            {
                failures = outcome.Failures;
                wait = ReopenDelay;
            }

            if (wait <= TimeSpan.Zero)
            {
                if (IsDisposed)
                    yield break;
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            bool stopped = false;
            try
            {
                await Task.Delay(wait, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Descarte do medidor durante a espera.
                stopped = true;
            }
            if (stopped)
                yield break;
        }
    }

    // Uma consulta da leitura contínua, com a política de falhas seguidas; registra o resultado no estado.
    private async Task<PollOutcome> PollOnceAsync(int failuresSoFar, CancellationToken token)
    {
        var notes = new Notifications();
        try
        {
            // Também a cada volta de ReadContinuouslyAsync: com o intervalo zero, a enumeração nunca prende a thread de quem a consome.
            await new ThreadPoolHop();
            await _io.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return new PollOutcome(await ReadCoreAsync(notes, token).ConfigureAwait(false), 0, null);
            }
            catch (BrymenMeterException ex)
            {
                int failures = failuresSoFar + 1;
                if (ex.Code != BrymenErrorCodes.CableNotFound && failures < MaxConsecutiveFailures)
                {
                    RecordFailure(ex, notes);
                    return new PollOutcome(null, failures, null);
                }
                // 3ª falha seguida, ou cabo ausente: falha persistente.
                var error = ex.Code is BrymenErrorCodes.NoDataReceived
                    ? new BrymenMeterException(BrymenErrorCodes.TransmissionTimeout, $"{MeterMessages.TransmissionTimeout} ({ex.Message})", ex)
                    : ex;
                RecordFailure(error, notes);
                return new PollOutcome(null, 0, error);
            }
            catch (Exception ex) when (IsUnexpected(ex))
            {
                // Falha inesperada (um defeito): registrada como persistente, para o laço seguir tentando sem morrer calado.
                return new PollOutcome(null, 0, RecordUnexpectedFailure(ex, "na leitura do medidor", notes));
            }
            finally
            {
                _io.Release();
            }
        }
        finally
        {
            Raise(notes);
        }
    }

    // Chamado sob _io. Uma falha de comunicação fecha o cabo (a próxima operação o reabre) e é relançada sem registro:
    // quem chama decide o código (a leitura contínua troca a 3ª por E9016).
    private async Task<DmmReading> ReadCoreAsync(Notifications notes, CancellationToken token)
    {
        try
        {
            EnsureOpen(notes);
            var reading = await _connection.ReadRealTimeAsync(token).ConfigureAwait(false);
            SetState(MeterState.Connected, notes);
            return reading;
        }
        catch (BrymenMeterException)
        {
            _connection.Close();
            throw;
        }
    }

    // Chamado sob _io: reabre o cabo depois de uma falha (estado Connecting enquanto isso).
    private void EnsureOpen(Notifications notes)
    {
        if (_connection.IsOpen)
            return;
        SetState(MeterState.Connecting, notes);
        var device = _connection.Open();
        lock (_sync)
            _device = device;
    }

    private async Task RunLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            await foreach (var reading in ReadLoopAsync(_options.PollInterval, cts.Token).ConfigureAwait(false))
            {
                if (cts.IsCancellationRequested)
                    break;
                RaiseReading(reading);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // StopAsync ou Dispose.
        }
        catch (BrymenMeterException)
        {
            // Falha persistente sem AutoReconnect: já registrada e anunciada em ErrorOccurred; o laço termina.
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // Descartado antes da 1ª consulta.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_loopCts, cts))
                    _loopCts = null;
            }
            cts.Dispose();
        }
    }

    private Operation BeginOperation(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new Operation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token));
    }

    // Cancelamento ou ObjectDisposedException causados pelo descarte do medidor, e não pelo token de quem chamou.
    private bool IsDisposalFailure(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException or ObjectDisposedException && (IsDisposed && !cancellationToken.IsCancellationRequested);

    private ObjectDisposedException Disposed(Exception inner) => new(GetType().FullName, inner);

    // Uma exceção fora do contrato das camadas de baixo (um IHidDeviceProvider ou IHidDevice próprio que lança
    // InvalidOperationException, por exemplo); o cancelamento e o descarte seguem os caminhos próprios.
    private static bool IsUnexpected(Exception ex) => ex is not (BrymenMeterException or OperationCanceledException or ObjectDisposedException);

    // Chamado sob _io: fecha o cabo e registra a falha como INTERNAL, com a original dentro, como as de comunicação.
    private BrymenMeterException RecordUnexpectedFailure(Exception ex, string where, Notifications notes)
    {
        _connection.Close();
        var error = new BrymenMeterException(BrymenErrorCodes.Internal, $"Falha inesperada {where}: {ex.Message}", ex);
        RecordFailure(error, notes);
        return error;
    }

    private void RecordFailure(BrymenMeterException ex, Notifications notes)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _lastError = ex.ToError(_time.GetUtcNow());
            SetStateCore(MeterState.Faulted, notes);
            notes.Add(_lastError);
        }
    }

    private void SetState(MeterState state, Notifications notes)
    {
        lock (_sync)
            SetStateCore(state, notes);
    }

    // Chamado sob _sync. Disposed é terminal.
    private void SetStateCore(MeterState state, Notifications notes)
    {
        if (_state == state || _state == MeterState.Disposed)
            return;
        notes.Add(new MeterStateChangedEventArgs(_state, state));
        _state = state;
    }

    private void Raise(Notifications notes)
    {
        foreach (object item in notes.Items)
        {
            switch (item)
            {
                case MeterStateChangedEventArgs change when change.Current == MeterState.Disposed || !IsDisposed:
                    Invoke(StateChanged, change, reportFailures: true);
                    break;
                case BrymenMeterError error when !IsDisposed:
                    Invoke(ErrorOccurred, error, reportFailures: false);
                    break;
            }
        }
    }

    private void RaiseReading(DmmReading reading)
    {
        if (!IsDisposed)
            Invoke(ReadingReceived, reading, reportFailures: true);
    }

    // Cada handler é chamado à parte: um que lança não impede os outros.
    private void Invoke<T>(EventHandler<T>? handlers, T args, bool reportFailures)
    {
        if (handlers is null)
            return;
        var previous = t_raising;
        t_raising = this;
        try
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<T>)handler)(this, args);
                }
                catch (Exception ex)
                {
                    if (reportFailures)
                        ReportHandlerFailure(ex);
                    // Exceções dos handlers de ErrorOccurred são ignoradas: anunciá-las lá de novo poderia não ter fim.
                }
            }
        }
        finally
        {
            t_raising = previous;
        }
    }

    private void ReportHandlerFailure(Exception ex)
    {
        if (IsDisposed)
            return;
        var error = new BrymenMeterError(BrymenErrorCodes.EventHandlerFailed,
            $"Um handler de evento do BrymenMeter lançou {ex.GetType().Name}: {ex.Message}", ex, _time.GetUtcNow());
        Invoke(ErrorOccurred, error, reportFailures: false);
    }

    /// <summary>Resultado de uma consulta da leitura contínua: a leitura, ou as falhas seguidas, ou a falha persistente.</summary>
    private readonly record struct PollOutcome(DmmReading? Reading, int Failures, BrymenMeterException? Persistent);

    /// <summary>Eventos a disparar depois que a operação libera os locks, na ordem em que aconteceram.</summary>
    private sealed class Notifications
    {
        private List<object>? _items;

        public IReadOnlyList<object> Items => _items is null ? [] : _items;

        public void Add(object item) => (_items ??= []).Add(item);
    }

    /// <summary>Token de uma operação: o de quem chamou, ligado ao descarte do medidor.</summary>
    private readonly struct Operation(CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose() => source.Dispose();
    }
}
