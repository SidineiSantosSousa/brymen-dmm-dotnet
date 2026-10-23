using System.Globalization;
using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Logger;

/// <summary>
/// CSV das amostras do datalogger, com o mesmo cabeçalho do export do original (relatório de RE, §8.3) e os valores de
/// MainData/Sub Data no formato do original (<see cref="Data"/>).
/// </summary>
internal static class LoggerCsv
{
    public const string ExtendedHeader = CsvFormat.Header + ",ElapsedSeconds,MainSiValue,SubSiValue,Flags";

    public static string HeaderFor(bool extended) => extended ? ExtendedHeader : CsvFormat.Header;

    public static string Row(LoggerSession session, LoggerSample sample, bool extended)
    {
        string item = (sample.Index + 1).ToString(CultureInfo.InvariantCulture);
        string time = CsvFormat.FormatTime(sample.Elapsed);
        var main = sample.Main;
        var sub = sample.Secondary;
        string mainData = Data(main), mainUnit = CsvFormat.Unit(main.Prefix, main.Unit), mainFunc = Function(main, session.MainFunction);
        string subData = sub is null ? "" : Data(sub), subUnit = sub is null ? "" : CsvFormat.Unit(sub.Prefix, sub.Unit), subFunc = sub is null ? "" : Function(sub, session.SecondaryFunction);
        if (!extended)
            return CsvFormat.Row(item, time, mainData, mainUnit, mainFunc, subData, subUnit, subFunc);

        List<string> flags = [];
        AddFlags(flags, "MAIN", main);
        if (sub is not null)
            AddFlags(flags, "SUB", sub);
        return CsvFormat.Row(item, time, mainData, mainUnit, mainFunc, subData, subUnit, subFunc,
            sample.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            CsvFormat.FormatNumber(main.SiValue),
            CsvFormat.FormatNumber(sub?.SiValue),
            string.Join('|', flags));
    }

    /// <summary>
    /// Valor como na tela de importação e no CSV do original, que formata a contagem com <c>sprintf("%05.*f")</c>
    /// (<c>"%04.*f"</c> sem casas, <c>"%06.*f"</c> com sinal) e as casas da faixa [BIN 0x431877..0x4319FA]: sempre 4
    /// dígitos, com zeros à esquerda (287 com 2 casas → "02.87", 6 sem casas → "0006", −287 → "-02.87"). Temperatura
    /// usa <c>"%03.0f"</c> [BIN 0x432015..0x43208B] ("025"). Overload e faixa inválida ficam com o
    /// <see cref="LoggerValue.Text"/> ("OL", "----" ou a contagem bruta); o <c>text</c> do JSON segue o LCD, sem zeros à
    /// esquerda.
    /// </summary>
    internal static string Data(LoggerValue value)
    {
        if (value.Overload || !value.IsRangeValid || value.Decimals is not { } decimals)
            return value.Text;
        string sign = value.Negative ? "-" : "";
        if (value.Unit is "°C" or "°F")
            return sign + value.Count.ToString(value.Negative ? "D2" : "D3", CultureInfo.InvariantCulture);
        string digits = value.Count.ToString("D4", CultureInfo.InvariantCulture);
        if (decimals <= 0)
            return sign + digits;
        int point = digits.Length - decimals;
        return string.Concat(sign, digits.AsSpan(0, point), ".", digits.AsSpan(point));
    }

    // Temperatura: T1/T2/T1-T2 pela função da sessão (Table 3); nas demais, o acoplamento AC/DC.
    private static string Function(LoggerValue value, string? sessionFunction)
    {
        if (value.Unit is "°C" or "°F" && sessionFunction is { } f)
        {
            if (f.Contains("T1-T2", StringComparison.Ordinal) || f.Contains("T1−T2", StringComparison.Ordinal))
                return "T1-T2";
            if (f.Contains("T2", StringComparison.Ordinal))
                return "T2";
            if (f.Contains("T1", StringComparison.Ordinal))
                return "T1";
        }
        return CsvFormat.ModeLabel(value.Mode);
    }

    private static void AddFlags(List<string> flags, string display, LoggerValue value)
    {
        if (value.Overload)
            flags.Add(display + "_OVERLOAD");
        if (value.LowBattery)
            flags.Add(display + "_LOW_BATTERY");
    }
}
