using System.Globalization;

namespace Brymen.Dmm.Hid;

/// <summary>Opções de comunicação com o cabo óptico BC-86X (kit BU-86X).</summary>
public sealed class Bu86xLinkOptions
{
    /// <summary>Tempo máximo de espera por cada relatório de entrada.</summary>
    public TimeSpan ReportTimeout { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Tempo usado para descartar relatórios atrasados antes de cada comando.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Silêncio exigido no descarte antes do primeiro comando depois de abrir o dispositivo e depois de uma troca
    /// interrompida (timeout, cancelamento, relatório curto). O resto da resposta interrompida ainda pode estar a
    /// caminho: os relatórios chegam a cada ≈ 8 ms e o <c>66</c> leva ≈ 65 ms para começar.
    /// </summary>
    public TimeSpan RecoveryDrainTimeout { get; set; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// A resposta do BC-86X não chegou inteira: o relatório <see cref="ReportsReceived"/> + 1 não veio dentro de
/// <see cref="Bu86xLinkOptions.ReportTimeout"/>. Com ≥ 1 relatório recebido, o medidor processou o comando.
/// </summary>
public sealed class Bu86xTimeoutException(byte command, int reportsReceived, int reportsExpected, Exception? inner = null)
    : TimeoutException(CreateMessage(command, reportsReceived, reportsExpected), inner)
{
    /// <summary>Código do comando (o 3º byte de <c>00 52 cmd</c>).</summary>
    public byte Command { get; } = command;

    /// <summary>Relatórios completos recebidos antes do timeout.</summary>
    public int ReportsReceived { get; } = reportsReceived;

    /// <summary>Relatórios esperados na resposta.</summary>
    public int ReportsExpected { get; } = reportsExpected;

    private static string CreateMessage(byte command, int received, int expected) => received == 0
        ? string.Create(CultureInfo.InvariantCulture, $"Sem resposta do medidor ao comando 52 {command:X2}.")
        : string.Create(CultureInfo.InvariantCulture, $"Resposta incompleta do medidor ao comando 52 {command:X2}: {received} de {expected} relatórios.");
}

/// <summary>
/// Um relatório da resposta do BC-86X chegou com menos de <see cref="Bu86xLink.ReportDataLength"/> bytes. O relatório
/// curto conta em <see cref="ReportsReceived"/>, que por isso é sempre ≥ 1: como em <see cref="Bu86xTimeoutException"/>,
/// ≥ 1 relatório recebido indica que o medidor processou o comando.
/// </summary>
public sealed class Bu86xShortReportException(byte command, int reportsReceived, int reportsExpected, int reportLength)
    : HidException(CreateMessage(command, reportsReceived, reportsExpected, reportLength))
{
    /// <summary>Código do comando (o 3º byte de <c>00 52 cmd</c>).</summary>
    public byte Command { get; } = command;

    /// <summary>Relatórios recebidos depois do comando, incluindo o curto (que é o de número <see cref="ReportsReceived"/>).</summary>
    public int ReportsReceived { get; } = reportsReceived;

    /// <summary>Relatórios esperados na resposta.</summary>
    public int ReportsExpected { get; } = reportsExpected;

    /// <summary>Bytes de dados do relatório curto.</summary>
    public int ReportLength { get; } = reportLength;

    private static string CreateMessage(byte command, int received, int expected, int length) => string.Create(CultureInfo.InvariantCulture,
        $"Relatório {received}/{expected} curto do BC-86X no comando 52 {command:X2}: {length} bytes (esperado {Bu86xLink.ReportDataLength}).");
}

/// <summary>
/// Enquadramento do cabo BC-86X: um comando é o relatório de saída <c>[00h | 00h 52h cmd]</c>
/// e a resposta são N relatórios de entrada de 8 bytes. O quadro retornado reproduz o formato das
/// capturas no Windows e da numeração dos PDFs da Brymen: cada relatório é precedido do report ID 00h
/// (9 bytes por relatório; 27 bytes para tempo real, 36 para memória).
/// Chamadas concorrentes são serializadas.
/// </summary>
public sealed class Bu86xLink(IHidDevice device, Bu86xLinkOptions? options = null) : IDisposable
{
    /// <summary>Vendor ID USB do cabo BC-86X (0820h).</summary>
    public const ushort VendorId = 0x0820;

    /// <summary>Product ID USB do cabo BC-86X (0001h).</summary>
    public const ushort ProductId = 0x0001;

    /// <summary>Bytes de dados de cada relatório de entrada do BC-86X (sem o report ID).</summary>
    public const int ReportDataLength = 8;

    /// <summary>Bytes de cada relatório no quadro devolvido: o report ID 00h e os 8 bytes de dados.</summary>
    public const int FrameReportLength = ReportDataLength + 1;

    // Mais que a maior resposta (4 relatórios) com folga; o hidraw guarda até 64 relatórios e o Windows 32.
    private const int MaxDrainReports = 64;

    private readonly Bu86xLinkOptions _options = options ?? new Bu86xLinkOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    // Lido e escrito só sob _gate. Começa ligado: relatórios de uma troca feita por um handle anterior
    // (o serviço reabre o cabo depois de falhas) chegam também neste.
    private bool _recoveryPending = true;

    // Zerado na entrada de cada chamada e escrito, depois, só sob _gate; lido por LastCommandReportsReceived.
    private int _lastCommandReports;

    /// <summary>Dispositivo HID do cabo, descartado junto com o link.</summary>
    public IHidDevice Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

    /// <summary>
    /// Relatórios de entrada recebidos depois da escrita do comando na chamada mais recente de
    /// <see cref="ExecuteAsync(byte, int, TimeSpan, CancellationToken)"/>, contando um relatório curto; 0 se a escrita
    /// não terminou, inclusive numa chamada que falhou antes de começar (link já descartado, cancelamento na espera pela
    /// vez). Vale para qualquer desfecho da troca: sucesso, timeout, relatório curto, falha de E/S, cabo removido,
    /// cancelamento ou descarte durante a chamada. Com ≥ 1, o medidor processou o comando; com 0, não há como saber.
    /// Leia depois que a chamada terminar; com chamadas concorrentes, o valor é o da última troca, de qualquer
    /// chamador, ou 0 se outra chamada acabou de entrar (o lado seguro: 0 nunca faz supor um comando processado).
    /// </summary>
    public int LastCommandReportsReceived => Volatile.Read(ref _lastCommandReports);

    /// <summary>
    /// Procura o cabo: o primeiro VID 0820/PID 0001 ou, com <paramref name="devicePath"/>, o dispositivo nesse
    /// caminho (comparado também depois de resolver links, como um link criado por regra udev). Um caminho que não
    /// aparece na enumeração é consultado diretamente se o provider implementar <see cref="IHidDevicePathProbe"/>
    /// (no Linux: <c>docker run --device /dev/hidraw3:/dev/hidraw0</c>, um nó com outro nome ou um <c>/sys</c>
    /// invisível) e só é aceito com o VID/PID do BC-86X.
    /// </summary>
    /// <exception cref="HidException">O caminho existe mas não pôde ser consultado (permissão, por exemplo).</exception>
    public static HidDeviceInfo? FindDevice(IHidDeviceProvider provider, string? devicePath = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var devices = provider.Enumerate(VendorId, ProductId);
        if (string.IsNullOrEmpty(devicePath))
            return devices.Count > 0 ? devices[0] : null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var match = devices.FirstOrDefault(d => string.Equals(d.Path, devicePath, comparison));
        if (match is null && Path.IsPathFullyQualified(devicePath) && File.Exists(devicePath))
        {
            string real = PathResolver.TryGetRealPath(devicePath);
            match = devices.FirstOrDefault(d => string.Equals(d.Path, real, comparison));
        }
        if (match is null && provider is IHidDevicePathProbe probe
            && probe.Probe(devicePath) is { VendorId: Bu86xLink.VendorId, ProductId: Bu86xLink.ProductId } probed)
        {
            match = probed;
        }
        return match;
    }

    /// <summary>Procura e abre o cabo; lança <see cref="HidException"/> se ele não for encontrado ou não abrir.</summary>
    public static Bu86xLink Open(IHidDeviceProvider provider, string? devicePath = null, Bu86xLinkOptions? options = null)
    {
        var info = FindDevice(provider, devicePath)
            ?? throw new HidException(string.IsNullOrEmpty(devicePath)
                ? "Cabo USB BC-86X (VID 0820, PID 0001) não encontrado."
                : $"Cabo USB BC-86X (VID 0820, PID 0001) não encontrado em {devicePath}.");
        return new Bu86xLink(provider.Open(info), options);
    }

    /// <summary>Envia <paramref name="command"/> e devolve o quadro de <paramref name="reportCount"/> × 9 bytes.</summary>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou dentro de <see cref="Bu86xLinkOptions.ReportTimeout"/>.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou com menos de <see cref="ReportDataLength"/> bytes.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public Task<byte[]> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, reportCount, _options.ReportTimeout, cancellationToken);

    /// <summary>
    /// Como <see cref="ExecuteAsync(byte, int, CancellationToken)"/>, mas espera até <paramref name="firstReportTimeout"/>
    /// pelo 1º relatório da resposta; os seguintes continuam com <see cref="Bu86xLinkOptions.ReportTimeout"/>. Serve para
    /// um comando que o medidor demora a começar a responder, como o 1º <c>66</c> depois da leitura do último bloco da
    /// memória (≈ 2 s no BM525s).
    /// </summary>
    /// <param name="command">Código do comando (o 3º byte de <c>00 52 cmd</c>).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real, 4 na memória.</param>
    /// <param name="firstReportTimeout">Positivo, ou <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancela a troca.</param>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou com menos de <see cref="ReportDataLength"/> bytes.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public async Task<byte[]> ExecuteAsync(byte command, int reportCount, TimeSpan firstReportTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        if (firstReportTimeout <= TimeSpan.Zero && firstReportTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(firstReportTimeout), firstReportTimeout, "A espera pelo 1º relatório precisa ser positiva.");
        // Antes de qualquer saída antecipada: sem isso, um link descartado por outra thread logo antes desta chamada
        // deixaria a contagem da troca anterior, e quem lê a memória tomaria por processado um 89 que nem foi escrito.
        Volatile.Write(ref _lastCommandReports, 0);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastCommandReports, 0);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var buffer = new byte[Math.Max(Device.InputReportLength, ReportDataLength)];
            TimeSpan quiet = _recoveryPending ? MaxTimeout(_options.DrainTimeout, _options.RecoveryDrainTimeout) : _options.DrainTimeout;
            await DrainAsync(buffer, quiet, cancellationToken).ConfigureAwait(false);

            // Daqui em diante, qualquer saída antes do último relatório deixa o resto da resposta a caminho.
            _recoveryPending = true;
            // 0x52 = "Command 2", o código de família do BM52x (protocolo §2.3).
            await Device.WriteReportAsync(0x00, new byte[] { 0x00, 0x52, command }, cancellationToken).ConfigureAwait(false);

            var frame = new byte[reportCount * FrameReportLength];
            for (int i = 0; i < reportCount; i++)
            {
                int n;
                try
                {
                    n = await Device.ReadReportAsync(buffer, i == 0 ? firstReportTimeout : _options.ReportTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new Bu86xTimeoutException(command, i, reportCount, ex);
                }
                Volatile.Write(ref _lastCommandReports, i + 1);
                if (n < ReportDataLength)
                    throw new Bu86xShortReportException(command, i + 1, reportCount, n);
                // frame[i * 9] permanece 00h (report ID).
                buffer.AsSpan(0, ReportDataLength).CopyTo(frame.AsSpan(i * FrameReportLength + 1));
            }
            _recoveryPending = false;
            return frame;
        }
        catch (Exception ex) when (IsDisposed && ex is not ObjectDisposedException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Dispose concorrente: cada plataforma acusa o handle fechado de um jeito (EBADF, ERROR_OPERATION_ABORTED...).
            throw new ObjectDisposedException("O Bu86xLink foi descartado durante a chamada.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Uma resposta interrompida (timeout, cancelamento) deixa relatórios na fila do driver; descartá-los
    // antes do próximo comando evita respostas defasadas.
    private async Task DrainAsync(byte[] scratch, TimeSpan quiet, CancellationToken cancellationToken)
    {
        for (int i = 0; i < MaxDrainReports; i++)
        {
            try
            {
                await Device.ReadReportAsync(scratch, quiet, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private static TimeSpan MaxTimeout(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>Fecha o dispositivo. Uma chamada em andamento falha com <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // O SemaphoreSlim não é descartado: sem AvailableWaitHandle não há recurso nativo, e descartá-lo faria o
        // Release de uma chamada em andamento lançar ObjectDisposedException.
        Device.Dispose();
    }
}
