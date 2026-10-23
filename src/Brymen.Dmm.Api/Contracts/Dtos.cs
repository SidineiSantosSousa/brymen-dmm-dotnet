using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Meter;

namespace Brymen.Dmm.Api.Contracts;

/// <summary>Um display do LCD (principal ou secundário).</summary>
/// <param name="Digits">Os 4 caracteres crus do LCD, com espaço para dígito apagado.</param>
/// <param name="Text">Texto como exibido (ex.: "0.287", "-12.34", "0.L").</param>
/// <param name="Value">Valor na escala do display; null em overload ou texto não numérico.</param>
/// <param name="Prefix">"", "n", "u", "m", "k" ou "M".</param>
/// <param name="Unit">"V", "A", "Ohm", "Hz", "S", "F", "%", "dB", "dBm", "°C", "°F" ou "".</param>
/// <param name="UnitSymbol">Unidade para exibição (ex.: "mV", "µA", "kΩ").</param>
/// <param name="Mode">"AC", "DC", "AC+DC" ou "NONE".</param>
/// <param name="SiValue">Valor × 10^prefixo (sem conversão para %, dB/dBm e temperatura).</param>
public sealed record DisplayDto(
    string Digits,
    string Text,
    bool Negative,
    bool Overload,
    double? Value,
    string Prefix,
    string Unit,
    string UnitSymbol,
    string Mode,
    double? SiValue);

/// <summary>Leitura em tempo real.</summary>
/// <param name="Sequence">Número sequencial da leitura desde o início do serviço.</param>
/// <param name="Flags">Anunciadores acesos (ex.: "AUTO", "HOLD_H", "LOW_BATTERY").</param>
/// <param name="Comparator">Resultado do comparador; null se ele estiver desligado ou a leitura não for numérica.</param>
/// <param name="RawHex">Os 27 bytes recebidos, com report IDs.</param>
public sealed record ReadingDto(
    long Sequence,
    DateTimeOffset Timestamp,
    DisplayDto? Main,
    DisplayDto? Secondary,
    IReadOnlyList<string> Flags,
    ComparatorResult? Comparator,
    string RawHex);

public sealed record MeterDeviceDto(
    string Path,
    string VendorId,
    string ProductId,
    string? ReleaseNumber,
    string? Manufacturer,
    string? Product,
    string? SerialNumber);

public sealed record MeterErrorDto(string Code, string Message, DateTimeOffset Timestamp);

/// <summary>Estado do link, do cabo e do polling.</summary>
/// <param name="Transport">"hid" ou "simulated".</param>
/// <param name="Importing">Importação do datalogger em andamento (polling pausado).</param>
public sealed record MeterStatusDto(
    bool LinkEnabled,
    MeterConnectionState State,
    string Transport,
    bool DeviceOpen,
    MeterDeviceDto? Device,
    MeterErrorDto? LastError,
    bool Importing,
    long ReadingCount,
    DateTimeOffset? LastReadingAt,
    double PollIntervalMs);

/// <summary>Corpo de <c>PUT /api/v1/meter/link</c>.</summary>
public sealed record LinkRequest(bool? Enabled);

/// <summary>Página de resultados.</summary>
public sealed record PageDto<T>(int Offset, int Limit, int Total, IReadOnlyList<T> Items);

public sealed record AcquisitionSettingsDto(AcquisitionMode Mode, int IntervalSeconds);

/// <summary>Corpo de <c>PUT /api/v1/acquisition/settings</c>.</summary>
/// <param name="IntervalSeconds">Segundos inteiros entre amostras no modo <c>interval</c> (≥ 1).</param>
public sealed record AcquisitionSettingsRequest(AcquisitionMode? Mode, int? IntervalSeconds);

/// <summary>Faixa Max/Min de um display desde o início do buffer, ou desde a última troca de unidade ou acoplamento.</summary>
/// <param name="Unit">Unidade SI sem prefixo ("V", "Ohm", "Hz"...).</param>
/// <param name="Mode">"AC", "DC", "AC+DC" ou "NONE", como em <see cref="DisplayDto.Mode"/>.</param>
public sealed record ValueRangeDto(double Min, double Max, string Unit, string Mode);

public sealed record AcquisitionStatusDto(
    AcquisitionState State,
    AcquisitionMode Mode,
    int IntervalSeconds,
    int SampleCount,
    int MaxSamples,
    bool BufferFull,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    DateTimeOffset? FirstSampleAt,
    DateTimeOffset? LastSampleAt,
    string? BackupFile,
    MeterErrorDto? BackupError,
    ValueRangeDto? MainRange,
    ValueRangeDto? SecondaryRange);

/// <summary>Uma amostra do buffer de aquisição.</summary>
/// <param name="Item">Número da amostra, 1-based (coluna <c>#item</c> do CSV).</param>
/// <param name="Time">Tempo decorrido no formato do original, <c>%3d:%02d:%02d.%1d</c>.</param>
public sealed record AcquisitionSampleDto(
    int Item,
    DateTimeOffset Timestamp,
    double ElapsedSeconds,
    string Time,
    DisplayDto? Main,
    DisplayDto? Secondary,
    IReadOnlyList<string> Flags,
    ComparatorResult? Comparator);

/// <summary>Limites do comparador.</summary>
/// <param name="Enabled">Calcula Pass/High/Low em cada leitura e amostra.</param>
/// <param name="High">Limite Hi, na unidade SI do display principal.</param>
/// <param name="Low">Limite Lo, na unidade SI do display principal.</param>
/// <param name="Alarm">Alarme sonoro no painel quando o resultado não é Pass.</param>
public sealed record ComparatorSettingsDto(bool Enabled, double High, double Low, bool Alarm);

/// <summary>Corpo de <c>PUT /api/v1/comparator</c>; campos ausentes mantêm o valor atual.</summary>
public sealed record ComparatorSettingsRequest(bool? Enabled, double? High, double? Low, bool? Alarm);

public sealed record ImportProgressDto(int BlocksRead, int BlocksTotal, int TotalBytes, double Percent, int Retries, int Restarts);

/// <summary>Job de importação do datalogger.</summary>
public sealed record ImportJobDto(
    string Id,
    ImportJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ImportProgressDto Progress,
    MeterErrorDto? Error,
    int? ModelId,
    int? SessionCount,
    int? SampleCount,
    IReadOnlyList<string> Warnings);

/// <summary>Sessão gravada no datalogger.</summary>
/// <param name="Number">Número da sessão, 1-based.</param>
/// <param name="IntervalSeconds">Intervalo de gravação (Table 3-1).</param>
public sealed record LoggerSessionDto(
    int Number,
    string HeadAddress,
    string? NextAddress,
    int Function,
    int Select,
    int Status,
    bool IsDual,
    double IntervalSeconds,
    string MainFunction,
    string? SecondaryFunction,
    int LengthBytes,
    int SampleCount);

public sealed record LoggerValueDto(
    int Count,
    int Range,
    bool Negative,
    bool Overload,
    bool LowBattery,
    string Text,
    double? Value,
    string Prefix,
    string Unit,
    string UnitSymbol,
    string Mode,
    double? SiValue);

/// <summary>Amostra de uma sessão do datalogger.</summary>
/// <param name="Item">Número da amostra, 1-based.</param>
/// <param name="ElapsedSeconds">Índice × intervalo (a memória não guarda relógio).</param>
public sealed record LoggerSampleDto(
    int Item,
    double ElapsedSeconds,
    string Time,
    LoggerValueDto Main,
    LoggerValueDto? Secondary);
