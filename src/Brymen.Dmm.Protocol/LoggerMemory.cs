namespace Brymen.Dmm.Protocol;

/// <summary>Um bloco de memória (resposta de 88h/89h/8Ah): 24 bytes úteis + checksum de 16 bits.</summary>
/// <param name="Data">Os 24 bytes úteis do bloco.</param>
/// <param name="ChecksumReceived">Checksum recebido (bytes 29–30, little-endian).</param>
/// <param name="ChecksumComputed">Soma dos bytes 1 a 27, módulo 65536.</param>
public sealed record MemoryBlock(byte[] Data, ushort ChecksumReceived, ushort ChecksumComputed)
{
    /// <summary>Se o checksum recebido confere com o calculado.</summary>
    public bool IsChecksumValid => ChecksumReceived == ChecksumComputed;
}

/// <summary>Cabeçalho global da memória do datalogger (A0_007Ah..A0_007Fh, Table 2).</summary>
/// <param name="ModelId">Model_Id (o 1º byte da memória).</param>
/// <param name="TotalBytes">Bytes usados da memória, a partir do ponteiro 0.</param>
/// <param name="TotalSessions">Sessões gravadas.</param>
public sealed record LoggerHeader(byte ModelId, int TotalBytes, int TotalSessions)
{
    /// <summary>Número de blocos de 24 bytes necessários (1 × 88h + N × 89h).</summary>
    public int BlocksNeeded => (TotalBytes + Bm52xCommands.MemoryBlockDataLength - 1) / Bm52xCommands.MemoryBlockDataLength;
}

/// <summary>Valor gravado de um display numa amostra (Tables 4–6).</summary>
public sealed record LoggerValue
{
    /// <summary>Contagem bruta (inteiro de 16 bits little-endian).</summary>
    public int Count { get; init; }

    /// <summary>Range bits (Table 6).</summary>
    public int Range { get; init; }

    /// <summary>Valor negativo.</summary>
    public bool Negative { get; init; }

    /// <summary>Fora da faixa (<c>OL</c>).</summary>
    public bool Overload { get; init; }

    /// <summary>Bateria fraca no momento da amostra.</summary>
    public bool LowBattery { get; init; }

    /// <summary>Texto formatado com as casas decimais da faixa (ex.: "0.013", "OL").</summary>
    public required string Text { get; init; }

    /// <summary>Valor na escala da faixa; null se overload.</summary>
    public double? Value { get; init; }

    /// <summary>Prefixo: "", "n", "u" (micro), "m", "k" ou "M".</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Unidade sem prefixo (ex.: "V", "A", "Ohm", "Hz", "°C").</summary>
    public string Unit { get; init; } = "";

    /// <summary>Acoplamento da função gravada (AC, DC, AC+DC ou nenhum).</summary>
    public MeasurementMode Mode { get; init; }

    /// <summary><see cref="Value"/> × 10^prefixo (sem conversão para %, dB/dBm e temperatura); null se overload.</summary>
    public double? SiValue { get; init; }

    /// <summary>Casas decimais da faixa (Table 6); null se a faixa ou a função forem desconhecidas.</summary>
    public int? Decimals { get; init; }

    /// <summary>false se a faixa for N/A para a função (o valor fica null e o texto mostra a contagem bruta).</summary>
    public bool IsRangeValid { get; init; } = true;

    /// <summary>Os 3 bytes gravados (X_0 X_1 X_2), em hexadecimal maiúsculo.</summary>
    public string? Raw { get; init; }

    /// <summary>Unidade para exibição (ex.: "mV", "µA", "kΩ", "°C").</summary>
    public string UnitSymbol => (Prefix == "u" ? "µ" : Prefix) + (Unit == "Ohm" ? "Ω" : Unit);
}

/// <summary>Uma amostra de uma sessão gravada.</summary>
/// <param name="Index">Posição na sessão, a partir de 0.</param>
/// <param name="Elapsed">Tempo desde o início da sessão: <paramref name="Index"/> × intervalo de gravação.</param>
/// <param name="Main">Valor do display principal.</param>
/// <param name="Secondary">Valor do display secundário, nas sessões duplas.</param>
public sealed record LoggerSample(int Index, TimeSpan Elapsed, LoggerValue Main, LoggerValue? Secondary);

/// <summary>Uma sessão (session page) do datalogger.</summary>
public sealed record LoggerSession
{
    /// <summary>Número da sessão, 1-based.</summary>
    public required int Number { get; init; }

    /// <summary>Endereço do DLE inicial (ex.: "A0_0080").</summary>
    public required string HeadAddress { get; init; }

    /// <summary>Posição do DLE inicial na imagem da memória (ponteiro; A0_0080h = 6).</summary>
    public int HeadPointer { get; init; }

    /// <summary>Campo PSn: endereço da sessão anterior ("don't care" na 1ª).</summary>
    public string? PreviousAddress { get; init; }

    /// <summary>Campo NSn: endereço da próxima sessão ("don't care" na última).</summary>
    public string? NextAddress { get; init; }

    /// <summary>Ponteiro equivalente a <see cref="NextAddress"/>; null se o endereço estiver fora da memória (A0_007Ah..A3_FFFFh).</summary>
    public int? NextPointer { get; init; }

    /// <summary>DLE EEh e STX A0h presentes no início da sessão.</summary>
    public bool HeadMarkersOk { get; init; }

    /// <summary>DLE EEh e ETX C0h presentes logo após os dados.</summary>
    public bool EndMarkersOk { get; init; }

    /// <summary>Bfunction da sessão (Table 3).</summary>
    public byte Function { get; init; }

    /// <summary>Bselect da sessão (Table 3).</summary>
    public byte Select { get; init; }

    /// <summary>Bstatus da sessão: bit 7 = dupla, bits 5 e 4 na Table 3, nibble baixo = intervalo (Table 3-1).</summary>
    public byte Status { get; init; }

    /// <summary>Sessão dupla: cada amostra tem o principal e o secundário (6 bytes).</summary>
    public bool IsDual { get; init; }

    /// <summary>Intervalo de gravação (Table 3-1).</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>Função do display principal (ex.: "ACV", "DCmV", "OHM").</summary>
    public required string MainFunction { get; init; }

    /// <summary>Função do display secundário, nas sessões duplas.</summary>
    public string? SecondaryFunction { get; init; }

    /// <summary>Campo DL: bytes de amostras da sessão.</summary>
    public int LengthBytes { get; init; }

    /// <summary>Amostras declaradas por <see cref="LengthBytes"/> (6 bytes por amostra dual, 3 por single).</summary>
    public int DeclaredSampleCount => LengthBytes / (IsDual ? 6 : 3);

    /// <summary>Amostras decodificadas, na ordem da gravação.</summary>
    public required IReadOnlyList<LoggerSample> Samples { get; init; }
}

/// <summary>Conteúdo decodificado da memória do datalogger.</summary>
/// <param name="Header">Cabeçalho global.</param>
/// <param name="Sessions">Sessões, na ordem da memória.</param>
/// <param name="Warnings">Inconsistências encontradas na decodificação (marcadores, encadeamento, comprimentos, faixas).</param>
public sealed record LoggerMemory(LoggerHeader Header, IReadOnlyList<LoggerSession> Sessions, IReadOnlyList<string> Warnings);
