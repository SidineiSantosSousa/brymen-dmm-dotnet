using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Brymen.Dmm.Api.Contracts;

/// <summary>
/// Contexto do System.Text.Json gerado em compile-time (camelCase, enums como string), registrado na frente da
/// cadeia de resolvers do ASP.NET Core para publicar trimmed/AOT.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MeterStatusDto))]
[JsonSerializable(typeof(LinkRequest))]
[JsonSerializable(typeof(ReadingDto))]
// Exigido pelo gerador da OpenAPI para descrever o text/event-stream de /readings/stream.
[JsonSerializable(typeof(SseItem<ReadingDto>))]
[JsonSerializable(typeof(AcquisitionStatusDto))]
[JsonSerializable(typeof(AcquisitionSettingsDto))]
[JsonSerializable(typeof(AcquisitionSettingsRequest))]
[JsonSerializable(typeof(AcquisitionSampleDto))]
[JsonSerializable(typeof(PageDto<AcquisitionSampleDto>))]
[JsonSerializable(typeof(ComparatorSettingsDto))]
[JsonSerializable(typeof(ComparatorSettingsRequest))]
[JsonSerializable(typeof(ImportJobDto))]
[JsonSerializable(typeof(IReadOnlyList<ImportJobDto>))]
[JsonSerializable(typeof(LoggerSessionDto))]
[JsonSerializable(typeof(IReadOnlyList<LoggerSessionDto>))]
[JsonSerializable(typeof(PageDto<LoggerSampleDto>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
