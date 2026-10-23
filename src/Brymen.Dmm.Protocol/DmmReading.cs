namespace Brymen.Dmm.Protocol;

/// <summary>Acoplamento indicado no LCD (DC/AC) de um display.</summary>
public enum MeasurementMode
{
    /// <summary>Nem AC nem DC aceso (ex.: Ω, Hz, temperatura).</summary>
    None,

    /// <summary>Anunciador DC.</summary>
    DC,

    /// <summary>Anunciador AC.</summary>
    AC,

    /// <summary>AC e DC acesos ao mesmo tempo (AC+DC).</summary>
    AcDc,
}

/// <summary>Anunciadores globais do LCD (§3.5 da especificação). Nomes em <see cref="DmmFlagNames"/>.</summary>
[Flags]
public enum DmmFlags : uint
{
    /// <summary>Nenhum anunciador aceso.</summary>
    None = 0,

    /// <summary><c>HI</c>: limite alto do comparador do medidor.</summary>
    Hi = 1u << 0,

    /// <summary><c>LO</c>: limite baixo do comparador do medidor.</summary>
    Lo = 1u << 1,

    /// <summary><c>MIN</c>: registro do mínimo.</summary>
    Min = 1u << 2,

    /// <summary><c>MAX</c>: registro do máximo.</summary>
    Max = 1u << 3,

    /// <summary><c>AVG</c>: média.</summary>
    Avg = 1u << 4,

    /// <summary><c>MAX_MIN_DASH</c>: traço entre MAX e MIN (MAX−MIN).</summary>
    MaxMinDash = 1u << 5,

    /// <summary><c>DELTA</c> (Δ): medida relativa.</summary>
    Delta = 1u << 6,

    /// <summary><c>DELTA_PERCENT</c>: o “%” de Δ%.</summary>
    DeltaPercent = 1u << 7,

    /// <summary><c>LOZ</c>: entrada de baixa impedância.</summary>
    LoZ = 1u << 8,

    /// <summary><c>LPF</c>: filtro passa-baixas.</summary>
    Lpf = 1u << 9,

    /// <summary><c>MAIN_T1</c>: T1 no display principal.</summary>
    MainT1 = 1u << 10,

    /// <summary><c>MAIN_T2</c>: T2 no display principal.</summary>
    MainT2 = 1u << 11,

    /// <summary><c>T1_T2_DASH</c>: traço de T1−T2 no display principal.</summary>
    T1T2Dash = 1u << 12,

    /// <summary><c>AT</c>: o símbolo @ (semântica não confirmada).</summary>
    At = 1u << 13,

    /// <summary><c>LOW_BATTERY</c>: bateria do multímetro fraca (W8000 no original).</summary>
    LowBattery = 1u << 14,

    /// <summary><c>SEC_T1</c>: T1 no display secundário.</summary>
    SecondaryT1 = 1u << 15,

    /// <summary><c>SEC_T2</c>: T2 no display secundário.</summary>
    SecondaryT2 = 1u << 16,

    /// <summary><c>CONTINUITY_BEEP</c>: o •))) da continuidade.</summary>
    ContinuityBeep = 1u << 17,

    /// <summary><c>HOLD_H</c>: o [H] (provavelmente Hold).</summary>
    HoldBox = 1u << 18,

    /// <summary><c>C_BOX</c>: o [C] (provavelmente captura de pico).</summary>
    CrestBox = 1u << 19,

    /// <summary><c>R_BOX</c>: o [R] (provavelmente gravação).</summary>
    RecordBox = 1u << 20,

    /// <summary><c>AUTO</c>: faixa automática.</summary>
    Auto = 1u << 21,
}

/// <summary>Um display (principal ou secundário) decodificado.</summary>
public sealed record DisplayReading
{
    /// <summary>Os 4 caracteres crus do LCD, com espaço para dígito apagado (ex.: <c>" 0L "</c>).</summary>
    public required string Digits { get; init; }

    /// <summary>Texto como exibido: sinal + caracteres com pontos, sem espaços nas pontas (ex.: <c>"-12.34"</c>, <c>"0.L"</c>).</summary>
    public required string Text { get; init; }

    /// <summary>Sinal de menos aceso.</summary>
    public bool Negative { get; init; }

    /// <summary>Fora da faixa (<c>OL</c> no LCD).</summary>
    public bool Overload { get; init; }

    /// <summary>Valor na escala do display (ex.: 12.34 para "12.34 mV"); null se overload ou não numérico.</summary>
    public double? Value { get; init; }

    /// <summary>Prefixo: "", "n", "u" (micro), "m", "k" ou "M".</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Unidade sem prefixo: "V", "A", "Ohm", "Hz", "S", "F", "%", "dB", "dBm", "°C", "°F" ou "".</summary>
    public string Unit { get; init; } = "";

    /// <summary>Acoplamento indicado no display (AC, DC, AC+DC ou nenhum).</summary>
    public MeasurementMode Mode { get; init; }

    /// <summary><see cref="Value"/> × 10^prefixo (sem conversão para %, dB/dBm e temperatura).</summary>
    public double? SiValue { get; init; }

    /// <summary>Unidade para exibição (ex.: "mV", "µA", "kΩ", "°C").</summary>
    public string UnitSymbol => (Prefix == "u" ? "µ" : Prefix) + (Unit == "Ohm" ? "Ω" : Unit);
}

/// <summary>Bytes não documentados do quadro de tempo real, guardados para diagnóstico (§3.8).</summary>
/// <param name="Byte2">Byte 2 do quadro.</param>
/// <param name="Byte18">Byte 18 do quadro.</param>
/// <param name="Byte24LowNibble">Nibble baixo do byte 24 (o alto tem [H], [C], [R] e AUTO).</param>
/// <param name="Bytes25To27">Bytes 25 a 27, em hexadecimal maiúsculo.</param>
/// <param name="ModelBytes">Bytes 20 a 23 (Model ID), em hexadecimal maiúsculo: <c>52525252</c> no BM52x e <c>82828282</c> no BM82x.</param>
public sealed record RealTimeDiagnostics(byte Byte2, byte Byte18, byte Byte24LowNibble, string Bytes25To27, string ModelBytes);

/// <summary>Leitura completa de um quadro Cs_RTD.</summary>
public sealed record DmmReading
{
    /// <summary>Instante em que o quadro foi recebido.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Display principal; null se totalmente apagado.</summary>
    public DisplayReading? Main { get; init; }

    /// <summary>Display secundário; null se totalmente apagado.</summary>
    public DisplayReading? Secondary { get; init; }

    /// <summary>Anunciadores globais acesos (nomes estáveis em <see cref="DmmFlagNames"/>).</summary>
    public DmmFlags Flags { get; init; }

    /// <summary>
    /// Família do medidor, pelo Model ID dos bytes 20–23 (<see cref="MeterFamily.Bm52x"/> ou
    /// <see cref="MeterFamily.Bm82x"/>). O decodificador sempre a preenche; o padrão, <see cref="MeterFamily.Bm52x"/>, é o
    /// das leituras de antes da família BM82x.
    /// </summary>
    public MeterFamily Family { get; init; } = MeterFamily.Bm52x;

    /// <summary>Os 27 bytes recebidos (com report IDs), em hexadecimal maiúsculo.</summary>
    public required string RawHex { get; init; }

    /// <summary>Bytes não documentados do quadro, para diagnóstico.</summary>
    public RealTimeDiagnostics? Diagnostics { get; init; }
}
