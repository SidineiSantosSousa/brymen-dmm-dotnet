namespace Brymen.Dmm.Api.Errors;

/// <summary>
/// Códigos de erro expostos em <c>ProblemDetails.extensions.code</c>. Os códigos W/E são os do software
/// original (relatório de RE, §12), para facilitar o suporte cruzado; os de comunicação vêm da biblioteca
/// (<see cref="BrymenErrorCodes"/>). Os demais são próprios da API.
/// </summary>
public static class ErrorCodes
{
    /// <summary>"You must link to the DMM ... before starting data logging."</summary>
    public const string NotLinked = "E9002";

    /// <summary>"Unable to create file for real time temporary storage in hard disk."</summary>
    public const string BackupFile = "E9003";

    /// <summary>"Not a valid floating point value !" (valor/intervalo inválido).</summary>
    public const string InvalidValue = "E9007";

    /// <summary>"Limit setting is unacceptable."</summary>
    public const string InvalidLimit = "E9008";

    /// <summary>"USB adapter cable not found."</summary>
    public const string CableNotFound = BrymenErrorCodes.CableNotFound;

    /// <summary>"Data transmission timeout. Please check meter and cable connection as well as meter power."</summary>
    public const string TransmissionTimeout = BrymenErrorCodes.TransmissionTimeout;

    /// <summary>"Your meter is power off, disconnected or without built-in DATA LOGGING function."</summary>
    public const string NoDataLogger = BrymenErrorCodes.NoDataLogger;

    /// <summary>
    /// Próprio da biblioteca: a família do medidor não tem datalogger (BM82x, como o Minipa ET-2517A). A importação é
    /// recusada sem nenhum comando de memória.
    /// </summary>
    public const string DataLoggerNotSupported = BrymenErrorCodes.DataLoggerNotSupported;

    /// <summary>"Hi Limit is lower than or equal to Low Limit !"</summary>
    public const string HiNotAboveLo = "W7010";

    /// <summary>"No data received." + "Check cable connection..." (falha de leitura do tempo real).</summary>
    public const string NoDataReceived = BrymenErrorCodes.NoDataReceived;

    /// <summary>"The battery power of the DMM is low."</summary>
    public const string LowBattery = "W8000";

    /// <summary>"A handshaking error occurred." Usado para quadro inválido ou inconsistente.</summary>
    public const string InvalidFrame = BrymenErrorCodes.InvalidFrame;

    public const string NotFound = "NOT_FOUND";

    /// <summary>Requisição de uma página de outra origem que alteraria o estado (ou abriria o WebSocket do hub).</summary>
    public const string Forbidden = "FORBIDDEN";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string Conflict = "CONFLICT";
    public const string Validation = "VALIDATION";
    public const string Internal = "INTERNAL";
}

/// <summary>Erro com código e mensagem para o usuário.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>
/// Falha de operação da própria API, com código de <see cref="ErrorCodes"/> (ex.: CONFLICT). As falhas do cabo e do
/// medidor chegam da biblioteca como <see cref="BrymenMeterException"/>.
/// </summary>
public sealed class ApiException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;

    public ApiError ToError() => new(Code, Message);
}

/// <summary>Conversão das falhas da biblioteca Brymen.Dmm para os erros da API.</summary>
internal static class BrymenMeterExceptionExtensions
{
    /// <summary>O código (<see cref="BrymenErrorCodes"/>, os mesmos de <see cref="ErrorCodes"/>) e a mensagem da falha.</summary>
    public static ApiError ToApiError(this BrymenMeterException exception) => new(exception.Code, exception.Message);
}
