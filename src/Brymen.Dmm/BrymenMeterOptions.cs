using System.Globalization;
using Brymen.Dmm.Hid;

namespace Brymen.Dmm;

/// <summary>
/// Opções do <see cref="BrymenMeter"/> e do <see cref="MeterConnection"/>. Os valores são copiados na abertura: mudar a
/// instância depois não afeta um medidor já aberto.
/// </summary>
public sealed class BrymenMeterOptions
{
    /// <summary>
    /// Caminho do cabo (ex.: <c>/dev/hidraw0</c>, <c>/dev/brymen-bu86x</c> ou <c>\\?\hid#vid_0820&amp;pid_0001...</c>);
    /// <see langword="null"/> (padrão) usa o primeiro VID 0820h/PID 0001h da enumeração.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// Família do medidor: <see cref="MeterFamilySelection.Auto"/> (padrão) a detecta pelo Model ID do 1º quadro de tempo
    /// real depois da abertura (o pedido é o <c>82 66</c>, que as duas famílias aceitam); <see cref="MeterFamilySelection.Bm52x"/>
    /// e <see cref="MeterFamilySelection.Bm82x"/> a fixam, e um quadro com o Model ID da outra família vira
    /// <see cref="BrymenErrorCodes.InvalidFrame"/>.
    /// </summary>
    public MeterFamilySelection Family { get; set; } = MeterFamilySelection.Auto;

    /// <summary>
    /// Intervalo entre o início de duas consultas de tempo real na leitura contínua (<see cref="BrymenMeter.Start"/> e
    /// <see cref="BrymenMeter.ReadContinuouslyAsync"/> sem intervalo). Padrão: 200 ms, cerca de 5 leituras/s;
    /// <see cref="TimeSpan.Zero"/> consulta sem pausa, como o software original. No BM82x, um intervalo menor perde menos
    /// pedidos (§8.5.5 da especificação).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Tempo máximo de espera por cada relatório de entrada. Padrão: 1 s.</summary>
    public TimeSpan ReportTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Prazo da espera longa do 1º <c>66</c> depois da abertura e depois de comandos de memória, quando maior que
    /// <see cref="ReportTimeout"/>. Padrão: <see cref="MeterConnection.DefaultRealTimeAfterMemoryTimeout"/> (3 s): no
    /// BM525s real, o <c>66</c> seguinte à leitura do último bloco da memória leva ≈ 2,06 s. Com a família BM52x
    /// conhecida (ou depois de um comando de memória respondido), é a espera pelo 1º relatório de um envio só; com a
    /// família desconhecida (a detecção) ou BM82x, é o prazo dos reenvios de <see cref="RealTimeResendTimeout"/> (ou
    /// <see cref="RealTimeResendBudget"/>, se maior), porque o ET-2517A pode descartar esse pedido (§8.5.4).
    /// </summary>
    public TimeSpan RealTimeAfterMemoryTimeout { get; set; } = MeterConnection.DefaultRealTimeAfterMemoryTimeout;

    /// <summary>
    /// Espera pelo 1º relatório de cada envio de uma consulta de tempo real do BM82x (e da detecção da família) antes de
    /// reenviar o mesmo pedido, sem fechar o cabo: o ET-2517A descarta parte dos pedidos, e esperar mais não adianta.
    /// Padrão: <see cref="MeterConnection.DefaultRealTimeResendTimeout"/> (300 ms), acima das latências normais (36 a
    /// 237 ms). Vale também na espera longa do 1º <c>66</c> depois da abertura e da memória, até o prazo de
    /// <see cref="RealTimeAfterMemoryTimeout"/>, menos com a família BM52x conhecida (ou depois de um comando de memória
    /// respondido), em que a consulta é um envio só: o BM52x nunca descartou pedidos.
    /// </summary>
    public TimeSpan RealTimeResendTimeout { get; set; } = MeterConnection.DefaultRealTimeResendTimeout;

    /// <summary>
    /// Tempo total de uma consulta de tempo real com reenvios (<see cref="RealTimeResendTimeout"/>); só quando ele acaba
    /// sem resposta a consulta falha com <see cref="BrymenErrorCodes.NoDataReceived"/>. Padrão:
    /// <see cref="MeterConnection.DefaultRealTimeResendBudget"/> (2 s).
    /// </summary>
    public TimeSpan RealTimeResendBudget { get; set; } = MeterConnection.DefaultRealTimeResendBudget;

    /// <summary>Espera antes de reabrir o cabo depois de uma falha persistente, na reconexão automática. Padrão: 2 s.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Na leitura contínua, reabre o cabo depois de <see cref="ReconnectDelay"/> em vez de terminar com a falha (cabo
    /// removido, medidor desligado). Padrão: <see langword="true"/>.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Silêncio usado para descartar relatórios atrasados antes de cada comando (<see cref="Bu86xLinkOptions.DrainTimeout"/>).
    /// Padrão: 5 ms.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Silêncio exigido no descarte depois de abrir o cabo e depois de uma troca interrompida ou com reenvios
    /// (<see cref="Bu86xLinkOptions.RecoveryDrainTimeout"/>). Padrão: 100 ms. Depois de uma consulta com reenvios que não
    /// terminou com o quadro inteiro (cancelada ou com o orçamento esgotado), o próximo comando no mesmo cabo aberto exige
    /// pelo menos <see cref="RealTimeResendTimeout"/> de silêncio, que cobre a latência do ET-2517A.
    /// </summary>
    public TimeSpan RecoveryDrainTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Transporte HID; <see langword="null"/> (padrão) usa <see cref="HidDeviceProviders.CreateForCurrentPlatform"/>
    /// (hidraw no Linux, hid.dll no Windows). Use um <see cref="SimulatedHidDeviceProvider"/> para testes sem hardware.
    /// </summary>
    public IHidDeviceProvider? DeviceProvider { get; set; }

    /// <summary>
    /// Relógio dos intervalos, das esperas e dos instantes das leituras. Padrão: <see cref="TimeProvider.System"/>. Os
    /// timeouts de cada relatório ficam a cargo do dispositivo HID (o <see cref="SimulatedHidDevice"/> aceita um relógio).
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Opções da leitura da memória do datalogger.</summary>
    public LoggerReadOptions LoggerRead { get; set; } = new();

    /// <summary>Cópia validada, usada pelo medidor aberto.</summary>
    /// <exception cref="ArgumentException">Algum valor está fora da faixa aceita.</exception>
    internal BrymenMeterOptions ValidatedCopy()
    {
        RequireAtLeast(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        RequirePositive(ReportTimeout, nameof(ReportTimeout));
        RequirePositive(RealTimeAfterMemoryTimeout, nameof(RealTimeAfterMemoryTimeout));
        RequirePositive(RealTimeResendTimeout, nameof(RealTimeResendTimeout));
        RequirePositive(RealTimeResendBudget, nameof(RealTimeResendBudget));
        if (!Enum.IsDefined(Family))
            throw new ArgumentOutOfRangeException("options", Family, $"{nameof(BrymenMeterOptions)}.{nameof(Family)} inválida.");
        RequireAtLeast(ReconnectDelay, TimeSpan.Zero, nameof(ReconnectDelay));
        RequireAtLeast(DrainTimeout, TimeSpan.Zero, nameof(DrainTimeout));
        RequireAtLeast(RecoveryDrainTimeout, TimeSpan.Zero, nameof(RecoveryDrainTimeout));
        if (TimeProvider is null)
            throw new ArgumentException($"{nameof(BrymenMeterOptions)}.{nameof(TimeProvider)} não pode ser nulo.", "options");
        if (LoggerRead is null)
            throw new ArgumentException($"{nameof(BrymenMeterOptions)}.{nameof(LoggerRead)} não pode ser nulo.", "options");

        return new BrymenMeterOptions
        {
            DevicePath = string.IsNullOrWhiteSpace(DevicePath) ? null : DevicePath,
            Family = Family,
            PollInterval = PollInterval,
            ReportTimeout = ReportTimeout,
            RealTimeAfterMemoryTimeout = RealTimeAfterMemoryTimeout,
            RealTimeResendTimeout = RealTimeResendTimeout,
            RealTimeResendBudget = RealTimeResendBudget,
            ReconnectDelay = ReconnectDelay,
            AutoReconnect = AutoReconnect,
            DrainTimeout = DrainTimeout,
            RecoveryDrainTimeout = RecoveryDrainTimeout,
            DeviceProvider = DeviceProvider,
            TimeProvider = TimeProvider,
            LoggerRead = LoggerRead.ValidatedCopy(),
        };
    }

    internal Bu86xLinkOptions ToLinkOptions() => new()
    {
        ReportTimeout = ReportTimeout,
        DrainTimeout = DrainTimeout,
        RecoveryDrainTimeout = RecoveryDrainTimeout,
        TimeProvider = TimeProvider,
    };

    internal static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException("options", value, string.Create(CultureInfo.InvariantCulture, $"{name} precisa ser positivo."));
    }

    internal static void RequireAtLeast(TimeSpan value, TimeSpan min, string name)
    {
        if (value < min)
            throw new ArgumentOutOfRangeException("options", value, string.Create(CultureInfo.InvariantCulture, $"{name} não pode ser menor que {min:c}."));
    }
}

/// <summary>
/// Opções da leitura da memória do datalogger (<see cref="LoggerMemoryReader"/>). Os padrões seguem o software original
/// e os testes no BM525s real (docs/protocol/bm52x-usb-protocol.md §2.7 e §4.11).
/// </summary>
public sealed class LoggerReadOptions
{
    /// <summary>
    /// Confere o modelo antes de ler a memória, como o original: uma consulta de tempo real (até 2 tentativas) com o
    /// Model ID (bytes 20–23) igual a <c>52h</c>; com <c>82h</c> (BM82x), <see cref="BrymenErrorCodes.DataLoggerNotSupported"/>;
    /// senão, <see cref="BrymenErrorCodes.NoDataLogger"/>. Pelo <see cref="MeterConnection"/>, a consulta segue a família da
    /// conexão (o <c>82 66</c> da detecção, se ela ainda não é conhecida). Padrão: <see langword="true"/>.
    /// </summary>
    public bool VerifyModel { get; set; } = true;

    /// <summary>
    /// Espera depois da resposta do <c>88</c>, antes do próximo comando (o original faz <c>Sleep(200)</c>). No medidor
    /// real, um <c>8A</c> cerca de 15 ms depois do <c>88</c> avançou o ponteiro como um <c>89</c> em 9 de 16 tentativas.
    /// Padrão: 200 ms.
    /// </summary>
    public TimeSpan HeadSettleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Espera depois de reabrir o cabo, antes de uma nova tentativa (o original usa <c>Sleep(10)</c>). Padrão: 10 ms.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Tentativas do <c>88</c>, sempre com outro <c>88</c> (nunca com <c>8A</c>). Padrão: 3.</summary>
    public int MaxHeadAttempts { get; set; } = 3;

    /// <summary>Tentativas por bloco: 1 × <c>89</c> + até N − 1 × <c>8A</c>, como o original. Padrão: 4.</summary>
    public int MaxBlockAttempts { get; set; } = 4;

    /// <summary>Recomeços a partir do <c>88</c> antes de desistir com <see cref="BrymenErrorCodes.TransmissionTimeout"/>. Padrão: 3.</summary>
    public int MaxRestarts { get; set; } = 3;

    internal LoggerReadOptions ValidatedCopy()
    {
        BrymenMeterOptions.RequireAtLeast(HeadSettleDelay, TimeSpan.Zero, nameof(HeadSettleDelay));
        BrymenMeterOptions.RequireAtLeast(RetryDelay, TimeSpan.Zero, nameof(RetryDelay));
        if (MaxHeadAttempts < 1)
            throw new ArgumentOutOfRangeException("options", MaxHeadAttempts, $"{nameof(MaxHeadAttempts)} precisa ser ao menos 1.");
        if (MaxBlockAttempts < 1)
            throw new ArgumentOutOfRangeException("options", MaxBlockAttempts, $"{nameof(MaxBlockAttempts)} precisa ser ao menos 1.");
        if (MaxRestarts < 0)
            throw new ArgumentOutOfRangeException("options", MaxRestarts, $"{nameof(MaxRestarts)} não pode ser negativo.");
        return new LoggerReadOptions
        {
            VerifyModel = VerifyModel,
            HeadSettleDelay = HeadSettleDelay,
            RetryDelay = RetryDelay,
            MaxHeadAttempts = MaxHeadAttempts,
            MaxBlockAttempts = MaxBlockAttempts,
            MaxRestarts = MaxRestarts,
        };
    }
}
