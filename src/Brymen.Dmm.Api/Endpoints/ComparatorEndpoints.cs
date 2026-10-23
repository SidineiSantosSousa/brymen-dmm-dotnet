using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Brymen.Dmm.Api.Endpoints;

/// <summary>Comparator (Hi/Lo, alarme) do original.</summary>
internal static class ComparatorEndpoints
{
    public static IEndpointRouteBuilder MapComparatorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/comparator").WithTags("Comparador");
        group.MapGet("", Get)
            .WithName("GetComparator")
            .WithSummary("Limites do comparador")
            .WithDescription("Hi/Lo aplicados ao siValue do display principal. Comparações estritas: v > Hi dá High, v < Lo dá Low, senão Pass.");
        group.MapPut("", UpdateAsync)
            .WithName("UpdateComparator")
            .WithSummary("Altera os limites do comparador")
            .WithDescription("Campos ausentes mantêm o valor atual. Hi ≤ Lo responde 400 com W7010. Persistido em settings.json no DataDirectory.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static Ok<ComparatorSettingsDto> Get(ComparatorService comparator) => TypedResults.Ok(DtoMapper.ToDto(comparator.Current));

    private static async Task<Results<Ok<ComparatorSettingsDto>, ProblemHttpResult>> UpdateAsync(
        ComparatorSettingsRequest request, ComparatorService comparator, CancellationToken cancellationToken)
    {
        // Os campos ausentes saem do valor atual lido sob o gate da gravação, e não de uma leitura anterior a ele.
        var (applied, error) = await comparator.UpdateAsync(current => new ComparatorSettings(
            request.Enabled ?? current.Enabled,
            request.High ?? current.High,
            request.Low ?? current.Low,
            request.Alarm ?? current.Alarm), cancellationToken);
        if (error is not null || applied is null)
            return ApiProblems.From(error ?? new ApiError(ErrorCodes.Internal, "Comparador não alterado."));
        return TypedResults.Ok(DtoMapper.ToDto(applied));
    }
}
