using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Brymen.Dmm.Api.Endpoints;

/// <summary>Graphical Recorder / Data List do original.</summary>
internal static class AcquisitionEndpoints
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 10_000;

    public static IEndpointRouteBuilder MapAcquisitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/acquisition").WithTags("Aquisição");
        group.MapGet("", GetStatus)
            .WithName("GetAcquisition")
            .WithSummary("Estado da aquisição")
            .WithDescription("Modo, contagem de amostras, capacidade (Acquisition:MaxSamples), arquivo de backup e faixas Max/Min observadas.");
        group.MapGet("/settings", GetSettings)
            .WithName("GetAcquisitionSettings")
            .WithSummary("Modo e intervalo de amostragem");
        group.MapPut("/settings", UpdateSettings)
            .WithName("UpdateAcquisitionSettings")
            .WithSummary("Altera o modo e o intervalo")
            .WithDescription("Modos: fullSpeed (toda leitura), interval (N segundos inteiros, como o \"Customization...\") e manual (POST /samples). "
                + $"Campos ausentes mantêm o valor atual. Intervalo fora de 0..{Recorder.MaxIntervalSeconds} (ou 0 no modo interval) responde 400 com E9007; "
                + "com a gravação em andamento responde 409.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/start", Start)
            .WithName("StartAcquisition")
            .WithSummary("Inicia a gravação")
            .WithDescription("As amostras se somam às do buffer. Exige o link ativo (409 com E9002, como o original). Cria um novo backup ~BDMDLn.csv.")
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/stop", Stop)
            .WithName("StopAcquisition")
            .WithSummary("Para a gravação");
        group.MapPost("/samples", AddSample)
            .WithName("AddAcquisitionSample")
            .WithSummary("Grava uma amostra manual")
            .WithDescription("Só no modo manual com a gravação iniciada. A amostra é a leitura atual; sem leitura responde 503 com W7012 e, "
                + "durante a importação do datalogger, 409.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/samples", GetSamples)
            .WithName("GetAcquisitionSamples")
            .WithSummary("Amostras do buffer (paginado)")
            .WithDescription($"offset a partir de 0 e limit de 1 a {MaxPageSize} (padrão {DefaultPageSize}).")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/samples.csv", ExportCsv)
            .WithName("ExportAcquisitionCsv")
            .WithSummary("Exporta o buffer em CSV")
            .WithDescription("Formato exato do \"Export → Text/Data\" do original: #item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc. "
                + "Com ?format=extended acrescenta Timestamp (ISO-8601), MainSiValue, SubSiValue, Flags e Comparator.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapDelete("/samples", Clear)
            .WithName("ClearAcquisitionSamples")
            .WithSummary("Apaga o buffer");
        return app;
    }

    private static Ok<AcquisitionStatusDto> GetStatus(Recorder recorder, TimeProvider time)
        => TypedResults.Ok(DtoMapper.ToDto(recorder.GetStatus(), time.GetUtcNow()));

    private static Ok<AcquisitionSettingsDto> GetSettings(Recorder recorder)
    {
        var status = recorder.GetStatus();
        return TypedResults.Ok(new AcquisitionSettingsDto(status.Mode, status.IntervalSeconds));
    }

    private static Results<Ok<AcquisitionSettingsDto>, ProblemHttpResult> UpdateSettings(AcquisitionSettingsRequest request, Recorder recorder)
    {
        if (recorder.UpdateSettings(request.Mode, request.IntervalSeconds) is { } error)
            return ApiProblems.From(error);
        var status = recorder.GetStatus();
        return TypedResults.Ok(new AcquisitionSettingsDto(status.Mode, status.IntervalSeconds));
    }

    private static Results<Ok<AcquisitionStatusDto>, ProblemHttpResult> Start(Recorder recorder, TimeProvider time)
    {
        if (recorder.Start() is { } error)
            return ApiProblems.From(error);
        return TypedResults.Ok(DtoMapper.ToDto(recorder.GetStatus(), time.GetUtcNow()));
    }

    private static Ok<AcquisitionStatusDto> Stop(Recorder recorder, TimeProvider time)
    {
        recorder.Stop();
        return TypedResults.Ok(DtoMapper.ToDto(recorder.GetStatus(), time.GetUtcNow()));
    }

    private static Results<Created<AcquisitionSampleDto>, ProblemHttpResult> AddSample(Recorder recorder)
    {
        var (sample, error) = recorder.AddManual();
        if (error is not null || sample is null)
            return ApiProblems.From(error ?? new ApiError(ErrorCodes.Internal, "Amostra não gravada."));
        return TypedResults.Created($"/api/v1/acquisition/samples?offset={sample.Index}&limit=1", DtoMapper.ToDto(sample));
    }

    private static Results<Ok<PageDto<AcquisitionSampleDto>>, ProblemHttpResult> GetSamples(Recorder recorder, int? offset, int? limit)
    {
        if (ValidatePage(offset, limit, out int start, out int count) is { } problem)
            return problem;
        var (items, total) = recorder.GetPage(start, count);
        return TypedResults.Ok(new PageDto<AcquisitionSampleDto>(start, count, total, [.. items.Select(DtoMapper.ToDto)]));
    }

    private static Results<PushStreamHttpResult, ProblemHttpResult> ExportCsv(Recorder recorder, string? format, CancellationToken cancellationToken)
    {
        if (ParseCsvFormat(format, out bool extended) is { } problem)
            return problem;
        var samples = recorder.Snapshot();
        return TypedResults.Stream(
            stream => CsvWriter.WriteAsync(stream, AcquisitionCsv.HeaderFor(extended), samples.Select(s => AcquisitionCsv.Row(s, extended)), cancellationToken),
            CsvFormat.ContentType,
            extended ? "samples-extended.csv" : "samples.csv");
    }

    private static NoContent Clear(Recorder recorder)
    {
        recorder.Clear();
        return TypedResults.NoContent();
    }

    public static ProblemHttpResult? ValidatePage(int? offset, int? limit, out int start, out int count)
    {
        start = offset ?? 0;
        count = limit ?? DefaultPageSize;
        if (start < 0)
            return ApiProblems.Validation("offset deve ser maior ou igual a 0.");
        if (count is < 1 or > MaxPageSize)
            return ApiProblems.Validation($"limit deve estar entre 1 e {MaxPageSize}.");
        return null;
    }

    public static ProblemHttpResult? ParseCsvFormat(string? format, out bool extended)
    {
        extended = string.Equals(format, "extended", StringComparison.OrdinalIgnoreCase);
        if (format is null || extended || string.Equals(format, "original", StringComparison.OrdinalIgnoreCase))
            return null;
        return ApiProblems.Validation("format deve ser \"original\" (padrão) ou \"extended\".");
    }
}

/// <summary>Escrita assíncrona de CSV na resposta (linhas CRLF, UTF-8 sem BOM).</summary>
internal static class CsvWriter
{
    /// <summary>
    /// Escreve o cabeçalho e as linhas, que só são formatadas conforme saem. Pare com o <c>RequestAborted</c>: depois que
    /// o cliente desconecta, o Kestrel descarta as escritas sem erro, e sem o token as linhas seriam formatadas até o fim
    /// (até 1 milhão no buffer da aquisição).
    /// </summary>
    public static async Task WriteAsync(Stream stream, string header, IEnumerable<string> rows, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, CsvFormat.Encoding, bufferSize: 16 * 1024, leaveOpen: true) { NewLine = CsvFormat.NewLine };
        await writer.WriteLineAsync(header.AsMemory(), cancellationToken);
        foreach (string row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(row.AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }
}
