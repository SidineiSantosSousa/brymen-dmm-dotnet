using System.Globalization;

namespace Brymen.Dmm.Protocol.Tests;

/// <summary>As duas famílias no decodificador do tempo real: o Model ID 52h ou 82h, os quatro bytes iguais (§8.3).</summary>
public class FamilyDecoderTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static byte[] WithModel(string modelHex)
    {
        var frame = new RtdFrame().Main("1234").ToArray();
        Convert.FromHexString(modelHex).CopyTo(frame, 19);
        return frame;
    }

    [Theory]
    [InlineData("52525252", MeterFamily.Bm52x)]
    [InlineData("82828282", MeterFamily.Bm82x)]
    public void Accepts_the_model_id_of_both_families(string model, MeterFamily family)
    {
        var frame = WithModel(model);

        var reading = Bm52xRealtime.Decode(frame, Timestamp);

        Assert.Equal(family, reading.Family);
        Assert.Equal(model, reading.Diagnostics!.ModelBytes);
        Assert.Equal("1234", reading.Main!.Digits);
        Assert.True(Bm52xRealtime.TryGetFamily(frame, out var detected));
        Assert.Equal(family, detected);
        Assert.Equal(reading, Bm52xRealtime.Decode(frame, Timestamp, family));
    }

    [Theory]
    [InlineData("52528282", "52525252 (BM52x) ou 82828282 (BM82x)")]
    [InlineData("82825252", "52525252 (BM52x) ou 82828282 (BM82x)")]
    [InlineData("82828252", "82828282")]
    [InlineData("52825282", "52525252 (BM52x) ou 82828282 (BM82x)")]
    [InlineData("82525252", "52525252")]
    [InlineData("82008282", "82828282")]
    public void Rejects_mixed_model_bytes(string model, string expected)
    {
        var frame = WithModel(model);

        Assert.False(Bm52xRealtime.TryDecode(frame, Timestamp, out var reading, out string? error));
        Assert.Null(reading);
        Assert.Equal($"bytes 20..23 = {model}, esperado {expected}", error);
        Assert.False(Bm52xRealtime.TryGetFamily(frame, out _));
        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, Timestamp));
        Assert.Equal(error, exception.Message);
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("53535353")]
    [InlineData("FFFFFFFF")]
    [InlineData("F7F7F7F7")]
    public void Rejects_unknown_model_ids_even_with_four_equal_bytes(string model)
    {
        // O original converte FF, F7... em 0x82 [RE 0x00424B5A]; nenhum medidor do kit confirmou isso (§8.6).
        var frame = WithModel(model);

        Assert.False(Bm52xRealtime.TryDecode(frame, Timestamp, out _, out string? error));
        Assert.Equal($"bytes 20..23 = {model}, esperado 52525252 (BM52x) ou 82828282 (BM82x)", error);
        Assert.False(Bm52xRealtime.TryGetFamily(frame, out _));
    }

    [Fact]
    public void The_expected_family_refuses_a_valid_frame_of_the_other_family()
    {
        var bm82x = WithModel("82828282");
        var bm52x = WithModel("52525252");

        Assert.False(Bm52xRealtime.TryDecode(bm82x, Timestamp, MeterFamily.Bm52x, out var reading, out string? error));
        Assert.Null(reading);
        Assert.Equal("bytes 20..23 = 82828282 (BM82x), esperado 52525252 (BM52x)", error);
        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(bm52x, Timestamp, MeterFamily.Bm82x));
        Assert.Equal("bytes 20..23 = 52525252 (BM52x), esperado 82828282 (BM82x)", exception.Message);

        // Um Model ID inválido diz o esperado da família da conexão, e os outros erros continuam juntos.
        var broken = new RtdFrame().Main("1234").Set(10, 0x01).ToArray();
        Convert.FromHexString("00000000").CopyTo(broken, 19);
        Assert.False(Bm52xRealtime.TryDecode(broken, Timestamp, MeterFamily.Bm82x, out _, out error));
        Assert.Equal("byte 10 (report ID) = 01, esperado 00; bytes 20..23 = 00000000, esperado 82828282", error);
    }

    [Fact]
    public void TryGetFamily_needs_a_whole_frame()
    {
        Assert.False(Bm52xRealtime.TryGetFamily(WithModel("82828282").AsSpan(0, 26), out _));
        Assert.False(Bm52xRealtime.TryGetFamily([], out _));
    }

    [Fact]
    public void Family_helpers_describe_both_families()
    {
        Assert.True(MeterFamilies.TryFromModelId(0x52, out var bm52x));
        Assert.True(MeterFamilies.TryFromModelId(0x82, out var bm82x));
        Assert.False(MeterFamilies.TryFromModelId(0x00, out _));

        Assert.Equal((MeterFamily.Bm52x, MeterFamily.Bm82x), (bm52x, bm82x));
        Assert.Equal((0x52, 0x82), (bm52x.Code(), bm82x.Code()));
        Assert.Equal((Bm52xCommands.Bm52xFamily, Bm52xCommands.Bm82xFamily), ((byte)bm52x, (byte)bm82x));
        Assert.Equal(Bm52xCommands.Family, Bm52xCommands.Bm52xFamily);
        Assert.True(bm52x.HasDataLogger());
        Assert.False(bm82x.HasDataLogger());
        Assert.Equal(("BM52x", "BM82x"), (bm52x.Name(), bm82x.Name()));
        Assert.Equal("Minipa ET-2615A (Brymen BM525s, BM52x)", bm52x.Describe());
        Assert.Equal("Minipa ET-2517A (Brymen BM827s, BM82x)", bm82x.Describe());
    }

    [Fact]
    public void Readings_created_without_the_decoder_keep_the_BM52x_default()
    {
        var reading = new DmmReading { Timestamp = Timestamp, RawHex = "" };

        Assert.Equal(MeterFamily.Bm52x, reading.Family);
    }

    public static TheoryData<string, string, string> SweepLines()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string line in File.ReadLines(TestData.CapturePath("et2517a-sweep-2026-10-23.txt")))
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            string[] parts = trimmed.Split(' ', 3);
            if (parts[1] == "TIMEOUT")
                continue;
            data.Add(parts[0], parts[1], parts[2]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SweepLines))]
    public void Decodes_every_frame_of_the_ET2517A_sweep_like_the_capture_column(string time, string hex, string decoded)
    {
        // A coluna da captura foi gerada com os bytes de modelo trocados para 52 (§8.4.14); o quadro real é decodificado
        // direto, com a família BM82x.
        var reading = Bm52xRealtime.Decode(Convert.FromHexString(hex), Timestamp);

        string[] columns = decoded.Split(" | ");
        Assert.True(columns.Length == 3, $"{time}: {decoded}");
        Assert.Equal(MeterFamily.Bm82x, reading.Family);
        AssertDisplay(columns[0], reading.Main, time);
        AssertDisplay(columns[1], reading.Secondary, time);
        string flags = string.Join(",", DmmFlagNames.ToNames(reading.Flags));
        Assert.Equal(columns[2].Trim(), flags);
    }

    [Fact]
    public void The_sweep_has_the_43_frames_of_the_specification()
    {
        var frames = SweepLines().Select(row => (string)row[1]).ToList();

        Assert.Equal(43, frames.Count);
        Assert.Equal(30, frames.Distinct().Count());
    }

    // "<texto> <unidade> <modo>[ OL]", ou "(apagado)"; a unidade pode ser vazia ("diod  None").
    private static void AssertDisplay(string expected, DisplayReading? actual, string time)
    {
        if (expected.Trim() == "(apagado)")
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        string[] tokens = expected.Trim().Split(' ');
        Assert.True(tokens.Length >= 3, $"{time}: {expected}");
        Assert.Equal(tokens[0], actual.Text);
        Assert.Equal(tokens[1], actual.UnitSymbol);
        Assert.Equal(tokens[2], actual.Mode.ToString());
        Assert.Equal(tokens.Length > 3 && tokens[3] == "OL", actual.Overload);
        if (actual.Value is { } value && !actual.Overload)
            Assert.Equal(value, double.Parse(actual.Text.TrimStart('-'), CultureInfo.InvariantCulture) * (actual.Negative ? -1 : 1));
    }
}
