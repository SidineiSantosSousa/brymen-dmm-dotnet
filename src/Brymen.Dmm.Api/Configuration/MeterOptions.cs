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

/// <summary>Seção <c>Meter</c> do appsettings.</summary>
public sealed class MeterOptions
{
    public const string SectionName = "Meter";

    public MeterTransport Transport { get; set; } = MeterTransport.Auto;

    /// <summary>Caminho do dispositivo (ex.: <c>/dev/hidraw0</c>); null usa o primeiro VID 0820/PID 0001.</summary>
    public string? DevicePath { get; set; }

    /// <summary>Ativa o link (e o polling) ao iniciar o serviço.</summary>
    public bool AutoLink { get; set; } = true;

    /// <summary>Intervalo entre consultas <c>52 66</c>. O original consulta sem pausa.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Tempo máximo de espera por cada relatório de entrada.</summary>
    public TimeSpan ReportTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Espera antes de tentar reabrir o cabo depois de uma falha.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Latência de cada resposta do transporte Simulated; padrão: a do <c>BrymenMeter.OpenSimulatedAsync</c> (20 ms).</summary>
    public TimeSpan SimulatedLatency { get; set; } = BrymenMeter.SimulatedLatency;
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
        Check(errors, "Meter:PollInterval", options.PollInterval, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Check(errors, "Meter:ReportTimeout", options.ReportTimeout, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
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
