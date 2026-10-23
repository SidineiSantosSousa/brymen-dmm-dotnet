namespace Brymen.Dmm.Protocol.Tests;

/// <summary>Monta quadros RTD sintéticos de 27 bytes, com numeração 1-based como na especificação.</summary>
internal sealed class RtdFrame
{
    private static readonly Dictionary<char, byte> Segments = new()
    {
        [' '] = 0x00, ['0'] = 0xAF, ['1'] = 0xA0, ['2'] = 0xCB, ['3'] = 0xE9, ['4'] = 0xE4,
        ['5'] = 0x6D, ['6'] = 0x6F, ['7'] = 0xA8, ['8'] = 0xEF, ['9'] = 0xED, ['A'] = 0xEE,
        ['b'] = 0x67, ['C'] = 0x0F, ['d'] = 0xE3, ['E'] = 0x4F, ['F'] = 0x4E, ['g'] = 0xEC,
        ['H'] = 0xE6, ['i'] = 0x20, ['L'] = 0x07, ['n'] = 0x62, ['o'] = 0x63, ['P'] = 0xCE,
        ['r'] = 0x42, ['t'] = 0x47, ['U'] = 0xA7, ['u'] = 0x23, ['y'] = 0xE5, ['-'] = 0x40,
    };

    private readonly byte[] _bytes = new byte[Bm52xCommands.RealTimeFrameLength];

    public RtdFrame()
    {
        _bytes.AsSpan(19, 4).Fill(Bm52xCommands.Family);
    }

    public static IReadOnlyDictionary<char, byte> SegmentTable => Segments;

    public byte this[int byteNumber]
    {
        get => _bytes[byteNumber - 1];
        set => _bytes[byteNumber - 1] = value;
    }

    public RtdFrame Set(int byteNumber, byte value)
    {
        this[byteNumber] = value;
        return this;
    }

    public RtdFrame SetBit(int byteNumber, int bit)
    {
        this[byteNumber] |= (byte)(1 << bit);
        return this;
    }

    /// <summary>Escreve 4 caracteres no display principal; "." acende o ponto à direita do caractere anterior.</summary>
    public RtdFrame Main(string text) => Digits(5, text);

    public RtdFrame Secondary(string text) => Digits(11, text);

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public DmmReading Decode() => Bm52xRealtime.Decode(_bytes, DateTimeOffset.UnixEpoch);

    private RtdFrame Digits(int firstByte, string text)
    {
        int k = -1;
        foreach (char c in text)
        {
            if (c == '.')
            {
                // Sem caractere antes, o ponto cairia no byte anterior ao display (b4 ou b10).
                Assert.True(k >= 0, $"ponto sem dígito à esquerda em \"{text}\"");
                this[firstByte + k] |= 0x10;
                continue;
            }
            k++;
            // Preserva o bit 4 do 4º dígito, que é anunciador (dB / %4~20mA).
            byte keep = k == 3 ? (byte)(this[firstByte + k] & 0x10) : (byte)0;
            this[firstByte + k] = (byte)(Segments[c] | keep);
        }
        Assert.Equal(3, k);
        return this;
    }
}
