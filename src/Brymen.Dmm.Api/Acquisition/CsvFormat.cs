using System.Globalization;
using System.Text;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Acquisition;

/// <summary>
/// Formato CSV do "Export → Text/Data Only" do original (relatório de RE, §10.4): cabeçalho exato, separador vírgula,
/// linhas CRLF, <c>#item</c> a partir de 1, <c>Time</c> em <c>%3d:%02d:%02d.%1d</c>, dados como exibidos no display,
/// unidade com <c>Ohm</c> no lugar de Ω e <c>u</c> no lugar de µ, e função <c>DC</c>/<c>AC</c>/<c>DC+AC</c>/<c>T1</c>/<c>T2</c>/<c>T1-T2</c>.
/// </summary>
internal static class CsvFormat
{
    public const string Header = "#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc";
    public const string NewLine = "\r\n";
    public const string ContentType = "text/csv; charset=utf-8";

    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Tempo decorrido como o <c>sprintf("%3d:%02d:%02d.%1d", h, min, s, ms / 100)</c> do original.</summary>
    public static string FormatTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        long hours = (long)elapsed.TotalHours;
        return string.Create(CultureInfo.InvariantCulture, $"{hours,3}:{elapsed.Minutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}");
    }

    public static string FormatNumber(double? value)
        => value is { } v && double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>Texto do display; em overload o '0' do LCD vira 'O' ("O.L", "OL"), como o original.</summary>
    public static string DisplayData(DisplayReading? display)
    {
        if (display is null)
            return "";
        return display.Overload ? display.Text.Replace('0', 'O') : display.Text;
    }

    public static string DisplayUnit(DisplayReading? display)
        => display is null ? "" : Unit(display.Prefix, display.Unit);

    public static string MainFunction(DisplayReading? display, DmmFlags flags)
    {
        if (display is null)
            return "";
        bool t1 = flags.HasFlag(DmmFlags.MainT1), t2 = flags.HasFlag(DmmFlags.MainT2);
        if (t1 && t2 && flags.HasFlag(DmmFlags.T1T2Dash))
            return "T1-T2";
        return t1 ? "T1" : t2 ? "T2" : ModeLabel(display.Mode);
    }

    public static string SecondaryFunction(DisplayReading? display, DmmFlags flags)
    {
        if (display is null)
            return "";
        bool t1 = flags.HasFlag(DmmFlags.SecondaryT1), t2 = flags.HasFlag(DmmFlags.SecondaryT2);
        return t1 && t2 ? "T1-T2" : t1 ? "T1" : t2 ? "T2" : ModeLabel(display.Mode);
    }

    public static string ModeLabel(MeasurementMode mode) => mode switch
    {
        MeasurementMode.AC => "AC",
        MeasurementMode.DC => "DC",
        MeasurementMode.AcDc => "DC+AC",
        _ => "",
    };

    /// <summary>Prefixo + unidade no estilo do original: <c>mV</c>, <c>kOhm</c>, <c>uA</c>; °C/°F viram <c>C</c>/<c>F</c>.</summary>
    public static string Unit(string prefix, string unit)
    {
        string baseUnit = unit switch
        {
            "Ω" => "Ohm",
            "°C" => "C",
            "°F" => "F",
            _ => unit,
        };
        return (prefix == "µ" ? "u" : prefix) + baseUnit;
    }

    public static string Escape(string field)
    {
        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Junta os campos com vírgula, sem vírgula final (7 separadores para as 8 colunas do original).</summary>
    public static string Row(params ReadOnlySpan<string> fields)
    {
        var line = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                line.Append(',');
            line.Append(Escape(fields[i]));
        }
        return line.ToString();
    }
}
