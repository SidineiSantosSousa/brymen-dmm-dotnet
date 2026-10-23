using System.Text.Json;

namespace Brymen.Dmm.Protocol.Tests;

/// <summary>Todos os vetores de <c>docs/protocol/test-vectors.json</c>, campo a campo.</summary>
public class TestVectorTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string> AllVectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var vector in TestData.Vectors)
            data.Add(vector.GetProperty("name").GetString()!, vector.GetProperty("kind").GetString()!);
        return data;
    }

    [Fact]
    public void Vector_file_has_every_known_kind()
    {
        var kinds = TestData.Vectors.Select(v => v.GetProperty("kind").GetString()).ToList();
        Assert.Equal(14, kinds.Count);
        Assert.All(kinds, k => Assert.True(k is "rtd" or "memory-block" or "memory-dump", k));
        Assert.Contains(TestData.Vectors, v => v.GetProperty("kind").GetString() == "rtd"
            && !v.GetProperty("expected").GetProperty("valid").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void Vector_matches_expected(string name, string kind)
    {
        var vector = TestData.Vector(name);
        switch (kind)
        {
            case "rtd":
                CheckRtd(vector);
                break;
            case "memory-block":
                CheckMemoryBlock(vector);
                break;
            case "memory-dump":
                CheckMemoryDump(vector);
                break;
            default:
                Assert.Fail($"tipo de vetor desconhecido: {kind}");
                break;
        }
    }

    private static void CheckRtd(JsonElement vector)
    {
        byte[] frame = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
        var expected = vector.GetProperty("expected");
        bool valid = expected.GetProperty("valid").GetBoolean();

        bool decoded = Bm52xRealtime.TryDecode(frame, Timestamp, out var reading, out string? error);
        Assert.Equal(valid, decoded);

        if (!valid)
        {
            Assert.Null(reading);
            Assert.Equal(expected.GetProperty("error").GetString(), error);
            var exception = Assert.Throws<Bm52xFrameException>(() => Bm52xRealtime.Decode(frame, Timestamp));
            Assert.Equal(error, exception.Message);
            return;
        }

        Assert.Null(error);
        Assert.NotNull(reading);
        Assert.Equal(Timestamp, reading.Timestamp);
        Assert.Equal(Convert.ToHexString(frame), reading.RawHex);
        AssertDisplay(expected.GetProperty("main"), reading.Main);
        AssertDisplay(expected.GetProperty("secondary"), reading.Secondary);

        var expectedFlags = expected.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).Order().ToList();
        Assert.Equal(expectedFlags, DmmFlagNames.ToNames(reading.Flags).Order().ToList());

        var unknown = expected.GetProperty("unknown");
        var diagnostics = reading.Diagnostics;
        Assert.NotNull(diagnostics);
        Assert.Equal(expected.GetProperty("modelBytes").GetString(), diagnostics.ModelBytes);
        Assert.Equal(unknown.GetProperty("byte2").GetString(), diagnostics.Byte2.ToString("X2"));
        Assert.Equal(unknown.GetProperty("byte18").GetString(), diagnostics.Byte18.ToString("X2"));
        Assert.Equal(unknown.GetProperty("byte24LowNibble").GetInt32(), diagnostics.Byte24LowNibble);
        Assert.Equal(unknown.GetProperty("bytes25to27").GetString(), diagnostics.Bytes25To27);

        Assert.Equal(reading, Bm52xRealtime.Decode(frame, Timestamp));
    }

    private static void AssertDisplay(JsonElement expected, DisplayReading? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetProperty("digits").GetString(), actual.Digits);
        Assert.Equal(expected.GetProperty("text").GetString(), actual.Text);
        Assert.Equal(expected.GetProperty("negative").GetBoolean(), actual.Negative);
        Assert.Equal(expected.GetProperty("overload").GetBoolean(), actual.Overload);
        Assert.Equal(TestData.NullableDouble(expected.GetProperty("value")), actual.Value);
        Assert.Equal(expected.GetProperty("prefix").GetString(), actual.Prefix);
        Assert.Equal(expected.GetProperty("unit").GetString(), actual.Unit);
        Assert.Equal(expected.GetProperty("mode").GetString(), TestData.ModeName(actual.Mode));
        Assert.Equal(TestData.NullableDouble(expected.GetProperty("siValue")), actual.SiValue);
    }

    private static void CheckMemoryBlock(JsonElement vector)
    {
        byte[] frame = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
        var expected = vector.GetProperty("expected");

        if (!expected.GetProperty("reportIdsOk").GetBoolean())
        {
            Assert.Throws<Bm52xFrameException>(() => Bm52xMemory.ParseBlock(frame));
            return;
        }

        var block = Bm52xMemory.ParseBlock(frame);
        Assert.Equal(expected.GetProperty("checksumRx").GetInt32(), block.ChecksumReceived);
        Assert.Equal(expected.GetProperty("checksumCalc").GetInt32(), block.ChecksumComputed);
        Assert.Equal(expected.GetProperty("checksumOk").GetBoolean(), block.IsChecksumValid);
        Assert.Equal(expected.GetProperty("data24").GetString(), Convert.ToHexString(block.Data));

        if (expected.TryGetProperty("addressStart", out var addressStart))
        {
            int pointerStart = expected.GetProperty("pointerStart").GetInt32();
            Assert.Equal(addressStart.GetString(), Bm52xMemory.PointerToAddress(pointerStart));
        }

        if (expected.TryGetProperty("endMarkerOffsetInBlock", out var endOffset))
        {
            int offset = endOffset.GetInt32();
            Assert.Equal(0xEE, block.Data[offset]);
            Assert.Equal(0xC0, block.Data[offset + 1]);
        }

        if (expected.TryGetProperty("sameAsPrevious89", out var sameAsPrevious) && sameAsPrevious.GetBoolean())
        {
            var capture = TestData.ReadCapture(TestData.CapturePath("memory-dump-2.txt"));
            int index = capture.ToList().FindIndex(l => l.Command == "8A");
            Assert.Equal("89", capture[index - 1].Command);
            Assert.Equal(capture[index - 1].Frame, frame);
        }

        if (expected.TryGetProperty("header", out var header))
        {
            var parsed = Bm52xMemory.ParseHeader(block);
            Assert.Equal(header.GetProperty("modelId").GetInt32(), parsed.ModelId);
            Assert.Equal(header.GetProperty("totalBytes").GetInt32(), parsed.TotalBytes);
            Assert.Equal(header.GetProperty("totalSessions").GetInt32(), parsed.TotalSessions);
            Assert.Equal(header.GetProperty("commandsNeeded").GetInt32(), parsed.BlocksNeeded);
        }

        if (expected.TryGetProperty("firstSessionHead", out var head))
        {
            // Só o 1º bloco: o decodificador avisa que a imagem é curta, mas lê o cabeçalho da sessão.
            var memory = Bm52xMemory.Decode(block.Data);
            int totalBytes = memory.Header.TotalBytes, lengthBytes = head.GetProperty("lengthBytes").GetInt32();
            Assert.Equal(
                new[]
                {
                    Invariant($"TotalBytes = {totalBytes}, mas a imagem tem 24 bytes; só a parte disponível foi decodificada."),
                    Invariant($"Sessão 1: os dados declarados ({lengthBytes} bytes) passam do fim dos dados disponíveis (24 bytes); 0 de {head.GetProperty("samples").GetInt32()} amostras decodificadas."),
                },
                memory.Warnings);
            var session = Assert.Single(memory.Sessions);
            Assert.Equal(head.GetProperty("headAddress").GetString(), session.HeadAddress);
            Assert.Equal(head.GetProperty("dle").GetString(), block.Data[session.HeadPointer].ToString("X2"));
            Assert.Equal(head.GetProperty("stx").GetString(), block.Data[session.HeadPointer + 1].ToString("X2"));
            Assert.True(session.HeadMarkersOk);
            Assert.Equal(head.GetProperty("psAddress").GetString(), session.PreviousAddress);
            Assert.Equal(head.GetProperty("nsAddress").GetString(), session.NextAddress);
            Assert.Equal(head.GetProperty("bfunction").GetInt32(), session.Function);
            Assert.Equal(head.GetProperty("bselect").GetInt32(), session.Select);
            Assert.Equal(head.GetProperty("bstatus").GetInt32(), session.Status);
            Assert.Equal(head.GetProperty("dual").GetBoolean(), session.IsDual);
            Assert.Equal(head.GetProperty("intervalSeconds").GetDouble(), session.Interval.TotalSeconds);
            Assert.Equal(head.GetProperty("mainFunction").GetString(), session.MainFunction);
            Assert.Equal(head.GetProperty("secondaryFunction").GetString(), session.SecondaryFunction);
            Assert.Equal(head.GetProperty("lengthBytes").GetInt32(), session.LengthBytes);
            Assert.Equal(head.GetProperty("samples").GetInt32(), session.DeclaredSampleCount);
            Assert.Empty(session.Samples);
        }

        if (expected.TryGetProperty("firstSamplePartial", out var partial))
        {
            Assert.Equal(partial.GetProperty("ML_0").GetString(), block.Data[20].ToString("X2"));
            Assert.Equal(partial.GetProperty("ML_1").GetString(), block.Data[21].ToString("X2"));
            Assert.Equal(partial.GetProperty("ML_2").GetString(), block.Data[22].ToString("X2"));
            Assert.Equal(partial.GetProperty("SL_0").GetString(), block.Data[23].ToString("X2"));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void CheckMemoryDump(JsonElement vector)
    {
        var expected = vector.GetProperty("expected");
        string file = vector.GetProperty("file").GetString()!;
        byte[] image = TestData.AssembleImage(TestData.ReadCapture(Path.Combine(TestData.ProtocolDirectory, file)));

        Assert.Equal(expected.GetProperty("bytesReadWith95Commands").GetInt32(), image.Length);

        var memory = Bm52xMemory.Decode(image);
        Assert.Empty(memory.Warnings);
        Assert.Equal(expected.GetProperty("modelId").GetInt32(), memory.Header.ModelId);
        Assert.Equal(expected.GetProperty("totalBytes").GetInt32(), memory.Header.TotalBytes);
        Assert.Equal(expected.GetProperty("totalSessions").GetInt32(), memory.Header.TotalSessions);
        Assert.Equal(expected.GetProperty("commandsNeeded").GetInt32(), memory.Header.BlocksNeeded);
        Assert.Equal(
            expected.GetProperty("paddingAfterTotalBytes").GetString(),
            Convert.ToHexString(image.AsSpan(memory.Header.TotalBytes)));

        var expectedSessions = expected.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(expectedSessions.Count, memory.Sessions.Count);
        for (int i = 0; i < expectedSessions.Count; i++)
            AssertSession(expectedSessions[i], memory.Sessions[i], image);
    }

    private static void AssertSession(JsonElement expected, LoggerSession session, byte[] image)
    {
        int size = session.IsDual ? 6 : 3;
        int endPointer = session.HeadPointer + 14 + session.LengthBytes;

        Assert.Equal(expected.GetProperty("index").GetInt32(), session.Number);
        Assert.Equal(expected.GetProperty("headPointer").GetInt32(), session.HeadPointer);
        Assert.Equal(expected.GetProperty("headAddress").GetString(), session.HeadAddress);
        Assert.Equal(expected.GetProperty("dle").GetString(), image[session.HeadPointer].ToString("X2"));
        Assert.Equal(expected.GetProperty("stx").GetString(), image[session.HeadPointer + 1].ToString("X2"));
        Assert.Equal(expected.GetProperty("headOk").GetBoolean(), session.HeadMarkersOk);
        Assert.Equal(expected.GetProperty("psAddress").GetString(), session.PreviousAddress);
        Assert.Equal(expected.GetProperty("nsAddress").GetString(), session.NextAddress);
        Assert.Equal(expected.GetProperty("nsPointer").GetInt32(), session.NextPointer);
        Assert.Equal(expected.GetProperty("bfunction").GetInt32(), session.Function);
        Assert.Equal(expected.GetProperty("bselect").GetInt32(), session.Select);
        Assert.Equal(expected.GetProperty("bstatus").GetInt32(), session.Status);
        Assert.Equal(expected.GetProperty("dual").GetBoolean(), session.IsDual);
        Assert.Equal(expected.GetProperty("amp").GetBoolean(), (session.Status & 0x20) != 0);
        Assert.Equal(expected.GetProperty("fahrenheit").GetBoolean(), (session.Status & 0x10) != 0);
        Assert.Equal(expected.GetProperty("intervalCode").GetInt32(), session.Status & 0x0F);
        Assert.Equal(expected.GetProperty("intervalSeconds").GetDouble(), session.Interval.TotalSeconds);
        Assert.Equal(expected.GetProperty("mainFunction").GetString(), session.MainFunction);
        Assert.Equal(TestData.NullableString(expected.GetProperty("secondaryFunction")), session.SecondaryFunction);
        Assert.Equal(expected.GetProperty("lengthBytes").GetInt32(), session.LengthBytes);
        Assert.Equal(expected.GetProperty("sampleSize").GetInt32(), size);
        Assert.Equal(expected.GetProperty("samples").GetInt32(), session.Samples.Count);
        Assert.Equal(expected.GetProperty("samples").GetInt32(), session.DeclaredSampleCount);
        Assert.Equal(expected.GetProperty("lengthRemainder").GetInt32(), session.LengthBytes % size);
        Assert.Equal(expected.GetProperty("endPointer").GetInt32(), endPointer);
        Assert.Equal(expected.GetProperty("endMarker").GetString(), Convert.ToHexString(image.AsSpan(endPointer, 2)));
        Assert.Equal(expected.GetProperty("endOk").GetBoolean(), session.EndMarkersOk);

        Assert.All(session.Samples, s => Assert.Equal(expected.GetProperty("mainMode").GetString(), TestData.ModeName(s.Main.Mode)));
        string? secondaryMode = TestData.NullableString(expected.GetProperty("secondaryMode"));
        Assert.All(session.Samples, s => Assert.Equal(secondaryMode, s.Secondary is null ? null : TestData.ModeName(s.Secondary.Mode)));

        foreach (var sample in expected.GetProperty("firstSamples").EnumerateArray().Concat(expected.GetProperty("lastSamples").EnumerateArray()))
        {
            int index = sample.GetProperty("i").GetInt32();
            var actual = session.Samples[index];
            Assert.Equal(index, actual.Index);
            Assert.Equal(sample.GetProperty("tSeconds").GetDouble(), actual.Elapsed.TotalSeconds);
            AssertValue(sample.GetProperty("main"), actual.Main);
            if (sample.TryGetProperty("secondary", out var secondary))
            {
                Assert.NotNull(actual.Secondary);
                AssertValue(secondary, actual.Secondary);
            }
            else
            {
                Assert.Null(actual.Secondary);
            }
        }

        Assert.Equal(
            expected.GetProperty("mainCounts").EnumerateArray().Select(c => c.GetInt32()),
            session.Samples.Select(s => s.Main.Count));
        Assert.Equal(
            expected.GetProperty("secondaryCounts").EnumerateArray().Select(c => c.GetInt32()),
            session.Samples.Select(s => s.Secondary!.Count));

        static bool Clear(LoggerValue v) => !v.Negative && !v.Overload && !v.LowBattery && v.Range == 0 && v.Raw![..2] == "00";
        Assert.Equal(expected.GetProperty("allMainFlagsClear").GetBoolean(), session.Samples.All(s => Clear(s.Main)));
        Assert.Equal(expected.GetProperty("allSecondaryFlagsClear").GetBoolean(), session.Samples.All(s => Clear(s.Secondary!)));
        Assert.Equal(expected.GetProperty("mainValueMinV").GetDouble(), session.Samples.Min(s => s.Main.Value));
        Assert.Equal(expected.GetProperty("mainValueMaxV").GetDouble(), session.Samples.Max(s => s.Main.Value));
    }

    private static void AssertValue(JsonElement expected, LoggerValue actual)
    {
        Assert.Equal(expected.GetProperty("raw").GetString(), actual.Raw);
        Assert.Equal(expected.GetProperty("negative").GetBoolean(), actual.Negative);
        Assert.Equal(expected.GetProperty("overload").GetBoolean(), actual.Overload);
        Assert.Equal(expected.GetProperty("range").GetInt32(), actual.Range);
        Assert.Equal(expected.GetProperty("count").GetInt32(), actual.Count);
        if (expected.TryGetProperty("lowBattery", out var lowBattery))
            Assert.Equal(lowBattery.GetBoolean(), actual.LowBattery);
        Assert.Equal(expected.GetProperty("rangeValid").GetBoolean(), actual.IsRangeValid);
        Assert.Equal(expected.GetProperty("decimals").GetInt32(), actual.Decimals);
        Assert.Equal(expected.GetProperty("prefix").GetString(), actual.Prefix);
        Assert.Equal(expected.GetProperty("unit").GetString(), actual.Unit);
        Assert.Equal(TestData.NullableDouble(expected.GetProperty("value")), actual.Value);
        Assert.Equal(TestData.NullableDouble(expected.GetProperty("siValue")), actual.SiValue);
        Assert.Equal(expected.GetProperty("text").GetString(), actual.Text);
    }
}
