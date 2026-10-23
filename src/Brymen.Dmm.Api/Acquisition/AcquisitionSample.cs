using System.Globalization;
using System.Text.Json.Serialization;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.Api.Acquisition;

/// <summary>Modo de amostragem do gravador (diálogo "Interval" do original).</summary>
public enum AcquisitionMode
{
    /// <summary>Toda leitura do polling vira amostra.</summary>
    [JsonStringEnumMemberName("fullSpeed")] FullSpeed,

    /// <summary>Uma amostra a cada N segundos inteiros ("Customization...").</summary>
    [JsonStringEnumMemberName("interval")] Interval,

    /// <summary>Uma amostra por <c>POST /api/v1/acquisition/samples</c>.</summary>
    [JsonStringEnumMemberName("manual")] Manual,
}

public enum AcquisitionState
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("recording")] Recording,
}

/// <summary>Uma amostra do buffer de aquisição.</summary>
/// <param name="Index">Posição no buffer, 0-based (<c>#item</c> do CSV = Index + 1).</param>
/// <param name="Elapsed">Tempo desde a primeira amostra do buffer.</param>
internal sealed record AcquisitionSample(int Index, TimeSpan Elapsed, DmmReading Reading, ComparatorResult? Comparison)
{
    public DateTimeOffset Timestamp => Reading.Timestamp;
}

/// <summary>Linhas CSV das amostras de aquisição.</summary>
internal static class AcquisitionCsv
{
    public const string ExtendedHeader = CsvFormat.Header + ",Timestamp,MainSiValue,SubSiValue,Flags,Comparator";

    public static string HeaderFor(bool extended) => extended ? ExtendedHeader : CsvFormat.Header;

    public static string Row(AcquisitionSample sample, bool extended)
    {
        var r = sample.Reading;
        string item = (sample.Index + 1).ToString(CultureInfo.InvariantCulture);
        string time = CsvFormat.FormatTime(sample.Elapsed);
        if (!extended)
        {
            return CsvFormat.Row(
                item, time,
                CsvFormat.DisplayData(r.Main), CsvFormat.DisplayUnit(r.Main), CsvFormat.MainFunction(r.Main, r.Flags),
                CsvFormat.DisplayData(r.Secondary), CsvFormat.DisplayUnit(r.Secondary), CsvFormat.SecondaryFunction(r.Secondary, r.Flags));
        }
        return CsvFormat.Row(
            item, time,
            CsvFormat.DisplayData(r.Main), CsvFormat.DisplayUnit(r.Main), CsvFormat.MainFunction(r.Main, r.Flags),
            CsvFormat.DisplayData(r.Secondary), CsvFormat.DisplayUnit(r.Secondary), CsvFormat.SecondaryFunction(r.Secondary, r.Flags),
            r.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            CsvFormat.FormatNumber(r.Main?.SiValue),
            CsvFormat.FormatNumber(r.Secondary?.SiValue),
            string.Join('|', DmmFlagNames.ToNames(r.Flags)),
            sample.Comparison?.ToString() ?? "");
    }
}
