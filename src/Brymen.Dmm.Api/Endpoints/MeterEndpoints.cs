using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Meter;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Endpoints;

/// <summary>Link/Unlink e leituras em tempo real (Digital Meter do original).</summary>
internal static class MeterEndpoints
{
    /// <summary>Evento SSE periódico, sem dados, que mantém a conexão viva em proxies; o <c>EventSource</c> o ignora.</summary>
    public const string HeartbeatEvent = "heartbeat";

    public static IEndpointRouteBuilder MapMeterEndpoints(this IEndpointRouteBuilder app)
    {
        var meter = app.MapGroup("/api/v1/meter").WithTags("Medidor");
        meter.MapGet("", GetStatus)
            .WithName("GetMeter")
            .WithSummary("Estado do medidor")
            .WithDescription("Estado do link, do cabo BC-86X (caminho, VID/PID, fabricante), do polling e o último erro registrado.");
        meter.MapPut("/link", SetLinkAsync)
            .WithName("SetMeterLink")
            .WithSummary("Ativa ou desativa o link")
            .WithDescription("Equivale ao botão Link/Unlink do original. Com o link ativo o serviço consulta 52 66 a cada Meter:PollInterval. "
                + "Sem cabo responde 503 com o código E9015 e o link fica desativado.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        var readings = app.MapGroup("/api/v1/readings").WithTags("Leituras");
        readings.MapGet("/latest", GetLatest)
            .WithName("GetLatestReading")
            .WithSummary("Última leitura")
            .WithDescription("Display principal e secundário, anunciadores e resultado do comparador. Sem leitura atual responde 503 (E9015, E9016, W7012 ou W8011); "
                + "com o link desativado, 409 com E9002 (503 com E9015 se o Link falhou por falta do cabo); durante a importação do datalogger (polling pausado), 409 com CONFLICT.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        readings.MapGet("/stream", Stream)
            .WithName("StreamReadings")
            .WithSummary("Leituras ao vivo (Server-Sent Events)")
            .WithDescription("Fluxo text/event-stream com um evento \"reading\" por leitura (o id do evento é o número sequencial). A primeira mensagem é a última leitura conhecida. "
                + "A cada Sse:HeartbeatInterval (15 s) vai um evento \"heartbeat\" sem dados, para proxies não fecharem a conexão; o fluxo termina quando o serviço é encerrado.");
        return app;
    }

    private static Ok<MeterStatusDto> GetStatus(MeterSession session, ReadingBroadcaster broadcaster)
        => TypedResults.Ok(DtoMapper.ToDto(session.GetSnapshot(), session, broadcaster));

    private static async Task<Results<Ok<MeterStatusDto>, ProblemHttpResult>> SetLinkAsync(
        LinkRequest request, MeterSession session, ReadingBroadcaster broadcaster, CancellationToken cancellationToken)
    {
        if (request.Enabled is not { } enabled)
            return ApiProblems.Validation("Informe \"enabled\": true ou false.");
        if (await session.SetLinkAsync(enabled, cancellationToken) is { } error)
            return ApiProblems.From(error);
        if (!enabled)
            broadcaster.ClearLatest();
        return TypedResults.Ok(DtoMapper.ToDto(session.GetSnapshot(), session, broadcaster));
    }

    // O motivo vem do estado atual, e não do histórico (lastError): link desativado pelo usuário é E9002 mesmo depois
    // de uma falha do cabo, e uma falha já superada por uma leitura boa não volta como se fosse de agora.
    private static Results<Ok<ReadingDto>, ProblemHttpResult> GetLatest(MeterSession session, ReadingBroadcaster broadcaster)
    {
        var snapshot = session.GetSnapshot();
        if (!snapshot.LinkEnabled)
            return ApiProblems.From(snapshot.LinkFailure?.ToApiError() ?? new ApiError(ErrorCodes.NotLinked, "O link com o multímetro está desativado."));
        if (snapshot.Importing)
            return ApiProblems.Conflict(Messages.ImportInProgress);
        if (broadcaster.GetCurrent(snapshot) is { } latest)
            return TypedResults.Ok(DtoMapper.ToDto(latest));
        return ApiProblems.From(snapshot.CurrentFault?.ToApiError() ?? new ApiError(ErrorCodes.NoDataReceived, Messages.NoDataReceived));
    }

    private static ServerSentEventsResult<ReadingDto?> Stream(
        ReadingBroadcaster broadcaster, IOptions<SseSettings> settings, TimeProvider time, IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(MeterEndpoints).FullName!);
        return TypedResults.ServerSentEvents(StreamReadingsAsync(
            broadcaster, settings.Value.HeartbeatInterval, time, logger, lifetime.ApplicationStopping, cancellationToken));
    }

    // Termina no fim da requisição, no ApplicationStopping (sem esperar o ShutdownTimeout do Kestrel) ou quando o
    // broadcaster é encerrado. A espera pela próxima leitura e pelo heartbeat nunca lança: o fim é um "yield break".
    private static async IAsyncEnumerable<SseItem<ReadingDto?>> StreamReadingsAsync(
        ReadingBroadcaster broadcaster, TimeSpan heartbeatInterval, TimeProvider time, ILogger logger,
        CancellationToken stopping, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        using var subscription = broadcaster.Subscribe(out var first);
        using var heartbeat = new PeriodicTimer(heartbeatInterval, time);
        var drops = new DropReporter(logger, "SSE /api/v1/readings/stream");
        long last = 0;
        if (first is not null)
        {
            last = first.Sequence;
            yield return ToEvent(first);
        }

        var reader = subscription.Reader;
        Task<bool>? nextReading = null;
        Task<bool>? nextBeat = null;
        try
        {
            while (true)
            {
                nextReading ??= WaitQuietlyAsync(reader.WaitToReadAsync(cts.Token));
                nextBeat ??= WaitQuietlyAsync(heartbeat.WaitForNextTickAsync(cts.Token));
                var done = await Task.WhenAny(nextReading, nextBeat).ConfigureAwait(false);
                if (!await done.ConfigureAwait(false))
                    yield break;
                if (done == nextBeat)
                {
                    nextBeat = null;
                    yield return new SseItem<ReadingDto?>(null, HeartbeatEvent);
                    continue;
                }
                nextReading = null;
                while (reader.TryRead(out var live))
                {
                    drops.Collect(subscription);
                    if (live.Sequence <= last)
                        continue;
                    last = live.Sequence;
                    yield return ToEvent(live);
                }
            }
        }
        finally
        {
            drops.Collect(subscription);
            drops.Flush();
        }
    }

    private static SseItem<ReadingDto?> ToEvent(LiveReading live) => new(DtoMapper.ToDto(live), "reading")
    {
        EventId = live.Sequence.ToString(CultureInfo.InvariantCulture),
    };

    // false no cancelamento: o fluxo termina limpo, sem exceção no meio da resposta.
    private static async Task<bool> WaitQuietlyAsync(ValueTask<bool> wait)
    {
        try
        {
            return await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
