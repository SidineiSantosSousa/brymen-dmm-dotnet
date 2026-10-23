using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Security;

/// <summary>
/// Proteção contra requisições forjadas por páginas de outra origem (CSRF e WebSocket entre sites). O CORS só decide se
/// o browser entrega a resposta à página: um POST sem corpo (requisição simples, sem preflight), como
/// <c>POST /api/v1/acquisition/start</c>, e o WebSocket do hub chegam ao servidor vindos de qualquer origem. Aqui, os
/// métodos que alteram estado e o upgrade para WebSocket só passam quando vêm da própria origem, de uma origem de
/// <c>Cors:AllowedOrigins</c> ou de um cliente fora do browser; os outros recebem 403 com o código <c>FORBIDDEN</c>.
/// </summary>
/// <remarks>
/// A regra é a do <c>CrossOriginProtection</c> do Go 1.25. Com <c>Sec-Fetch-Site</c> (browsers atuais, que não deixam a
/// página alterá-lo), passam <c>same-origin</c> e <c>none</c>; o valor não depende do <c>Host</c>, então um proxy reverso
/// que reescreve o <c>Host</c> não atrapalha. Sem ele, passa a requisição sem <c>Origin</c> (curl, clientes .NET) ou com a
/// <c>Origin</c> igual ao <c>Host</c>. Não protege contra DNS rebinding, em que a página passa a ter a mesma origem da API:
/// para isso, restrinja <c>AllowedHosts</c>.
/// </remarks>
internal sealed class CrossSiteRequestGuard
{
    private const string SecFetchSite = "Sec-Fetch-Site";

    private readonly HashSet<string> _allowedOrigins;
    private readonly ILogger<CrossSiteRequestGuard> _logger;

    public CrossSiteRequestGuard(IOptions<CorsSettings> cors, ILogger<CrossSiteRequestGuard> logger)
    {
        _allowedOrigins = new HashSet<string>(cors.Value.AllowedOrigins, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (Check(context.Request) is not { } source)
            return next(context);

        _logger.LogDebug("Requisição {Method} {Path} de outra origem recusada ({Source}).", context.Request.Method, context.Request.Path, source);
        var problem = ApiProblems.Create(
            StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, ApiProblems.TitleFor(ErrorCodes.Forbidden),
            $"Requisição de outra origem ({source}) recusada. Para liberar um cliente browser hospedado em outro endereço, "
            + "inclua a origem em Cors:AllowedOrigins.");
        return problem.ExecuteAsync(context);
    }

    /// <summary>Null quando a requisição pode seguir; senão, a origem recusada (para a mensagem e o log).</summary>
    internal string? Check(HttpRequest request)
    {
        if (!ChangesState(request))
            return null;

        string? origin = request.Headers.Origin is { Count: > 0 } values ? values.ToString() : null;
        if (origin is not null && _allowedOrigins.Contains(origin))
            return null;

        if (request.Headers[SecFetchSite] is { Count: > 0 } site)
        {
            string value = site.ToString();
            if (value.Equals("same-origin", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;
            return origin ?? $"{SecFetchSite}: {value}";
        }

        // Browser antigo ou cliente fora do browser: sem Origin não há página por trás.
        if (origin is null || IsSameHost(origin, request.Host))
            return null;
        return origin;
    }

    // Métodos seguros não alteram estado, exceto o GET de upgrade para WebSocket, que o CORS também não protege.
    private static bool ChangesState(HttpRequest request)
    {
        string method = request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method))
            return true;
        foreach (string? upgrade in request.Headers.Upgrade)
        {
            if (upgrade is not null && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // A Origin serializada pelo browser não traz a porta padrão, como o Host que ele mesmo envia.
    private static bool IsSameHost(string origin, HostString host)
        => host.HasValue
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase);
}
