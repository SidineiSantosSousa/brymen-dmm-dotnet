using System.Text.Json.Nodes;
using Brymen.Dmm.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Brymen.Dmm.Api;

/// <summary>Informações do documento OpenAPI, descrições das tags e o campo <c>code</c> dos ProblemDetails.</summary>
internal static class OpenApiSetup
{
    private static readonly Dictionary<string, string> TagDescriptions = new(StringComparer.Ordinal)
    {
        ["Medidor"] = "Link/Unlink, estado do cabo BC-86X (VID 0820, PID 0001) e modelo detectado (BM52x ou BM82x).",
        ["Leituras"] = "Digital Meter: última leitura e fluxo ao vivo por Server-Sent Events.",
        ["Aquisição"] = "Graphical Recorder / Data List: gravação em memória com backup contínuo e export CSV.",
        ["Comparador"] = "Comparator Hi/Lo com alarme, persistido em settings.json.",
        ["Datalogger"] = "Import data from DMM: leitura e decodificação da memória do datalogger.",
    };

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Brymen DMM REST",
                Version = "v1",
                Description = "Port do Bs82-52x Data Recording System V6.0.0.9 com o cabo óptico BC-86X para a família BM52x (Brymen BM525s "
                    + "= Minipa ET-2615A, com datalogger) e a BM82x (Minipa ET-2517A, sem datalogger), detectada pelo Model ID (model em GET /api/v1/meter): "
                    + "leituras em tempo real, gravador, comparador Hi/Lo e importação do datalogger. "
                    + "Erros seguem a RFC 9457 (ProblemDetails) com o código do original em \"code\" (E9015, E9016, W7012...).",
            };
            foreach (var tag in document.Tags ?? Enumerable.Empty<OpenApiTag>())
            {
                if (tag.Name is { } name && TagDescriptions.TryGetValue(name, out string? description))
                    tag.Description = description;
            }
            return Task.CompletedTask;
        });

        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(MeterModelDto))
                DescribeModel(schema);
            else if (context.JsonTypeInfo.Type == typeof(RealTimeStatisticsDto))
                DescribeRealTimeStatistics(schema);

            if (context.JsonTypeInfo.Type == typeof(ProblemDetails) || context.JsonTypeInfo.Type == typeof(HttpValidationProblemDetails))
            {
                schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                schema.Properties["code"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Código do erro: os do software original (E9015 cabo não encontrado, E9016 timeout, W7012 sem dados, "
                        + "E9002 sem link, E9007 valor inválido, W7010 Hi ≤ Lo, W8011 quadro inválido, E9017 sem resposta ou sem datalogger...), "
                        + "o da biblioteca E9014 (família sem datalogger, como o Minipa ET-2517A) ou NOT_FOUND, CONFLICT, VALIDATION, INTERNAL.",
                };
            }
            return Task.CompletedTask;
        });
    }

    // O objeto "model" de GET /api/v1/meter e do evento "status": os valores possíveis vão no enum, para os clientes gerados.
    private static void DescribeModel(OpenApiSchema schema)
    {
        schema.Description = "Medidor detectado pelo Model ID (bytes 20–23 do quadro de tempo real), ou fixado em Meter:Family. "
            + "O Model ID identifica a família, e não o modelo exato.";
        Describe(schema, "family", "Família do medidor.", "BM52x", "BM82x");
        Describe(schema, "modelId", "Model ID e código do pedido de tempo real (52 66 ou 82 66).", "0x52", "0x82");
        Describe(schema, "name", "Família com os modelos validados no hardware.", "Minipa ET-2615A (Brymen BM525s, BM52x)", "Minipa ET-2517A (Brymen BM827s, BM82x)");
        Describe(schema, "supportsDataLogger", "Se o medidor tem datalogger (só a família BM52x); false = POST /api/v1/logger/imports responde 409 com E9014.");
    }

    private static void DescribeRealTimeStatistics(OpenApiSchema schema)
    {
        schema.Description = "Consultas de tempo real desde a partida do serviço. Os pedidos que o ET-2517A descarta e que um reenvio recupera "
            + "não são falhas: não geram W7012 nem contam para o E9016 do polling.";
        Describe(schema, "queries", "Consultas terminadas, com ou sem resposta.");
        Describe(schema, "requests", "Pedidos 66 enviados: o 1º de cada consulta, os reenvios e o 52 66 de reserva da detecção.");
        Describe(schema, "resends", "Reenvios depois da janela do 1º relatório (Meter:RealTimeResendTimeout).");
        Describe(schema, "lostRequests", "Pedidos sem resposta dentro da janela: os reenviados e os das consultas que falharam.");
        Describe(schema, "recoveredQueries", "Consultas que só tiveram resposta depois de ao menos um reenvio.");
        Describe(schema, "failedQueries", "Consultas sem resposta completa (cada uma é um W7012 do polling).");
    }

    private static void Describe(OpenApiSchema schema, string property, string description, params string[] values)
    {
        if (schema.Properties is null || !schema.Properties.TryGetValue(property, out var value) || value is not OpenApiSchema target)
            return;
        target.Description = description;
        if (values.Length > 0)
            target.Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v))];
    }
}
