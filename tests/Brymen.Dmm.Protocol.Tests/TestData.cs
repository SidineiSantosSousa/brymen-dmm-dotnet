using System.Globalization;
using System.Text.Json;

namespace Brymen.Dmm.Protocol.Tests;

/// <summary>Acesso aos arquivos de <c>docs/protocol</c>, vinculados ao output do projeto de teste.</summary>
internal static class TestData
{
    public static string ProtocolDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "protocol");

    private static readonly Lazy<JsonDocument> VectorsDocument =
        new(() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProtocolDirectory, "test-vectors.json"))));

    public static IEnumerable<JsonElement> Vectors => VectorsDocument.Value.RootElement.GetProperty("vectors").EnumerateArray();

    public static JsonElement Vector(string name) => Vectors.Single(v => v.GetProperty("name").GetString() == name);

    public static string CapturePath(string fileName) => Path.Combine(ProtocolDirectory, "captures", fileName);

    /// <summary>Lê uma captura no formato <c>"&lt;cmd&gt; &lt;hex&gt;"</c>, ignorando comentários (#) e linhas vazias.</summary>
    public static IReadOnlyList<CaptureLine> ReadCapture(string path)
    {
        var lines = new List<CaptureLine>();
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, parts.Length);
            lines.Add(new CaptureLine(parts[0].ToUpperInvariant(), Convert.FromHexString(parts[1])));
        }
        return lines;
    }

    /// <summary>
    /// Remonta a imagem da memória como o medidor a entrega: 88h zera o ponteiro, 89h acrescenta o bloco e 8Ah
    /// relê o bloco atual. Para quando já tem os blocos de que o cabeçalho precisa e chega um novo 88h.
    /// </summary>
    public static byte[] AssembleImage(IEnumerable<CaptureLine> capture)
    {
        var blocks = new List<byte[]>();
        LoggerHeader? header = null;
        foreach (var (command, frame) in capture)
        {
            var block = Bm52xMemory.ParseBlock(frame);
            Assert.True(block.IsChecksumValid, $"checksum inválido no comando {command}");
            switch (command)
            {
                case "88":
                    if (header is not null && blocks.Count >= header.BlocksNeeded)
                    {
                        Assert.Equal(blocks[0], block.Data);
                        return Concat(blocks);
                    }
                    blocks.Clear();
                    blocks.Add(block.Data);
                    header = Bm52xMemory.ParseHeader(block);
                    break;
                case "89":
                    blocks.Add(block.Data);
                    break;
                case "8A":
                    Assert.Equal(blocks[^1], block.Data);
                    break;
                default:
                    Assert.Fail($"comando inesperado na captura: {command}");
                    break;
            }
        }
        return Concat(blocks);
    }

    public static double? NullableDouble(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : element.GetDouble();

    public static string? NullableString(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    public static double ParseDouble(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string ModeName(MeasurementMode mode) => mode switch
    {
        MeasurementMode.AC => "AC",
        MeasurementMode.DC => "DC",
        MeasurementMode.AcDc => "AC+DC",
        _ => "NONE",
    };

    private static byte[] Concat(List<byte[]> blocks) => [.. blocks.SelectMany(b => b)];
}

internal readonly record struct CaptureLine(string Command, byte[] Frame);
