using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Api.Tests;

public sealed class CsvFormatTests
{
    [Theory]
    [InlineData(0L, "  0:00:00.0")]
    [InlineData(10_499_999L, "  0:00:01.0")]          // 1,0499999 s: décimos truncados, como ms / 100
    [InlineData(37_234_560_000L, "  1:02:03.4")]      // 1 h 02 min 03,456 s
    [InlineData(359_999_990_000L, "  9:59:59.9")]
    [InlineData(36_000_000_000_000L, "1000:00:00.0")] // a largura 3 é mínima
    public void FormatTime_matches_the_original_sprintf(long ticks, string expected)
        => Assert.Equal(expected, CsvFormat.FormatTime(TimeSpan.FromTicks(ticks)));

    [Fact]
    public void Units_follow_the_original_spelling()
    {
        Assert.Equal("kOhm", CsvFormat.Unit("k", "Ohm"));
        Assert.Equal("uA", CsvFormat.Unit("u", "A"));
        Assert.Equal("mV", CsvFormat.Unit("m", "V"));
        Assert.Equal("C", CsvFormat.Unit("", "°C"));
        Assert.Equal("DC+AC", CsvFormat.ModeLabel(MeasurementMode.AcDc));
        Assert.Equal("", CsvFormat.ModeLabel(MeasurementMode.None));
    }

    [Fact]
    public void Overload_text_uses_letter_O()
    {
        var display = new DisplayReading { Digits = " 0L ", Text = "0.L", Overload = true, Unit = "Ohm", Prefix = "M" };

        Assert.Equal("O.L", CsvFormat.DisplayData(display));
        Assert.Equal("MOhm", CsvFormat.DisplayUnit(display));
    }

    [Fact]
    public void Row_quotes_only_when_needed()
        => Assert.Equal("1,a,\"b,c\",\"d\"\"e\"", CsvFormat.Row("1", "a", "b,c", "d\"e"));

    [Fact]
    public async Task Csv_export_stops_formatting_rows_when_the_client_disconnects()
    {
        // O MemoryStream aceita tudo, como a resposta do Kestrel depois do abort: só o token interrompe o laço.
        using var cts = new CancellationTokenSource();
        int formatted = 0;
        var rows = Enumerable.Range(0, 1_000_000).Select(i =>
        {
            formatted++;
            if (i == 10)
                cts.Cancel();
            return "1,  0:00:00.0,0.122,V,AC,0.000,Hz,";
        });
        using var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Endpoints.CsvWriter.WriteAsync(stream, CsvFormat.Header, rows, cts.Token));

        Assert.Equal(11, formatted);
    }
}

public sealed class MeterSessionTests
{
    [Fact]
    public async Task Unlink_canceled_while_an_import_command_holds_the_cable_still_unlinks_and_signals()
    {
        // O 88 da importação fica preso no medidor simulado: o unlink espera pelo cabo, e o cliente desiste no meio.
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var responder = SimulatedBm52xMeter.FromCaptures();
        responder.FaultInjector = (command, _) =>
        {
            if (command == Bm52xCommands.MemoryHead)
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            return SimulatedFault.None;
        };
        var provider = Meter.MeterTransportFactory.CreateSimulated(responder, TimeSpan.Zero);
        using var session = new Meter.MeterSession(provider, Microsoft.Extensions.Options.Options.Create(new MeterOptions { ReportTimeout = TimeSpan.FromSeconds(5) }),
            TimeProvider.System, NullLogger<Meter.MeterSession>.Instance);
        Assert.Null(await session.SetLinkAsync(true, CancellationToken.None));
        var access = await session.AcquireExclusiveAsync(CancellationToken.None);
        await using (access)
        {
            var head = Task.Run(() => access.ExecuteAsync(Bm52xCommands.MemoryHead, Bm52xCommands.MemoryReportCount, CancellationToken.None));
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));

            using var cts = new CancellationTokenSource();
            var unlink = session.SetLinkAsync(false, cts.Token);
            var changed = session.Changed;
            await cts.CancelAsync();
            release.Release();

            Assert.Null(await unlink.WaitAsync(TimeSpan.FromSeconds(10)));
            await head.WaitAsync(TimeSpan.FromSeconds(10));
            await changed.WaitAsync(TimeSpan.FromSeconds(10));
            var snapshot = session.GetSnapshot();
            Assert.False(snapshot.LinkEnabled);
            Assert.Equal(Meter.MeterConnectionState.Unlinked, snapshot.State);
            Assert.True(snapshot.Importing);
        }
        Assert.False(session.GetSnapshot().DeviceOpen);
    }

    [Fact]
    public async Task Communication_failure_closes_the_cable_and_an_invalid_frame_keeps_it_open()
    {
        int next = (int)SimulatedFault.None;
        var responder = SimulatedBm52xMeter.FromCaptures();
        responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime ? (SimulatedFault)Interlocked.Exchange(ref next, (int)SimulatedFault.None) : SimulatedFault.None;
        using var session = CreateSession(Meter.MeterTransportFactory.CreateSimulated(responder, TimeSpan.Zero));
        Assert.Null(await session.SetLinkAsync(true, CancellationToken.None));
        Assert.NotNull(await session.PollRealTimeAsync(CancellationToken.None));

        // Sem resposta: W7012 do MeterConnection, com a causa, e o cabo fechado; a consulta seguinte o reabre.
        Volatile.Write(ref next, (int)SimulatedFault.Drop);
        var noData = await Assert.ThrowsAsync<BrymenMeterException>(() => session.PollRealTimeAsync(CancellationToken.None));
        Assert.Equal(BrymenErrorCodes.NoDataReceived, noData.Code);
        Assert.IsAssignableFrom<TimeoutException>(noData.InnerException);
        Assert.False(session.GetSnapshot().DeviceOpen);
        Assert.NotNull(await session.PollRealTimeAsync(CancellationToken.None));
        Assert.True(session.GetSnapshot().DeviceOpen);

        // Quadro inválido: W8011, e o cabo continua aberto (o polling da API só o fecha em falha de comunicação).
        Volatile.Write(ref next, (int)SimulatedFault.CorruptModel);
        var invalid = await Assert.ThrowsAsync<BrymenMeterException>(() => session.PollRealTimeAsync(CancellationToken.None));
        Assert.Equal(BrymenErrorCodes.InvalidFrame, invalid.Code);
        Assert.True(session.GetSnapshot().DeviceOpen);
        Assert.NotNull(await session.PollRealTimeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Exclusive_access_reads_the_memory_with_the_library_reader_and_closes_the_unlinked_cable()
    {
        // O 20º 89 se perde sem resposta: o leitor fecha o cabo e recomeça do 88, e o acesso exclusivo o reabre.
        int nextCount = 0;
        var responder = SimulatedBm52xMeter.FromCaptures();
        responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.MemoryNext && Interlocked.Increment(ref nextCount) == 20 ? SimulatedFault.Drop : SimulatedFault.None;
        using var session = CreateSession(Meter.MeterTransportFactory.CreateSimulated(responder, TimeSpan.Zero));
        var progress = new List<LoggerReadProgress>();

        LoggerMemoryImage image;
        await using (var access = await session.AcquireExclusiveAsync(CancellationToken.None))
        {
            // Link desativado: o cabo é aberto só para a importação, e o polling fica parado.
            var during = session.GetSnapshot();
            Assert.True(during.Importing);
            Assert.True(during.DeviceOpen);
            Assert.False(session.IsPollingActive);
            image = await access.ReadLoggerImageAsync(new SyncProgress(progress.Add), CancellationToken.None);
        }

        Assert.Equal(SimulatedBm52xMeter.LoadDefaultMemoryImage(), image.ToArray());
        Assert.Equal(1, image.Restarts);
        Assert.Equal(image.BlockCount, progress[^1].BlocksRead);
        var after = session.GetSnapshot();
        Assert.False(after.Importing);
        Assert.False(after.DeviceOpen);
        Assert.Equal(Meter.MeterConnectionState.Unlinked, after.State);
    }

    private static Meter.MeterSession CreateSession(IHidDeviceProvider provider)
        => new(provider, Microsoft.Extensions.Options.Options.Create(new MeterOptions { ReportTimeout = TimeSpan.FromMilliseconds(200) }),
            TimeProvider.System, NullLogger<Meter.MeterSession>.Instance);

    /// <summary>Progresso síncrono (o <see cref="Progress{T}"/> o entregaria depois, em outra thread).</summary>
    private sealed class SyncProgress(Action<LoggerReadProgress> report) : IProgress<LoggerReadProgress>
    {
        public void Report(LoggerReadProgress value) => report(value);
    }
}

public sealed class LoggerCsvTests
{
    [Theory]
    [InlineData(13, 3, false, "V", "0.013")]
    [InlineData(287, 2, false, "V", "02.87")]
    [InlineData(13, 1, false, "V", "001.3")]
    [InlineData(6, 0, false, "A", "0006")]
    [InlineData(1234, 2, false, "Ohm", "12.34")]
    [InlineData(287, 2, true, "V", "-02.87")]
    [InlineData(6, 0, true, "A", "-0006")]
    [InlineData(25, 0, false, "°C", "025")]
    [InlineData(5, 0, true, "°C", "-05")]
    [InlineData(1999, 0, false, "°F", "1999")]
    public void Session_csv_data_keeps_the_leading_zeros_of_the_original(int count, int decimals, bool negative, string unit, string expected)
    {
        var value = new LoggerValue { Count = count, Decimals = decimals, Negative = negative, Unit = unit, Text = "lcd" };

        Assert.Equal(expected, LoggerCsv.Data(value));
    }

    [Fact]
    public void Session_csv_data_keeps_the_text_of_overload_and_invalid_ranges()
    {
        Assert.Equal("OL", LoggerCsv.Data(new LoggerValue { Count = 0, Decimals = 2, Overload = true, Unit = "V", Text = "OL" }));
        Assert.Equal("----", LoggerCsv.Data(new LoggerValue { Count = 0, Decimals = 0, Overload = true, Unit = "°C", Text = "----" }));
        Assert.Equal("4660", LoggerCsv.Data(new LoggerValue { Count = 4660, IsRangeValid = false, Text = "4660" }));
    }
}

public sealed class CsvBackupWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "brymen-dmm-backup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Retention_keeps_the_newest_files_counting_the_new_one()
    {
        Directory.CreateDirectory(_directory);
        foreach (int n in Enumerable.Range(1, 24))
            File.WriteAllText(Path.Combine(_directory, $"~BDMDL{n}.csv"), "");
        File.WriteAllText(Path.Combine(_directory, "outro.csv"), "");

        using (var writer = CsvBackupWriter.Create(_directory, retention: 20, NullLogger.Instance))
            Assert.Equal("~BDMDL25.csv", Path.GetFileName(writer.FilePath));

        var names = Directory.EnumerateFiles(_directory, "~BDMDL*.csv").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal);
        var expected = Enumerable.Range(6, 20).Select(n => $"~BDMDL{n}.csv").Order(StringComparer.Ordinal);
        Assert.Equal(expected, names);
        Assert.True(File.Exists(Path.Combine(_directory, "outro.csv")));
    }

    [Fact]
    public void Retention_of_one_keeps_only_the_current_file()
    {
        for (int i = 0; i < 3; i++)
            CsvBackupWriter.Create(_directory, retention: 1, NullLogger.Instance).Dispose();

        Assert.Equal(["~BDMDL3.csv"], Directory.EnumerateFiles(_directory).Select(f => Path.GetFileName(f)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(AcquisitionOptionsValidator.MaxBackupRetention + 1, false)]
    public void Retention_is_validated(int retention, bool valid)
        => Assert.Equal(valid, new AcquisitionOptionsValidator().Validate(null, new AcquisitionOptions { BackupRetention = retention }).Succeeded);

    [Fact]
    public void Default_retention_is_20() => Assert.Equal(20, new AcquisitionOptions().BackupRetention);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class RealtimeOptionsValidatorTests
{
    [Theory]
    [InlineData("http://painel.local:8080", true)]
    [InlineData("https://painel.test", true)]
    [InlineData("http://painel.test/", true)]
    [InlineData("*", false)]
    [InlineData("http://*.test", false)]
    [InlineData("http://painel.test/app", false)]
    [InlineData("ftp://painel.test", false)]
    [InlineData("painel.test", false)]
    public void Cors_origins_must_be_scheme_host_and_port(string origin, bool valid)
    {
        var settings = new CorsSettings { AllowedOrigins = RealtimeOptionsValidator.NormalizeOrigins([origin]) };

        Assert.Equal(valid, new RealtimeOptionsValidator().Validate(null, settings).Succeeded);
    }

    [Fact]
    public void Cors_origins_are_normalized()
        => Assert.Equal(["http://a.test", "http://b.test:81"], RealtimeOptionsValidator.NormalizeOrigins(["http://a.test/", " ", "http://b.test:81", "HTTP://A.TEST"]));

    [Theory]
    [InlineData(0.5, false)]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(601, false)]
    public void SignalR_keep_alive_is_validated(double seconds, bool valid)
        => Assert.Equal(valid, new RealtimeOptionsValidator().Validate(null, new SignalRSettings { KeepAliveInterval = TimeSpan.FromSeconds(seconds) }).Succeeded);

    [Fact]
    public void Defaults_match_the_design()
    {
        Assert.False(new SignalRSettings().EnableDetailedErrors);
        Assert.Equal(TimeSpan.FromSeconds(15), new SignalRSettings().KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), new SseSettings().HeartbeatInterval);
        Assert.Empty(new CorsSettings().AllowedOrigins);
    }
}
