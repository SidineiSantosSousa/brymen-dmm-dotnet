using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Comparator;

/// <summary>Resultado do comparador para uma leitura.</summary>
public enum ComparatorResult
{
    /// <summary>Lo ≤ v ≤ Hi.</summary>
    Pass,

    /// <summary>v &gt; Hi (o original bipa a 1047 Hz).</summary>
    High,

    /// <summary>v &lt; Lo (o original bipa a 523 Hz).</summary>
    Low,
}

/// <summary>
/// Limites do comparador (equivalem a <c>fHiLimit</c>/<c>fLowLimit</c> do <c>BTCDL.ini</c>). Os limites valem para o
/// <c>siValue</c> do display principal: V, A, Ohm, Hz, F e S sem prefixo; %, dB/dBm e °C/°F como exibidos.
/// </summary>
/// <param name="Enabled">Calcula o resultado em cada leitura e amostra.</param>
/// <param name="Alarm">Pede alarme sonoro aos clientes quando o resultado não é <see cref="ComparatorResult.Pass"/> (botão Alarm ON/OFF).</param>
public sealed record ComparatorSettings(bool Enabled, double High, double Low, bool Alarm)
{
    /// <summary>Padrões do <c>BTCDL.ini</c> distribuído (o padrão interno do original, 100/300, é invertido).</summary>
    public static ComparatorSettings Default { get; } = new(Enabled: false, High: 200, Low: 100, Alarm: false);

    public ApiError? Validate()
    {
        if (!double.IsFinite(High) || !double.IsFinite(Low))
            return new ApiError(ErrorCodes.InvalidValue, "Os limites Hi e Lo devem ser números finitos.");
        if (High <= Low)
            return new ApiError(ErrorCodes.HiNotAboveLo, Messages.HiNotAboveLo);
        return null;
    }
}

/// <summary>Comparador Hi/Lo: aplica os limites atuais e os persiste em <c>settings.json</c>.</summary>
internal sealed class ComparatorService(SettingsStore store)
{
    public ComparatorSettings Current => store.Current.Comparator ?? ComparatorSettings.Default;

    /// <summary>Comparações estritas, como o original: <c>v &gt; Hi</c> → High, <c>v &lt; Lo</c> → Low, senão Pass.</summary>
    public static ComparatorResult? Evaluate(ComparatorSettings settings, double? value)
    {
        if (!settings.Enabled || value is not { } v || !double.IsFinite(v))
            return null;
        if (v > settings.High)
            return ComparatorResult.High;
        if (v < settings.Low)
            return ComparatorResult.Low;
        return ComparatorResult.Pass;
    }

    public ComparatorResult? Evaluate(DmmReading reading) => Evaluate(Current, reading.Main?.SiValue);

    /// <summary>
    /// Aplica <paramref name="change"/> aos limites atuais, valida e grava. O valor atual é lido sob o gate da gravação:
    /// num PUT parcial, os campos ausentes vêm do estado mais recente, mesmo com outro PUT concorrente. Devolve os limites
    /// gravados, ou o erro.
    /// </summary>
    public async Task<(ComparatorSettings? Settings, ApiError? Error)> UpdateAsync(
        Func<ComparatorSettings, ComparatorSettings> change, CancellationToken cancellationToken)
    {
        ComparatorSettings? applied = null;
        ApiError? invalid = null;
        try
        {
            await store.UpdateAsync(current =>
            {
                var next = change(current.Comparator ?? ComparatorSettings.Default);
                invalid = next.Validate();
                if (invalid is not null)
                    return null;
                applied = next;
                return current with { Comparator = next };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Os limites novos só passam a valer depois de gravados: o SettingsStore troca o estado após o arquivo.
            return (null, new ApiError(ErrorCodes.Internal, $"Não foi possível gravar {Path.GetFileName(store.FilePath)}: {ex.Message}"));
        }
        return invalid is not null ? (null, invalid) : (applied, null);
    }
}
