using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Gravador com relógio falso (<see cref="FakeTimeProvider"/>): tempos determinísticos, sem esperar o relógio real.</summary>
public sealed class RecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "brymen-dmm-recorder-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Start);
    private readonly SimulatedBm52xMeter _responder = SimulatedBm52xMeter.FromCaptures();
    private readonly ReadingBroadcaster _broadcaster = new();
    private MeterSession _session = null!;
    private Recorder _recorder = null!;

    public async Task InitializeAsync()
    {
        var provider = MeterTransportFactory.CreateSimulated(_responder, TimeSpan.Zero);
        _session = new MeterSession(provider, Options.Create(new MeterOptions()), _time, NullLogger<MeterSession>.Instance);
        Assert.Null(await _session.SetLinkAsync(true, CancellationToken.None));
        _recorder = new Recorder(
            Options.Create(new AcquisitionOptions { BackupEnabled = false }),
            new DataDirectory(_directory),
            new HostingEnvironment { ContentRootPath = _directory },
            _broadcaster, _session, _time, NullLogger<Recorder>.Instance);
    }

    public Task DisposeAsync()
    {
        _recorder.Dispose();
        _session.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void Elapsed_time_uses_the_monotonic_clock_even_when_the_wall_clock_jumps()
    {
        Assert.Null(_recorder.Start());

        // Relógio de parede de um Pi sem RTC: 2001 até o NTP, depois um salto de 25 anos e um recuo de 1 hora.
        _recorder.OnReading(Publish(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        _recorder.OnReading(Publish(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)));
        _time.Advance(TimeSpan.FromMilliseconds(250));
        _recorder.OnReading(Publish(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));

        var samples = _recorder.Snapshot();
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1750)], samples.Select(s => s.Elapsed));
        string[] rows = [.. samples.Select(s => AcquisitionCsv.Row(s, extended: false).Split(',')[1])];
        Assert.Equal(["  0:00:00.0", "  0:00:01.5", "  0:00:01.7"], rows);
    }

    [Fact]
    public async Task Interval_mode_samples_on_each_tick_without_repeating_a_reading()
    {
        Assert.Null(_recorder.UpdateSettings(AcquisitionMode.Interval, 2));
        Assert.Null(_recorder.Start());

        Publish(Start);
        _time.Advance(TimeSpan.FromSeconds(2));
        await WaitForCountAsync(1);

        // Dois ticks sem leitura nova: nada entra (o original só amostra "dado válido" novo).
        _time.Advance(TimeSpan.FromSeconds(2));
        _time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(1, _recorder.Count);

        Publish(Start.AddSeconds(6));
        _time.Advance(TimeSpan.FromSeconds(2));
        await WaitForCountAsync(2);

        var samples = _recorder.Snapshot();
        Assert.Equal(TimeSpan.Zero, samples[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(6), samples[1].Elapsed);
    }

    [Fact]
    public async Task Samples_are_refused_while_the_logger_import_holds_the_cable()
    {
        Assert.Null(_recorder.UpdateSettings(AcquisitionMode.Manual, null));
        Assert.Null(_recorder.Start());
        Publish(Start);

        await using (await _session.AcquireExclusiveAsync(CancellationToken.None))
        {
            var (sample, error) = _recorder.AddManual();
            Assert.Null(sample);
            Assert.Equal(ErrorCodes.Conflict, error?.Code);
        }

        var (after, none) = _recorder.AddManual();
        Assert.Null(none);
        Assert.Equal(0, after?.Index);
    }

    [Theory]
    [InlineData(AcquisitionMode.FullSpeed, 100_000, ErrorCodes.InvalidValue)]
    [InlineData(AcquisitionMode.Manual, -1, ErrorCodes.InvalidValue)]
    [InlineData(AcquisitionMode.Interval, 0, ErrorCodes.InvalidValue)]
    [InlineData(AcquisitionMode.Interval, Recorder.MaxIntervalSeconds + 1, ErrorCodes.InvalidValue)]
    [InlineData(AcquisitionMode.FullSpeed, 0, null)]
    [InlineData(AcquisitionMode.Interval, Recorder.MaxIntervalSeconds, null)]
    public void Interval_is_validated_in_every_mode(AcquisitionMode mode, int interval, string? expected)
    {
        Assert.Equal(expected, _recorder.UpdateSettings(mode, interval)?.Code);

        var status = _recorder.GetStatus();
        Assert.Equal(expected is null ? mode : AcquisitionMode.FullSpeed, status.Mode);
        Assert.Equal(expected is null && interval >= 1 ? interval : 1, status.IntervalSeconds);
    }

    [Fact]
    public void Each_recording_creates_a_backup_and_only_the_newest_ones_are_kept()
    {
        string backup = Path.Combine(_directory, "backup-retention");
        using var recorder = new Recorder(
            Options.Create(new AcquisitionOptions { BackupDirectory = backup, BackupRetention = 3 }),
            new DataDirectory(_directory),
            new HostingEnvironment { ContentRootPath = _directory },
            _broadcaster, _session, _time, NullLogger<Recorder>.Instance);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(recorder.Start());
            recorder.OnReading(Publish(Start));
            recorder.Stop();
        }

        Assert.Equal(["~BDMDL3.csv", "~BDMDL4.csv", "~BDMDL5.csv"], Directory.EnumerateFiles(backup).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        Assert.EndsWith("~BDMDL5.csv", recorder.GetStatus().BackupFile);
    }

    [Fact]
    public void Sample_subscribers_receive_every_sample_in_order()
    {
        using var subscription = _recorder.SubscribeSamples();
        Assert.Null(_recorder.Start());

        for (int i = 0; i < 10; i++)
            _recorder.OnReading(Publish(Start.AddSeconds(i)));
        _recorder.Stop();
        _recorder.OnReading(Publish(Start.AddSeconds(20)));

        var received = new List<AcquisitionSample>();
        while (subscription.Reader.TryRead(out var sample))
            received.Add(sample);
        Assert.Equal(Enumerable.Range(0, 10), received.Select(s => s.Index));
        Assert.Equal(0, subscription.TakeDropped());
    }

    [Fact]
    public void Max_min_range_restarts_when_the_unit_or_the_coupling_changes()
    {
        Assert.Null(_recorder.Start());

        _recorder.OnReading(Publish(Display(0.09, "V", MeasurementMode.AC)));
        _recorder.OnReading(Publish(Display(0.12, "V", MeasurementMode.AC)));
        Assert.Equal(new ValueRange(0.09, 0.12, "V", MeasurementMode.AC), _recorder.GetStatus().MainRange);

        // Chave de ACV para Ω: a faixa não mistura 0,09 V com 1,2 MΩ.
        _recorder.OnReading(Publish(Display(1_200_000, "Ohm", MeasurementMode.None)));
        _recorder.OnReading(Publish(Display(null, "Ohm", MeasurementMode.None, overload: true)));
        _recorder.OnReading(Publish(Display(1_000_000, "Ohm", MeasurementMode.None)));
        Assert.Equal(new ValueRange(1_000_000, 1_200_000, "Ohm", MeasurementMode.None), _recorder.GetStatus().MainRange);

        // Mesma unidade com outro acoplamento (ACV → DCV) também recomeça.
        _recorder.OnReading(Publish(Display(5, "V", MeasurementMode.DC)));
        Assert.Equal(new ValueRange(5, 5, "V", MeasurementMode.DC), _recorder.GetStatus().MainRange);
        Assert.Equal(6, _recorder.Count);
    }

    private static DmmReading Display(double? siValue, string unit, MeasurementMode mode, bool overload = false) => new()
    {
        Timestamp = Start,
        RawHex = "",
        Main = new DisplayReading
        {
            Digits = "    ",
            Text = overload ? "0.L" : siValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Overload = overload,
            Value = siValue,
            SiValue = siValue,
            Unit = unit,
            Mode = mode,
        },
    };

    private LiveReading Publish(DmmReading reading) => _broadcaster.Publish(reading, null, _time.GetTimestamp());

    private LiveReading Publish(DateTimeOffset wallClock)
    {
        var frame = Infrastructure.TestHelpers.JoinReports(_responder.Respond(0, new byte[] { 0x00, 0x52, Bm52xCommands.RealTime })!);
        return _broadcaster.Publish(Bm52xRealtime.Decode(frame, wallClock), null, _time.GetTimestamp());
    }

    private async Task WaitForCountAsync(int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_recorder.Count < count)
            await Task.Delay(10, cts.Token);
        Assert.Equal(count, _recorder.Count);
    }
}

public sealed class ReadingBroadcasterTests
{
    [Fact]
    public void Slow_subscriber_never_blocks_publishing_and_keeps_the_newest_readings()
    {
        var broadcaster = new ReadingBroadcaster();
        using var subscription = broadcaster.Subscribe();
        Assert.Equal(1, broadcaster.SubscriberCount);

        const int total = 500;
        for (int i = 0; i < total; i++)
            broadcaster.Publish(new DmmReading { Timestamp = DateTimeOffset.UnixEpoch, RawHex = "" }, null, i);

        var received = new List<long>();
        while (subscription.Reader.TryRead(out var live))
            received.Add(live.Sequence);

        Assert.Equal(total, received[^1]);
        Assert.Equal(ReadingBroadcaster.SubscriberCapacity, received.Count);
        Assert.Equal(received.Order(), received);
        Assert.Equal(total - ReadingBroadcaster.SubscriberCapacity, subscription.TakeDropped());
    }

    [Fact]
    public void Subscribe_with_latest_returns_the_last_reading_and_only_newer_ones_in_the_channel()
    {
        var broadcaster = new ReadingBroadcaster();
        broadcaster.Publish(new DmmReading { Timestamp = DateTimeOffset.UnixEpoch, RawHex = "" }, null, 0);

        using var subscription = broadcaster.Subscribe(out var latest);
        broadcaster.Publish(new DmmReading { Timestamp = DateTimeOffset.UnixEpoch, RawHex = "" }, null, 1);

        Assert.Equal(1, latest?.Sequence);
        Assert.True(subscription.Reader.TryRead(out var next));
        Assert.Equal(2, next.Sequence);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Subscription_is_removed_on_dispose()
    {
        var broadcaster = new ReadingBroadcaster();
        var subscription = broadcaster.Subscribe();
        var pending = subscription.Reader.WaitToReadAsync().AsTask();
        Assert.Equal(1, broadcaster.SubscriberCount);

        subscription.Dispose();

        Assert.False(await pending);
        Assert.Equal(0, broadcaster.SubscriberCount);
    }

    [Fact]
    public void Dropped_readings_are_counted_and_the_rest_keeps_the_order()
    {
        var broadcaster = new ReadingBroadcaster();
        using var subscription = broadcaster.Subscribe(capacity: 10);

        for (int i = 0; i < 25; i++)
            broadcaster.Publish(new DmmReading { Timestamp = DateTimeOffset.UnixEpoch, RawHex = "" }, null, i);

        Assert.Equal(15, subscription.TakeDropped());
        Assert.Equal(0, subscription.TakeDropped());
        var sequences = new List<long>();
        while (subscription.Reader.TryRead(out var live))
            sequences.Add(live.Sequence);
        Assert.Equal(Enumerable.Range(16, 10).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task Complete_ends_every_subscription_and_refuses_new_ones()
    {
        var broadcaster = new ReadingBroadcaster();
        using var direct = broadcaster.Subscribe();
        using var withLatest = broadcaster.Subscribe(out _);
        var pending = withLatest.Reader.WaitToReadAsync().AsTask();
        Assert.Equal(2, broadcaster.SubscriberCount);

        broadcaster.Complete();

        Assert.False(await direct.Reader.WaitToReadAsync());
        Assert.False(await pending);
        using var late = broadcaster.Subscribe();
        Assert.True(late.Reader.Completion.IsCompleted);
        Assert.Equal(0, broadcaster.SubscriberCount);
    }
}
