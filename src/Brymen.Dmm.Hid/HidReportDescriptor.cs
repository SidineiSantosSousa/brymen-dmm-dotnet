namespace Brymen.Dmm.Hid;

/// <summary>Tamanhos dos relatórios de uma interface HID, em bytes de dados (sem o byte de report ID).</summary>
/// <param name="InputReportLength">Maior relatório de entrada.</param>
/// <param name="OutputReportLength">Maior relatório de saída.</param>
/// <param name="FeatureReportLength">Maior relatório de feature.</param>
/// <param name="UsesReportIds">Se o descriptor declara report IDs (relatórios numerados).</param>
internal readonly record struct HidReportLayout(int InputReportLength, int OutputReportLength, int FeatureReportLength, bool UsesReportIds)
{
    /// <summary>Layout do BC-86X (8 bytes de entrada, 3 de saída, sem report IDs), usado quando o descriptor não está disponível.</summary>
    public static HidReportLayout Bu86xDefault { get; } = new(8, 3, 0, false);
}

/// <summary>
/// Parser mínimo de HID report descriptor (HID 1.11 §6.2.2): soma Report Size × Report Count dos itens
/// Input, Output e Feature por report ID, respeitando Push/Pop. Itens locais e coleções são ignorados.
/// </summary>
internal static class HidReportDescriptor
{
    private const int MaxReportBits = 0xFFFF * 8;

    /// <summary>Calcula os tamanhos dos relatórios; lança <see cref="FormatException"/> se o descriptor for inválido.</summary>
    public static HidReportLayout Parse(ReadOnlySpan<byte> descriptor) =>
        TryParse(descriptor, out var layout)
            ? layout
            : throw new FormatException("HID report descriptor inválido ou sem itens Input/Output/Feature.");

    /// <summary>Calcula os tamanhos dos relatórios; devolve <see langword="false"/> se o descriptor estiver truncado, malformado ou sem itens principais.</summary>
    public static bool TryParse(ReadOnlySpan<byte> descriptor, out HidReportLayout layout)
    {
        layout = default;
        var global = new GlobalState(0, 0, 0);
        var stack = new Stack<GlobalState>();
        // Bits acumulados por report ID: [0] Input, [1] Output, [2] Feature.
        var bits = new Dictionary<byte, long[]>();
        bool usesReportIds = false, anyMainItem = false;

        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i++];
            if (prefix == 0xFE)
            {
                // Item longo: bDataSize, bLongItemTag e os dados; nenhum é relevante para os tamanhos.
                if (i + 2 > descriptor.Length)
                    return false;
                i += 2 + descriptor[i];
                if (i > descriptor.Length)
                    return false;
                continue;
            }

            int size = (prefix & 0x03) == 0x03 ? 4 : prefix & 0x03;
            if (i + size > descriptor.Length)
                return false;
            uint value = 0;
            for (int k = 0; k < size; k++)
                value |= (uint)descriptor[i + k] << (8 * k);
            i += size;

            int type = (prefix >> 2) & 0x03;
            int tag = prefix >> 4;
            if (type == 0)
            {
                int kind = tag switch { 0x8 => 0, 0x9 => 1, 0xB => 2, _ => -1 };
                if (kind < 0)
                    continue;
                ulong itemBits = (ulong)global.ReportSize * global.ReportCount;
                if (itemBits > MaxReportBits)
                    return false;
                if (!bits.TryGetValue(global.ReportId, out var acc))
                    bits[global.ReportId] = acc = new long[3];
                acc[kind] += (long)itemBits;
                if (acc[kind] > MaxReportBits)
                    return false;
                anyMainItem = true;
            }
            else if (type == 1)
            {
                switch (tag)
                {
                    case 0x7:
                        global = global with { ReportSize = value };
                        break;
                    case 0x8:
                        // Report ID 0 é reservado (HID 1.11 §6.2.2.7).
                        if (value is 0 or > 0xFF)
                            return false;
                        global = global with { ReportId = (byte)value };
                        usesReportIds = true;
                        break;
                    case 0x9:
                        global = global with { ReportCount = value };
                        break;
                    case 0xA:
                        stack.Push(global);
                        break;
                    case 0xB:
                        if (!stack.TryPop(out global))
                            return false;
                        break;
                }
            }
        }

        if (!anyMainItem)
            return false;

        int input = 0, output = 0, feature = 0;
        foreach (var acc in bits.Values)
        {
            input = Math.Max(input, BitsToBytes(acc[0]));
            output = Math.Max(output, BitsToBytes(acc[1]));
            feature = Math.Max(feature, BitsToBytes(acc[2]));
        }
        layout = new HidReportLayout(input, output, feature, usesReportIds);
        return true;
    }

    private static int BitsToBytes(long bits) => (int)((bits + 7) / 8);

    private readonly record struct GlobalState(uint ReportSize, uint ReportCount, byte ReportId);
}
