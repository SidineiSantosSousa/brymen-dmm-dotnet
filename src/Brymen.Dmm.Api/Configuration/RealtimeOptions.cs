using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Configuration;

/// <summary>Seção <c>SignalR</c> do appsettings: hub <c>/hubs/meter</c>.</summary>
public sealed class SignalRSettings
{
    public const string SectionName = "SignalR";

    /// <summary>Manda ao cliente a mensagem das exceções dos métodos do hub (só para depuração).</summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>
    /// Intervalo dos pings do servidor quando não há outra mensagem. O <c>ServerTimeout</c> dos clientes (30 s por
    /// padrão) deve ser pelo menos o dobro deste valor.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Seção <c>Cors</c> do appsettings: origens de clientes browser (painel, SignalR) hospedados em outro endereço.</summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Origens permitidas, como <c>http://painel.local:8080</c> (esquema, host e porta, sem caminho). Vazio (padrão)
    /// desliga o CORS. A política aceita credenciais, exigidas pelo cliente SignalR no browser, e por isso não aceita <c>*</c>.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>Seção <c>Sse</c> do appsettings: <c>GET /api/v1/readings/stream</c>.</summary>
public sealed class SseSettings
{
    public const string SectionName = "Sse";

    /// <summary>Intervalo do evento <c>heartbeat</c>, que mantém a conexão viva em proxies que fecham conexões ociosas.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Validação das seções <c>SignalR</c>, <c>Cors</c> e <c>Sse</c> na partida (sem reflexão, compatível com AOT).</summary>
internal sealed class RealtimeOptionsValidator : IValidateOptions<SignalRSettings>, IValidateOptions<CorsSettings>, IValidateOptions<SseSettings>
{
    public ValidateOptionsResult Validate(string? name, SignalRSettings options)
    {
        if (options.KeepAliveInterval < TimeSpan.FromSeconds(1) || options.KeepAliveInterval > TimeSpan.FromMinutes(10))
            return ValidateOptionsResult.Fail($"SignalR:KeepAliveInterval deve estar entre 00:00:01 e 00:10:00 (valor: {options.KeepAliveInterval:c}).");
        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        List<string> errors = [];
        foreach (string origin in options.AllowedOrigins)
        {
            if (!IsOrigin(origin))
                errors.Add($"Cors:AllowedOrigins: '{origin}' não é uma origem válida; use esquema, host e porta, como http://painel.local:8080 (sem caminho e sem *).");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public ValidateOptionsResult Validate(string? name, SseSettings options)
    {
        if (options.HeartbeatInterval < TimeSpan.FromMilliseconds(10) || options.HeartbeatInterval > TimeSpan.FromMinutes(10))
            return ValidateOptionsResult.Fail($"Sse:HeartbeatInterval deve estar entre 00:00:00.010 e 00:10:00 (valor: {options.HeartbeatInterval:c}).");
        return ValidateOptionsResult.Success;
    }

    /// <summary>Remove entradas vazias e a barra final (<c>http://x/</c> não casaria com o cabeçalho <c>Origin</c>).</summary>
    public static string[] NormalizeOrigins(string[] origins)
        => [.. origins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim().TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool IsOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0 && !origin.Contains('*', StringComparison.Ordinal);
}
