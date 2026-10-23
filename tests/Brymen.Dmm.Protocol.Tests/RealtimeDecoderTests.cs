namespace Brymen.Dmm.Protocol.Tests;

public class RealtimeDecoderTests
{
    // Leitura esperada de cada linha "66" das capturas (§3.7 e comentários dos arquivos): dígitos do principal e nibble baixo do byte 24.
    private static readonly Dictionary<string, (string Digits, int Byte24LowNibble, string Bytes25To27)[]> CaptureReadings = new()
    {
        ["rtd-acv.txt"] = [("0287", 1, "000000"), ("0156", 0, "000000"), .. Enumerable.Repeat(("0090", 0, "000000"), 12)],
        ["cmd-sequences-2026-10-22.txt"] = [.. Enumerable.Repeat(("0084", 0, "000D00"), 4)],
    };

    public static TheoryData<string, int, string> RtdCaptureLines()
    {
        var data = new TheoryData<string, int, string>();
        foreach (string file in CaptureReadings.Keys)
        {
            // Command 3 = 66 (tempo real); o cmd-sequences usa "<Command2><Command3>".
            var lines = TestData.ReadCapture(TestData.CapturePath(file)).Where(l => l.Command.EndsWith("66", StringComparison.Ordinal)).ToList();
            Assert.Equal(CaptureReadings[file].Length, lines.Count);
            for (int i = 0; i < lines.Count; i++)
                data.Add(file, i, Convert.ToHexString(lines[i].Frame));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RtdCaptureLines))]
    public void Decodes_every_real_capture_as_acv_with_hz(string file, int line, string hex)
    {
        var (digits, byte24LowNibble, bytes25To27) = CaptureReadings[file][line];
        double value = double.Parse("0." + digits[1..], System.Globalization.CultureInfo.InvariantCulture);

        var reading = Bm52xRealtime.Decode(Convert.FromHexString(hex), DateTimeOffset.UnixEpoch);

        Assert.Equal(
            new DisplayReading { Digits = digits, Text = "0." + digits[1..], Value = value, Unit = "V", Mode = MeasurementMode.AC, SiValue = value },
            reading.Main);
        Assert.Equal(
            new DisplayReading { Digits = "0000", Text = "0.000", Value = 0.0, Unit = "Hz", Mode = MeasurementMode.None, SiValue = 0.0 },
            reading.Secondary);
        Assert.Equal(DmmFlags.Auto, reading.Flags);
        Assert.Equal(new RealTimeDiagnostics(0x00, 0x06, (byte)byte24LowNibble, bytes25To27, "52525252"), reading.Diagnostics);
        Assert.Equal(hex, reading.RawHex);
    }

    [Fact]
    public void Capture_with_unknown_trailing_bytes_keeps_them_in_diagnostics()
    {
        var reading = Bm52xRealtime.Decode(
            Convert.FromHexString("00001000BFAFEFE40000BFAFAFAF00012006005252525210000D00"), DateTimeOffset.UnixEpoch);

        Assert.Equal("0.084", reading.Main!.Text);
        Assert.Equal("000D00", reading.Diagnostics!.Bytes25To27);
        Assert.Equal(0x06, reading.Diagnostics.Byte18);
    }

    [Fact]
    public void Seven_segment_table_matches_the_original_for_all_256_values()
    {
        foreach (int value in Enumerable.Range(0, 256))
        {
            char expected = RtdFrame.SegmentTable.FirstOrDefault(e => e.Value == (value & 0xEF)).Key;
            if (expected == default)
                expected = '?';
            Assert.Equal(expected, Bm52xRealtime.DecodeSegments((byte)value));
        }
        Assert.Equal(30, RtdFrame.SegmentTable.Count);
    }

    // Coluna "Segmentos" da tabela da §3.4, transcrita à parte das chaves hexadecimais.
    [Theory]
    [InlineData('0', "abcdef")]
    [InlineData('1', "bc")]
    [InlineData('2', "abdeg")]
    [InlineData('3', "abcdg")]
    [InlineData('4', "bcfg")]
    [InlineData('5', "acdfg")]
    [InlineData('6', "acdefg")]
    [InlineData('7', "abc")]
    [InlineData('8', "abcdefg")]
    [InlineData('9', "abcdfg")]
    [InlineData('A', "abcefg")]
    [InlineData('b', "cdefg")]
    [InlineData('C', "adef")]
    [InlineData('d', "bcdeg")]
    [InlineData('E', "adefg")]
    [InlineData('F', "aefg")]
    [InlineData('g', "abcfg")]
    [InlineData('H', "bcefg")]
    [InlineData('i', "c")]
    [InlineData('L', "def")]
    [InlineData('n', "ceg")]
    [InlineData('o', "cdeg")]
    [InlineData('P', "abefg")]
    [InlineData('r', "eg")]
    [InlineData('t', "defg")]
    [InlineData('U', "bcdef")]
    [InlineData('u', "cde")]
    [InlineData('y', "bcdfg")]
    [InlineData('-', "g")]
    [InlineData(' ', "")]
    public void Seven_segment_characters_follow_the_bit_layout(char expected, string segments)
    {
        // bit7=b, bit6=g, bit5=c, bit4=p, bit3=a, bit2=f, bit1=e, bit0=d.
        const string bitOfSegment = "d0e1f2a3c5g6b7";
        int value = 0;
        foreach (char segment in segments)
            value |= 1 << (bitOfSegment[bitOfSegment.IndexOf(segment) + 1] - '0');

        Assert.Equal(expected, Bm52xRealtime.DecodeSegments((byte)value));
        // O ponto decimal (bit 4) não muda o caractere.
        Assert.Equal(expected, Bm52xRealtime.DecodeSegments((byte)(value | 0x10)));
    }

    [Fact]
    public void Every_digit_byte_in_every_position_decodes_without_exception()
    {
        foreach (int position in (int[])[5, 6, 7, 8, 11, 12, 13, 14])
        {
            for (int value = 0; value < 256; value++)
            {
                var frame = new RtdFrame().Main("1234").Secondary("5678").Set(position, (byte)value);

                bool ok = Bm52xRealtime.TryDecode(frame.ToArray(), DateTimeOffset.UnixEpoch, out var reading, out string? error);

                Assert.True(ok, error);
                var display = position < 10 ? reading!.Main : reading!.Secondary;
                Assert.NotNull(display);
                int k = (position < 10 ? position - 5 : position - 11);
                Assert.Equal(Bm52xRealtime.DecodeSegments((byte)value), display.Digits[k]);
                if (display.Value is null)
                    Assert.Null(display.SiValue);
            }
        }
    }

    [Fact]
    public void SiValue_uses_exact_decimal_arithmetic()
    {
        // 0.287 mV: com double puro, 0.287 * 1e-3 não dá exatamente 0.000287.
        var reading = new RtdFrame().Main("0.287").SetBit(17, 5).SetBit(17, 6).Decode();

        Assert.Equal(0.287, reading.Main!.Value);
        Assert.Equal("m", reading.Main.Prefix);
        Assert.Equal(0.000287, reading.Main.SiValue);
        Assert.Equal("0.000287", reading.Main.SiValue!.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("mV", reading.Main.UnitSymbol);
    }

    public static TheoryData<int, int, bool, string, string> Prefixes() => new()
    {
        { 17, 3, true, "n", "1.234E-08" },
        { 17, 7, true, "u", "1.234E-05" },
        { 17, 6, true, "m", "0.01234" },
        { 16, 7, true, "k", "12340" },
        { 16, 6, true, "M", "12340000" },
        { 15, 2, false, "n", "1.234E-08" },
        { 15, 6, false, "u", "1.234E-05" },
        { 15, 7, false, "m", "0.01234" },
        { 16, 2, false, "k", "12340" },
        { 16, 3, false, "M", "12340000" },
    };

    [Theory]
    [MemberData(nameof(Prefixes))]
    public void Decodes_prefix_of_each_display(int byteNumber, int bit, bool main, string prefix, string siValue)
    {
        var reading = new RtdFrame().Main("12.34").Secondary("12.34").SetBit(byteNumber, bit).Decode();
        var display = main ? reading.Main! : reading.Secondary!;
        var other = main ? reading.Secondary! : reading.Main!;

        Assert.Equal(prefix, display.Prefix);
        Assert.Equal(12.34, display.Value);
        Assert.Equal(TestData.ParseDouble(siValue), display.SiValue);
        Assert.Equal("", other.Prefix);
        Assert.Equal(12.34, other.SiValue);
    }

    public static TheoryData<int, int, bool, string> Units() => new()
    {
        { 17, 5, true, "V" },
        { 17, 4, true, "A" },
        { 16, 5, true, "Ohm" },
        { 16, 4, true, "Hz" },
        { 17, 1, true, "S" },
        { 17, 0, true, "F" },
        { 17, 2, true, "%" },
        { 8, 4, true, "dB" },
        { 15, 4, false, "V" },
        { 15, 5, false, "A" },
        { 16, 1, false, "Ohm" },
        { 16, 0, false, "Hz" },
        { 15, 1, false, "S" },
        { 15, 0, false, "F" },
        { 15, 3, false, "%" },
        { 14, 4, false, "%" },
    };

    [Theory]
    [MemberData(nameof(Units))]
    public void Decodes_unit_of_each_display(int byteNumber, int bit, bool main, string unit)
    {
        var reading = new RtdFrame().Main("1.234").Secondary("5.678").SetBit(byteNumber, bit).Decode();
        var display = main ? reading.Main! : reading.Secondary!;
        var other = main ? reading.Secondary! : reading.Main!;

        Assert.Equal(unit, display.Unit);
        Assert.Equal("", other.Unit);
        // dB e %4~20mA usam o bit 4 do último dígito, que não é ponto decimal.
        Assert.Equal(main ? "1.234" : "5.678", display.Text);
        Assert.Equal(display.Value, display.SiValue);
    }

    [Fact]
    public void Ohm_symbol_and_micro_are_unicode()
    {
        var reading = new RtdFrame().Main("1.234").SetBit(16, 5).SetBit(16, 7).Secondary("5.678").SetBit(15, 5).SetBit(15, 6).Decode();

        Assert.Equal("kΩ", reading.Main!.UnitSymbol);
        Assert.Equal(1234.0, reading.Main.SiValue);
        Assert.Equal("µA", reading.Secondary!.UnitSymbol);
        Assert.Equal(0.000005678, reading.Secondary.SiValue);
    }

    [Fact]
    public void dB_with_m_is_dBm_without_prefix()
    {
        var dBm = new RtdFrame().Main(" 12.3").SetBit(4, 7).SetBit(8, 4).SetBit(17, 6).Decode();
        Assert.Equal("dBm", dBm.Main!.Unit);
        Assert.Equal("", dBm.Main.Prefix);
        Assert.Equal(-12.3, dBm.Main.Value);
        Assert.Equal(-12.3, dBm.Main.SiValue);
        Assert.Equal("dBm", dBm.Main.UnitSymbol);

        var dB = new RtdFrame().Main("12.34").SetBit(8, 4).Decode();
        Assert.Equal("dB", dB.Main!.Unit);
        Assert.Equal(12.34, dB.Main.SiValue);
    }

    [Theory]
    [InlineData(" 25C", false, 25.0, "25C", "°C")]
    [InlineData("23.5C", false, 23.5, "23.5C", "°C")]
    [InlineData("-10F", false, null, "-10F", "°F")]
    [InlineData(" 10F", true, -10.0, "-10F", "°F")]
    [InlineData("100.F", false, 100.0, "100.F", "°F")]
    public void Decodes_temperature_from_fourth_digit(string digits, bool negative, double? value, string text, string unit)
    {
        var frame = new RtdFrame().Main(digits).Secondary(digits).SetBit(17, 6).SetBit(15, 7);
        if (negative)
            frame.SetBit(4, 7).SetBit(9, 5);
        var reading = frame.Decode();

        foreach (var display in (DisplayReading[])[reading.Main!, reading.Secondary!])
        {
            Assert.Equal(unit, display.Unit);
            Assert.Equal("", display.Prefix);
            Assert.Equal(value, display.Value);
            Assert.Equal(value, display.SiValue);
            Assert.Equal(text, display.Text);
            Assert.False(display.Overload);
            Assert.Equal(unit, display.UnitSymbol);
        }
    }

    [Fact]
    public void Temperature_without_sensor_is_not_numeric()
    {
        var reading = new RtdFrame().Main("---C").Decode();

        Assert.Equal("°C", reading.Main!.Unit);
        Assert.Equal("---C", reading.Main.Text);
        Assert.Null(reading.Main.Value);
        Assert.False(reading.Main.Overload);
    }

    [Theory]
    [InlineData(" 0.L ", " 0L ", "0.L")]
    [InlineData(" 0L ", " 0L ", "0L")]
    [InlineData("  0L", "  0L", "0L")]
    [InlineData("0.L  ", "0L  ", "0.L")]
    [InlineData("  .0L", "  0L", ".0L")]
    [InlineData("L   ", "L   ", "L")]
    public void Overload_follows_the_0_L_rule(string lcd, string digits, string text)
    {
        var reading = new RtdFrame().Main(lcd).SetBit(16, 5).SetBit(16, 6).Decode();

        Assert.True(reading.Main!.Overload);
        Assert.Equal(digits, reading.Main.Digits);
        Assert.Equal(text, reading.Main.Text);
        Assert.Null(reading.Main.Value);
        Assert.Null(reading.Main.SiValue);
        Assert.Equal("MΩ", reading.Main.UnitSymbol);
    }

    [Fact]
    public void Negative_overload_keeps_the_sign_in_the_text()
    {
        var reading = new RtdFrame().Main(" 0.L ").SetBit(4, 7).Decode();

        Assert.True(reading.Main!.Overload);
        Assert.True(reading.Main.Negative);
        Assert.Equal("-0.L", reading.Main.Text);
    }

    [Theory]
    [InlineData("0L1 ")]
    [InlineData("-0L-")]
    [InlineData("0000")]
    public void Only_space_zero_and_L_with_an_L_are_overload(string lcd)
    {
        var reading = new RtdFrame().Main(lcd).Decode();
        Assert.False(reading.Main!.Overload);
    }

    [Theory]
    [InlineData("----", "----")]
    [InlineData("Auto", "Auto")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("Er 1", "Er 1")]
    public void Non_numeric_display_keeps_text_and_null_value(string lcd, string text)
    {
        var reading = new RtdFrame().Main(lcd).SetBit(17, 5).Decode();

        Assert.Equal(text, reading.Main!.Text);
        Assert.Null(reading.Main.Value);
        Assert.Null(reading.Main.SiValue);
        Assert.False(reading.Main.Overload);
    }

    [Fact]
    public void Invalid_segment_combination_is_question_mark()
    {
        var reading = new RtdFrame().Main("1234").Set(6, 0x01).Decode();

        Assert.Equal("1?34", reading.Main!.Digits);
        Assert.Null(reading.Main.Value);
    }

    [Fact]
    public void Blank_displays_are_null()
    {
        var bothBlank = new RtdFrame().Decode();
        Assert.Null(bothBlank.Main);
        Assert.Null(bothBlank.Secondary);

        // Ponto decimal sozinho não conta como dígito aceso.
        var onlyPoints = new RtdFrame().Set(5, 0x10).Set(12, 0x10).SetBit(4, 7).Decode();
        Assert.Null(onlyPoints.Main);
        Assert.Null(onlyPoints.Secondary);

        var mainOnly = new RtdFrame().Main("1234").Decode();
        Assert.NotNull(mainOnly.Main);
        Assert.Null(mainOnly.Secondary);

        var secondaryOnly = new RtdFrame().Secondary("1234").Decode();
        Assert.Null(secondaryOnly.Main);
        Assert.Equal(1234.0, secondaryOnly.Secondary!.Value);
    }

    [Fact]
    public void Leading_blank_digits_are_trimmed()
    {
        var reading = new RtdFrame().Main("  12").Secondary(" 1.23").Decode();

        Assert.Equal("  12", reading.Main!.Digits);
        Assert.Equal("12", reading.Main.Text);
        Assert.Equal(12.0, reading.Main.Value);
        Assert.Equal("1.23", reading.Secondary!.Text);
        Assert.Equal(1.23, reading.Secondary.Value);
    }

    [Fact]
    public void Sign_of_each_display_is_independent()
    {
        var reading = new RtdFrame().Main("12.34").Secondary("5.678").SetBit(4, 7).Decode();
        Assert.True(reading.Main!.Negative);
        Assert.Equal("-12.34", reading.Main.Text);
        Assert.Equal(-12.34, reading.Main.Value);
        Assert.False(reading.Secondary!.Negative);
        Assert.Equal(5.678, reading.Secondary.Value);

        var secondary = new RtdFrame().Main("12.34").Secondary("5.678").SetBit(9, 5).Decode();
        Assert.False(secondary.Main!.Negative);
        Assert.True(secondary.Secondary!.Negative);
        Assert.Equal("-5.678", secondary.Secondary.Text);
        Assert.Equal(-5.678, secondary.Secondary.Value);
    }

    [Theory]
    [InlineData(3, 4, 5, MeasurementMode.AC)]
    [InlineData(3, 5, 4, MeasurementMode.DC)]
    [InlineData(9, 6, 7, MeasurementMode.AC)]
    [InlineData(9, 7, 6, MeasurementMode.DC)]
    public void Decodes_ac_dc_and_acdc(int byteNumber, int bit, int otherBit, MeasurementMode mode)
    {
        bool main = byteNumber == 3;
        var single = new RtdFrame().Main("1234").Secondary("1234").SetBit(byteNumber, bit).Decode();
        Assert.Equal(mode, main ? single.Main!.Mode : single.Secondary!.Mode);
        Assert.Equal(MeasurementMode.None, main ? single.Secondary!.Mode : single.Main!.Mode);

        var both = new RtdFrame().Main("1234").Secondary("1234").SetBit(byteNumber, bit).SetBit(byteNumber, otherBit).Decode();
        Assert.Equal(MeasurementMode.AcDc, main ? both.Main!.Mode : both.Secondary!.Mode);
    }

    public static TheoryData<int, int, string> FlagBits() => new()
    {
        { 3, 7, "HI" },
        { 3, 6, "LO" },
        { 3, 3, "MIN" },
        { 3, 2, "MAX_MIN_DASH" },
        { 3, 1, "AVG" },
        { 3, 0, "MAX" },
        { 4, 6, "DELTA" },
        { 4, 5, "DELTA_PERCENT" },
        { 4, 4, "LOZ" },
        { 4, 2, "LPF" },
        { 4, 0, "MAIN_T1" },
        { 4, 3, "MAIN_T2" },
        { 4, 1, "T1_T2_DASH" },
        { 9, 4, "AT" },
        { 9, 3, "LOW_BATTERY" },
        { 9, 2, "SEC_T1" },
        { 9, 1, "SEC_T2" },
        { 9, 0, "CONTINUITY_BEEP" },
        { 24, 7, "HOLD_H" },
        { 24, 6, "C_BOX" },
        { 24, 5, "R_BOX" },
        { 24, 4, "AUTO" },
    };

    [Theory]
    [MemberData(nameof(FlagBits))]
    public void Decodes_each_flag_bit(int byteNumber, int bit, string name)
    {
        var reading = new RtdFrame().Main("1234").SetBit(byteNumber, bit).Decode();

        Assert.Equal(new[] { name }, DmmFlagNames.ToNames(reading.Flags));
        Assert.True(DmmFlagNames.TryParse(name, out var flag));
        Assert.Equal(flag, reading.Flags);
    }

    [Fact]
    public void Flag_names_follow_the_enum_bit_order()
    {
        string[] expected =
        [
            "HI", "LO", "MIN", "MAX", "AVG", "MAX_MIN_DASH", "DELTA", "DELTA_PERCENT", "LOZ", "LPF", "MAIN_T1",
            "MAIN_T2", "T1_T2_DASH", "AT", "LOW_BATTERY", "SEC_T1", "SEC_T2", "CONTINUITY_BEEP", "HOLD_H", "C_BOX",
            "R_BOX", "AUTO",
        ];
        var all = Enum.GetValues<DmmFlags>().Where(f => f != DmmFlags.None).Aggregate(DmmFlags.None, (a, f) => a | f);

        Assert.Equal(expected, DmmFlagNames.ToNames(all));
        Assert.Equal(expected, DmmFlagNames.All);
        Assert.Empty(DmmFlagNames.ToNames(DmmFlags.None));
        Assert.Equal(new[] { "LOW_BATTERY", "AUTO" }, DmmFlagNames.ToNames(DmmFlags.Auto | DmmFlags.LowBattery | (DmmFlags)(1u << 30)));
        Assert.True(DmmFlagNames.TryParse("low_battery", out var flag));
        Assert.Equal(DmmFlags.LowBattery, flag);
        Assert.False(DmmFlagNames.TryParse("NOPE", out _));
        Assert.False(DmmFlagNames.TryParse(null, out _));
    }

    [Fact]
    public void At_annunciator_is_accepted_and_exposed()
    {
        var reading = new RtdFrame().Main("1234").SetBit(9, 4).SetBit(24, 4).Decode();

        Assert.True(reading.Flags.HasFlag(DmmFlags.At));
        Assert.Equal(new[] { "AT", "AUTO" }, DmmFlagNames.ToNames(reading.Flags));
        Assert.Equal(1234.0, reading.Main!.Value);
    }

    [Fact]
    public void Keeps_undocumented_bytes_for_diagnostics()
    {
        var frame = new RtdFrame().Main("1234").Set(2, 0x5A).Set(18, 0x0E).Set(24, 0x1B).Set(25, 0x01).Set(26, 0x02).Set(27, 0x03);
        var reading = frame.Decode();

        Assert.Equal(new RealTimeDiagnostics(0x5A, 0x0E, 0x0B, "010203", "52525252"), reading.Diagnostics);
        Assert.Equal(DmmFlags.Auto, reading.Flags);
        Assert.Equal(Convert.ToHexString(frame.ToArray()), reading.RawHex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(26)]
    [InlineData(28)]
    [InlineData(36)]
    public void Rejects_truncated_or_oversized_frames(int length)
    {
        var frame = new byte[length];
        bool ok = Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out var reading, out string? error);

        Assert.False(ok);
        Assert.Null(reading);
        Assert.StartsWith($"comprimento {length}, esperado 27", error);
        Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(19)]
    public void Rejects_nonzero_report_id(int byteNumber)
    {
        var frame = new RtdFrame().Main("1234").Set(byteNumber, 0x01).ToArray();

        Assert.False(Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out _, out string? error));
        Assert.Equal($"byte {byteNumber} (report ID) = 01, esperado 00", error);
    }

    [Theory]
    [InlineData(20, 0x82)]
    [InlineData(21, 0x00)]
    [InlineData(22, 0x53)]
    [InlineData(23, 0x82)]
    public void Rejects_model_bytes_other_than_52(int byteNumber, int value)
    {
        var frame = new RtdFrame().Main("1234").Set(byteNumber, (byte)value).ToArray();

        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch));
        Assert.StartsWith("bytes 20..23 = ", exception.Message);
        Assert.EndsWith(", esperado 52525252", exception.Message);
    }

    [Fact]
    public void Reports_all_structural_errors_together()
    {
        var frame = new RtdFrame().Set(1, 0xAA).Set(19, 0xBB).Set(23, 0x82).ToArray();

        Assert.False(Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out _, out string? error));
        Assert.Equal("byte 1 (report ID) = AA, esperado 00; byte 19 (report ID) = BB, esperado 00; bytes 20..23 = 52525282, esperado 52525252", error);
    }

    [Theory]
    [InlineData(17, 0xC0, "prefixos m e µ acesos juntos no display principal (byte 17 = C0)")]
    [InlineData(17, 0xE0, "prefixos m e µ acesos juntos no display principal (byte 17 = E0)")]
    [InlineData(15, 0xC0, "prefixos m e µ acesos juntos no display secundário (byte 15 = C0)")]
    [InlineData(16, 0xC0, "2 prefixos acesos juntos no display principal")]
    [InlineData(16, 0x0C, "2 prefixos acesos juntos no display secundário")]
    public void Rejects_double_prefix(int byteNumber, int value, string message)
    {
        var frame = new RtdFrame().Main("1234").Secondary("1234").Set(byteNumber, (byte)value).ToArray();

        Assert.False(Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out _, out string? error));
        Assert.Equal(message, error);
        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Rejects_m_and_micro_even_with_dB()
    {
        var frame = new RtdFrame().Main("1234").SetBit(8, 4).Set(17, 0xC0).ToArray();
        Assert.False(Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out var reading, out string? error));
        Assert.Null(reading);
        Assert.Equal("prefixos m e µ acesos juntos no display principal (byte 17 = C0)", error);
    }

    [Fact]
    public void Secondary_display_overload_and_temperature()
    {
        var overload = new RtdFrame().Main("1.234").SetBit(17, 5).Secondary(" 0.L ").SetBit(16, 1).SetBit(16, 2).Decode();
        Assert.Equal(1.234, overload.Main!.Value);
        Assert.Equal(
            new DisplayReading { Digits = " 0L ", Text = "0.L", Overload = true, Prefix = "k", Unit = "Ohm" },
            overload.Secondary);

        var temperature = new RtdFrame().Main("1.234").SetBit(17, 5).Secondary(" 25C").SetBit(9, 2).Decode();
        Assert.Equal(("V", 1.234), (temperature.Main!.Unit, temperature.Main.Value));
        Assert.Equal(
            new DisplayReading { Digits = " 25C", Text = "25C", Value = 25.0, Unit = "°C", SiValue = 25.0 },
            temperature.Secondary);
        Assert.Equal(DmmFlags.SecondaryT1, temperature.Flags);
    }

    [Fact]
    public void Random_frames_never_throw_and_keep_the_invariants()
    {
        (int Byte, int Bit)[] mainPrefixes = [(17, 7), (17, 6), (17, 3), (16, 7), (16, 6)];
        (int Byte, int Bit)[] secondaryPrefixes = [(15, 7), (15, 6), (15, 2), (16, 3), (16, 2)];
        var random = new Random(52_66);
        var frame = new byte[Bm52xCommands.RealTimeFrameLength];
        int decoded = 0;
        for (int i = 0; i < 20_000; i++)
        {
            random.NextBytes(frame);
            frame[0] = frame[9] = frame[18] = 0x00;
            frame.AsSpan(19, 4).Fill(Bm52xCommands.Family);

            // Em 3 de cada 4 quadros, no máximo um prefixo por display: exercita a decodificação, e não só a rejeição.
            if (i % 4 != 0)
            {
                frame[14] &= 0x3B;
                frame[15] &= 0x33;
                frame[16] &= 0x37;
                foreach (var prefixes in (ReadOnlySpan<(int Byte, int Bit)[]>)[mainPrefixes, secondaryPrefixes])
                {
                    int pick = random.Next(prefixes.Length + 1);
                    if (pick < prefixes.Length)
                        frame[prefixes[pick].Byte - 1] |= (byte)(1 << prefixes[pick].Bit);
                }
            }

            if (!Bm52xRealtime.TryDecode(frame, DateTimeOffset.UnixEpoch, out var reading, out string? error))
            {
                // Com IDs e modelo válidos, só sobra a rejeição de prefixo duplo.
                Assert.Contains(" acesos juntos no display ", error, StringComparison.Ordinal);
                Assert.Equal(error, Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch)).Message);
                continue;
            }

            decoded++;
            Assert.Equal(Convert.ToHexString(frame), reading.RawHex);
            foreach (var display in (DisplayReading?[])[reading.Main, reading.Secondary])
            {
                if (display is null)
                    continue;
                Assert.Equal(4, display.Digits.Length);
                Assert.Equal(display.Text.Trim(' '), display.Text);
                Assert.Equal(display.Value is null, display.SiValue is null);
                if (display.Overload)
                    Assert.Null(display.Value);
                if (display.Value is double value)
                {
                    // O valor é o próprio texto, sem espaços e sem o 'C'/'F' da temperatura.
                    string number = display.Text.Replace(" ", "", StringComparison.Ordinal);
                    if (display.Unit is "°C" or "°F")
                        number = number[..^1];
                    Assert.Equal(TestData.ParseDouble(number), value);
                }
            }
        }
        Assert.True(decoded > 10_000, $"só {decoded} quadros decodificados");
    }

    [Fact]
    public void Decode_allocates_only_the_reading()
    {
        byte[] frame = Convert.FromHexString("00001000BFCBEFA80000BFAFAFAF00012006005252525211000000");
        for (int i = 0; i < 10; i++)
            Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Bm52xRealtime.Decode(frame, DateTimeOffset.UnixEpoch);
        long perDecode = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // DmmReading, 2 DisplayReading, Digits/Text, RawHex e diagnósticos: ≈ 640 bytes em 64 bits.
        // Com StringBuilder e lista de erros por quadro passava de 1 100 bytes.
        Assert.InRange(perDecode, 1, 900);
    }

    [Fact]
    public void Milli_and_kilo_on_main_are_rejected_but_dBm_with_kilo_is_not_double()
    {
        Assert.False(Bm52xRealtime.TryDecode(new RtdFrame().Main("1234").SetBit(17, 6).SetBit(16, 7).ToArray(), DateTimeOffset.UnixEpoch, out _, out _));

        // Com dB, o "m" pertence à unidade: só o "k" é prefixo.
        var reading = new RtdFrame().Main("1234").SetBit(8, 4).SetBit(17, 6).SetBit(16, 7).Decode();
        Assert.Equal("dBm", reading.Main!.Unit);
        Assert.Equal("k", reading.Main.Prefix);
    }

    [Fact]
    public void Decode_and_TryDecode_agree()
    {
        byte[] frame = new RtdFrame().Main("12.34").Secondary("50.12").SetBit(3, 4).SetBit(17, 5).SetBit(16, 0).SetBit(24, 4).ToArray();
        var at = new DateTimeOffset(2026, 10, 22, 8, 30, 0, TimeSpan.FromHours(-3));

        Assert.True(Bm52xRealtime.TryDecode(frame, at, out var reading, out _));
        Assert.Equal(reading, Bm52xRealtime.Decode(frame, at));
        Assert.Equal(at, reading.Timestamp);
    }
}
