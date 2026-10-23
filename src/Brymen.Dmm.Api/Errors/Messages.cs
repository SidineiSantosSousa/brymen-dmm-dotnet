namespace Brymen.Dmm.Api.Errors;

/// <summary>
/// Mensagens do software original (string table), traduzidas. As de cabo ausente (E9015), medidor sem datalogger
/// (E9017) e quadro inválido (W8011) vêm da biblioteca Brymen.Dmm, na mensagem da <c>BrymenMeterException</c>. As do
/// polling (W7012 e E9016) são montadas aqui com a causa: a W7012 da API avisa que a nova tentativa é automática. A da
/// família sem datalogger (E9014) é a da recusa da importação, antes de criar o job.
/// </summary>
internal static class Messages
{
    public const string NoDataReceived = "Nenhum dado recebido. Verifique a conexão do cabo; nova tentativa automática em instantes.";
    public const string TransmissionTimeout = "Tempo esgotado na transmissão de dados. Verifique o medidor, a conexão do cabo e se o medidor está ligado.";
    public const string NotLinked = "Ative o link com o multímetro antes de iniciar a gravação de dados.";
    public const string BackupFile = "Não foi possível criar o arquivo de backup em tempo real no disco.";
    public const string HiNotAboveLo = "O limite Hi é menor ou igual ao limite Lo.";
    public const string LowBattery = "A bateria do multímetro está fraca.";
    public const string ImportInProgress = "Importação do datalogger em andamento; o polling do tempo real está pausado.";
    public const string DataLoggerNotSupported = "O multímetro não tem datalogger: nenhum comando de memória foi enviado e nenhuma importação foi criada.";
}
