using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Brymen.Dmm.Protocol;

/// <summary>Decodificador do quadro Cs_RTD de 27 bytes (§3 da especificação), das famílias BM52x e BM82x (§8).</summary>
/// <remarks>
/// <para>
/// Os números de byte nos comentários são 1-based e incluem os report IDs (bytes 1, 10 e 19), como no PDF corrigido.
/// Ao contrário do original, quadros com "@" aceso (<c>b9.4</c>) não são rejeitados: a flag <see cref="DmmFlags.At"/> é exposta.
/// </para>
/// <para>
/// As duas famílias usam o mesmo mapa (§8.3): o Model ID dos bytes 20–23 precisa ter os quatro bytes iguais, com
/// <c>52h</c> (<see cref="MeterFamily.Bm52x"/>) ou <c>82h</c> (<see cref="MeterFamily.Bm82x"/>), e vai em
/// <see cref="DmmReading.Family"/>.
/// </para>
/// </remarks>
public static class Bm52xRealtime
{
    private static readonly int[] ReportIdPositions = [1, 10, 19];

    // Anunciadores globais (§3.5): byte 1-based, bit e flag.
    private static readonly (int Byte, int Bit, DmmFlags Flag)[] FlagBits =
    [
        (3, 7, DmmFlags.Hi),
        (3, 6, DmmFlags.Lo),
        (3, 3, DmmFlags.Min),
        (3, 2, DmmFlags.MaxMinDash),
        (3, 1, DmmFlags.Avg),
        (3, 0, DmmFlags.Max),
        (4, 6, DmmFlags.Delta),
        (4, 5, DmmFlags.DeltaPercent),
        (4, 4, DmmFlags.LoZ),
        (4, 3, DmmFlags.MainT2),
        (4, 2, DmmFlags.Lpf),
        (4, 1, DmmFlags.T1T2Dash),
        (4, 0, DmmFlags.MainT1),
        (9, 4, DmmFlags.At),
        (9, 3, DmmFlags.LowBattery),
        (9, 2, DmmFlags.SecondaryT1),
        (9, 1, DmmFlags.SecondaryT2),
        (9, 0, DmmFlags.ContinuityBeep),
        (24, 7, DmmFlags.HoldBox),
        (24, 6, DmmFlags.CrestBox),
        (24, 5, DmmFlags.RecordBox),
        (24, 4, DmmFlags.Auto),
    ];

    /// <summary>Decodifica o quadro; lança <see cref="Bm52xFrameException"/> se ele for inválido.</summary>
    public static DmmReading Decode(ReadOnlySpan<byte> frame, DateTimeOffset timestamp)
        => TryDecode(frame, timestamp, out var reading, out var error) ? reading : throw new Bm52xFrameException(error);

    /// <summary>
    /// Decodifica o quadro e confere a família; lança <see cref="Bm52xFrameException"/> se ele for inválido ou se o Model ID
    /// for o de outra família.
    /// </summary>
    /// <param name="frame">Os 27 bytes, com os report IDs.</param>
    /// <param name="timestamp">Instante da leitura.</param>
    /// <param name="expectedFamily">A família esperada (a da conexão).</param>
    public static DmmReading Decode(ReadOnlySpan<byte> frame, DateTimeOffset timestamp, MeterFamily expectedFamily)
        => TryDecode(frame, timestamp, expectedFamily, out var reading, out var error) ? reading : throw new Bm52xFrameException(error);

    /// <summary>Decodifica o quadro sem lançar exceção; em caso de quadro inválido, <paramref name="error"/> diz o motivo.</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        DateTimeOffset timestamp,
        [NotNullWhen(true)] out DmmReading? reading,
        [NotNullWhen(false)] out string? error)
        => TryDecodeCore(frame, timestamp, null, out reading, out error);

    /// <summary>
    /// Como <see cref="TryDecode(ReadOnlySpan{byte}, DateTimeOffset, out DmmReading?, out string?)"/>, e recusa também um
    /// quadro válido com o Model ID de outra família (a troca do medidor no mesmo cabo, §8.3).
    /// </summary>
    /// <param name="frame">Os 27 bytes, com os report IDs.</param>
    /// <param name="timestamp">Instante da leitura.</param>
    /// <param name="expectedFamily">A família esperada (a da conexão).</param>
    /// <param name="reading">A leitura, quando o retorno é <see langword="true"/>.</param>
    /// <param name="error">O motivo da recusa, quando o retorno é <see langword="false"/>.</param>
    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        DateTimeOffset timestamp,
        MeterFamily expectedFamily,
        [NotNullWhen(true)] out DmmReading? reading,
        [NotNullWhen(false)] out string? error)
        => TryDecodeCore(frame, timestamp, expectedFamily, out reading, out error);

    /// <summary>
    /// A família do quadro pelo Model ID (bytes 20–23), sem decodificar o resto: os quatro bytes iguais, com <c>52h</c> ou
    /// <c>82h</c>. Não confere os report IDs.
    /// </summary>
    /// <param name="frame">Os 27 bytes, com os report IDs.</param>
    /// <param name="family">A família, quando o retorno é <see langword="true"/>.</param>
    public static bool TryGetFamily(ReadOnlySpan<byte> frame, out MeterFamily family)
    {
        family = default;
        if (frame.Length != Bm52xCommands.RealTimeFrameLength)
            return false;
        var model = frame.Slice(19, 4);
        return model.IndexOfAnyExcept(model[0]) < 0 && MeterFamilies.TryFromModelId(model[0], out family);
    }

    private static bool TryDecodeCore(
        ReadOnlySpan<byte> frame,
        DateTimeOffset timestamp,
        MeterFamily? expectedFamily,
        [NotNullWhen(true)] out DmmReading? reading,
        [NotNullWhen(false)] out string? error)
    {
        reading = null;
        error = ValidateFrame(frame, expectedFamily);
        if (error is not null)
            return false;

        var main = DecodeMainAnnunciators(frame, out error);
        if (error is not null)
            return false;
        var secondary = DecodeSecondaryAnnunciators(frame, out error);
        if (error is not null)
            return false;

        reading = new DmmReading
        {
            Timestamp = timestamp,
            Main = BuildDisplay(frame.Slice(4, 4), main),
            Secondary = BuildDisplay(frame.Slice(10, 4), secondary),
            Flags = DecodeFlags(frame),
            Family = (MeterFamily)At(frame, 20),
            RawHex = Convert.ToHexString(frame),
            Diagnostics = new RealTimeDiagnostics(
                Byte2: At(frame, 2),
                Byte18: At(frame, 18),
                Byte24LowNibble: (byte)(At(frame, 24) & 0x0F),
                Bytes25To27: Convert.ToHexString(frame.Slice(24, 3)),
                ModelBytes: Convert.ToHexString(frame.Slice(19, 4))),
        };
        return true;
    }

    /// <summary>Caractere do LCD para um byte de dígito, pela tabela do original (§3.4). O bit 4 (ponto) é ignorado; '?' = inválido.</summary>
    public static char DecodeSegments(byte value) => SevenSegment.ToChar(value);

    private static string? ValidateFrame(ReadOnlySpan<byte> frame, MeterFamily? expectedFamily)
    {
        if (frame.Length != Bm52xCommands.RealTimeFrameLength)
            return Invariant($"comprimento {frame.Length}, esperado {Bm52xCommands.RealTimeFrameLength} bytes");

        // A lista só é criada se houver erro: o caminho normal (polling contínuo) não aloca nada aqui.
        List<string>? errors = null;
        foreach (int n in ReportIdPositions)
        {
            if (At(frame, n) != 0)
                (errors ??= []).Add(Invariant($"byte {n} (report ID) = {At(frame, n):X2}, esperado 00"));
        }

        if (ValidateModel(frame.Slice(19, 4), expectedFamily) is { } modelError)
            (errors ??= []).Add(modelError);

        return errors is null ? null : string.Join("; ", errors);
    }

    // Os quatro bytes iguais, com o código de uma das famílias (§8.3). O "esperado" da mensagem é o da família que o
    // quadro parece ter (a maioria dos bytes), ou as duas quando não dá para dizer.
    private static string? ValidateModel(ReadOnlySpan<byte> model, MeterFamily? expectedFamily)
    {
        bool uniform = model.IndexOfAnyExcept(model[0]) < 0;
        if (uniform && MeterFamilies.TryFromModelId(model[0], out var family))
        {
            if (expectedFamily is not { } expected || expected == family)
                return null;
            return Invariant($"bytes 20..23 = {Convert.ToHexString(model)} ({family.Name()}), esperado {ModelBytes(expected)} ({expected.Name()})");
        }

        int bm52x = model.Count(Bm52xCommands.Bm52xFamily), bm82x = model.Count(Bm52xCommands.Bm82xFamily);
        string wanted = expectedFamily is { } e ? ModelBytes(e)
            : bm52x > bm82x ? ModelBytes(MeterFamily.Bm52x)
            : bm82x > bm52x ? ModelBytes(MeterFamily.Bm82x)
            : $"{ModelBytes(MeterFamily.Bm52x)} ({MeterFamily.Bm52x.Name()}) ou {ModelBytes(MeterFamily.Bm82x)} ({MeterFamily.Bm82x.Name()})";
        return $"bytes 20..23 = {Convert.ToHexString(model)}, esperado {wanted}";
    }

    private static string ModelBytes(MeterFamily family) => family == MeterFamily.Bm52x ? "52525252" : "82828282";

    private static DisplayAnnunciators DecodeMainAnnunciators(ReadOnlySpan<byte> frame, out string? error)
    {
        byte b3 = At(frame, 3), b4 = At(frame, 4), b8 = At(frame, 8), b16 = At(frame, 16), b17 = At(frame, 17);
        bool micro = Bit(b17, 7), milli = Bit(b17, 6), dB = Bit(b8, 4);

        // Com dB aceso, o "m" de b17.6 forma "dBm" e não é prefixo (§3.5).
        error = PickPrefix("principal", 17, b17, micro, milli, out string prefix,
            (micro, "u"), (milli && !dB, "m"), (Bit(b17, 3), "n"), (Bit(b16, 7), "k"), (Bit(b16, 6), "M"));

        string unit =
            dB ? (milli ? "dBm" : "dB")
            : Bit(b16, 4) ? "Hz"
            : Bit(b16, 5) ? "Ohm"
            : Bit(b17, 0) ? "F"
            : Bit(b17, 1) ? "S"
            : Bit(b17, 2) ? "%"
            : Bit(b17, 4) ? "A"
            : Bit(b17, 5) ? "V"
            : "";

        return new DisplayAnnunciators(Bit(b4, 7), ModeOf(ac: Bit(b3, 4), dc: Bit(b3, 5)), prefix, unit);
    }

    private static DisplayAnnunciators DecodeSecondaryAnnunciators(ReadOnlySpan<byte> frame, out string? error)
    {
        byte b9 = At(frame, 9), b14 = At(frame, 14), b15 = At(frame, 15), b16 = At(frame, 16);
        bool milli = Bit(b15, 7), micro = Bit(b15, 6);

        error = PickPrefix("secundário", 15, b15, micro, milli, out string prefix,
            (milli, "m"), (micro, "u"), (Bit(b15, 2), "n"), (Bit(b16, 3), "M"), (Bit(b16, 2), "k"));

        // "%4~20mA" (b14.4) é um único anunciador; a unidade exposta é "%".
        string unit =
            Bit(b14, 4) ? "%"
            : Bit(b16, 0) ? "Hz"
            : Bit(b16, 1) ? "Ohm"
            : Bit(b15, 0) ? "F"
            : Bit(b15, 1) ? "S"
            : Bit(b15, 3) ? "%"
            : Bit(b15, 4) ? "V"
            : Bit(b15, 5) ? "A"
            : "";

        return new DisplayAnnunciators(Bit(b9, 5), ModeOf(ac: Bit(b9, 6), dc: Bit(b9, 7)), prefix, unit);
    }

    private static string? PickPrefix(
        string display,
        int byteNumber,
        byte prefixByte,
        bool micro,
        bool milli,
        out string prefix,
        params ReadOnlySpan<(bool Lit, string Prefix)> candidates)
    {
        prefix = "";
        if (micro && milli)
            return Invariant($"prefixos m e µ acesos juntos no display {display} (byte {byteNumber} = {prefixByte:X2})");

        int lit = 0;
        foreach (var (isLit, candidate) in candidates)
        {
            if (!isLit)
                continue;
            lit++;
            prefix = candidate;
        }

        if (lit <= 1)
            return null;
        prefix = "";
        return Invariant($"{lit} prefixos acesos juntos no display {display}");
    }

    private static DisplayReading? BuildDisplay(ReadOnlySpan<byte> digitBytes, DisplayAnnunciators annunciators)
    {
        Span<char> chars = stackalloc char[4];
        for (int k = 0; k < chars.Length; k++)
            chars[k] = SevenSegment.ToChar(digitBytes[k]);

        if (chars.IndexOfAnyExcept(' ') < 0)
            return null;

        string prefix = annunciators.Prefix;
        string unit = annunciators.Unit;

        // Temperatura: o 4º dígito é 'C' ou 'F' e o número são os 3 primeiros (§3.4).
        bool temperature = chars[3] is 'C' or 'F';
        if (temperature)
        {
            unit = chars[3] == 'C' ? "°C" : "°F";
            prefix = "";
        }
        int numericLength = temperature ? 3 : 4;

        // O bit 4 do dígito k acende o ponto à direita dele; o do 4º dígito é outro anunciador (dB, %4~20mA).
        // 4 caracteres + até 3 pontos, montados na pilha: só Digits e Text viram strings.
        Span<char> text = stackalloc char[7];
        Span<char> number = stackalloc char[7];
        int textLength = 0, numberLength = 0;
        for (int k = 0; k < chars.Length; k++)
        {
            bool point = k < 3 && SevenSegment.HasDecimalPoint(digitBytes[k]);
            text[textLength++] = chars[k];
            if (point)
                text[textLength++] = '.';

            if (k < numericLength)
            {
                if (chars[k] != ' ')
                    number[numberLength++] = chars[k];
                if (point)
                    number[numberLength++] = '.';
            }
        }

        var numeric = chars[..numericLength];
        bool overload = numeric.Contains('L') && numeric.IndexOfAnyExcept(' ', '0', 'L') < 0;

        decimal? value = null;
        if (!overload
            && IsDigitsOnly(numeric)
            && decimal.TryParse(number[..numberLength].TrimEnd('.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed))
        {
            value = annunciators.Negative ? -parsed : parsed;
        }

        ReadOnlySpan<char> body = text[..textLength].Trim(' ');
        return new DisplayReading
        {
            Digits = new string(chars),
            Text = annunciators.Negative ? string.Concat("-", body) : new string(body),
            Negative = annunciators.Negative,
            Overload = overload,
            Value = value is decimal v ? SiScale.ToDouble(v) : null,
            Prefix = prefix,
            Unit = unit,
            Mode = annunciators.Mode,
            SiValue = value is decimal s ? SiScale.ToDouble(SiScale.ToSi(s, prefix, unit)) : null,
        };
    }

    private static bool IsDigitsOnly(ReadOnlySpan<char> chars)
    {
        bool any = false;
        foreach (char c in chars)
        {
            if (c == ' ')
                continue;
            if (!char.IsAsciiDigit(c))
                return false;
            any = true;
        }
        return any;
    }

    private static DmmFlags DecodeFlags(ReadOnlySpan<byte> frame)
    {
        var flags = DmmFlags.None;
        foreach (var (byteNumber, bit, flag) in FlagBits)
        {
            if (Bit(At(frame, byteNumber), bit))
                flags |= flag;
        }
        return flags;
    }

    private static MeasurementMode ModeOf(bool ac, bool dc) => (ac, dc) switch
    {
        (true, true) => MeasurementMode.AcDc,
        (true, false) => MeasurementMode.AC,
        (false, true) => MeasurementMode.DC,
        _ => MeasurementMode.None,
    };

    private static byte At(ReadOnlySpan<byte> frame, int byteNumber) => frame[byteNumber - 1];

    private static bool Bit(byte value, int bit) => (value & (1 << bit)) != 0;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private readonly record struct DisplayAnnunciators(bool Negative, MeasurementMode Mode, string Prefix, string Unit);
}

/// <summary>Nomes estáveis dos anunciadores (iguais aos de <c>docs/protocol/test-vectors.json</c>, ex.: "AUTO", "LOW_BATTERY").</summary>
public static class DmmFlagNames
{
    // Na ordem dos bits de DmmFlags.
    private static readonly (DmmFlags Flag, string Name)[] Table =
    [
        (DmmFlags.Hi, "HI"),
        (DmmFlags.Lo, "LO"),
        (DmmFlags.Min, "MIN"),
        (DmmFlags.Max, "MAX"),
        (DmmFlags.Avg, "AVG"),
        (DmmFlags.MaxMinDash, "MAX_MIN_DASH"),
        (DmmFlags.Delta, "DELTA"),
        (DmmFlags.DeltaPercent, "DELTA_PERCENT"),
        (DmmFlags.LoZ, "LOZ"),
        (DmmFlags.Lpf, "LPF"),
        (DmmFlags.MainT1, "MAIN_T1"),
        (DmmFlags.MainT2, "MAIN_T2"),
        (DmmFlags.T1T2Dash, "T1_T2_DASH"),
        (DmmFlags.At, "AT"),
        (DmmFlags.LowBattery, "LOW_BATTERY"),
        (DmmFlags.SecondaryT1, "SEC_T1"),
        (DmmFlags.SecondaryT2, "SEC_T2"),
        (DmmFlags.ContinuityBeep, "CONTINUITY_BEEP"),
        (DmmFlags.HoldBox, "HOLD_H"),
        (DmmFlags.CrestBox, "C_BOX"),
        (DmmFlags.RecordBox, "R_BOX"),
        (DmmFlags.Auto, "AUTO"),
    ];

    /// <summary>Todos os nomes, na ordem dos bits de <see cref="DmmFlags"/>.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Array.ConvertAll(Table, e => e.Name));

    /// <summary>Nomes das flags acesas, na ordem dos bits do enum; bits sem nome são ignorados.</summary>
    public static IReadOnlyList<string> ToNames(DmmFlags flags)
    {
        var names = new List<string>();
        foreach (var (flag, name) in Table)
        {
            if ((flags & flag) != 0)
                names.Add(name);
        }
        return names;
    }

    /// <summary>Converte um nome (ex.: "LOW_BATTERY", sem diferenciar maiúsculas) na flag correspondente.</summary>
    public static bool TryParse(string? name, out DmmFlags flag)
    {
        foreach (var (candidate, candidateName) in Table)
        {
            if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
            {
                flag = candidate;
                return true;
            }
        }
        flag = DmmFlags.None;
        return false;
    }
}
