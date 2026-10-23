using static Brymen.Dmm.Protocol.Tests.LoggerImageBuilder;

namespace Brymen.Dmm.Protocol.Tests;

public class MemoryDecoderTests
{
    // §4.14: contagem do principal por faixa de amostras ("início–fim:count").
    private const string DumpMainCounts =
        "0–21:13 22–23:12 24–25:13 26–31:12 32:11 33–37:12 38:11 39–44:12 45–77:11 78:10 79–80:11 81:10 " +
        "82–83:11 84–98:10 99:11 100–116:10 117:9 118–127:10 128–133:9 134–137:10 138:9 139:10 140–141:9 " +
        "142:10 143–145:9 146–149:10 150–162:9 163:8 164–168:9 169–228:8 229–230:7 231–236:8 237:7 238–239:8 " +
        "240:7 241–246:8 247–248:7 249:8 250–253:7 254–255:8 256–331:7 332:6 333–341:7 342–344:6 345–349:7 " +
        "350–354:6 355:7 356–358:6 359–361:7 362–364:6 365–366:7 367–374:6";

    [Theory]
    [InlineData("memory-dump-1.txt")]
    [InlineData("memory-dump-2.txt")]
    public void Decodes_full_real_dump(string file)
    {
        byte[] image = TestData.AssembleImage(TestData.ReadCapture(TestData.CapturePath(file)));
        var memory = Bm52xMemory.Decode(image);

        Assert.Empty(memory.Warnings);
        Assert.Equal(new LoggerHeader(0x01, 2272, 1), memory.Header);
        Assert.Equal(95, memory.Header.BlocksNeeded);

        var session = Assert.Single(memory.Sessions);
        Assert.Equal(1, session.Number);
        Assert.Equal("A0_0080", session.HeadAddress);
        Assert.Equal(6, session.HeadPointer);
        Assert.Equal("A0_0418", session.PreviousAddress);
        Assert.Equal("A0_095A", session.NextAddress);
        Assert.Equal(2272, session.NextPointer);
        Assert.Equal(0x01, session.Function);
        Assert.Equal(0x00, session.Select);
        Assert.Equal(0x80, session.Status);
        Assert.True(session.IsDual);
        Assert.True(session.HeadMarkersOk);
        Assert.True(session.EndMarkersOk);
        Assert.Equal(TimeSpan.FromMilliseconds(50), session.Interval);
        Assert.Equal("ACV", session.MainFunction);
        Assert.Equal("Hz", session.SecondaryFunction);
        Assert.Equal(2250, session.LengthBytes);
        Assert.Equal(375, session.Samples.Count);

        Assert.Equal(ExpandRuns(DumpMainCounts), session.Samples.Select(s => s.Main.Count));
        Assert.Equal(Enumerable.Range(0, 375), session.Samples.Select(s => s.Index));
        Assert.Equal(TimeSpan.Zero, session.Samples[0].Elapsed);
        Assert.Equal(TimeSpan.FromTicks(187_000_000), session.Samples[^1].Elapsed);

        foreach (var sample in session.Samples)
        {
            Assert.Equal(TimeSpan.FromTicks(sample.Index * TimeSpan.FromMilliseconds(50).Ticks), sample.Elapsed);
            Assert.Equal(TestData.ParseDouble(sample.Main.Text), sample.Main.Value);
            Assert.Equal(sample.Main.Value, sample.Main.SiValue);
            Assert.Equal(("", "V", MeasurementMode.AC, 3), (sample.Main.Prefix, sample.Main.Unit, sample.Main.Mode, sample.Main.Decimals));
            Assert.False(sample.Main.Negative || sample.Main.Overload || sample.Main.LowBattery);

            var secondary = sample.Secondary!;
            Assert.Equal(("0.000", 0.0, "Hz", MeasurementMode.None), (secondary.Text, secondary.Value, secondary.Unit, secondary.Mode));
            Assert.Equal("Hz", secondary.UnitSymbol);
        }

        Assert.Equal("0.013", session.Samples[0].Main.Text);
        Assert.Equal(0.013, session.Samples[21].Main.Value);
        Assert.Equal("0.012", session.Samples[22].Main.Text);
        Assert.All(session.Samples.Skip(372), s => Assert.Equal(0.006, s.Main.Value));
        Assert.Equal(0.006, session.Samples.Min(s => s.Main.Value));
        Assert.Equal(0.013, session.Samples.Max(s => s.Main.Value));
    }

    [Fact]
    public void Both_dumps_are_byte_identical()
    {
        byte[] first = TestData.AssembleImage(TestData.ReadCapture(TestData.CapturePath("memory-dump-1.txt")));
        byte[] second = TestData.AssembleImage(TestData.ReadCapture(TestData.CapturePath("memory-dump-2.txt")));

        Assert.Equal(first, second);
        Assert.Equal(new byte[8], first[2272..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(32)]
    [InlineData(35)]
    [InlineData(37)]
    public void ParseBlock_rejects_wrong_length(int length)
    {
        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.ParseBlock(new byte[length]));
        Assert.Equal($"comprimento {length}, esperado 36 bytes", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(28)]
    public void ParseBlock_rejects_nonzero_report_id(int byteNumber)
    {
        byte[] frame = HeadFrame();
        frame[byteNumber - 1] = 0x7F;

        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.ParseBlock(frame));
        Assert.Equal($"byte {byteNumber} (report ID) = 7F, esperado 00", exception.Message);
    }

    [Fact]
    public void ParseBlock_reports_invalid_checksum_without_throwing()
    {
        byte[] frame = HeadFrame();
        frame[28] ^= 0x01;
        var badReceived = Bm52xMemory.ParseBlock(frame);
        Assert.False(badReceived.IsChecksumValid);
        Assert.Equal(0x0596, badReceived.ChecksumReceived);
        Assert.Equal(0x0597, badReceived.ChecksumComputed);

        frame = HeadFrame();
        frame[3] = 0x09; // TotalBytes_1: 08h → 09h
        var badData = Bm52xMemory.ParseBlock(frame);
        Assert.False(badData.IsChecksumValid);
        Assert.Equal(0x0597, badData.ChecksumReceived);
        Assert.Equal(0x0598, badData.ChecksumComputed);
    }

    [Fact]
    public void ParseBlock_checksum_is_16_bit_little_endian_sum()
    {
        byte[] frame = new byte[36];
        foreach (int i in (int[])[1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 21, 22, 23, 24, 25, 26])
            frame[i] = 0xFF;
        frame[28] = 0xE8; // 24 × FFh = 17E8h
        frame[29] = 0x17;
        frame[30] = 0x55; // bytes 31–36 são "don't care"

        var block = Bm52xMemory.ParseBlock(frame);
        Assert.True(block.IsChecksumValid);
        Assert.Equal(0x17E8, block.ChecksumComputed);
        Assert.All(block.Data, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ParseHeader_validates_input()
    {
        Assert.Throws<ArgumentNullException>(() => Bm52xMemory.ParseHeader(null!));
        Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.ParseHeader(new MemoryBlock([1, 2, 3], 6, 6)));
        Assert.Equal(new LoggerHeader(0x00, 0x123456, 0x0302), Bm52xMemory.ParseHeader(new MemoryBlock([0x00, 0x56, 0x34, 0x12, 0x02, 0x03], 0, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Decode_requires_the_global_header(int length)
    {
        Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.Decode(new byte[length]));
    }

    [Fact]
    public void Empty_memory_has_no_sessions()
    {
        var memory = Bm52xMemory.Decode([0x01, 0x06, 0x00, 0x00, 0x00, 0x00]);
        Assert.Empty(memory.Sessions);
        Assert.Empty(memory.Warnings);

        var inconsistent = Bm52xMemory.Decode([0x01, 0x40, 0x00, 0x00, 0x00, 0x00]);
        Assert.Empty(inconsistent.Sessions);
        Assert.Contains(inconsistent.Warnings, w => w.StartsWith("TotalSession = 0, mas TotalBytes = 64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0xA0, 0x00, 0x7A, 0)]
    [InlineData(0xA0, 0x00, 0x80, 6)]
    [InlineData(0xA0, 0x09, 0x5A, 2272)]
    [InlineData(0xA1, 0x09, 0x5A, 2272)]
    [InlineData(0xA0, 0xFF, 0xFF, 65413)]
    [InlineData(0xA2, 0x00, 0x00, 65414)]
    [InlineData(0xA3, 0x00, 0x00, 65414)]
    [InlineData(0xA2, 0x12, 0x34, 0x10000 + 0x1234 - 0x7A)]
    [InlineData(0xA3, 0xFF, 0xFF, 0x1FFFF - 0x7A)]
    public void Converts_address_to_pointer_like_the_original(int a2, int a1, int a0, int pointer)
    {
        Assert.Equal(pointer, Bm52xMemory.AddressToPointer((byte)a2, (byte)a1, (byte)a0));

        // O caminho inverso sempre usa a página par (A0/A2).
        string address = Bm52xMemory.PointerToAddress(pointer);
        Assert.Equal(Bm52xMemory.FormatAddress((byte)(a2 & 0xFE), (byte)a1, (byte)a0), address);
    }

    [Fact]
    public void Address_below_page_A0_is_negative()
    {
        Assert.True(Bm52xMemory.AddressToPointer(0x00, 0x00, 0x00) < 0);
        Assert.True(Bm52xMemory.AddressToPointer(0x9F, 0xFF, 0xFF) < 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Bm52xMemory.PointerToAddress(-1));
    }

    [Fact]
    public void Decodes_multiple_synthetic_sessions()
    {
        var builder = new LoggerImageBuilder()
            // DCV single, 1 s.
            .Add(0x02, 0x00, 0x03,
                Value(1234), Value(1234, range: 1, negative: true), Value(0, range: 2, overload: true), Value(9999, range: 3, lowBattery: true))
            // OHM single, 60 s.
            .Add(0x05, 0x00, 0x0B, Value(1234, range: 4), Value(6000, range: 0))
            // DCA / ACA dual, 0,5 s (bit 5 = A).
            .Add(0x08, 0x01, 0x80 | 0x20 | 0x02, Dual(Value(1234), Value(567, range: 1)))
            // T1-T2 / T2 em °F, dual, 5 s.
            .Add(0x06, 0x03, 0x80 | 0x10 | 0x07, Dual(Value(72, negative: true), Value(0, overload: true)), Dual(Value(5), Value(80)));
        byte[] image = builder.Build();

        var memory = Bm52xMemory.Decode(image);

        Assert.Empty(memory.Warnings);
        Assert.Equal(4, memory.Header.TotalSessions);
        Assert.Equal(image.Length, memory.Header.TotalBytes);
        Assert.Equal(4, memory.Sessions.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, memory.Sessions.Select(s => s.Number));
        Assert.Equal(builder.HeadPointers, memory.Sessions.Select(s => s.HeadPointer));
        Assert.Equal("A0_0080", memory.Sessions[0].HeadAddress);
        for (int i = 1; i < 4; i++)
        {
            Assert.Equal(memory.Sessions[i - 1].HeadAddress, memory.Sessions[i].PreviousAddress);
            Assert.Equal(memory.Sessions[i].HeadAddress, memory.Sessions[i - 1].NextAddress);
        }
        Assert.Equal(image.Length, memory.Sessions[^1].NextPointer);

        var dcv = memory.Sessions[0];
        Assert.False(dcv.IsDual);
        Assert.Equal(("DCV", (string?)null, TimeSpan.FromSeconds(1)), (dcv.MainFunction, dcv.SecondaryFunction, dcv.Interval));
        Assert.Equal(4, dcv.Samples.Count);
        Assert.All(dcv.Samples, s => Assert.Null(s.Secondary));
        Assert.Equal(new[] { "1.234", "-12.34", "OL", "9999" }, dcv.Samples.Select(s => s.Main.Text));
        Assert.Equal(new double?[] { 1.234, -12.34, null, 9999.0 }, dcv.Samples.Select(s => s.Main.Value));
        Assert.Equal(new[] { false, false, false, true }, dcv.Samples.Select(s => s.Main.LowBattery));
        Assert.True(dcv.Samples[2].Main.Overload);
        Assert.Null(dcv.Samples[2].Main.SiValue);
        Assert.Equal(MeasurementMode.DC, dcv.Samples[0].Main.Mode);
        Assert.Equal(TimeSpan.FromSeconds(3), dcv.Samples[3].Elapsed);

        var ohm = memory.Sessions[1];
        Assert.Equal("OHM", ohm.MainFunction);
        Assert.Equal(("1.234", "M", "Ohm", 1234000.0), (ohm.Samples[0].Main.Text, ohm.Samples[0].Main.Prefix, ohm.Samples[0].Main.Unit, ohm.Samples[0].Main.SiValue));
        Assert.Equal("MΩ", ohm.Samples[0].Main.UnitSymbol);
        Assert.Equal(("600.0", "", 600.0), (ohm.Samples[1].Main.Text, ohm.Samples[1].Main.Prefix, ohm.Samples[1].Main.SiValue));
        Assert.Equal(TimeSpan.FromMinutes(1), ohm.Samples[1].Elapsed);

        var amp = memory.Sessions[2];
        Assert.True(amp.IsDual);
        Assert.Equal(("DCA", "ACA"), (amp.MainFunction, amp.SecondaryFunction));
        var ampSample = Assert.Single(amp.Samples);
        Assert.Equal(("1.234", "A", MeasurementMode.DC), (ampSample.Main.Text, ampSample.Main.Unit, ampSample.Main.Mode));
        Assert.Equal(("5.67", "A", MeasurementMode.AC), (ampSample.Secondary!.Text, ampSample.Secondary.Unit, ampSample.Secondary.Mode));
        Assert.False(ampSample.Secondary.LowBattery);

        var temperature = memory.Sessions[3];
        Assert.Equal(("T1-T2(°F)", "T2(°F)"), (temperature.MainFunction, temperature.SecondaryFunction));
        Assert.Equal(TimeSpan.FromSeconds(5), temperature.Interval);
        Assert.Equal(("-72", -72.0, -72.0, "°F", 0), (temperature.Samples[0].Main.Text, temperature.Samples[0].Main.Value, temperature.Samples[0].Main.SiValue, temperature.Samples[0].Main.Unit, temperature.Samples[0].Main.Decimals));
        Assert.Equal(("----", (double?)null, true), (temperature.Samples[0].Secondary!.Text, temperature.Samples[0].Secondary!.Value, temperature.Samples[0].Secondary!.Overload));
        Assert.Equal(("5", "80"), (temperature.Samples[1].Main.Text, temperature.Samples[1].Secondary!.Text));
        Assert.Equal(TimeSpan.FromSeconds(5), temperature.Samples[1].Elapsed);
    }

    [Fact]
    public void Decodes_single_synthetic_session()
    {
        byte[] image = new LoggerImageBuilder()
            .Add(0x03, 0x00, 0x01, Value(1234), Value(5999, range: 1, negative: true), Value(0))
            .Build();

        var memory = Bm52xMemory.Decode(image);

        Assert.Empty(memory.Warnings);
        var session = Assert.Single(memory.Sessions);
        Assert.False(session.IsDual);
        Assert.Equal("DCmV", session.MainFunction);
        Assert.Null(session.SecondaryFunction);
        Assert.Equal(9, session.LengthBytes);
        Assert.Equal(3, session.DeclaredSampleCount);
        Assert.Equal(new[] { "12.34", "-599.9", "0.00" }, session.Samples.Select(s => s.Main.Text));
        Assert.Equal(new double?[] { 0.01234, -0.5999, 0.0 }, session.Samples.Select(s => s.Main.SiValue));
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, session.Samples.Select(s => s.Elapsed));
        Assert.Equal("mV", session.Samples[0].Main.UnitSymbol);
    }

    public static TheoryData<int, int, int, string, string?> FunctionEncoder() => new()
    {
        { 0x01, 0x00, 0x00, "ACV", "Hz" },
        { 0x01, 0x01, 0x00, "Hz", "ACV" },
        { 0x02, 0x00, 0x00, "DCV", null },
        { 0x02, 0x01, 0x00, "DCV", "ACV" },
        { 0x02, 0x02, 0x00, "DCV+ACV", "ACV" },
        { 0x03, 0x00, 0x00, "DCmV", null },
        { 0x03, 0x01, 0x00, "DCmV", "ACmV" },
        { 0x03, 0x02, 0x00, "DCmV+ACmV", "ACmV" },
        { 0x03, 0x03, 0x00, "Hz", null },
        { 0x03, 0x04, 0x00, "Duty", null },
        { 0x04, 0x00, 0x00, "ACmV", "Hz" },
        { 0x04, 0x01, 0x00, "Hz", "ACmV" },
        { 0x05, 0x00, 0x00, "OHM", null },
        { 0x05, 0x01, 0x00, "Continuity", null },
        { 0x05, 0x02, 0x00, "nS", null },
        { 0x06, 0x00, 0x00, "T1(°C)", null },
        { 0x06, 0x00, 0x10, "T1(°F)", null },
        { 0x06, 0x01, 0x00, "T2(°C)", null },
        { 0x06, 0x01, 0x10, "T2(°F)", null },
        { 0x06, 0x02, 0x00, "T1(°C)", "T2(°C)" },
        { 0x06, 0x02, 0x10, "T1(°F)", "T2(°F)" },
        { 0x06, 0x03, 0x00, "T1-T2(°C)", "T2(°C)" },
        { 0x06, 0x03, 0x10, "T1-T2(°F)", "T2(°F)" },
        { 0x07, 0x00, 0x00, "Capacitance", null },
        { 0x07, 0x01, 0x00, "Diode", null },
        { 0x08, 0x00, 0x20, "DCA", null },
        { 0x08, 0x00, 0x00, "DCmA", null },
        { 0x08, 0x01, 0x20, "DCA", "ACA" },
        { 0x08, 0x01, 0x00, "DCmA", "ACmA" },
        { 0x08, 0x02, 0x20, "DCA+ACA", "ACA" },
        { 0x08, 0x02, 0x00, "DCmA+ACmA", "ACmA" },
        { 0x08, 0x03, 0x20, "ACA", "Hz" },
        { 0x08, 0x03, 0x00, "ACmA", "Hz" },
        { 0x09, 0x00, 0x00, "DCuA", null },
        { 0x09, 0x01, 0x00, "DCuA", "ACuA" },
        { 0x09, 0x02, 0x00, "DCuA+ACuA", "ACuA" },
        { 0x09, 0x03, 0x00, "ACuA", "Hz" },
    };

    [Theory]
    [MemberData(nameof(FunctionEncoder))]
    public void Decodes_function_encoder(int function, int select, int flags, string main, string? secondary)
    {
        bool dual = secondary is not null;
        byte status = (byte)(flags | (dual ? 0x80 : 0x00));
        byte[] sample = dual ? Dual(Value(1), Value(1)) : Value(1);

        var memory = Bm52xMemory.Decode(new LoggerImageBuilder().Add((byte)function, (byte)select, status, sample).Build());

        Assert.Empty(memory.Warnings);
        var session = Assert.Single(memory.Sessions);
        Assert.Equal(main, session.MainFunction);
        Assert.Equal(secondary, session.SecondaryFunction);
        Assert.Equal(dual, session.IsDual);
        var expectedMode = main.Contains('+') ? MeasurementMode.AcDc
            : main.StartsWith("AC", StringComparison.Ordinal) ? MeasurementMode.AC
            : main.StartsWith("DC", StringComparison.Ordinal) ? MeasurementMode.DC
            : MeasurementMode.None;
        Assert.Equal(expectedMode, session.Samples[0].Main.Mode);
    }

    [Theory]
    [InlineData(0x0, 0.05)]
    [InlineData(0x1, 0.1)]
    [InlineData(0x2, 0.5)]
    [InlineData(0x3, 1.0)]
    [InlineData(0x4, 2.0)]
    [InlineData(0x5, 3.0)]
    [InlineData(0x6, 4.0)]
    [InlineData(0x7, 5.0)]
    [InlineData(0x8, 10.0)]
    [InlineData(0x9, 15.0)]
    [InlineData(0xA, 30.0)]
    [InlineData(0xB, 60.0)]
    [InlineData(0xC, 120.0)]
    [InlineData(0xD, 180.0)]
    [InlineData(0xE, 300.0)]
    [InlineData(0xF, 600.0)]
    public void Decodes_interval_table(int code, double seconds)
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, (byte)code, Value(1), Value(2), Value(3), Value(4)).Build();

        var session = Assert.Single(Bm52xMemory.Decode(image).Sessions);

        Assert.Equal(seconds, session.Interval.TotalSeconds);
        Assert.Equal(seconds * 3, session.Samples[3].Elapsed.TotalSeconds, 9);
        Assert.Equal(session.Interval * 3, session.Samples[3].Elapsed);
    }

    public static TheoryData<int, int, int, int, int, string, string, string, string, string> RangeTable() => new()
    {
        // função, seleção, status, faixa, count → texto, valor, prefixo, unidade, valor SI
        { 0x01, 0x00, 0x80, 0, 13, "0.013", "0.013", "", "V", "0.013" },
        { 0x02, 0x00, 0x00, 1, 1234, "12.34", "12.34", "", "V", "12.34" },
        { 0x02, 0x00, 0x00, 2, 1000, "100.0", "100", "", "V", "100" },
        { 0x02, 0x00, 0x00, 3, 1000, "1000", "1000", "", "V", "1000" },
        { 0x03, 0x00, 0x00, 0, 287, "2.87", "2.87", "m", "V", "0.00287" },
        { 0x03, 0x00, 0x00, 1, 5999, "599.9", "599.9", "m", "V", "0.5999" },
        { 0x03, 0x03, 0x00, 2, 5000, "500.0", "500", "", "Hz", "500" },
        { 0x03, 0x03, 0x00, 3, 1234, "1.234", "1.234", "k", "Hz", "1234" },
        { 0x03, 0x03, 0x00, 5, 1234, "123.4", "123.4", "k", "Hz", "123400" },
        { 0x03, 0x03, 0x00, 6, 1234, "1.234", "1.234", "M", "Hz", "1234000" },
        { 0x03, 0x04, 0x00, 0, 5000, "50.00", "50", "", "%", "50" },
        { 0x03, 0x04, 0x00, 1, 1000, "100.0", "100", "", "%", "100" },
        { 0x05, 0x00, 0x00, 0, 6000, "600.0", "600", "", "Ohm", "600" },
        { 0x05, 0x00, 0x00, 1, 1234, "1.234", "1.234", "k", "Ohm", "1234" },
        { 0x05, 0x00, 0x00, 2, 1234, "12.34", "12.34", "k", "Ohm", "12340" },
        { 0x05, 0x00, 0x00, 3, 1234, "123.4", "123.4", "k", "Ohm", "123400" },
        { 0x05, 0x00, 0x00, 5, 1234, "12.34", "12.34", "M", "Ohm", "12340000" },
        { 0x05, 0x01, 0x00, 0, 123, "12.3", "12.3", "", "Ohm", "12.3" },
        { 0x05, 0x02, 0x00, 0, 1234, "12.34", "12.34", "n", "S", "1.234E-08" },
        { 0x07, 0x00, 0x00, 0, 1234, "12.34", "12.34", "n", "F", "1.234E-08" },
        { 0x07, 0x00, 0x00, 1, 1234, "123.4", "123.4", "n", "F", "1.234E-07" },
        { 0x07, 0x00, 0x00, 2, 1234, "1.234", "1.234", "u", "F", "1.234E-06" },
        { 0x07, 0x00, 0x00, 4, 5000, "500.0", "500", "u", "F", "0.0005" },
        { 0x07, 0x00, 0x00, 5, 287, "0.287", "0.287", "m", "F", "0.000287" },
        { 0x07, 0x00, 0x00, 6, 2500, "25.00", "25", "m", "F", "0.025" },
        { 0x07, 0x01, 0x00, 0, 512, "0.512", "0.512", "", "V", "0.512" },
        { 0x08, 0x00, 0x20, 0, 5999, "5.999", "5.999", "", "A", "5.999" },
        { 0x08, 0x00, 0x20, 1, 1234, "12.34", "12.34", "", "A", "12.34" },
        { 0x08, 0x00, 0x00, 0, 1234, "12.34", "12.34", "m", "A", "0.01234" },
        { 0x08, 0x00, 0x00, 1, 1234, "123.4", "123.4", "m", "A", "0.1234" },
        { 0x09, 0x00, 0x00, 0, 287, "28.7", "28.7", "u", "A", "2.87E-05" },
        { 0x09, 0x00, 0x00, 1, 6000, "6000", "6000", "u", "A", "0.006" },
        { 0x06, 0x00, 0x00, 0, 25, "25", "25", "", "°C", "25" },
        { 0x06, 0x00, 0x10, 7, 1999, "1999", "1999", "", "°F", "1999" },
    };

    [Theory]
    [MemberData(nameof(RangeTable))]
    public void Decodes_range_table(int function, int select, int status, int range, int count, string text, string value, string prefix, string unit, string siValue)
    {
        bool dual = (status & 0x80) != 0;
        byte[] sample = dual ? Dual(Value(count, range), Value(0)) : Value(count, range);
        var memory = Bm52xMemory.Decode(new LoggerImageBuilder().Add((byte)function, (byte)select, (byte)status, sample).Build());

        Assert.Empty(memory.Warnings);
        var main = Assert.Single(Assert.Single(memory.Sessions).Samples).Main;
        Assert.True(main.IsRangeValid);
        Assert.Equal(range, main.Range);
        Assert.Equal(count, main.Count);
        Assert.Equal(text, main.Text);
        Assert.Equal(TestData.ParseDouble(value), main.Value);
        Assert.Equal(prefix, main.Prefix);
        Assert.Equal(unit, main.Unit);
        Assert.Equal(TestData.ParseDouble(siValue), main.SiValue);
        Assert.Equal(text.Contains('.') ? text.Length - text.IndexOf('.') - 1 : 0, main.Decimals);

        var negative = Bm52xMemory.Decode(new LoggerImageBuilder()
            .Add((byte)function, (byte)select, (byte)status, dual ? Dual(Value(count, range, negative: true), Value(0)) : Value(count, range, negative: true))
            .Build()).Sessions[0].Samples[0].Main;
        Assert.Equal("-" + text, negative.Text);
        Assert.Equal(-TestData.ParseDouble(value), negative.Value);
        Assert.Equal(-TestData.ParseDouble(siValue), negative.SiValue);
    }

    [Fact]
    public void Invalid_range_yields_warning_and_null_value()
    {
        byte[] image = new LoggerImageBuilder().Add(0x03, 0x00, 0x00, Value(1234, range: 2), Value(1234, range: 0), Value(77, range: 0xF)).Build();

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("2 valor(es) do display principal com faixa inválida para DCmV", warning);
        var samples = memory.Sessions[0].Samples;
        Assert.Equal(3, samples.Count);
        Assert.False(samples[0].Main.IsRangeValid);
        Assert.Null(samples[0].Main.Value);
        Assert.Null(samples[0].Main.Decimals);
        Assert.Equal("1234", samples[0].Main.Text);
        Assert.Equal(("", ""), (samples[0].Main.Prefix, samples[0].Main.Unit));
        Assert.True(samples[1].Main.IsRangeValid);
        Assert.Equal(12.34, samples[1].Main.Value);
    }

    [Fact]
    public void Unknown_function_is_exposed_with_raw_counts()
    {
        byte[] image = new LoggerImageBuilder().Add(0x0A, 0x00, 0x80, Dual(Value(13), Value(42, negative: true))).Build();

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("função desconhecida (Bfunction 0Ah, Bselect 00h, Bstatus 80h)", warning);
        var session = memory.Sessions[0];
        Assert.Equal(("?", "?"), (session.MainFunction, session.SecondaryFunction));
        var sample = Assert.Single(session.Samples);
        Assert.Equal(("13", (double?)null, false), (sample.Main.Text, sample.Main.Value, sample.Main.IsRangeValid));
        Assert.Equal(("-42", 42), (sample.Secondary!.Text, sample.Secondary.Count));
    }

    [Fact]
    public void Dual_flag_disagreeing_with_table_3_warns()
    {
        // ACV + Hz é dual na Table 3, mas o Bstatus diz single: lê registros de 3 bytes.
        var single = Bm52xMemory.Decode(new LoggerImageBuilder().Add(0x01, 0x00, 0x00, Value(13), Value(14)).Build());
        Assert.Contains(single.Warnings, w => w.Contains("indica single, mas a Table 3 prevê dual para ACV", StringComparison.Ordinal));
        Assert.Equal(2, single.Sessions[0].Samples.Count);
        Assert.Null(single.Sessions[0].SecondaryFunction);

        // OHM é single, mas o Bstatus diz dual.
        var dual = Bm52xMemory.Decode(new LoggerImageBuilder().Add(0x05, 0x00, 0x80, Dual(Value(13), Value(14))).Build());
        string warning = Assert.Single(dual.Warnings);
        Assert.Contains("indica dual, mas a Table 3 prevê single para OHM", warning);
        var sample = Assert.Single(dual.Sessions[0].Samples);
        Assert.Equal("?", dual.Sessions[0].SecondaryFunction);
        Assert.Equal(("14", false), (sample.Secondary!.Text, sample.Secondary.IsRangeValid));
    }

    [Fact]
    public void Count_above_9999_is_flagged_as_suspicious()
    {
        var memory = Bm52xMemory.Decode(new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(0xFFFF), Value(10000), Value(9999)).Build());

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("2 contagem(ns) do display principal acima de 9999 (1ª na amostra 0: 65535)", warning);
        Assert.Equal(65.535, memory.Sessions[0].Samples[0].Main.Value);
    }

    [Fact]
    public void Wrong_start_markers_warn_but_session_is_decoded()
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1234)).Build();
        image[6] = 0x00;
        image[7] = 0x55;

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("início DLE/STX = 00 55 no ponteiro 6 (A0_0080), esperado EE A0", warning);
        var session = Assert.Single(memory.Sessions);
        Assert.False(session.HeadMarkersOk);
        Assert.Equal(1.234, session.Samples[0].Main.Value);
    }

    [Fact]
    public void Wrong_end_markers_warn()
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1234)).Build();
        image[^1] = 0xC1;

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("fim DLE/ETX = EE C1 no ponteiro 23, esperado EE C0", warning);
        Assert.False(memory.Sessions[0].EndMarkersOk);
        Assert.Single(memory.Sessions[0].Samples);
    }

    [Fact]
    public void Length_not_multiple_of_sample_size_warns()
    {
        byte[] image = new LoggerImageBuilder().AddRaw(0x02, 0x00, 0x00, [.. Value(1), .. Value(2), 0x00]).Build();

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("comprimento 7 não é múltiplo de 3", warning);
        Assert.Equal(2, memory.Sessions[0].Samples.Count);
        Assert.Equal(2, memory.Sessions[0].DeclaredSampleCount);
        Assert.True(memory.Sessions[0].EndMarkersOk);
    }

    [Fact]
    public void Wrong_next_address_falls_back_to_the_computed_end()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Add(0x05, 0x00, 0x00, Value(2));
        byte[] image = builder.Build();
        int ns = builder.HeadPointers[0] + 5;
        image[ns] = 0x00;
        image[ns + 1] = 0x10;

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("Sessão 1: NS = A0_1000", warning);
        Assert.Contains($"usando o ponteiro {builder.HeadPointers[1]}", warning);
        Assert.Equal(2, memory.Sessions.Count);
        Assert.Equal("OHM", memory.Sessions[1].MainFunction);
    }

    [Fact]
    public void Wrong_length_falls_back_to_the_next_address()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1), Value(2)).Add(0x05, 0x00, 0x00, Value(3));
        byte[] image = builder.Build();
        image[builder.HeadPointers[0] + 11] = 3; // comprimento 6 → 3

        var memory = Bm52xMemory.Decode(image);

        Assert.Contains(memory.Warnings, w => w.Contains("Sessão 1: fim DLE/ETX", StringComparison.Ordinal));
        Assert.Contains(memory.Warnings, w => w.Contains($"usando o ponteiro {builder.HeadPointers[1]}", StringComparison.Ordinal));
        Assert.Equal(2, memory.Sessions.Count);
        Assert.Single(memory.Sessions[0].Samples);
        Assert.Equal(("OHM", 0.3), (memory.Sessions[1].MainFunction, memory.Sessions[1].Samples[0].Main.Value));
    }

    [Fact]
    public void Unrecoverable_chain_stops_with_warning()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Add(0x05, 0x00, 0x00, Value(2)).Add(0x05, 0x00, 0x00, Value(3));
        byte[] image = builder.Build();
        image[builder.HeadPointers[0] + 11] = 9;            // comprimento errado
        image[builder.HeadPointers[0] + 5] = 0x00;          // e NS também
        image[builder.HeadPointers[1]] = 0x00;              // sem marcador no início da sessão 2

        var memory = Bm52xMemory.Decode(image);

        Assert.Single(memory.Sessions);
        Assert.Contains(memory.Warnings, w => w.Contains("Sessão 2: não foi possível localizar o início", StringComparison.Ordinal)
            && w.Contains("2 sessão(ões) ignorada(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_previous_address_warns()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Add(0x05, 0x00, 0x00, Value(2));
        byte[] image = builder.Build();
        image[builder.HeadPointers[1] + 2] = 0x81;

        var memory = Bm52xMemory.Decode(image);

        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("Sessão 2: PS = A0_0081, mas a sessão anterior começa em A0_0080", warning);
        Assert.Equal(2, memory.Sessions.Count);
    }

    [Fact]
    public void Truncated_image_decodes_what_is_available()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1), Value(2), Value(3)).Add(0x05, 0x00, 0x00, Value(4));
        byte[] image = builder.Build();

        Assert.Equal([6, 31], builder.HeadPointers);

        var cutInData = Bm52xMemory.Decode(image.AsSpan(0, builder.HeadPointers[0] + 14 + 7));
        Assert.Equal(
            [
                "TotalBytes = 50, mas a imagem tem 27 bytes; só a parte disponível foi decodificada.",
                "Sessão 1: os dados declarados (9 bytes) passam do fim dos dados disponíveis (27 bytes); 2 de 3 amostras decodificadas.",
                "Sessão 2: o cabeçalho no ponteiro 31 (A0_0099) passa do fim dos dados (27 bytes); 1 sessão(ões) ignorada(s).",
            ],
            cutInData.Warnings);
        var partial = Assert.Single(cutInData.Sessions);
        Assert.Equal(new double?[] { 0.001, 0.002 }, partial.Samples.Select(s => s.Main.Value));
        Assert.False(partial.EndMarkersOk);

        var cutInSecondHead = Bm52xMemory.Decode(image.AsSpan(0, builder.HeadPointers[1] + 10));
        Assert.Equal(
            [
                "TotalBytes = 50, mas a imagem tem 41 bytes; só a parte disponível foi decodificada.",
                "Sessão 2: o cabeçalho no ponteiro 31 (A0_0099) passa do fim dos dados (41 bytes); 1 sessão(ões) ignorada(s).",
            ],
            cutInSecondHead.Warnings);
        var complete = Assert.Single(cutInSecondHead.Sessions);
        Assert.Equal(3, complete.Samples.Count);
        Assert.True(complete.EndMarkersOk);
    }

    [Fact]
    public void TotalBytes_smaller_than_the_sessions_limits_the_decoded_data()
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1), Value(2), Value(3)).Build();
        image[1] = 26; // TotalBytes cobre só 2 amostras; a imagem tem todas.

        var memory = Bm52xMemory.Decode(image);

        Assert.Equal(
            [
                "Sessão 1: os dados declarados (9 bytes) passam do fim dos dados disponíveis (26 bytes); 2 de 3 amostras decodificadas.",
                "A última sessão termina no ponteiro 31, mas TotalBytes = 26.",
            ],
            memory.Warnings);
        Assert.Equal(2, memory.Sessions[0].Samples.Count);
    }

    [Theory]
    [InlineData(0xA0, 0x09, 0x5A, 2272)]
    [InlineData(0xA1, 0x09, 0x5A, 2272)]
    [InlineData(0xA2, 0x00, 0x80, 65_542)]
    [InlineData(0xA3, 0xFF, 0xFF, 0x1FFFF - 0x7A)]
    [InlineData(0xA4, 0x00, 0x00, null)]
    [InlineData(0xFF, 0xFF, 0xFF, null)]
    [InlineData(0xA0, 0x00, 0x79, null)]
    [InlineData(0x00, 0x00, 0x00, null)]
    public void Next_pointer_is_null_outside_the_physical_memory(int a2, int a1, int a0, int? pointer)
    {
        // A memória vai de A0_007Ah a A3_FFFFh; o NS da última sessão é "don't care" e pode trazer qualquer coisa.
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Build();
        image[11] = (byte)a0;
        image[12] = (byte)a1;
        image[13] = (byte)a2;

        var memory = Bm52xMemory.Decode(image);

        Assert.Empty(memory.Warnings);
        Assert.Equal(pointer, memory.Sessions[0].NextPointer);
        Assert.Equal(Bm52xMemory.FormatAddress((byte)a2, (byte)a1, (byte)a0), memory.Sessions[0].NextAddress);
    }

    [Fact]
    public void Next_address_outside_the_memory_falls_back_to_the_computed_end()
    {
        var builder = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Add(0x05, 0x00, 0x00, Value(2));
        byte[] image = builder.Build();
        image.AsSpan(builder.HeadPointers[0] + 5, 3).Fill(0xFF);

        var memory = Bm52xMemory.Decode(image);

        Assert.Equal(
            [$"Sessão 1: NS = FF_FFFF (endereço inválido), mas a sessão termina no ponteiro {builder.HeadPointers[1]}; usando o ponteiro {builder.HeadPointers[1]}."],
            memory.Warnings);
        Assert.Null(memory.Sessions[0].NextPointer);
        Assert.Equal(("OHM", 0.2), (memory.Sessions[1].MainFunction, memory.Sessions[1].Samples[0].Main.Value));
    }

    [Fact]
    public void Overload_counts_are_not_flagged_as_suspicious()
    {
        var overload = Bm52xMemory.Decode(new LoggerImageBuilder()
            .Add(0x01, 0x00, 0x80, Dual(Value(0xFFFF, overload: true), Value(0xFFFF, overload: true)), Dual(Value(13), Value(0)))
            .Build());
        Assert.Empty(overload.Warnings);
        Assert.Equal(("OL", (double?)null), (overload.Sessions[0].Samples[0].Main.Text, overload.Sessions[0].Samples[0].Main.Value));

        var secondary = Bm52xMemory.Decode(new LoggerImageBuilder().Add(0x01, 0x00, 0x80, Dual(Value(13), Value(10_000))).Build());
        Assert.Equal(["Sessão 1: 1 contagem(ns) do display secundário acima de 9999 (1ª na amostra 0: 10000); valores suspeitos."], secondary.Warnings);
    }

    [Theory]
    [InlineData(0, 0, "0.000")]
    [InlineData(0, 1, "0.00")]
    [InlineData(0, 2, "0.0")]
    [InlineData(0, 3, "0")]
    [InlineData(1, 0, "0.001")]
    [InlineData(1, 1, "0.01")]
    [InlineData(1, 2, "0.1")]
    [InlineData(1, 3, "1")]
    [InlineData(10, 0, "0.010")]
    [InlineData(999, 0, "0.999")]
    [InlineData(9999, 0, "9.999")]
    [InlineData(9999, 3, "9999")]
    [InlineData(65_535, 0, "65.535")]
    [InlineData(65_535, 2, "6553.5")]
    [InlineData(65_535, 3, "65535")]
    public void Formats_counts_with_the_range_decimals(int count, int range, string text)
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(count, range), Value(count, range, negative: true)).Build();

        var samples = Bm52xMemory.Decode(image).Sessions[0].Samples;

        Assert.Equal(text, samples[0].Main.Text);
        Assert.Equal("-" + text, samples[1].Main.Text);
        Assert.Equal(TestData.ParseDouble(text), samples[0].Main.Value);
    }

    [Fact]
    public void Random_images_never_throw_and_keep_the_invariants()
    {
        var random = new Random(52_88);
        for (int i = 0; i < 5_000; i++)
        {
            byte[] image = new byte[random.Next(0, 600)];
            random.NextBytes(image);
            if (image.Length >= 8)
            {
                // Cabeçalho plausível na maioria dos casos, para que as sessões sejam de fato percorridas.
                if (random.Next(4) != 0)
                {
                    int total = random.Next(0, image.Length + 50);
                    (image[1], image[2], image[3]) = ((byte)total, (byte)(total >> 8), 0);
                    (image[4], image[5]) = ((byte)random.Next(0, 6), 0);
                }
                if (random.Next(2) == 0)
                    (image[6], image[7]) = (0xEE, 0xA0);
            }

            LoggerMemory memory;
            try
            {
                memory = Bm52xMemory.Decode(image);
            }
            catch (Bm52xFrameException)
            {
                Assert.True(image.Length < 6);
                continue;
            }

            Assert.True(memory.Sessions.Count <= memory.Header.TotalSessions);
            int previousHead = 0;
            foreach (var session in memory.Sessions)
            {
                Assert.True(session.HeadPointer > previousHead);
                previousHead = session.HeadPointer;
                Assert.True(session.Samples.Count <= session.DeclaredSampleCount);
                Assert.All(session.Samples, s =>
                {
                    Assert.Equal(session.IsDual, s.Secondary is not null);
                    foreach (var value in (LoggerValue?[])[s.Main, s.Secondary])
                    {
                        if (value is null)
                            continue;
                        Assert.Equal(value.Value is null, value.SiValue is null);
                        if (value.Overload || !value.IsRangeValid)
                            Assert.Null(value.Value);
                        Assert.Equal(6, value.Raw!.Length);
                    }
                });
            }
        }
    }

    [Fact]
    public void Command_8A_rereads_the_current_block()
    {
        // cmd-sequences: 88, 8A, 89, 8A. "P − 24 e lê 24 bytes" (§4.1).
        var lines = TestData.ReadCapture(TestData.CapturePath("cmd-sequences-2026-10-22.txt"))
            .Where(l => l.Command is "5288" or "5289" or "528A").ToList();
        Assert.Equal(["5288", "528A", "5289", "528A"], lines.Select(l => l.Command));

        var blocks = lines.Select(l => Bm52xMemory.ParseBlock(l.Frame)).ToList();
        Assert.All(blocks, b => Assert.True(b.IsChecksumValid));
        Assert.Equal(blocks[0].Data, blocks[1].Data);
        Assert.Equal(blocks[2].Data, blocks[3].Data);
        Assert.Equal("01E008000100EEA01804A05A09A0010080CA0800000D0000", Convert.ToHexString(blocks[0].Data));
        Assert.Equal("0000000D00000000000D00000000000D00000000000D0000", Convert.ToHexString(blocks[2].Data));
    }

    [Fact]
    public void ParseBlock_reports_every_bad_report_id()
    {
        byte[] frame = HeadFrame();
        frame[0] = 0x01;
        frame[27] = 0x02;

        var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.ParseBlock(frame));
        Assert.Equal("byte 1 (report ID) = 01, esperado 00; byte 28 (report ID) = 02, esperado 00", exception.Message);
    }

    [Fact]
    public void More_sessions_declared_than_present_warns()
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Build();
        image[4] = 3;

        var memory = Bm52xMemory.Decode(image);

        Assert.Single(memory.Sessions);
        string warning = Assert.Single(memory.Warnings);
        Assert.Contains("Sessão 2: o cabeçalho no ponteiro 25 (A0_0093) passa do fim dos dados (25 bytes); 2 sessão(ões) ignorada(s)", warning);
    }

    [Fact]
    public void TotalBytes_mismatch_warns_and_extra_bytes_are_ignored()
    {
        byte[] image = new LoggerImageBuilder().Add(0x02, 0x00, 0x00, Value(1)).Build();

        var padded = Bm52xMemory.Decode([.. image, 0xEE, 0xA0, 0x01, 0x02]);
        Assert.Empty(padded.Warnings);
        Assert.Single(padded.Sessions);

        image[1] += 4; // TotalBytes maior que o fim real da sessão
        var mismatch = Bm52xMemory.Decode([.. image, 0x00, 0x00, 0x00, 0x00]);
        string warning = Assert.Single(mismatch.Warnings);
        Assert.Contains("A última sessão termina no ponteiro 25, mas TotalBytes = 29", warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sessions_beyond_64_KiB_use_pages_A2_and_A3(bool readAliases)
    {
        // 10 920 amostras duplas = 65 520 bytes: a 2ª sessão começa no ponteiro 65 542 = A2_0080.
        var big = Enumerable.Range(0, 10_920).Select(i => Dual(Value(i % 10_000), Value(0))).ToArray();
        var builder = new LoggerImageBuilder { UseReadAliases = readAliases }
            .Add(0x01, 0x00, 0x80, big)
            .Add(0x02, 0x00, 0x03, Value(4321));
        byte[] image = builder.Build();

        var memory = Bm52xMemory.Decode(image);

        Assert.Empty(memory.Warnings);
        Assert.Equal(2, memory.Sessions.Count);
        Assert.Equal(65_542, builder.HeadPointers[1]);
        Assert.Equal(readAliases ? "A3_0080" : "A2_0080", memory.Sessions[0].NextAddress);
        Assert.Equal(65_542, memory.Sessions[0].NextPointer);
        Assert.Equal("A2_0080", memory.Sessions[1].HeadAddress);
        Assert.Equal(readAliases ? "A1_0080" : "A0_0080", memory.Sessions[1].PreviousAddress);
        Assert.Equal(10_920, memory.Sessions[0].Samples.Count);
        Assert.Equal(9.999, memory.Sessions[0].Samples[9_999].Main.Value);
        // A amostra 10 899 começa no ponteiro 20 + 6 × 10 899 = 65 414 = A2_0000: a imagem é contínua na troca de página.
        Assert.Equal(20 + 6 * 10_899, Bm52xMemory.AddressToPointer(0xA2, 0x00, 0x00));
        Assert.Equal(
            new double?[] { 0.898, 0.899, 0.900, 0.919 },
            new[] { 10_898, 10_899, 10_900, 10_919 }.Select(i => memory.Sessions[0].Samples[i].Main.Value));
        Assert.Equal(4.321, Assert.Single(memory.Sessions[1].Samples).Main.Value);
        Assert.Equal(image.Length, memory.Sessions[1].NextPointer);
    }

    private static byte[] HeadFrame() => Convert.FromHexString(
        "0001E008000100EEA0001804A05A09A001000080CA0800000D0000009705000000000000");

    private static IEnumerable<int> ExpandRuns(string runs)
    {
        foreach (string run in runs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = run.Split(':');
            string[] bounds = parts[0].Split('–');
            int first = int.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
            int last = int.Parse(bounds[^1], System.Globalization.CultureInfo.InvariantCulture);
            int count = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            for (int i = first; i <= last; i++)
                yield return count;
        }
    }
}
