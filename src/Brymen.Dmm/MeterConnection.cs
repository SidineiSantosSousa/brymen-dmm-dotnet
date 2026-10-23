using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>
/// Conexão de baixo nível com o cabo BC-86X e o medidor BM52x, que o <see cref="BrymenMeter"/> compõe: procura e abre o
/// cabo (<see cref="BrymenMeterOptions.DevicePath"/> ou o primeiro VID 0820h/PID 0001h), guarda o
/// <see cref="Bu86xLink"/> atual, aplica a política de timeout do tempo real e lê a memória do datalogger com o
/// <see cref="LoggerMemoryReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Política de timeout do tempo real: o 1º <c>52 66</c> depois da criação da conexão e depois de qualquer comando de
/// memória (<c>88</c>, <c>89</c>, <c>8A</c>) espera até <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/> (3 s)
/// pelo 1º relatório; os demais usam <see cref="BrymenMeterOptions.ReportTimeout"/>. No BM525s real, o <c>66</c> seguinte
/// à leitura do último bloco da memória leva ≈ 2,06 s, e na partida a memória pode ter sido lida por outro programa. É
/// uma espera longa só: o <c>66</c> que termina, com resposta ou sem ela, volta ao <c>ReportTimeout</c>. Um <c>66</c>
/// cancelado não conta.
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

    private readonly BrymenMeterOptions _options;
    private readonly Bu86xLinkOptions _linkOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Lock _openLock = new();

    // Protegidos por _sync.
    private Bu86xLink? _link;
    private HidDeviceInfo? _device;
    private bool _disposed;

    // Escritos só sob _gate.
    private bool _realTimeMayBeSlow = true;
    private int _lastCommandReports;

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
    /// Se o próximo <c>52 66</c> espera até <see cref="BrymenMeterOptions.RealTimeAfterMemoryTimeout"/> pelo 1º relatório
    /// (o 1º depois da criação da conexão ou de comandos de memória).
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
    /// <paramref name="reportCount"/> × 9 bytes, com os report IDs. Abre o cabo se preciso e aplica a política de timeout
    /// do tempo real (veja as observações da classe).
    /// </summary>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou curto.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">
    /// O cabo foi fechado durante o comando (<see cref="Close"/>), ou a conexão foi descartada.
    /// </exception>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<byte[]> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(command, reportCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Como <see cref="ExecuteAsync"/>, mas uma falha de comunicação (timeout, relatório curto, E/S, cabo fechado durante o
    /// comando) volta no resultado, com os relatórios recebidos, em vez de lançar.
    /// </summary>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<MeterCommandResult> TryExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryExecuteCoreAsync(command, reportCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Um <c>52 66</c>: o quadro de tempo real de 27 bytes, com a política de timeout (veja as observações da classe).</summary>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="HidException">Falha de E/S, relatório curto ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O cabo foi fechado durante o comando, ou a conexão foi descartada.</exception>
    /// <exception cref="BrymenMeterException">Cabo não encontrado ao abrir (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public Task<byte[]> ExecuteRealTimeAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(Bm52xCommands.RealTime, Bm52xCommands.RealTimeReportCount, cancellationToken);

    /// <summary>Um <c>52 66</c> decodificado, com o instante do <see cref="TimeProvider"/>.</summary>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.CableNotFound"/> (cabo ausente), <see cref="BrymenErrorCodes.NoDataReceived"/> (sem
    /// resposta completa, falha de E/S ou cabo fechado durante o comando) ou <see cref="BrymenErrorCodes.InvalidFrame"/>.
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
        return Decode(frame, _options.TimeProvider.GetUtcNow());
    }

    /// <summary>
    /// Lê a memória do datalogger com o <see cref="LoggerMemoryReader"/> e as opções de
    /// <see cref="BrymenMeterOptions.LoggerRead"/>. A conexão fica ocupada até o fim da leitura: os outros comandos esperam.
    /// </summary>
    /// <param name="progress">Recebe o progresso a cada bloco, na thread da leitura (<see cref="IProgress{T}.Report"/> síncrono).</param>
    /// <param name="cancellationToken">Cancela a leitura.</param>
    /// <exception cref="BrymenMeterException">
    /// <see cref="BrymenErrorCodes.CableNotFound"/>, <see cref="BrymenErrorCodes.NoDataLogger"/>,
    /// <see cref="BrymenErrorCodes.TransmissionTimeout"/> ou <see cref="BrymenErrorCodes.InvalidFrame"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A conexão foi descartada.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    public async Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
    internal static DmmReading Decode(byte[] frame, DateTimeOffset timestamp)
    {
        if (!Bm52xRealtime.TryDecode(frame, timestamp, out var reading, out string? error))
            throw new BrymenMeterException(BrymenErrorCodes.InvalidFrame, $"{MeterMessages.InvalidFrame} {error}");
        return reading;
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
    private async Task<MeterCommandResult> TryExecuteCoreAsync(byte command, int reportCount, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await ExecuteCoreAsync(command, reportCount, cancellationToken).ConfigureAwait(false);
            return new MeterCommandResult(frame, null, reportCount);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex) && !IsDisposed)
        {
            // Relatórios que chegaram depois do comando, em qualquer falha (≥ 1: o medidor o processou).
            return new MeterCommandResult(null, ex, LastCommandReportsReceived);
        }
    }

    // Chamado sob _gate.
    private async Task<byte[]> ExecuteCoreAsync(byte command, int reportCount, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastCommandReports, 0);
        var link = OpenLink();
        try
        {
            if (command != Bm52xCommands.RealTime)
            {
                Volatile.Write(ref _realTimeMayBeSlow, true);
                return await link.ExecuteAsync(command, reportCount, cancellationToken).ConfigureAwait(false);
            }
            if (!_realTimeMayBeSlow)
                return await link.ExecuteAsync(command, reportCount, cancellationToken).ConfigureAwait(false);

            TimeSpan firstReport = _options.ReportTimeout > _options.RealTimeAfterMemoryTimeout ? _options.ReportTimeout : _options.RealTimeAfterMemoryTimeout;
            byte[] frame;
            try
            {
                frame = await link.ExecuteAsync(command, reportCount, firstReport, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Volatile.Write(ref _realTimeMayBeSlow, false);
                throw;
            }
            Volatile.Write(ref _realTimeMayBeSlow, false);
            return frame;
        }
        finally
        {
            Volatile.Write(ref _lastCommandReports, link.LastCommandReportsReceived);
        }
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

    /// <summary>Transporte do leitor da memória dentro de <see cref="ReadLoggerImageAsync"/>, que já tem o _gate.</summary>
    private sealed class GatedTransport(MeterConnection connection) : ILoggerMemoryTransport
    {
        public Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken)
            => connection.TryExecuteCoreAsync(command, reportCount, cancellationToken);

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            connection.Close();
            return Task.CompletedTask;
        }
    }
}
