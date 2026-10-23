namespace Brymen.Dmm.Protocol;

/// <summary>
/// Comandos do protocolo BM52x (família 0x52) via cabo BC-86X. O relatório de saída é
/// <c>[00h | 00h 52h cmd]</c>; veja <c>docs/protocol/bm52x-usb-protocol.md</c> §2.3.
/// </summary>
public static class Bm52xCommands
{
    /// <summary>Código de família do BM52x (Command 2) e Model ID dos bytes 20–23 do tempo real.</summary>
    public const byte Family = 0x52;

    /// <summary>Cs_RTD: leitura em tempo real (3 relatórios / 27 bytes).</summary>
    public const byte RealTime = 0x66;

    /// <summary>Cs_HMD: zera o ponteiro da memória e lê o 1º bloco (4 relatórios / 36 bytes).</summary>
    public const byte MemoryHead = 0x88;

    /// <summary>Cs_NMD: avança o ponteiro 24 bytes e lê o próximo bloco.</summary>
    public const byte MemoryNext = 0x89;

    /// <summary>Cs_CMD: relê o bloco atual (recuperação de erro).</summary>
    public const byte MemoryCurrent = 0x8A;

    /// <summary>Relatórios da resposta ao <see cref="RealTime"/>.</summary>
    public const int RealTimeReportCount = 3;

    /// <summary>Relatórios da resposta a <see cref="MemoryHead"/>, <see cref="MemoryNext"/> e <see cref="MemoryCurrent"/>.</summary>
    public const int MemoryReportCount = 4;

    /// <summary>Bytes do quadro de tempo real, com os 3 report IDs (formato das capturas no Windows).</summary>
    public const int RealTimeFrameLength = 27;

    /// <summary>Bytes do quadro de um bloco de memória, com os 4 report IDs (formato das capturas no Windows).</summary>
    public const int MemoryFrameLength = 36;

    /// <summary>Bytes úteis por bloco de memória.</summary>
    public const int MemoryBlockDataLength = 24;
}

/// <summary>Quadro recebido do medidor inválido ou inconsistente.</summary>
public sealed class Bm52xFrameException(string message) : FormatException(message);
