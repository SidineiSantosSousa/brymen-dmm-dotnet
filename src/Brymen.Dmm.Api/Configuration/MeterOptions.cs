using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Configuration;

/// <summary>Transporte usado para falar com o cabo BC-86X.</summary>
public enum MeterTransport
{
    /// <summary>HID da plataforma (Linux hidraw ou Windows hid.dll); simulado se a plataforma não tiver suporte.</summary>
    Auto,

    /// <summary>Sempre o HID da plataforma.</summary>
    Hid,

    /// <summary>Medidor simulado a partir das capturas reais de <c>docs/protocol/captures</c>.</summary>
    Simulated,
}

/// <summary>Multímetro reproduzido pelo transporte Simulated (<c>Meter:SimulatedModel</c>).</summary>
public enum SimulatedMeterModel
{
    /// <summary>
    /// Brymen BM525s (= Minipa ET-2615A, família BM52x, Model ID 52h): as capturas de tempo real em ACV + Hz e a memória
    /// do datalogger do dump real; responde a <c>52 66</c> e a <c>82 66</c> e nunca descarta pedidos.
    /// </summary>
    Bm525s,

    /// <summary>
    /// Minipa ET-2517A (família BM82x, Model ID 82h): os 43 quadros da varredura de 2026-10-23, sem datalogger, só com
    /// <c>82 66</c> e com o descarte de pedidos de tempo real medido no hardware (tanto mais quanto maior o intervalo
    /// entre as consultas).
    /// </summary>
    Et2517a,
}

/// <summary>Seção <c>Meter</c> do appsettings.</summary>
public sealed class MeterOptions
{
    public const string SectionName = "Meter";

    public MeterTransport Transport { get; set; } = MeterTransport.Auto;

    /// <summary>Caminho do dispositivo (ex.: <c>/dev/hidraw0</c>); null usa o primeiro VID 0820/PID 0001.</summary>
    public string? DevicePath { get; set; }

    /// <summary>Ativa o link (e o polling) ao iniciar o serviço.</summary>
    public bool AutoLink { get; set; } = true;

    /// <summary>
    /// Família do medidor: <c>Auto</c> (padrão) a detecta pelo Model ID do 1º quadro de tempo real (pedido <c>82 66</c>,
    /// que o BM52x e o BM82x aceitam); <c>Bm52x</c> e <c>Bm82x</c> a fixam (veja <see cref="BrymenMeterOptions.Family"/>).
    /// </summary>
    public MeterFamilySelection Family { get; set; } = MeterFamilySelection.Auto;

    /// <summary>
    /// Intervalo entre o início de duas consultas de tempo real (<c>52 66</c> no BM52x, <c>82 66</c> no BM82x). O
    /// original consulta sem pausa; no ET-2517A, um intervalo menor perde menos pedidos.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Tempo máximo de espera por cada relatório de entrada.</summary>
    public TimeSpan ReportTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Espera pelo 1º relatório de cada envio do tempo real do BM82x (e da detecção da família) antes de reenviar o mesmo
    /// pedido, sem fechar o cabo (<see cref="BrymenMeterOptions.RealTimeResendTimeout"/>). Padrão: 300 ms.
    /// </summary>
    public TimeSpan RealTimeResendTimeout { get; set; } = MeterConnection.DefaultRealTimeResendTimeout;

    /// <summary>
    /// Tempo total de uma consulta de tempo real com reenvios; só quando ele acaba sem resposta a consulta é uma falha
    /// (W7012) do polling (<see cref="BrymenMeterOptions.RealTimeResendBudget"/>). Padrão: 2 s.
    /// </summary>
    public TimeSpan RealTimeResendBudget { get; set; } = MeterConnection.DefaultRealTimeResendBudget;

    /// <summary>Espera antes de tentar reabrir o cabo depois de uma falha.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Latência de cada resposta do transporte Simulated; padrão: a do <c>BrymenMeter.OpenSimulatedAsync</c> (20 ms).</summary>
    public TimeSpan SimulatedLatency { get; set; } = BrymenMeter.SimulatedLatency;

    /// <summary>Multímetro do transporte Simulated: <c>Bm525s</c> (padrão) ou <c>Et2517a</c>, com o descarte de pedidos.</summary>
    public SimulatedMeterModel SimulatedModel { get; set; } = SimulatedMeterModel.Bm525s;
}

/// <summary>Validação de <see cref="MeterOptions"/> na partida (sem reflexão, compatível com AOT).</summary>
internal sealed class MeterOptionsValidator : IValidateOptions<MeterOptions>
{
    public ValidateOptionsResult Validate(string? name, MeterOptions options)
    {
        List<string> errors = [];
        if (!Enum.IsDefined(options.Transport))
            errors.Add("Meter:Transport deve ser Auto, Hid ou Simulated.");
        if (options.DevicePath is { } path && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            errors.Add($"Meter:DevicePath não é um caminho válido: '{path}'.");
        if (!Enum.IsDefined(options.Family))
            errors.Add("Meter:Family deve ser Auto, Bm52x ou Bm82x.");
        if (!Enum.IsDefined(options.SimulatedModel))
            errors.Add("Meter:SimulatedModel deve ser Bm525s ou Et2517a.");
        Check(errors, "Meter:PollInterval", options.PollInterval, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Check(errors, "Meter:ReportTimeout", options.ReportTimeout, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
        Check(errors, "Meter:RealTimeResendTimeout", options.RealTimeResendTimeout, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
        Check(errors, "Meter:RealTimeResendBudget", options.RealTimeResendBudget, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1));
        Check(errors, "Meter:ReconnectDelay", options.ReconnectDelay, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        Check(errors, "Meter:SimulatedLatency", options.SimulatedLatency, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(List<string> errors, string key, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
            errors.Add($"{key} deve estar entre {min:c} e {max:c} (valor: {value:c}).");
    }
}
