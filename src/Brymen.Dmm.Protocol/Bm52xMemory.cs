using System.Globalization;

namespace Brymen.Dmm.Protocol;

/// <summary>Leitura da memória do datalogger (§4 da especificação).</summary>
public static class Bm52xMemory
{
    /// <summary>Ponteiro do DLE da 1ª sessão (A0_0080h).</summary>
    public const int FirstSessionPointer = 6;

    /// <summary>Endereço do ponteiro 0 (Model_Id) na página A0.</summary>
    public const int BaseAddress = 0x7A;

    /// <summary>Máximo de sessões aceito pelo medidor (Table 2).</summary>
    public const int MaxSessions = 999;

    /// <summary>Maior contagem de um display de 10 000 contagens; acima disso a amostra é suspeita.</summary>
    public const int MaxCount = 9999;

    /// <summary>Byte alto do último endereço da memória física (A3_FFFFh, PDF-M p3 e p7).</summary>
    private const byte LastPage = 0xA3;

    private const int HeaderLength = 6;
    private const int SessionHeadLength = 14;
    private const int SessionEndLength = 2;
    private const byte Dle = 0xEE;
    private const byte Stx = 0xA0;
    private const byte Etx = 0xC0;
    private const string UnknownFunction = "?";

    private static readonly int[] ReportIdPositions = [1, 10, 19, 28];

    /// <summary>Extrai os 24 bytes úteis e o checksum de uma resposta de 36 bytes; lança <see cref="Bm52xFrameException"/> se a estrutura for inválida.</summary>
    /// <remarks>Checksum errado não é exceção: veja <see cref="MemoryBlock.IsChecksumValid"/> (o chamador decide reler com 8Ah).</remarks>
    public static MemoryBlock ParseBlock(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != Bm52xCommands.MemoryFrameLength)
            throw new Bm52xFrameException(Invariant($"comprimento {frame.Length}, esperado {Bm52xCommands.MemoryFrameLength} bytes"));

        List<string>? errors = null;
        foreach (int n in ReportIdPositions)
        {
            if (frame[n - 1] != 0)
                (errors ??= []).Add(Invariant($"byte {n} (report ID) = {frame[n - 1]:X2}, esperado 00"));
        }
        if (errors is not null)
            throw new Bm52xFrameException(string.Join("; ", errors));

        // Bytes 2–9, 11–18 e 20–27 (§4.2).
        var data = new byte[Bm52xCommands.MemoryBlockDataLength];
        frame.Slice(1, 8).CopyTo(data);
        frame.Slice(10, 8).CopyTo(data.AsSpan(8));
        frame.Slice(19, 8).CopyTo(data.AsSpan(16));

        // Soma dos bytes 1..27 mod 65536, little-endian nos bytes 29–30 (§4.3).
        int sum = 0;
        foreach (byte b in frame[..27])
            sum += b;
        ushort received = (ushort)(frame[28] | frame[29] << 8);
        return new MemoryBlock(data, received, (ushort)sum);
    }

    /// <summary>Lê o cabeçalho global a partir do 1º bloco (resposta do 88h).</summary>
    public static LoggerHeader ParseHeader(MemoryBlock headBlock)
    {
        ArgumentNullException.ThrowIfNull(headBlock);
        return ReadHeader(headBlock.Data);
    }

    /// <summary>
    /// Decodifica a imagem da memória: concatenação, a partir do ponteiro 0 (A0_007Ah), dos dados de 24 bytes
    /// de cada bloco lido (88h seguido de 89h...). Bytes além de <see cref="LoggerHeader.TotalBytes"/> são ignorados.
    /// </summary>
    /// <remarks>
    /// Só lança <see cref="Bm52xFrameException"/> se a imagem não tiver nem o cabeçalho. Inconsistências (marcadores,
    /// encadeamento, comprimentos, faixas) viram entradas em <see cref="LoggerMemory.Warnings"/> e a decodificação continua.
    /// </remarks>
    public static LoggerMemory Decode(ReadOnlySpan<byte> image)
    {
        var header = ReadHeader(image);
        var warnings = new List<string>();
        var sessions = new List<LoggerSession>();

        if (header.TotalBytes > image.Length)
        {
            warnings.Add(Invariant(
                $"TotalBytes = {header.TotalBytes}, mas a imagem tem {image.Length} bytes; só a parte disponível foi decodificada."));
        }
        var memory = image[..Math.Min(header.TotalBytes, image.Length)];

        if (header.TotalSessions > MaxSessions)
            warnings.Add(Invariant($"TotalSession = {header.TotalSessions}, acima do máximo de {MaxSessions} do medidor."));

        if (header.TotalSessions == 0)
        {
            if (header.TotalBytes > HeaderLength)
                warnings.Add(Invariant($"TotalSession = 0, mas TotalBytes = {header.TotalBytes} indica dados após o cabeçalho."));
            return new LoggerMemory(header, sessions, warnings);
        }

        int pointer = FirstSessionPointer;
        bool complete = false;
        for (int number = 1; number <= header.TotalSessions; number++)
        {
            if (pointer + SessionHeadLength > memory.Length)
            {
                warnings.Add(Invariant(
                    $"Sessão {number}: o cabeçalho no ponteiro {pointer} ({PointerToAddress(pointer)}) passa do fim dos dados ({memory.Length} bytes); {header.TotalSessions - number + 1} sessão(ões) ignorada(s)."));
                break;
            }

            var session = DecodeSession(memory, pointer, number, sessions.Count > 0 ? sessions[^1].HeadPointer : null, warnings);
            sessions.Add(session);

            int end = pointer + SessionHeadLength + session.LengthBytes + SessionEndLength;
            if (number == header.TotalSessions)
            {
                pointer = end;
                complete = true;
                break;
            }

            int? next = FindNextSession(memory, session, end, warnings);
            if (next is null)
            {
                warnings.Add(Invariant(
                    $"Sessão {number + 1}: não foi possível localizar o início (NS = {session.NextAddress}, fim da sessão {number} no ponteiro {end}); {header.TotalSessions - number} sessão(ões) ignorada(s)."));
                break;
            }
            pointer = next.Value;
        }

        if (complete && pointer != header.TotalBytes)
        {
            warnings.Add(Invariant(
                $"A última sessão termina no ponteiro {pointer}, mas TotalBytes = {header.TotalBytes}."));
        }

        return new LoggerMemory(header, sessions, warnings);
    }

    /// <summary>
    /// Endereço de 3 bytes (A2_A1A0h) → ponteiro, como o original:
    /// <c>((A2 − A0h) div 2)·10000h + A1·100h + A0 − 7Ah</c>. A1h/A3h são aliases de A0h/A2h. Pode dar negativo.
    /// </summary>
    public static int AddressToPointer(byte a2, byte a1, byte a0)
        => ((a2 - 0xA0) >> 1) * 0x10000 + a1 * 0x100 + a0 - BaseAddress;

    /// <summary>Ponteiro → endereço no formato "A2_A1A0" (ex.: 6 → "A0_0080"; 65 414 → "A2_0000").</summary>
    public static string PointerToAddress(int pointer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pointer);
        int linear = pointer + BaseAddress;
        int page = linear >> 16;
        return Invariant($"{0xA0 + 2 * page:X2}_{(linear >> 8) & 0xFF:X2}{linear & 0xFF:X2}");
    }

    /// <summary>Formata um endereço de 3 bytes como "A2_A1A0" (ex.: "A0_095A").</summary>
    public static string FormatAddress(byte a2, byte a1, byte a0) => Invariant($"{a2:X2}_{a1:X2}{a0:X2}");

    private static LoggerHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
            throw new Bm52xFrameException(Invariant($"dados com {data.Length} bytes; o cabeçalho da memória exige {HeaderLength}"));

        // Model_Id, TotalBytes (24 bits LE) e TotalSession (16 bits LE): Table 2.
        return new LoggerHeader(data[0], data[1] | data[2] << 8 | data[3] << 16, data[4] | data[5] << 8);
    }

    private static LoggerSession DecodeSession(ReadOnlySpan<byte> memory, int p, int number, int? previousHead, List<string> warnings)
    {
        bool headOk = memory[p] == Dle && memory[p + 1] == Stx;
        if (!headOk)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: início DLE/STX = {memory[p]:X2} {memory[p + 1]:X2} no ponteiro {p} ({PointerToAddress(p)}), esperado EE A0."));
        }

        // PS e NS são transmitidos LSB primeiro (Addr0, Addr1, Addr2).
        byte ps0 = memory[p + 2], ps1 = memory[p + 3], ps2 = memory[p + 4];
        byte ns0 = memory[p + 5], ns1 = memory[p + 6], ns2 = memory[p + 7];
        byte function = memory[p + 8], select = memory[p + 9], status = memory[p + 10];
        int length = memory[p + 11] | memory[p + 12] << 8 | memory[p + 13] << 16;

        // Fora de A0_007Ah..A3_FFFFh o NS não aponta para a memória (ex.: FF FF FF "don't care" na última sessão).
        int nsPointer = AddressToPointer(ns2, ns1, ns0);
        int? nextPointer = ns2 <= LastPage && nsPointer >= 0 ? nsPointer : null;

        if (previousHead is int expectedPrevious && AddressToPointer(ps2, ps1, ps0) != expectedPrevious)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: PS = {FormatAddress(ps2, ps1, ps0)}, mas a sessão anterior começa em {PointerToAddress(expectedPrevious)}."));
        }

        bool dual = (status & 0x80) != 0;
        int size = dual ? 6 : 3;
        var interval = LoggerFunctions.IntervalOf(status);
        var functions = LoggerFunctions.Lookup(function, select, status);
        var main = functions?.Main;
        var secondary = dual ? functions?.Secondary : null;

        if (functions is null)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: função desconhecida (Bfunction {function:X2}h, Bselect {select:X2}h, Bstatus {status:X2}h); valores expostos só como contagem."));
        }
        else if (dual != (functions.Value.Secondary is not null))
        {
            warnings.Add(Invariant(
                $"Sessão {number}: Bstatus {status:X2}h indica {(dual ? "dual" : "single")}, mas a Table 3 prevê {(dual ? "single" : "dual")} para {functions.Value.Main.Name}; amostras lidas com {size} bytes."));
        }

        if (length % size != 0)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: comprimento {length} não é múltiplo de {size}; {length % size} byte(s) final(is) ignorado(s)."));
        }

        int dataStart = p + SessionHeadLength;
        long dataEnd = (long)dataStart + length;
        int declared = length / size;
        int available = (int)Math.Max(0, Math.Min(dataEnd, memory.Length) - dataStart);
        int sampleCount = Math.Min(declared, available / size);
        if (sampleCount < declared)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: os dados declarados ({length} bytes) passam do fim dos dados disponíveis ({memory.Length} bytes); {sampleCount} de {declared} amostras decodificadas."));
        }

        bool endOk = dataEnd + SessionEndLength <= memory.Length
            && memory[(int)dataEnd] == Dle
            && memory[(int)dataEnd + 1] == Etx;
        if (!endOk && dataEnd + SessionEndLength <= memory.Length)
        {
            warnings.Add(Invariant(
                $"Sessão {number}: fim DLE/ETX = {memory[(int)dataEnd]:X2} {memory[(int)dataEnd + 1]:X2} no ponteiro {dataEnd}, esperado EE C0."));
        }

        var mainStats = new ValueStats("principal");
        var secondaryStats = new ValueStats("secundário");
        var samples = new LoggerSample[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            var record = memory.Slice(dataStart + i * size, size);
            var mainValue = DecodeValue(record[..3], main, isMain: true, i, mainStats);
            var secondaryValue = dual ? DecodeValue(record[3..], secondary, isMain: false, i, secondaryStats) : null;
            samples[i] = new LoggerSample(i, TimeSpan.FromTicks(interval.Ticks * i), mainValue, secondaryValue);
        }

        // Função desconhecida e secundário fora da Table 3 já têm aviso próprio; não repete um por faixa.
        if (main is not null)
            mainStats.AddRangeWarning(number, main.Name, warnings);
        if (secondary is not null)
            secondaryStats.AddRangeWarning(number, secondary.Name, warnings);
        mainStats.AddSuspiciousWarning(number, warnings);
        secondaryStats.AddSuspiciousWarning(number, warnings);

        return new LoggerSession
        {
            Number = number,
            HeadAddress = PointerToAddress(p),
            HeadPointer = p,
            PreviousAddress = FormatAddress(ps2, ps1, ps0),
            NextAddress = FormatAddress(ns2, ns1, ns0),
            NextPointer = nextPointer,
            Function = function,
            Select = select,
            Status = status,
            IsDual = dual,
            Interval = interval,
            MainFunction = main?.Name ?? UnknownFunction,
            SecondaryFunction = dual ? secondary?.Name ?? UnknownFunction : null,
            LengthBytes = length,
            HeadMarkersOk = headOk,
            EndMarkersOk = endOk,
            Samples = samples,
        };
    }

    /// <summary>
    /// Próxima sessão: o fim calculado (H + 10h + DL) e o NS devem coincidir. Se divergirem, vale o que tiver
    /// marcadores DLE/STX válidos; com ambos válidos, o fim calculado se os marcadores de fim estiverem certos.
    /// </summary>
    private static int? FindNextSession(ReadOnlySpan<byte> memory, LoggerSession session, int end, List<string> warnings)
    {
        int? ns = session.NextPointer;
        if (ns == end)
            return end;

        bool endHasHead = HasSessionHead(memory, end);
        bool nsHasHead = ns is int n && n > session.HeadPointer && HasSessionHead(memory, n);
        int? chosen = endHasHead && (!nsHasHead || session.EndMarkersOk) ? end
            : nsHasHead ? ns
            : null;

        if (chosen is int c)
        {
            string nsText = ns is int q ? Invariant($"ponteiro {q}") : "endereço inválido";
            warnings.Add(Invariant(
                $"Sessão {session.Number}: NS = {session.NextAddress} ({nsText}), mas a sessão termina no ponteiro {end}; usando o ponteiro {c}."));
        }
        return chosen;
    }

    private static bool HasSessionHead(ReadOnlySpan<byte> memory, int pointer)
        => pointer >= 0 && pointer + SessionHeadLength <= memory.Length && memory[pointer] == Dle && memory[pointer + 1] == Stx;

    private static LoggerValue DecodeValue(ReadOnlySpan<byte> x, LoggerFunction? function, bool isMain, int index, ValueStats stats)
    {
        // X_0: sinal (bit 7), OL (bit 6), LB (bit 5, só no principal), faixa (bits 3–0); count = X_1 | X_2 << 8 (§4.9).
        bool negative = (x[0] & 0x80) != 0;
        bool overload = (x[0] & 0x40) != 0;
        bool lowBattery = isMain && (x[0] & 0x20) != 0;
        int range = x[0] & 0x0F;
        int count = x[1] | x[2] << 8;
        string raw = Convert.ToHexString(x);

        // Com OL o valor é nulo e o conteúdo de D3..D0 não é documentado: não é contagem suspeita.
        if (count > MaxCount && !overload)
            stats.Suspicious(index, count);

        var spec = function?.GetRange(range);
        if (spec is not LoggerRange r)
        {
            stats.InvalidRange(index, range);
            return new LoggerValue
            {
                Count = count,
                Range = range,
                Negative = negative,
                Overload = overload,
                LowBattery = lowBattery,
                Text = overload ? "OL" : (negative ? "-" : "") + count.ToString(CultureInfo.InvariantCulture),
                Mode = function?.Mode ?? MeasurementMode.None,
                IsRangeValid = false,
                Raw = raw,
            };
        }

        decimal? value = overload ? null : new decimal(count, 0, 0, negative, (byte)r.Decimals);
        return new LoggerValue
        {
            Count = count,
            Range = range,
            Negative = negative,
            Overload = overload,
            LowBattery = lowBattery,
            // O original mostra "----" em temperatura com OL.
            Text = overload ? (function!.IsTemperature ? "----" : "OL") : FormatCount(count, r.Decimals, negative),
            Value = value is decimal v ? SiScale.ToDouble(v) : null,
            Prefix = r.Prefix,
            Unit = r.Unit,
            Mode = function!.Mode,
            SiValue = value is decimal s ? SiScale.ToDouble(SiScale.ToSi(s, r.Prefix, r.Unit)) : null,
            Decimals = r.Decimals,
            Raw = raw,
        };
    }

    /// <summary>Contagem com as casas da faixa (13 com 3 casas → "0.013"), montada na pilha: uma string por valor.</summary>
    private static string FormatCount(int count, int decimals, bool negative)
    {
        Span<char> digits = stackalloc char[10];
        count.TryFormat(digits, out int length, provider: CultureInfo.InvariantCulture);

        // Zeros à esquerda até sobrar um dígito antes do ponto.
        int width = Math.Max(length, decimals + 1);
        int padding = width - length;
        Span<char> text = stackalloc char[width + 2];
        int n = 0;
        if (negative)
            text[n++] = '-';
        for (int i = 0; i < width; i++)
        {
            if (i == width - decimals)
                text[n++] = '.';
            text[n++] = i < padding ? '0' : digits[i - padding];
        }
        return new string(text[..n]);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Agrega problemas por display para gerar um aviso por sessão, e não um por amostra.</summary>
    private sealed class ValueStats(string display)
    {
        private int _invalidRanges;
        private int _firstInvalidIndex;
        private int _firstInvalidRange;
        private int _suspicious;
        private int _firstSuspiciousIndex;
        private int _firstSuspiciousCount;

        public void InvalidRange(int index, int range)
        {
            if (_invalidRanges++ == 0)
                (_firstInvalidIndex, _firstInvalidRange) = (index, range);
        }

        public void Suspicious(int index, int count)
        {
            if (_suspicious++ == 0)
                (_firstSuspiciousIndex, _firstSuspiciousCount) = (index, count);
        }

        public void AddRangeWarning(int session, string function, List<string> warnings)
        {
            if (_invalidRanges > 0)
            {
                warnings.Add(Invariant(
                    $"Sessão {session}: {_invalidRanges} valor(es) do display {display} com faixa inválida para {function} (1ª na amostra {_firstInvalidIndex}, faixa {_firstInvalidRange}); valor deixado nulo."));
            }
        }

        public void AddSuspiciousWarning(int session, List<string> warnings)
        {
            if (_suspicious > 0)
            {
                warnings.Add(Invariant(
                    $"Sessão {session}: {_suspicious} contagem(ns) do display {display} acima de {MaxCount} (1ª na amostra {_firstSuspiciousIndex}: {_firstSuspiciousCount}); valores suspeitos."));
            }
        }
    }
}
