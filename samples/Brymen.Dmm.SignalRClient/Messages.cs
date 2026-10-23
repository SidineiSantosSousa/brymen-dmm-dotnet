using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brymen.Dmm.SignalRClient;

// Subconjunto dos DTOs da API (Brymen.Dmm.Api.Contracts) usado pelo exemplo; campos a mais no JSON são ignorados.

/// <summary>Um display do LCD (evento <c>reading</c>, campos <c>main</c> e <c>secondary</c>).</summary>
internal sealed record DisplayMessage(string Text, string UnitSymbol, string Mode, bool Overload, double? Value);

/// <summary>Evento <c>reading</c> e resultado de <c>GetLatest</c>: o DTO de <c>GET /api/v1/readings/latest</c>.</summary>
internal sealed record ReadingMessage(
    long Sequence,
    DateTimeOffset Timestamp,
    DisplayMessage? Main,
    DisplayMessage? Secondary,
    IReadOnlyList<string> Flags,
    string? Comparator);

internal sealed record DeviceMessage(string Path, string? Manufacturer, string? Product);

internal sealed record ErrorMessage(string Code, string Message, DateTimeOffset Timestamp);

/// <summary>
/// O medidor detectado (campo <c>model</c>): família <c>"BM52x"</c> ou <c>"BM82x"</c>, Model ID (<c>"0x52"</c> ou
/// <c>"0x82"</c>), nome com os modelos validados e se tem datalogger. Nulo antes da detecção e num serviço 0.1.0.
/// </summary>
internal sealed record ModelMessage(string Family, string ModelId, string Name, bool SupportsDataLogger);

/// <summary>Evento <c>status</c> e resultado de <c>GetStatus</c>: o DTO de <c>GET /api/v1/meter</c>.</summary>
internal sealed record StatusMessage(
    bool LinkEnabled,
    string State,
    string Transport,
    bool DeviceOpen,
    DeviceMessage? Device,
    ErrorMessage? LastError,
    bool Importing,
    long ReadingCount,
    ModelMessage? Model = null);

/// <summary>Contexto gerado em compile-time para o protocolo JSON do cliente (sem reflexão).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReadingMessage))]
[JsonSerializable(typeof(StatusMessage))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
