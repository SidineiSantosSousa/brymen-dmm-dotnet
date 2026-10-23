namespace Brymen.Dmm.Protocol;

/// <summary>
/// Tabela de 7 segmentos do software original (rotina 0x00426844, emulada para os 256 valores; §3.4).
/// Layout dos bits: <c>bit7=b, bit6=g, bit5=c, bit4=p (ponto), bit3=a, bit2=f, bit1=e, bit0=d</c>.
/// </summary>
internal static class SevenSegment
{
    /// <summary>Máscara do ponto decimal (bit 4), que não faz parte do caractere.</summary>
    public const byte DecimalPointMask = 0x10;

    public const char Invalid = '?';

    private static readonly char[] Table = BuildTable();

    /// <summary>Caractere do byte de dígito com o ponto decimal mascarado; <see cref="Invalid"/> se a combinação não existir.</summary>
    public static char ToChar(byte value) => Table[value & 0xEF];

    public static bool HasDecimalPoint(byte value) => (value & DecimalPointMask) != 0;

    private static char[] BuildTable()
    {
        var table = new char[256];
        Array.Fill(table, Invalid);
        table[0x00] = ' ';
        table[0xAF] = '0';
        table[0xA0] = '1';
        table[0xCB] = '2';
        table[0xE9] = '3';
        table[0xE4] = '4';
        table[0x6D] = '5';
        table[0x6F] = '6';
        table[0xA8] = '7';
        table[0xEF] = '8';
        table[0xED] = '9';
        table[0xEE] = 'A';
        table[0x67] = 'b';
        table[0x0F] = 'C';
        table[0xE3] = 'd';
        table[0x4F] = 'E';
        table[0x4E] = 'F';
        table[0xEC] = 'g';
        table[0xE6] = 'H';
        table[0x20] = 'i';
        table[0x07] = 'L';
        table[0x62] = 'n';
        table[0x63] = 'o';
        table[0xCE] = 'P';
        table[0x42] = 'r';
        table[0x47] = 't';
        table[0xA7] = 'U';
        table[0x23] = 'u';
        table[0xE5] = 'y';
        table[0x40] = '-';
        return table;
    }
}
