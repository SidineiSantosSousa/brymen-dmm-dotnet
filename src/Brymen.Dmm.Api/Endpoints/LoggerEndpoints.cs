using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Protocol;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Brymen.Dmm.Api.Endpoints;

/// <summary>"Import data from DMM": leitura da memória do datalogger.</summary>
internal static class LoggerEndpoints
{
    public static IEndpointRouteBuilder MapLoggerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/logger/imports").WithTags("Datalogger");
        group.MapPost("", StartAsync)
            .WithName("StartLoggerImport")
            .WithSummary("Inicia a importação da memória")
            .WithDescription("Responde 202 com Location do job. A leitura (88h e depois 89h..., checksum, recuperação com 8Ah ou recomeço do 88h) "
                + "roda em segundo plano com o polling pausado. Sem cabo responde 503 com E9015; com outra importação em andamento, 409 com CONFLICT; "
                + "num medidor sem datalogger (família BM82x, como o Minipa ET-2517A: model.supportsDataLogger = false em GET /api/v1/meter), "
                + "409 com E9014, sem criar job. Com a família ainda desconhecida, ela é detectada antes, por uma consulta 82 66.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("", List)
            .WithName("ListLoggerImports")
            .WithSummary("Importações conhecidas");
        group.MapGet("/{id}", Get)
            .WithName("GetLoggerImport")
            .WithSummary("Estado e progresso de uma importação")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{id}", Delete)
            .WithName("DeleteLoggerImport")
            .WithSummary("Cancela ou apaga uma importação")
            .WithDescription("Em andamento: cancela. Terminada: apaga o job e a imagem bruta imports/{id}.bin.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id}/image", GetImage)
            .WithName("GetLoggerImportImage")
            .WithSummary("Imagem bruta da memória")
            .WithDescription("Blocos de 24 bytes concatenados a partir do ponteiro 0 (A0_007Ah), como lidos do medidor.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/sessions", GetSessions)
            .WithName("GetLoggerSessions")
            .WithSummary("Sessões gravadas no datalogger")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/sessions/{number:int}", GetSession)
            .WithName("GetLoggerSession")
            .WithSummary("Uma sessão do datalogger")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/sessions/{number:int}/samples", GetSamples)
            .WithName("GetLoggerSessionSamples")
            .WithSummary("Amostras de uma sessão (paginado)")
            .WithDescription($"offset a partir de 0 e limit de 1 a {AcquisitionEndpoints.MaxPageSize} (padrão {AcquisitionEndpoints.DefaultPageSize}).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/sessions/{number:int}/samples.csv", ExportCsv)
            .WithName("ExportLoggerSessionCsv")
            .WithSummary("Exporta uma sessão em CSV")
            .WithDescription("Mesmo cabeçalho do original; Time = índice × intervalo. Com ?format=extended acrescenta ElapsedSeconds, MainSiValue, SubSiValue e Flags.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<Results<Accepted<ImportJobDto>, ProblemHttpResult>> StartAsync(LoggerImportService imports, CancellationToken cancellationToken)
    {
        var (job, error) = await imports.StartAsync(cancellationToken);
        if (job is null)
            return ApiProblems.From(error ?? new ApiError(ErrorCodes.Internal, "Importação não iniciada."));
        return TypedResults.Accepted($"/api/v1/logger/imports/{job.Id}", DtoMapper.ToDto(job.GetSnapshot()));
    }

    private static Ok<IReadOnlyList<ImportJobDto>> List(LoggerImportService imports)
        => TypedResults.Ok<IReadOnlyList<ImportJobDto>>([.. imports.List().Select(j => DtoMapper.ToDto(j.GetSnapshot()))]);

    private static Results<Ok<ImportJobDto>, ProblemHttpResult> Get(string id, LoggerImportService imports)
        => Find(id, imports) is { } job ? TypedResults.Ok(DtoMapper.ToDto(job.GetSnapshot())) : NotFound(id);

    private static Results<NoContent, ProblemHttpResult> Delete(string id, LoggerImportService imports)
        => LoggerImportService.IsValidId(id) && imports.Delete(id) ? TypedResults.NoContent() : NotFound(id);

    private static Results<FileStreamHttpResult, ProblemHttpResult> GetImage(string id, LoggerImportService imports)
    {
        if (Find(id, imports)?.GetSnapshot() is not { } job)
            return NotFound(id);
        if (job.ImagePath is not { } path)
            return ApiProblems.Conflict("A imagem desta importação ainda não está disponível.");
        FileStream image;
        try
        {
            image = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Inclui o arquivo apagado entre a consulta ao job e a abertura (DELETE concorrente).
            return ApiProblems.Conflict("A imagem desta importação não está disponível.");
        }
        return TypedResults.File(image, "application/octet-stream", $"{job.Id}.bin");
    }

    private static Results<Ok<IReadOnlyList<LoggerSessionDto>>, ProblemHttpResult> GetSessions(string id, LoggerImportService imports)
    {
        if (Memory(id, imports, out var problem) is not { } memory)
            return problem!;
        return TypedResults.Ok<IReadOnlyList<LoggerSessionDto>>([.. memory.Sessions.Select(DtoMapper.ToDto)]);
    }

    private static Results<Ok<LoggerSessionDto>, ProblemHttpResult> GetSession(string id, int number, LoggerImportService imports)
    {
        if (Session(id, number, imports, out var problem) is not { } session)
            return problem!;
        return TypedResults.Ok(DtoMapper.ToDto(session));
    }

    private static Results<Ok<PageDto<LoggerSampleDto>>, ProblemHttpResult> GetSamples(
        string id, int number, int? offset, int? limit, LoggerImportService imports)
    {
        if (AcquisitionEndpoints.ValidatePage(offset, limit, out int start, out int count) is { } invalid)
            return invalid;
        if (Session(id, number, imports, out var problem) is not { } session)
            return problem!;
        var items = session.Samples.Skip(start).Take(count).Select(DtoMapper.ToDto);
        return TypedResults.Ok(new PageDto<LoggerSampleDto>(start, count, session.Samples.Count, [.. items]));
    }

    private static Results<PushStreamHttpResult, ProblemHttpResult> ExportCsv(
        string id, int number, string? format, LoggerImportService imports, CancellationToken cancellationToken)
    {
        if (AcquisitionEndpoints.ParseCsvFormat(format, out bool extended) is { } invalid)
            return invalid;
        if (Session(id, number, imports, out var problem) is not { } session)
            return problem!;
        return TypedResults.Stream(
            stream => CsvWriter.WriteAsync(stream, LoggerCsv.HeaderFor(extended), session.Samples.Select(s => LoggerCsv.Row(session, s, extended)), cancellationToken),
            Acquisition.CsvFormat.ContentType,
            $"session-{number}{(extended ? "-extended" : "")}.csv");
    }

    private static ImportJob? Find(string id, LoggerImportService imports)
        => LoggerImportService.IsValidId(id) ? imports.Get(id) : null;

    private static LoggerMemory? Memory(string id, LoggerImportService imports, out ProblemHttpResult? problem)
    {
        problem = null;
        if (Find(id, imports)?.GetSnapshot() is not { } job)
        {
            problem = NotFound(id);
            return null;
        }
        if (job.Memory is null)
        {
            problem = job.State == ImportJobState.Running
                ? ApiProblems.Conflict("A importação ainda está em andamento.")
                : ApiProblems.Conflict(job.Error is { } e ? $"A importação não terminou com sucesso: [{e.Code}] {e.Message}" : "A importação não terminou com sucesso.");
            return null;
        }
        return job.Memory;
    }

    private static LoggerSession? Session(string id, int number, LoggerImportService imports, out ProblemHttpResult? problem)
    {
        if (Memory(id, imports, out problem) is not { } memory)
            return null;
        var session = memory.Sessions.FirstOrDefault(s => s.Number == number);
        if (session is null)
            problem = ApiProblems.NotFound($"A importação {id} não tem a sessão {number} (sessões: {memory.Sessions.Count}).");
        return session;
    }

    private static ProblemHttpResult NotFound(string id) => ApiProblems.NotFound($"Importação '{id}' não encontrada.");
}
