using Microsoft.AspNetCore.Http.HttpResults;

namespace Brymen.Dmm.Api.Errors;

/// <summary>Monta respostas <c>ProblemDetails</c> (RFC 9457) com o código em <c>extensions.code</c>.</summary>
internal static class ApiProblems
{
    public static ProblemHttpResult Create(int statusCode, string code, string title, string? detail = null)
        => TypedResults.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ProblemHttpResult From(ApiError error) => Create(StatusFor(error.Code), error.Code, TitleFor(error.Code), error.Message);

    public static ProblemHttpResult NotFound(string detail) => Create(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Recurso não encontrado", detail);

    public static ProblemHttpResult Validation(string detail) => Create(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Requisição inválida", detail);

    public static ProblemHttpResult Conflict(string detail) => Create(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "Operação não permitida no estado atual", detail);

    /// <summary>
    /// <c>CustomizeProblemDetails</c>: os ProblemDetails gerados pelo framework (rota inexistente, corpo ou parâmetro
    /// inválido, exceção não tratada) também recebem <c>code</c> e um título em português. Os da API já têm <c>code</c>.
    /// </summary>
    public static void AddMissingCode(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        if (problem.Extensions.ContainsKey("code"))
            return;
        string code = CodeForStatus(problem.Status ?? context.HttpContext.Response.StatusCode);
        problem.Extensions["code"] = code;
        problem.Title = TitleFor(code);
    }

    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.MethodNotAllowed,
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        >= 400 and < 500 => ErrorCodes.Validation,
        _ => ErrorCodes.Internal,
    };

    public static int StatusFor(string code) => code switch
    {
        ErrorCodes.CableNotFound or ErrorCodes.TransmissionTimeout or ErrorCodes.NoDataReceived
            or ErrorCodes.NoDataLogger or ErrorCodes.InvalidFrame => StatusCodes.Status503ServiceUnavailable,
        // E9014: o medidor responde, mas a família não tem datalogger; a importação não é possível no estado atual.
        ErrorCodes.NotLinked or ErrorCodes.Conflict or ErrorCodes.DataLoggerNotSupported => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidValue or ErrorCodes.InvalidLimit or ErrorCodes.HiNotAboveLo or ErrorCodes.Validation => StatusCodes.Status400BadRequest,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.MethodNotAllowed => StatusCodes.Status405MethodNotAllowed,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TitleFor(string code) => code switch
    {
        ErrorCodes.CableNotFound => "Cabo adaptador USB não encontrado",
        ErrorCodes.TransmissionTimeout => "Tempo esgotado na transmissão de dados",
        ErrorCodes.NoDataReceived => "Nenhum dado recebido",
        ErrorCodes.NoDataLogger => "Medidor sem resposta ou sem datalogger",
        ErrorCodes.DataLoggerNotSupported => "Medidor sem datalogger",
        ErrorCodes.InvalidFrame => "Quadro inválido recebido do medidor",
        ErrorCodes.NotLinked => "Link com o multímetro desativado",
        ErrorCodes.BackupFile => "Falha no arquivo de backup",
        ErrorCodes.InvalidValue => "Valor inválido",
        ErrorCodes.InvalidLimit => "Limite inaceitável",
        ErrorCodes.HiNotAboveLo => "Limite Hi menor ou igual ao limite Lo",
        ErrorCodes.Forbidden => "Requisição de outra origem recusada",
        ErrorCodes.NotFound => "Recurso não encontrado",
        ErrorCodes.MethodNotAllowed => "Método HTTP não permitido neste recurso",
        ErrorCodes.Conflict => "Operação não permitida no estado atual",
        ErrorCodes.Validation => "Requisição inválida",
        _ => "Erro interno",
    };
}
