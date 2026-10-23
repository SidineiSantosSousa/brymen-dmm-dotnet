namespace Brymen.Dmm.Protocol;

/// <summary>Uma linha da Table 6: casas decimais de <c>count</c>, prefixo e unidade.</summary>
internal readonly record struct LoggerRange(int Decimals, string Prefix, string Unit);

/// <summary>Função de um display numa sessão gravada (Table 3) e as suas faixas (Table 6).</summary>
internal sealed class LoggerFunction(string name, MeasurementMode mode, LoggerRange[] ranges, string? temperatureUnit = null)
{
    public string Name { get; } = name;
    public MeasurementMode Mode { get; } = mode;
    public bool IsTemperature => temperatureUnit is not null;

    /// <summary>Faixa pelos range bits; null se a faixa for N/A. Temperatura fica fora da Table 6: sempre 0 casas, como no original.</summary>
    public LoggerRange? GetRange(int range)
    {
        if (temperatureUnit is not null)
            return new LoggerRange(0, "", temperatureUnit);
        return (uint)range < (uint)ranges.Length ? ranges[range] : null;
    }
}

/// <summary>Par (principal, secundário) da Table 3. <see cref="Secondary"/> null = função single.</summary>
internal readonly record struct LoggerFunctionPair(LoggerFunction Main, LoggerFunction? Secondary);

/// <summary>Function Encoder (Table 3), intervalos (Table 3-1) e faixas (Table 6) da §4.7–§4.10.</summary>
internal static class LoggerFunctions
{
    private static readonly LoggerRange[] Volt = [new(3, "", "V"), new(2, "", "V"), new(1, "", "V"), new(0, "", "V")];
    private static readonly LoggerRange[] MilliVolt = [new(2, "m", "V"), new(1, "m", "V")];
    private static readonly LoggerRange[] Hertz =
        [new(3, "", "Hz"), new(2, "", "Hz"), new(1, "", "Hz"), new(3, "k", "Hz"), new(2, "k", "Hz"), new(1, "k", "Hz"), new(3, "M", "Hz")];
    private static readonly LoggerRange[] DutyRanges = [new(2, "", "%"), new(1, "", "%")];
    private static readonly LoggerRange[] Ohms =
        [new(1, "", "Ohm"), new(3, "k", "Ohm"), new(2, "k", "Ohm"), new(1, "k", "Ohm"), new(3, "M", "Ohm"), new(2, "M", "Ohm")];
    private static readonly LoggerRange[] ContinuityRanges = [new(1, "", "Ohm")];
    private static readonly LoggerRange[] NanoSiemens = [new(2, "n", "S")];
    private static readonly LoggerRange[] Farad =
        [new(2, "n", "F"), new(1, "n", "F"), new(3, "u", "F"), new(2, "u", "F"), new(1, "u", "F"), new(3, "m", "F"), new(2, "m", "F")];
    private static readonly LoggerRange[] DiodeRanges = [new(3, "", "V")];
    private static readonly LoggerRange[] Ampere = [new(3, "", "A"), new(2, "", "A")];
    private static readonly LoggerRange[] MilliAmpere = [new(2, "m", "A"), new(1, "m", "A")];
    private static readonly LoggerRange[] MicroAmpere = [new(1, "u", "A"), new(0, "u", "A")];

    private static readonly LoggerFunction Acv = new("ACV", MeasurementMode.AC, Volt);
    private static readonly LoggerFunction Dcv = new("DCV", MeasurementMode.DC, Volt);
    private static readonly LoggerFunction DcAcv = new("DCV+ACV", MeasurementMode.AcDc, Volt);
    private static readonly LoggerFunction AcmV = new("ACmV", MeasurementMode.AC, MilliVolt);
    private static readonly LoggerFunction DcmV = new("DCmV", MeasurementMode.DC, MilliVolt);
    private static readonly LoggerFunction DcAcmV = new("DCmV+ACmV", MeasurementMode.AcDc, MilliVolt);
    private static readonly LoggerFunction Hz = new("Hz", MeasurementMode.None, Hertz);
    private static readonly LoggerFunction Duty = new("Duty", MeasurementMode.None, DutyRanges);
    private static readonly LoggerFunction Ohm = new("OHM", MeasurementMode.None, Ohms);
    private static readonly LoggerFunction Continuity = new("Continuity", MeasurementMode.None, ContinuityRanges);
    private static readonly LoggerFunction NS = new("nS", MeasurementMode.None, NanoSiemens);
    private static readonly LoggerFunction Capacitance = new("Capacitance", MeasurementMode.None, Farad);
    private static readonly LoggerFunction Diode = new("Diode", MeasurementMode.None, DiodeRanges);
    private static readonly LoggerFunction Aca = new("ACA", MeasurementMode.AC, Ampere);
    private static readonly LoggerFunction Dca = new("DCA", MeasurementMode.DC, Ampere);
    private static readonly LoggerFunction DcAca = new("DCA+ACA", MeasurementMode.AcDc, Ampere);
    private static readonly LoggerFunction AcmA = new("ACmA", MeasurementMode.AC, MilliAmpere);
    private static readonly LoggerFunction DcmA = new("DCmA", MeasurementMode.DC, MilliAmpere);
    private static readonly LoggerFunction DcAcmA = new("DCmA+ACmA", MeasurementMode.AcDc, MilliAmpere);
    private static readonly LoggerFunction AcuA = new("ACuA", MeasurementMode.AC, MicroAmpere);
    private static readonly LoggerFunction DcuA = new("DCuA", MeasurementMode.DC, MicroAmpere);
    private static readonly LoggerFunction DcAcuA = new("DCuA+ACuA", MeasurementMode.AcDc, MicroAmpere);

    private static readonly LoggerFunction[] Temperatures = BuildTemperatures();

    // Table 3-1: nibble baixo de Bstatus.
    private static readonly TimeSpan[] Intervals =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(180),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(600),
    ];

    /// <summary>Intervalo de gravação pelo nibble baixo de Bstatus (Table 3-1).</summary>
    public static TimeSpan IntervalOf(byte status) => Intervals[status & 0x0F];

    /// <summary>Table 3. Bstatus bit 5 escolhe A/mA e bit 4 escolhe °F/°C; null = combinação fora da tabela.</summary>
    public static LoggerFunctionPair? Lookup(byte function, byte select, byte status)
    {
        bool ampere = (status & 0x20) != 0;
        int unit = (status & 0x10) != 0 ? 1 : 0;

        return (function, select) switch
        {
            (0x01, 0x00) => new(Acv, Hz),
            (0x01, 0x01) => new(Hz, Acv),
            (0x02, 0x00) => new(Dcv, null),
            (0x02, 0x01) => new(Dcv, Acv),
            (0x02, 0x02) => new(DcAcv, Acv),
            (0x03, 0x00) => new(DcmV, null),
            (0x03, 0x01) => new(DcmV, AcmV),
            (0x03, 0x02) => new(DcAcmV, AcmV),
            (0x03, 0x03) => new(Hz, null),
            (0x03, 0x04) => new(Duty, null),
            (0x04, 0x00) => new(AcmV, Hz),
            (0x04, 0x01) => new(Hz, AcmV),
            (0x05, 0x00) => new(Ohm, null),
            (0x05, 0x01) => new(Continuity, null),
            (0x05, 0x02) => new(NS, null),
            (0x06, 0x00) => new(Temperatures[TemperatureT1 + unit], null),
            (0x06, 0x01) => new(Temperatures[TemperatureT2 + unit], null),
            (0x06, 0x02) => new(Temperatures[TemperatureT1 + unit], Temperatures[TemperatureT2 + unit]),
            (0x06, 0x03) => new(Temperatures[TemperatureT1T2 + unit], Temperatures[TemperatureT2 + unit]),
            (0x07, 0x00) => new(Capacitance, null),
            (0x07, 0x01) => new(Diode, null),
            (0x08, 0x00) => new(ampere ? Dca : DcmA, null),
            (0x08, 0x01) => ampere ? new(Dca, Aca) : new(DcmA, AcmA),
            (0x08, 0x02) => ampere ? new(DcAca, Aca) : new(DcAcmA, AcmA),
            (0x08, 0x03) => new(ampere ? Aca : AcmA, Hz),
            (0x09, 0x00) => new(DcuA, null),
            (0x09, 0x01) => new(DcuA, AcuA),
            (0x09, 0x02) => new(DcAcuA, AcuA),
            (0x09, 0x03) => new(AcuA, Hz),
            _ => null,
        };
    }

    private const int TemperatureT1 = 0;
    private const int TemperatureT2 = 2;
    private const int TemperatureT1T2 = 4;

    // Pares (°C, °F) na ordem T1, T2, T1-T2.
    private static LoggerFunction[] BuildTemperatures()
    {
        var result = new List<LoggerFunction>(6);
        foreach (string name in (ReadOnlySpan<string>)["T1", "T2", "T1-T2"])
        {
            foreach (string unit in (ReadOnlySpan<string>)["°C", "°F"])
                result.Add(new LoggerFunction($"{name}({unit})", MeasurementMode.None, [], unit));
        }
        return [.. result];
    }
}
