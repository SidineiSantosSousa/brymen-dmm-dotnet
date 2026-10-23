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
    /// interrompida (timeout, cancelamento, relatório curto) ou com reenvios. O resto da resposta interrompida, ou a
    /// resposta atrasada de um envio anterior, ainda pode estar a caminho: os relatórios chegam a cada ≈ 8 ms e o
    /// <c>66</c> do BM525s leva ≈ 65 ms para começar. Depois de uma troca com <see cref="Bu86xResendPolicy"/> que não
    /// terminou com o quadro inteiro (orçamento esgotado, cancelamento, falha de E/S), o silêncio é de pelo menos
    /// <see cref="Bu86xResendPolicy.FirstReportWindow"/>, que cobre a latência do medidor (até ≈ 260 ms no ET-2517A).
    /// </summary>
    public TimeSpan RecoveryDrainTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Relógio do orçamento dos reenvios (<see cref="Bu86xResendPolicy.Budget"/>). Os timeouts de cada relatório ficam a
    /// cargo do dispositivo HID (o <see cref="SimulatedHidDevice"/> aceita o mesmo relógio). Padrão:
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Reenvio de um comando cujo 1º relatório não chegou a tempo, como o ET-2517A (família BM82x) exige no tempo real:
/// ele descarta parte dos pedidos, e esperar mais não adianta (§8.5 de <c>docs/protocol/bm52x-usb-protocol.md</c>). O
/// mesmo pedido sai de novo a cada <see cref="FirstReportWindow"/> sem o 1º relatório, sem fechar o cabo, até o
/// <see cref="Budget"/> da troca acabar; vale o 1º quadro completo de qualquer um dos envios.
/// </summary>
public sealed class Bu86xResendPolicy
{
    /// <summary>Cria a política.</summary>
    /// <param name="firstReportWindow">Espera pelo 1º relatório de cada envio antes de reenviar; positiva.</param>
    /// <param name="budget">Tempo total da troca, com os reenvios, contado da 1ª escrita; positivo.</param>
    /// <exception cref="ArgumentOutOfRangeException">Um dos tempos não é positivo.</exception>
    public Bu86xResendPolicy(TimeSpan firstReportWindow, TimeSpan budget)
    {
        if (firstReportWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(firstReportWindow), firstReportWindow, "A janela do 1º relatório precisa ser positiva.");
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget), budget, "O orçamento da troca precisa ser positivo.");
        FirstReportWindow = firstReportWindow;
        Budget = budget;
    }

    /// <summary>
    /// A política recomendada para o tempo real do BM82x (§8.5.3): janela de 300 ms, acima das latências normais (36 a
    /// 237 ms no ET-2517A), e orçamento de 2 s.
    /// </summary>
    public static Bu86xResendPolicy Default { get; } = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));

    /// <summary>Espera pelo 1º relatório de cada envio antes de reenviar.</summary>
    public TimeSpan FirstReportWindow { get; }

    /// <summary>Tempo total da troca, com os reenvios, contado da 1ª escrita.</summary>
    public TimeSpan Budget { get; }
}

/// <summary>
/// A resposta do BC-86X não chegou inteira: o relatório <see cref="ReportsReceived"/> + 1 não veio dentro de
/// <see cref="Bu86xLinkOptions.ReportTimeout"/> (ou, com reenvios, dentro do <see cref="Bu86xResendPolicy.Budget"/>). Com
/// ≥ 1 relatório recebido, o medidor processou o comando.
/// </summary>
public sealed class Bu86xTimeoutException : TimeoutException
{
    /// <summary>Cria a exceção de um comando da família BM52x (<c>00 52 cmd</c>), com um envio.</summary>
    /// <param name="command">Código do comando (o 3º byte de <c>00 52 cmd</c>).</param>
    /// <param name="reportsReceived">Relatórios completos recebidos antes do timeout.</param>
    /// <param name="reportsExpected">Relatórios esperados na resposta.</param>
    /// <param name="inner">A causa (o timeout do dispositivo).</param>
    public Bu86xTimeoutException(byte command, int reportsReceived, int reportsExpected, Exception? inner = null)
        : this(Bu86xLink.DefaultFamily, command, reportsReceived, reportsExpected, 1, inner)
    {
    }

    /// <summary>Cria a exceção de um comando com o “Command 2” <paramref name="family"/> (<c>00 family cmd</c>).</summary>
    /// <param name="family">Código de família do comando (o 2º byte de <c>00 52 cmd</c>): <c>52h</c> ou <c>82h</c>.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportsReceived">
    /// Relatórios completos recebidos antes do timeout; com reenvios, o maior número de relatórios de uma mesma resposta.
    /// </param>
    /// <param name="reportsExpected">Relatórios esperados na resposta.</param>
    /// <param name="attempts">Envios do comando (1 + reenvios).</param>
    /// <param name="inner">A causa (o timeout do dispositivo).</param>
    public Bu86xTimeoutException(byte family, byte command, int reportsReceived, int reportsExpected, int attempts, Exception? inner = null)
        : base(CreateMessage(family, command, reportsReceived, reportsExpected, attempts), inner)
    {
        Family = family;
        Command = command;
        ReportsReceived = reportsReceived;
        ReportsExpected = reportsExpected;
        Attempts = attempts;
    }

    /// <summary>Código de família do comando (o 2º byte de <c>00 52 cmd</c>): <c>52h</c> (padrão) ou <c>82h</c>.</summary>
    public byte Family { get; }

    /// <summary>Código do comando (o 3º byte de <c>00 52 cmd</c>).</summary>
    public byte Command { get; }

    /// <summary>
    /// Relatórios completos recebidos antes do timeout; com reenvios, o maior número de relatórios de uma mesma resposta.
    /// </summary>
    public int ReportsReceived { get; }

    /// <summary>Relatórios esperados na resposta.</summary>
    public int ReportsExpected { get; }

    /// <summary>Envios do comando (1 + reenvios); 1 sem <see cref="Bu86xResendPolicy"/>.</summary>
    public int Attempts { get; }

    private static string CreateMessage(byte family, byte command, int received, int expected, int attempts)
    {
        string sends = attempts > 1 ? string.Create(CultureInfo.InvariantCulture, $" em {attempts} envios") : "";
        return received == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Sem resposta do medidor ao comando {family:X2} {command:X2}{sends}.")
            : string.Create(CultureInfo.InvariantCulture, $"Resposta incompleta do medidor ao comando {family:X2} {command:X2}{sends}: {received} de {expected} relatórios.");
    }
}

/// <summary>
/// Um relatório da resposta do BC-86X chegou com menos de <see cref="Bu86xLink.ReportDataLength"/> bytes. O relatório
/// curto conta em <see cref="ReportsReceived"/>, que por isso é sempre ≥ 1: como em <see cref="Bu86xTimeoutException"/>,
/// ≥ 1 relatório recebido indica que o medidor processou o comando.
/// </summary>
public sealed class Bu86xShortReportException : HidException
{
    /// <summary>Cria a exceção de um comando da família BM52x (<c>00 52 cmd</c>).</summary>
    /// <param name="command">Código do comando (o 3º byte de <c>00 52 cmd</c>).</param>
    /// <param name="reportsReceived">Relatórios recebidos depois do comando, incluindo o curto.</param>
    /// <param name="reportsExpected">Relatórios esperados na resposta.</param>
    /// <param name="reportLength">Bytes de dados do relatório curto.</param>
    public Bu86xShortReportException(byte command, int reportsReceived, int reportsExpected, int reportLength)
        : this(Bu86xLink.DefaultFamily, command, reportsReceived, reportsExpected, reportLength)
    {
    }

    /// <summary>Cria a exceção de um comando com o “Command 2” <paramref name="family"/> (<c>00 family cmd</c>).</summary>
    /// <param name="family">Código de família do comando (o 2º byte): <c>52h</c> ou <c>82h</c>.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportsReceived">Relatórios recebidos depois do comando, incluindo o curto.</param>
    /// <param name="reportsExpected">Relatórios esperados na resposta.</param>
    /// <param name="reportLength">Bytes de dados do relatório curto.</param>
    public Bu86xShortReportException(byte family, byte command, int reportsReceived, int reportsExpected, int reportLength)
        : base(CreateMessage(family, command, reportsReceived, reportsExpected, reportLength))
    {
        Family = family;
        Command = command;
        ReportsReceived = reportsReceived;
        ReportsExpected = reportsExpected;
        ReportLength = reportLength;
    }

    /// <summary>Código de família do comando (o 2º byte de <c>00 52 cmd</c>): <c>52h</c> (padrão) ou <c>82h</c>.</summary>
    public byte Family { get; }

    /// <summary>Código do comando (o 3º byte de <c>00 52 cmd</c>).</summary>
    public byte Command { get; }

    /// <summary>Relatórios recebidos depois do comando, incluindo o curto (que é o de número <see cref="ReportsReceived"/>).</summary>
    public int ReportsReceived { get; }

    /// <summary>Relatórios esperados na resposta.</summary>
    public int ReportsExpected { get; }

    /// <summary>Bytes de dados do relatório curto.</summary>
    public int ReportLength { get; }

    private static string CreateMessage(byte family, byte command, int received, int expected, int length) => string.Create(CultureInfo.InvariantCulture,
        $"Relatório {received}/{expected} curto do BC-86X no comando {family:X2} {command:X2}: {length} bytes (esperado {Bu86xLink.ReportDataLength}).");
}

/// <summary>
/// Enquadramento do cabo BC-86X: um comando é o relatório de saída <c>[00h | 00h fam cmd]</c>, com o código de família
/// <c>fam</c> (“Command 2”: <see cref="DefaultFamily"/> = 52h no BM52x, 82h no tempo real do BM82x), e a resposta são N
/// relatórios de entrada de 8 bytes. O quadro retornado reproduz o formato das capturas no Windows e da numeração dos
/// PDFs da Brymen: cada relatório é precedido do report ID 00h (9 bytes por relatório; 27 bytes para tempo real, 36
/// para memória). Com uma <see cref="Bu86xResendPolicy"/>, o pedido sem o 1º relatório é reenviado sem fechar o cabo.
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

    /// <summary>
    /// “Command 2” das sobrecargas sem família: 52h, o código da família BM52x (protocolo §2.3), que serve para todos os
    /// comandos do BM525s. O BM82x só responde ao tempo real com 82h (<c>82 66</c>, §8.1).
    /// </summary>
    public const byte DefaultFamily = 0x52;

    // Mais que a maior resposta (4 relatórios) com folga; o hidraw guarda até 64 relatórios e o Windows 32.
    private const int MaxDrainReports = 64;

    private readonly Bu86xLinkOptions _options = options ?? new Bu86xLinkOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    // Lidos e escritos só sob _gate. Começa ligado: relatórios de uma troca feita por um handle anterior
    // (o serviço reabre o cabo depois de falhas) chegam também neste. _recoveryQuiet é o silêncio desse descarte (null:
    // o RecoveryQuiet), maior depois de uma troca com reenvios interrompida.
    private bool _recoveryPending = true;
    private TimeSpan? _recoveryQuiet;

    // Só publicação: zerados na entrada de cada chamada e escritos, depois, só sob _gate, com os contadores da troca
    // (ExchangeCounters), que são os que decidem o descarte e os números das exceções. Lidos por
    // LastCommandReportsReceived e LastCommandAttempts.
    private int _lastCommandReports;
    private int _lastCommandAttempts;

    /// <summary>Dispositivo HID do cabo, descartado junto com o link.</summary>
    public IHidDevice Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

    /// <summary>
    /// Relatórios de entrada recebidos depois da escrita do comando na chamada mais recente de
    /// <see cref="ExecuteAsync(byte, int, TimeSpan, CancellationToken)"/> (ou de outra sobrecarga), contando um relatório
    /// curto; 0 se a escrita não terminou, inclusive numa chamada que falhou antes de começar (link já descartado,
    /// cancelamento na espera pela vez). Vale para qualquer desfecho da troca: sucesso, timeout, relatório curto, falha de
    /// E/S, cabo removido, cancelamento ou descarte durante a chamada. Com reenvios
    /// (<see cref="ExecuteAsync(byte, byte, int, Bu86xResendPolicy, CancellationToken)"/>), é o maior número de relatórios
    /// de uma mesma resposta: o quadro inteiro no sucesso. Com ≥ 1, o medidor processou o comando (ao menos um dos envios);
    /// com 0, não há como saber. Leia depois que a chamada terminar; com chamadas concorrentes, o valor é o da última
    /// troca, de qualquer chamador, ou 0 se outra chamada acabou de entrar (o lado seguro: 0 nunca faz supor um comando
    /// processado). O descarte de recuperação e os números das exceções não dependem disso: vêm da contagem da própria
    /// troca.
    /// </summary>
    public int LastCommandReportsReceived => Volatile.Read(ref _lastCommandReports);

    /// <summary>
    /// Envios do comando na chamada mais recente: 1, ou 1 + reenvios com uma <see cref="Bu86xResendPolicy"/>; 0 se a
    /// escrita não terminou. Leia depois que a chamada terminar, como <see cref="LastCommandReportsReceived"/>.
    /// </summary>
    public int LastCommandAttempts => Volatile.Read(ref _lastCommandAttempts);

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

    /// <summary>Envia <c>00 52 command</c> e devolve o quadro de <paramref name="reportCount"/> × 9 bytes.</summary>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou dentro de <see cref="Bu86xLinkOptions.ReportTimeout"/>.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou com menos de <see cref="ReportDataLength"/> bytes.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public Task<byte[]> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken = default)
        => ExecuteAsync(DefaultFamily, command, reportCount, _options.ReportTimeout, cancellationToken);

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
    public Task<byte[]> ExecuteAsync(byte command, int reportCount, TimeSpan firstReportTimeout, CancellationToken cancellationToken = default)
        => ExecuteAsync(DefaultFamily, command, reportCount, firstReportTimeout, cancellationToken);

    /// <summary>
    /// Como <see cref="ExecuteAsync(byte, int, CancellationToken)"/>, com o “Command 2” <paramref name="family"/>: envia
    /// <c>00 family command</c> (por exemplo, <c>82 66</c>, o tempo real do BM82x).
    /// </summary>
    /// <param name="family">Código de família (o 2º byte do relatório de saída): 52h ou 82h.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real, 4 na memória.</param>
    /// <param name="cancellationToken">Cancela a troca.</param>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou dentro de <see cref="Bu86xLinkOptions.ReportTimeout"/>.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou com menos de <see cref="ReportDataLength"/> bytes.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public Task<byte[]> ExecuteAsync(byte family, byte command, int reportCount, CancellationToken cancellationToken = default)
        => ExecuteAsync(family, command, reportCount, _options.ReportTimeout, cancellationToken);

    /// <summary>
    /// Como <see cref="ExecuteAsync(byte, int, TimeSpan, CancellationToken)"/>, com o “Command 2”
    /// <paramref name="family"/>: envia <c>00 family command</c> e espera até <paramref name="firstReportTimeout"/> pelo
    /// 1º relatório, sem reenvio.
    /// </summary>
    /// <param name="family">Código de família (o 2º byte do relatório de saída): 52h ou 82h.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real, 4 na memória.</param>
    /// <param name="firstReportTimeout">Positivo, ou <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancela a troca.</param>
    /// <exception cref="Bu86xTimeoutException">Algum relatório não chegou a tempo.</exception>
    /// <exception cref="Bu86xShortReportException">Um relatório chegou com menos de <see cref="ReportDataLength"/> bytes.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public Task<byte[]> ExecuteAsync(byte family, byte command, int reportCount, TimeSpan firstReportTimeout, CancellationToken cancellationToken = default)
        => RunAsync(family, command, reportCount, firstReportTimeout, null, cancellationToken);

    /// <summary>
    /// Envia <c>00 family command</c> e, se o 1º relatório não chegar dentro de
    /// <see cref="Bu86xResendPolicy.FirstReportWindow"/>, envia o mesmo pedido de novo, sem fechar o cabo, até o
    /// <see cref="Bu86xResendPolicy.Budget"/> acabar (§8.5.3 da especificação). Vale o 1º quadro completo de qualquer um
    /// dos envios. Uma resposta incompleta (timeout de um relatório seguinte, relatório curto) é descartada, com o
    /// silêncio de <see cref="Bu86xLinkOptions.RecoveryDrainTimeout"/>, e o pedido também é reenviado. Depois de um
    /// reenvio, o próximo comando começa com o descarte de recuperação (<see cref="Bu86xLinkOptions.RecoveryDrainTimeout"/>),
    /// para que a resposta atrasada de um envio anterior não seja tomada pela dele. É o melhor esforço: o ET-2517A não
    /// responde aos pedidos que descarta (§8.5.2), e a resposta de um envio que ele atendeu chega dentro da janela, mas uma
    /// resposta que chegue depois do silêncio é lida pelo comando seguinte. Uma troca que não termina com o quadro inteiro
    /// (orçamento esgotado, cancelamento, falha de E/S) deixa para o próximo comando, neste link, um silêncio de pelo
    /// menos <see cref="Bu86xResendPolicy.FirstReportWindow"/>, que cobre a latência do medidor. O relógio do orçamento é
    /// <see cref="Bu86xLinkOptions.TimeProvider"/>.
    /// </summary>
    /// <param name="family">Código de família (o 2º byte do relatório de saída): 82h no tempo real do BM82x.</param>
    /// <param name="command">Código do comando (o 3º byte).</param>
    /// <param name="reportCount">Relatórios da resposta: 3 no tempo real.</param>
    /// <param name="resend">A janela do 1º relatório e o orçamento da troca.</param>
    /// <param name="cancellationToken">Cancela a troca.</param>
    /// <exception cref="Bu86xTimeoutException">
    /// O orçamento acabou sem um quadro completo (<see cref="Bu86xTimeoutException.Attempts"/> diz quantos envios saíram).
    /// </exception>
    /// <exception cref="Bu86xShortReportException">A última resposta tinha um relatório curto, e o orçamento acabou.</exception>
    /// <exception cref="HidException">Falha de E/S ou cabo desconectado.</exception>
    /// <exception cref="ObjectDisposedException">O link foi descartado, inclusive durante a chamada.</exception>
    public Task<byte[]> ExecuteAsync(byte family, byte command, int reportCount, Bu86xResendPolicy resend, CancellationToken cancellationToken = default)
        => RunAsync(family, command, reportCount, resend?.FirstReportWindow ?? Timeout.InfiniteTimeSpan, resend ?? throw new ArgumentNullException(nameof(resend)), cancellationToken);

    // Os argumentos são conferidos aqui, dentro do método assíncrono: um argumento inválido volta na tarefa, como antes.
    private async Task<byte[]> RunAsync(byte family, byte command, int reportCount, TimeSpan firstReportTimeout, Bu86xResendPolicy? resend, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reportCount, 1);
        if (firstReportTimeout <= TimeSpan.Zero && firstReportTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(firstReportTimeout), firstReportTimeout, "A espera pelo 1º relatório precisa ser positiva.");
        // Antes de qualquer saída antecipada: sem isso, um link descartado por outra thread logo antes desta chamada
        // deixaria a contagem da troca anterior, e quem lê a memória tomaria por processado um 89 que nem foi escrito.
        Volatile.Write(ref _lastCommandReports, 0);
        Volatile.Write(ref _lastCommandAttempts, 0);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastCommandReports, 0);
            Volatile.Write(ref _lastCommandAttempts, 0);
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var buffer = new byte[Math.Max(Device.InputReportLength, ReportDataLength)];
            TimeSpan quiet = _recoveryPending ? _recoveryQuiet ?? RecoveryQuiet : _options.DrainTimeout;
            await DrainAsync(buffer, quiet, cancellationToken).ConfigureAwait(false);

            // Daqui em diante, qualquer saída antes do último relatório deixa o resto da resposta a caminho. Com reenvios,
            // a resposta de um dos envios ainda pode chegar até a janela do 1º relatório depois do último deles.
            _recoveryPending = true;
            _recoveryQuiet = resend is null ? RecoveryQuiet : MaxTimeout(RecoveryQuiet, resend.FirstReportWindow);
            var counters = new ExchangeCounters();
            byte[] frame = resend is null
                ? await ExchangeOnceAsync(family, command, reportCount, firstReportTimeout, buffer, counters, cancellationToken).ConfigureAwait(false)
                : await ExchangeWithResendAsync(family, command, reportCount, resend, buffer, counters, cancellationToken).ConfigureAwait(false);
            // Depois de um reenvio, a resposta de outro envio ainda pode chegar: o próximo comando faz o descarte de
            // recuperação. Os contadores são os desta troca, e não os campos publicados, que outra chamada pode ter zerado.
            _recoveryPending = counters.Attempts > 1;
            _recoveryQuiet = null;
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

    private TimeSpan RecoveryQuiet => MaxTimeout(_options.DrainTimeout, _options.RecoveryDrainTimeout);

    // Chamado sob _gate: um envio, sem reenvio.
    private async Task<byte[]> ExchangeOnceAsync(byte family, byte command, int reportCount, TimeSpan firstReportTimeout, byte[] buffer,
        ExchangeCounters counters, CancellationToken cancellationToken)
    {
        await SendAsync(family, command, counters, cancellationToken).ConfigureAwait(false);
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
                throw new Bu86xTimeoutException(family, command, i, reportCount, 1, ex);
            }
            StoreReport(buffer, n, i, frame, family, command, reportCount, counters);
        }
        return frame;
    }

    // Chamado sob _gate: o algoritmo de reenvio da §8.5.3, com o mesmo pedido, sem fechar o cabo.
    private async Task<byte[]> ExchangeWithResendAsync(byte family, byte command, int reportCount, Bu86xResendPolicy resend, byte[] buffer,
        ExchangeCounters counters, CancellationToken cancellationToken)
    {
        var time = _options.TimeProvider ?? TimeProvider.System;
        long start = time.GetTimestamp();
        var frame = new byte[reportCount * FrameReportLength];
        Exception? lastFailure = null;
        await SendAsync(family, command, counters, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            TimeSpan remaining = resend.Budget - time.GetElapsedTime(start);
            if (remaining > TimeSpan.Zero)
            {
                TimeSpan window = resend.FirstReportWindow < remaining ? resend.FirstReportWindow : remaining;
                int n = -1;
                try
                {
                    n = await Device.ReadReportAsync(buffer, window, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    // O pedido se perdeu (ou o medidor ainda não respondeu): esperar mais não adianta, reenviar sim (§8.5.2).
                    lastFailure = ex;
                }

                if (n >= 0)
                {
                    try
                    {
                        StoreReport(buffer, n, 0, frame, family, command, reportCount, counters);
                        for (int i = 1; i < reportCount; i++)
                        {
                            try
                            {
                                n = await Device.ReadReportAsync(buffer, _options.ReportTimeout, cancellationToken).ConfigureAwait(false);
                            }
                            catch (TimeoutException ex)
                            {
                                throw new Bu86xTimeoutException(family, command, counters.Reports, reportCount, counters.Attempts, ex);
                            }
                            StoreReport(buffer, n, i, frame, family, command, reportCount, counters);
                        }
                        return frame;
                    }
                    catch (Exception ex) when (ex is Bu86xTimeoutException or Bu86xShortReportException)
                    {
                        // Resposta incompleta: o resto dela (ou a de outro envio) é descartado antes do reenvio.
                        lastFailure = ex;
                        await DrainAsync(buffer, RecoveryQuiet, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (time.GetElapsedTime(start) >= resend.Budget)
            {
                if (lastFailure is Bu86xShortReportException shortReport)
                    throw shortReport;
                throw new Bu86xTimeoutException(family, command, counters.Reports, reportCount, counters.Attempts, lastFailure);
            }
            await SendAsync(family, command, counters, cancellationToken).ConfigureAwait(false);
        }
    }

    // Chamado sob _gate: escreve o relatório de saída, conta o envio na troca e publica a contagem.
    private async Task SendAsync(byte family, byte command, ExchangeCounters counters, CancellationToken cancellationToken)
    {
        await Device.WriteReportAsync(0x00, new byte[] { 0x00, family, command }, cancellationToken).ConfigureAwait(false);
        counters.Attempts++;
        Volatile.Write(ref _lastCommandAttempts, counters.Attempts);
    }

    // Chamado sob _gate: guarda o relatório i (de n bytes) no quadro; um relatório curto encerra a resposta. O maior
    // número de relatórios de uma mesma resposta fica na troca e é publicado.
    private void StoreReport(byte[] buffer, int n, int i, byte[] frame, byte family, byte command, int reportCount, ExchangeCounters counters)
    {
        if (i + 1 > counters.Reports)
            counters.Reports = i + 1;
        Volatile.Write(ref _lastCommandReports, counters.Reports);
        if (n < ReportDataLength)
            throw new Bu86xShortReportException(family, command, i + 1, reportCount, n);
        // frame[i * 9] permanece 00h (report ID).
        buffer.AsSpan(0, ReportDataLength).CopyTo(frame.AsSpan(i * FrameReportLength + 1));
    }

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

    /// <summary>Envios e relatórios de uma troca, só da chamada que a faz (sob _gate).</summary>
    private sealed class ExchangeCounters
    {
        /// <summary>Envios do comando: 1 + reenvios.</summary>
        public int Attempts { get; set; }

        /// <summary>O maior número de relatórios de uma mesma resposta, contando um relatório curto.</summary>
        public int Reports { get; set; }
    }

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
