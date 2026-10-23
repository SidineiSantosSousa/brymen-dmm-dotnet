using System.Globalization;
using System.Text;
using Brymen.Dmm.Protocol;

/// <summary>Exporta a memória do datalogger em CSV: uma linha por amostra, com vírgula e ponto decimal (formato invariante).</summary>
internal static class CsvExport
{
    private static readonly string[] Header =
    [
        "Sessão", "Amostra", "Tempo (s)", "Função", "Principal", "Valor", "Unidade", "Modo",
        "Função secundária", "Secundário", "Valor secundário", "Unidade secundária", "Modo secundário",
    ];

    /// <summary>Grava <paramref name="memory"/> em <paramref name="path"/> (UTF-8 com BOM, que o Excel reconhece) e devolve o número de amostras.</summary>
    public static int Write(string path, LoggerMemory memory)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(',', Header));
        int samples = 0;
        foreach (var session in memory.Sessions)
        {
            foreach (var sample in session.Samples)
            {
                string[] fields =
                [
                    Invariant(session.Number),
                    Invariant(sample.Index + 1),
                    Invariant(sample.Elapsed.TotalSeconds),
                    session.MainFunction,
                    sample.Main.Text,
                    Invariant(sample.Main.Value),
                    sample.Main.UnitSymbol,
                    ModeName(sample.Main.Mode),
                    session.SecondaryFunction ?? "",
                    sample.Secondary?.Text ?? "",
                    Invariant(sample.Secondary?.Value),
                    sample.Secondary?.UnitSymbol ?? "",
                    sample.Secondary is { } secondary ? ModeName(secondary.Mode) : "",
                ];
                writer.WriteLine(string.Join(',', fields.Select(Escape)));
                samples++;
            }
        }
        return samples;
    }

    /// <summary>O acoplamento como aparece no LCD: AC, DC, AC+DC ou vazio.</summary>
    public static string ModeName(MeasurementMode mode) => mode switch
    {
        MeasurementMode.AC => "AC",
        MeasurementMode.DC => "DC",
        MeasurementMode.AcDc => "AC+DC",
        _ => "",
    };

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    // Aspas só quando preciso (vírgula, aspas ou quebra de linha no campo).
    private static string Escape(string field)
        => field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : field;
}
