using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Brymen.Dmm.Api;

/// <summary>Informações do documento OpenAPI, descrições das tags e o campo <c>code</c> dos ProblemDetails.</summary>
internal static class OpenApiSetup
{
    private static readonly Dictionary<string, string> TagDescriptions = new(StringComparer.Ordinal)
    {
        ["Medidor"] = "Link/Unlink e estado do cabo BC-86X (VID 0820, PID 0001).",
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
                Description = "Port do Bs82-52x Data Recording System V6.0.0.9 para o Brymen BM525s (= Minipa ET-2615A) com o cabo óptico BC-86X: "
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
            if (context.JsonTypeInfo.Type == typeof(ProblemDetails) || context.JsonTypeInfo.Type == typeof(HttpValidationProblemDetails))
            {
                schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
                schema.Properties["code"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description = "Código do erro: os do software original (E9015 cabo não encontrado, E9016 timeout, W7012 sem dados, "
                        + "E9002 sem link, E9007 valor inválido, W7010 Hi ≤ Lo, W8011 quadro inválido...) ou NOT_FOUND, CONFLICT, VALIDATION, INTERNAL.",
                };
            }
            return Task.CompletedTask;
        });
    }
}
