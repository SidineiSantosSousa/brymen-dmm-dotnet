namespace Brymen.Dmm;

/// <summary>
/// Códigos de erro do <see cref="BrymenMeter"/> e do <see cref="MeterConnection"/>. Os códigos W/E são os do software
/// original da Brymen (Bs82-52x V6.0.0.9), para facilitar o suporte cruzado.
/// </summary>
public static class BrymenErrorCodes
{
    /// <summary>
    /// <c>E9015</c>, “USB adapter cable not found.”: o cabo BC-86X (VID 0820h/PID 0001h) não foi encontrado, ou não pôde
    /// ser aberto (sem permissão no <c>/dev/hidrawN</c>, por exemplo).
    /// </summary>
    public const string CableNotFound = "E9015";

    /// <summary>
    /// <c>E9016</c>, “Data transmission timeout.”: a 3ª falha seguida da leitura contínua, ou a leitura da memória que
    /// esgotou as tentativas.
    /// </summary>
    public const string TransmissionTimeout = "E9016";

    /// <summary>
    /// <c>E9017</c>: o medidor está desligado, desconectado ou não tem a função DATA LOGGING (o Model ID do tempo real não
    /// é <c>52h</c>).
    /// </summary>
    public const string NoDataLogger = "E9017";

    /// <summary><c>W7012</c>, “No data received.”: um <c>52 66</c> sem resposta completa (timeout ou falha de E/S).</summary>
    public const string NoDataReceived = "W7012";

    /// <summary><c>W8011</c>, “A handshaking error occurred.”: quadro inválido ou inconsistente recebido do medidor.</summary>
    public const string InvalidFrame = "W8011";

    /// <summary>
    /// Um handler de <see cref="BrymenMeter.ReadingReceived"/> ou <see cref="BrymenMeter.StateChanged"/> lançou uma
    /// exceção (próprio da biblioteca; não muda o estado nem o <see cref="BrymenMeter.LastError"/>).
    /// </summary>
    public const string EventHandlerFailed = "EVENT_HANDLER";

    /// <summary>Falha inesperada (um defeito) na leitura contínua, registrada para que o laço não morra em silêncio (próprio da biblioteca).</summary>
    public const string Internal = "INTERNAL";
}

/// <summary>Uma falha registrada pelo <see cref="BrymenMeter"/>.</summary>
/// <param name="Code">Código da falha (<see cref="BrymenErrorCodes"/>).</param>
/// <param name="Message">Mensagem para o usuário, em português.</param>
/// <param name="Exception">Exceção que causou a falha, quando houver.</param>
/// <param name="Timestamp">Instante da falha, pelo <see cref="BrymenMeterOptions.TimeProvider"/>.</param>
public sealed record BrymenMeterError(string Code, string Message, Exception? Exception, DateTimeOffset Timestamp);

/// <summary>Falha de comunicação com o cabo ou com o medidor, com o código de <see cref="BrymenErrorCodes"/>.</summary>
public sealed class BrymenMeterException : Exception
{
    /// <summary>Cria a exceção com o código, a mensagem e a causa.</summary>
    /// <param name="code">Código da falha (<see cref="BrymenErrorCodes"/>).</param>
    /// <param name="message">Mensagem para o usuário, em português.</param>
    /// <param name="innerException">Exceção que causou a falha, quando houver.</param>
    public BrymenMeterException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    /// <summary>Código da falha (<see cref="BrymenErrorCodes"/>), por exemplo <c>E9015</c>.</summary>
    public string Code { get; }

    /// <summary>A falha como <see cref="BrymenMeterError"/> (com esta exceção), no instante <paramref name="timestamp"/>.</summary>
    /// <param name="timestamp">Instante da falha.</param>
    public BrymenMeterError ToError(DateTimeOffset timestamp) => new(Code, Message, this, timestamp);
}

/// <summary>Mensagens do software original (string table), traduzidas.</summary>
internal static class MeterMessages
{
    public const string CableNotFound = "Cabo adaptador USB não encontrado.";
    public const string NoDataReceived = "Nenhum dado recebido. Verifique a conexão do cabo e se o medidor está ligado.";
    public const string TransmissionTimeout = "Tempo esgotado na transmissão de dados. Verifique o medidor, a conexão do cabo e se o medidor está ligado.";
    public const string NoDataLogger = "O medidor está desligado, desconectado ou não tem a função DATA LOGGING.";
    public const string InvalidFrame = "Quadro inválido recebido do medidor:";
}
