namespace Brymen.Dmm.Hid.Tests;

public class HidReportDescriptorTests
{
    // Descriptor típico de um dispositivo vendor-defined como o BC-86X: entrada de 8 bytes e saída de 3, sem report IDs.
    internal static readonly byte[] Bu86xLikeDescriptor =
    [
        0x06, 0x00, 0xFF, // Usage Page (Vendor 0xFF00)
        0x09, 0x01,       // Usage (1)
        0xA1, 0x01,       // Collection (Application)
        0x15, 0x00,       //   Logical Minimum (0)
        0x26, 0xFF, 0x00, //   Logical Maximum (255)
        0x75, 0x08,       //   Report Size (8)
        0x95, 0x08,       //   Report Count (8)
        0x09, 0x01,       //   Usage (1)
        0x81, 0x02,       //   Input (Data, Var, Abs)
        0x95, 0x03,       //   Report Count (3)
        0x09, 0x01,       //   Usage (1)
        0x91, 0x02,       //   Output (Data, Var, Abs)
        0xC0,             // End Collection
    ];

    [Fact]
    public void ParsesTheBu86xLayout()
    {
        var layout = HidReportDescriptor.Parse(Bu86xLikeDescriptor);

        Assert.Equal(new HidReportLayout(8, 3, 0, false), layout);
        Assert.Equal(HidReportLayout.Bu86xDefault, layout);
    }

    [Fact]
    public void ReportIdsSelectTheLargestReportOfEachKind()
    {
        byte[] descriptor =
        [
            0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01,
            0x85, 0x01,       // Report ID (1)
            0x75, 0x08, 0x95, 0x08, 0x81, 0x02, // Input: 8 bytes
            0x95, 0x04, 0x91, 0x02,             // Output: 4 bytes
            0x85, 0x02,       // Report ID (2)
            0x95, 0x10, 0x81, 0x02,             // Input: 16 bytes
            0x95, 0x02, 0xB1, 0x02,             // Feature: 2 bytes
            0xC0,
        ];

        var layout = HidReportDescriptor.Parse(descriptor);

        Assert.Equal(new HidReportLayout(16, 4, 2, true), layout);
    }

    [Fact]
    public void BitFieldsAndConstantPaddingAreSummed()
    {
        byte[] descriptor =
        [
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01,
            0x75, 0x01, 0x95, 0x03, 0x81, 0x02, // 3 botões de 1 bit
            0x75, 0x05, 0x95, 0x01, 0x81, 0x03, // 5 bits de preenchimento constante
            0x75, 0x08, 0x95, 0x02, 0x81, 0x06, // X/Y relativos
            0x75, 0x04, 0x95, 0x01, 0x81, 0x02, // 4 bits soltos: arredonda para cima
            0xC0,
        ];

        Assert.Equal(new HidReportLayout(4, 0, 0, false), HidReportDescriptor.Parse(descriptor));
    }

    [Fact]
    public void PushAndPopRestoreTheGlobalState()
    {
        byte[] descriptor =
        [
            0x75, 0x08, 0x95, 0x02, // Report Size 8, Count 2
            0xA4,                   // Push
            0x75, 0x10, 0x95, 0x04, // Report Size 16, Count 4
            0x91, 0x02,             // Output: 8 bytes
            0xB4,                   // Pop
            0x81, 0x02,             // Input: 2 bytes
        ];

        Assert.Equal(new HidReportLayout(2, 8, 0, false), HidReportDescriptor.Parse(descriptor));
    }

    [Fact]
    public void LongItemsAndFourByteDataAreSkippedCorrectly()
    {
        byte[] descriptor =
        [
            0xFE, 0x03, 0x10, 0xAA, 0xBB, 0xCC, // Item longo com 3 bytes de dados
            0x77, 0x08, 0x00, 0x00, 0x00,       // Report Size (8) com 4 bytes de dados
            0x97, 0x05, 0x00, 0x00, 0x00,       // Report Count (5) com 4 bytes de dados
            0x81, 0x02,
        ];

        Assert.Equal(new HidReportLayout(5, 0, 0, false), HidReportDescriptor.Parse(descriptor));
    }

    public static TheoryData<string, byte[]> InvalidDescriptors => new()
    {
        { "vazio", [] },
        { "sem itens principais", [0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01, 0xC0] },
        { "item truncado", [0x75, 0x08, 0x95, 0x08, 0x81, 0x02, 0x26, 0xFF] },
        { "item longo truncado", [0x81, 0x02, 0xFE, 0x05, 0x10, 0x00] },
        { "Pop sem Push", [0x75, 0x08, 0x95, 0x01, 0x81, 0x02, 0xB4] },
        { "Report ID 0", [0x85, 0x00, 0x75, 0x08, 0x95, 0x01, 0x81, 0x02] },
        { "relatório gigante", [0x77, 0xFF, 0xFF, 0xFF, 0xFF, 0x97, 0xFF, 0xFF, 0xFF, 0xFF, 0x81, 0x02] },
    };

    [Theory]
    [MemberData(nameof(InvalidDescriptors))]
    public void InvalidDescriptorsAreRejected(string description, byte[] descriptor)
    {
        Assert.False(HidReportDescriptor.TryParse(descriptor, out _), description);
        Assert.Throws<FormatException>(() => HidReportDescriptor.Parse(descriptor));
    }
}
