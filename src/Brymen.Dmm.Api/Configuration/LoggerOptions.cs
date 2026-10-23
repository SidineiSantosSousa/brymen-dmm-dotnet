using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Configuration;

/// <summary>Seção <c>Logger</c> do appsettings ("Import data from DMM").</summary>
public sealed class LoggerOptions
{
    public const string SectionName = "Logger";

    /// <summary>
    /// Quantas importações terminadas ficam guardadas (na memória, decodificadas, e em <c>imports/{id}.bin</c>). Ao terminar
    /// uma nova, e na partida, as mais antigas são apagadas; uma importação em andamento não conta.
    /// </summary>
    public int ImportRetention { get; set; } = 10;
}

internal sealed class LoggerOptionsValidator : IValidateOptions<LoggerOptions>
{
    public const int MaxImportRetention = 1000;

    public ValidateOptionsResult Validate(string? name, LoggerOptions options)
        => options.ImportRetention is < 1 or > MaxImportRetention
            ? ValidateOptionsResult.Fail($"Logger:ImportRetention deve estar entre 1 e {MaxImportRetention} (valor: {options.ImportRetention}).")
            : ValidateOptionsResult.Success;
}
