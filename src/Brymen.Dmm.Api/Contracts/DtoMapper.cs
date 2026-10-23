using System.Globalization;
using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Contracts;

/// <summary>Conversão dos tipos internos e do Protocol para os DTOs do JSON.</summary>
internal static class DtoMapper
{
    public static string ModeName(MeasurementMode mode) => mode switch
    {
        MeasurementMode.AC => "AC",
        MeasurementMode.DC => "DC",
        MeasurementMode.AcDc => "AC+DC",
        _ => "NONE",
    };

    public static DisplayDto? ToDto(DisplayReading? display) => display is null ? null : new DisplayDto(
        display.Digits, display.Text, display.Negative, display.Overload, Finite(display.Value),
        display.Prefix, display.Unit, display.UnitSymbol, ModeName(display.Mode), Finite(display.SiValue));

    public static ReadingDto ToDto(LiveReading live) => new(
        live.Sequence, live.Reading.Timestamp, ToDto(live.Reading.Main), ToDto(live.Reading.Secondary),
        DmmFlagNames.ToNames(live.Reading.Flags), live.Comparison, live.Reading.RawHex);

    public static MeterStatusDto ToDto(MeterSnapshot snapshot, MeterSession session, ReadingBroadcaster broadcaster) => new(
        snapshot.LinkEnabled,
        snapshot.State,
        session.TransportName,
        snapshot.DeviceOpen,
        ToDto(snapshot.Device),
        ToDto(snapshot.LastError),
        snapshot.Importing,
        broadcaster.Count,
        broadcaster.Latest?.Reading.Timestamp,
        session.Options.PollInterval.TotalMilliseconds);

    public static MeterDeviceDto? ToDto(HidDeviceInfo? info) => info is null ? null : new MeterDeviceDto(
        info.Path, Hex(info.VendorId), Hex(info.ProductId), info.ReleaseNumber is { } r ? Hex(r) : null,
        info.Manufacturer, info.Product, info.SerialNumber);

    public static MeterErrorDto? ToDto(MeterError? error) => error is null ? null : new MeterErrorDto(error.Code, error.Message, error.Timestamp);

    public static AcquisitionStatusDto ToDto(AcquisitionStatus s, DateTimeOffset now) => new(
        s.State, s.Mode, s.IntervalSeconds, s.SampleCount, s.MaxSamples, s.BufferFull,
        s.StartedAt, s.StoppedAt, s.FirstSampleAt, s.LastSampleAt, s.BackupFile,
        s.BackupError is { } e ? new MeterErrorDto(e.Code, e.Message, now) : null,
        ToDto(s.MainRange), ToDto(s.SecondaryRange));

    private static ValueRangeDto? ToDto(ValueRange? range) => range is { } r ? new ValueRangeDto(r.Min, r.Max, r.Unit, ModeName(r.Mode)) : null;

    public static AcquisitionSampleDto ToDto(AcquisitionSample sample) => new(
        sample.Index + 1, sample.Timestamp, sample.Elapsed.TotalSeconds, CsvFormat.FormatTime(sample.Elapsed),
        ToDto(sample.Reading.Main), ToDto(sample.Reading.Secondary), DmmFlagNames.ToNames(sample.Reading.Flags), sample.Comparison);

    public static ComparatorSettingsDto ToDto(ComparatorSettings s) => new(s.Enabled, s.High, s.Low, s.Alarm);

    public static ImportJobDto ToDto(ImportJobSnapshot job)
    {
        var p = job.Progress;
        double percent = job.State == ImportJobState.Completed ? 100
            : p.BlocksTotal > 0 ? Math.Round(100.0 * Math.Min(p.BlocksRead, p.BlocksTotal) / p.BlocksTotal, 1) : 0;
        return new ImportJobDto(
            job.Id, job.State, job.CreatedAt, job.CompletedAt,
            new ImportProgressDto(p.BlocksRead, p.BlocksTotal, p.TotalBytes, percent, p.Retries, p.Restarts),
            job.Error is { } e ? new MeterErrorDto(e.Code, e.Message, job.CompletedAt ?? job.CreatedAt) : null,
            job.Memory?.Header.ModelId,
            job.Memory?.Sessions.Count,
            job.Memory?.Sessions.Sum(s => s.Samples.Count),
            job.Warnings);
    }

    public static LoggerSessionDto ToDto(LoggerSession s) => new(
        s.Number, s.HeadAddress, s.NextAddress, s.Function, s.Select, s.Status, s.IsDual, s.Interval.TotalSeconds,
        s.MainFunction, s.SecondaryFunction, s.LengthBytes, s.Samples.Count);

    public static LoggerValueDto ToDto(LoggerValue v) => new(
        v.Count, v.Range, v.Negative, v.Overload, v.LowBattery, v.Text, Finite(v.Value),
        v.Prefix, v.Unit, v.UnitSymbol, ModeName(v.Mode), Finite(v.SiValue));

    public static LoggerSampleDto ToDto(LoggerSample s) => new(
        s.Index + 1, s.Elapsed.TotalSeconds, CsvFormat.FormatTime(s.Elapsed), ToDto(s.Main), s.Secondary is { } sec ? ToDto(sec) : null);

    private static string Hex(ushort value) => "0x" + value.ToString("X4", CultureInfo.InvariantCulture);

    // O JSON não representa NaN/∞.
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
}
