using System.Globalization;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm;

/// <summary>Resultado de um comando enviado ao medidor por um <see cref="ILoggerMemoryTransport"/>.</summary>
/// <param name="Frame">Quadro completo (27 ou 36 bytes, com os report IDs), ou <see langword="null"/> em falha.</param>
/// <param name="Error">A falha (timeout, relatório curto, E/S), ou <see langword="null"/> em sucesso.</param>
/// <param name="ReportsReceived">
/// Relatórios recebidos depois da escrita do comando, mesmo em falha. Com ≥ 1, o medidor certamente processou o comando;
/// com 0, não há como saber.
/// </param>
public readonly record struct MeterCommandResult(byte[]? Frame, Exception? Error, int ReportsReceived)
{
    /// <summary>Se o quadro chegou completo.</summary>
    public bool Succeeded => Frame is not null;
}

/// <summary>
/// Transporte usado pelo <see cref="LoggerMemoryReader"/>: envia um comando e informa o desfecho, e fecha o cabo para
/// uma nova tentativa. O <see cref="MeterConnection"/> tem uma implementação; outra pode ser feita sobre um acesso
/// exclusivo próprio (como o da API REST, que pausa o polling durante a importação).
/// </summary>
public interface ILoggerMemoryTransport
{
    /// <summary>
    /// Envia <paramref name="command"/> e espera <paramref name="reportCount"/> relatórios. Uma falha de comunicação
    /// volta no resultado, e não como exceção.
    /// </summary>
    /// <exception cref="BrymenMeterException">O cabo não foi encontrado ao reabri-lo (<see cref="BrymenErrorCodes.CableNotFound"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken);

    /// <summary>Fecha o cabo para cancelar leituras pendentes; o próximo comando o reabre (o original reabre o HID a cada nova tentativa).</summary>
    Task ResetAsync(CancellationToken cancellationToken);
}

/// <summary>Progresso da leitura da memória do datalogger.</summary>
/// <param name="BlocksRead">Blocos de 24 bytes lidos até agora.</param>
/// <param name="BlocksTotal">Blocos a ler (1 × <c>88</c> + N × <c>89</c>), conhecido depois do <c>88</c>; 0 antes dele.</param>
/// <param name="TotalBytes">TotalBytes do cabeçalho da memória; 0 antes do <c>88</c>.</param>
/// <param name="Retries">Tentativas repetidas até agora (<c>88</c> repetidos e <c>8A</c>).</param>
/// <param name="Restarts">Recomeços a partir do <c>88</c> até agora.</param>
public readonly record struct LoggerReadProgress(int BlocksRead, int BlocksTotal, int TotalBytes, int Retries, int Restarts)
{
    /// <summary>Percentual lido, de 0 a 100 (0 enquanto <see cref="BlocksTotal"/> for desconhecido).</summary>
    public double Percent => BlocksTotal > 0 ? 100.0 * Math.Min(BlocksRead, BlocksTotal) / BlocksTotal : 0;
}

/// <summary>
/// Imagem bruta da memória do datalogger: os 24 bytes úteis de cada bloco, concatenados a partir do ponteiro 0, como o
/// medidor os entregou (o último bloco pode passar do TotalBytes, com zeros). Decodifique com <see cref="Decode"/>.
/// </summary>
public sealed class LoggerMemoryImage
{
    private readonly byte[] _image;
    private IReadOnlyList<ReadOnlyMemory<byte>>? _blocks;

    /// <summary>Cria a imagem a partir dos dados lidos.</summary>
    /// <param name="header">Cabeçalho da memória (do bloco do <c>88</c>).</param>
    /// <param name="image">Blocos de 24 bytes concatenados; o conteúdo é copiado.</param>
    /// <param name="retries">Tentativas repetidas durante a leitura.</param>
    /// <param name="restarts">Recomeços a partir do <c>88</c> durante a leitura.</param>
    /// <param name="warnings">Avisos da leitura (blocos diferentes na releitura, recomeços).</param>
    public LoggerMemoryImage(LoggerHeader header, ReadOnlySpan<byte> image, int retries = 0, int restarts = 0, IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (image.Length % Bm52xCommands.MemoryBlockDataLength != 0)
            throw new ArgumentException("A imagem deve ter um múltiplo de 24 bytes (blocos de memória).", nameof(image));
        Header = header;
        _image = image.ToArray();
        Retries = retries;
        Restarts = restarts;
        Warnings = warnings is null ? [] : [.. warnings];
    }

    /// <summary>Cabeçalho global da memória: Model ID, TotalBytes e número de sessões.</summary>
    public LoggerHeader Header { get; }

    /// <summary>Os blocos de 24 bytes concatenados, a partir do ponteiro 0 (o formato do <c>imports/{id}.bin</c> da API).</summary>
    public ReadOnlyMemory<byte> Image => _image;

    /// <summary>Número de blocos de 24 bytes lidos (1 × <c>88</c> + N × <c>89</c>).</summary>
    public int BlockCount => _image.Length / Bm52xCommands.MemoryBlockDataLength;

    /// <summary>Os blocos de 24 bytes, na ordem do ponteiro.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Blocks => _blocks ??= [.. Enumerable.Range(0, BlockCount).Select(GetBlock)];

    /// <summary>Tentativas repetidas durante a leitura (<c>88</c> repetidos e <c>8A</c>).</summary>
    public int Retries { get; }

    /// <summary>Recomeços a partir do <c>88</c> durante a leitura.</summary>
    public int Restarts { get; }

    /// <summary>Avisos da leitura (por exemplo, um bloco que veio diferente na releitura depois de um recomeço).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>O bloco <paramref name="index"/> (24 bytes; o bloco 0 é o do <c>88</c>).</summary>
    /// <param name="index">Índice do bloco, de 0 a <see cref="BlockCount"/> − 1.</param>
    public ReadOnlyMemory<byte> GetBlock(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockCount);
        return _image.AsMemory(index * Bm52xCommands.MemoryBlockDataLength, Bm52xCommands.MemoryBlockDataLength);
    }

    /// <summary>Cópia da imagem, para gravar em disco, por exemplo.</summary>
    public byte[] ToArray() => _image.ToArray();

    /// <summary>Decodifica as sessões e as amostras (<see cref="Bm52xMemory.Decode"/>).</summary>
    /// <exception cref="Bm52xFrameException">A imagem é inválida.</exception>
    public LoggerMemory Decode() => Bm52xMemory.Decode(_image);
}

/// <summary>
/// Leitura robusta da memória do datalogger (§4.11 da especificação): <c>88</c> e depois N × <c>89</c>, com checksum.
/// Depois de cada <c>88</c> há uma espera de <see cref="LoggerReadOptions.HeadSettleDelay"/> antes do próximo comando, e
/// uma falha do <c>88</c> é sempre recuperada com outro <c>88</c>. A recuperação com <c>8A</c> só é usada depois de um
/// <c>89</c> que o medidor certamente processou (ao menos 1 relatório chegou ou o checksum veio errado); sem nenhum
/// relatório não dá para saber se o ponteiro andou, e a leitura recomeça do <c>88</c>, conferindo os blocos já lidos.
/// </summary>
/// <remarks>
/// Uma instância faz uma leitura (<see cref="ReadAsync"/>). As falhas viram <see cref="BrymenMeterException"/>:
/// <see cref="BrymenErrorCodes.TransmissionTimeout"/> (tentativas esgotadas), <see cref="BrymenErrorCodes.InvalidFrame"/>
/// (cabeçalho inválido ou que mudou durante a leitura), <see cref="BrymenErrorCodes.NoDataLogger"/> (modelo),
/// <see cref="BrymenErrorCodes.DataLoggerNotSupported"/> (o Model ID <c>82h</c> da família BM82x, que não tem datalogger,
/// sem nenhum comando de memória) e, do transporte, <see cref="BrymenErrorCodes.CableNotFound"/>.
/// </remarks>
public sealed class LoggerMemoryReader
{
    /// <summary>Espaço de endereços da EEPROM (A0_007Ah..A3_FFFFh, §4.4): o maior TotalBytes aceito.</summary>
    public const int MaxTotalBytes = 0x20000;

    private const int ModelCheckAttempts = 2;

    private readonly ILoggerMemoryTransport _transport;
    private readonly LoggerReadOptions _options;
    private readonly IProgress<LoggerReadProgress>? _progress;
    private readonly TimeProvider _time;
    private readonly List<string> _warnings = [];
    private int _retries;
    private int _restarts;
    private int _started;

    /// <summary>Cria o leitor sobre <paramref name="transport"/>.</summary>
    /// <param name="transport">Transporte dos comandos.</param>
    /// <param name="options">Opções da leitura; padrão: <see cref="LoggerReadOptions"/> com os valores padrão.</param>
    /// <param name="progress">
    /// Recebe o progresso a cada bloco, na thread da leitura (<see cref="IProgress{T}.Report"/> é chamado de forma síncrona).
    /// </param>
    /// <param name="timeProvider">Relógio das esperas; padrão: <see cref="TimeProvider.System"/>.</param>
    public LoggerMemoryReader(ILoggerMemoryTransport transport, LoggerReadOptions? options = null, IProgress<LoggerReadProgress>? progress = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _options = (options ?? new LoggerReadOptions()).ValidatedCopy();
        _progress = progress;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Lê a memória inteira.</summary>
    /// <exception cref="BrymenMeterException">A leitura falhou (veja as observações da classe).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> foi cancelado.</exception>
    /// <exception cref="InvalidOperationException">A instância já fez uma leitura.</exception>
    public async Task<LoggerMemoryImage> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Cada LoggerMemoryReader faz uma só leitura; crie outro.");
        if (_options.VerifyModel)
            await VerifyModelAsync(cancellationToken).ConfigureAwait(false);

        var blocks = new List<byte[]>();
        LoggerHeader? header = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            var current = Bm52xMemory.ParseHeader(head);
            if (current.TotalBytes > MaxTotalBytes)
            {
                throw new BrymenMeterException(BrymenErrorCodes.InvalidFrame, string.Create(CultureInfo.InvariantCulture,
                    $"Cabeçalho da memória inválido: TotalBytes = {current.TotalBytes} (máximo {MaxTotalBytes})."));
            }

            if (header is null)
            {
                header = current;
                blocks.Add(head.Data);
            }
            else if (current != header)
            {
                throw new BrymenMeterException(BrymenErrorCodes.InvalidFrame, "O cabeçalho da memória mudou durante a leitura; repita a importação.");
            }
            else
            {
                Compare(blocks, 0, head.Data);
            }

            int total = Math.Max(header.BlocksNeeded, 1);
            Report(blocks.Count, total, header.TotalBytes);
            if (await ReadRemainingAsync(blocks, total, header.TotalBytes, cancellationToken).ConfigureAwait(false))
            {
                var image = new byte[blocks.Count * Bm52xCommands.MemoryBlockDataLength];
                for (int i = 0; i < blocks.Count; i++)
                    blocks[i].CopyTo(image, i * Bm52xCommands.MemoryBlockDataLength);
                return new LoggerMemoryImage(header, image, _retries, _restarts, _warnings);
            }

            if (++_restarts > _options.MaxRestarts)
            {
                throw new BrymenMeterException(BrymenErrorCodes.TransmissionTimeout, string.Create(CultureInfo.InvariantCulture,
                    $"{MeterMessages.TransmissionTimeout} A leitura da memória falhou depois de {_options.MaxRestarts} recomeços."));
            }
            _warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Recomeço {_restarts} a partir do 88h depois de falha no bloco {blocks.Count}."));
            await _transport.ResetAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_options.RetryDelay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // O original lê o Model ID (bytes 20–23 do tempo real) antes de importar e recusa medidores sem datalogger. O BM82x
    // (Model ID 82h) tem o código próprio, E9014: a família é conhecida e não tem memória.
    private async Task VerifyModelAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < ModelCheckAttempts; attempt++)
        {
            var result = await _transport.ExecuteAsync(Bm52xCommands.RealTime, Bm52xCommands.RealTimeReportCount, cancellationToken).ConfigureAwait(false);
            if (result.Frame is { Length: Bm52xCommands.RealTimeFrameLength } frame)
            {
                if (Bm52xRealtime.TryGetFamily(frame, out var family) && !family.HasDataLogger())
                    throw MeterConnection.NoDataLoggerFamily(family);
                if (frame.AsSpan(19, 4).ContainsAnyExcept(Bm52xCommands.Family))
                    break;
                return;
            }
            await _transport.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        throw new BrymenMeterException(BrymenErrorCodes.NoDataLogger, MeterMessages.NoDataLogger);
    }

    // false = é preciso recomeçar do 88 (ponteiro do medidor indefinido).
    private async Task<bool> ReadRemainingAsync(List<byte[]> blocks, int total, int totalBytes, CancellationToken cancellationToken)
    {
        for (int k = 1; k < total; k++)
        {
            var data = await ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (data is null)
                return false;
            if (k < blocks.Count)
                Compare(blocks, k, data);
            else
                blocks.Add(data);
            Report(Math.Max(blocks.Count, k + 1), total, totalBytes);
        }
        return true;
    }

    // Falha do 88 (timeout, checksum ou relatório curto): sempre outro 88. Um 8A logo depois do 88 não é confiável.
    private async Task<MemoryBlock> ReadHeadAsync(CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (int attempt = 1; attempt <= _options.MaxHeadAttempts; attempt++)
        {
            if (attempt > 1)
            {
                _retries++;
                await _transport.ResetAsync(cancellationToken).ConfigureAwait(false);
            }
            var (block, error, _) = await TryReadBlockAsync(Bm52xCommands.MemoryHead, cancellationToken).ConfigureAwait(false);
            // Antes de qualquer comando seguinte, inclusive o 88 de uma nova tentativa.
            if (block is not null || attempt < _options.MaxHeadAttempts)
                await Task.Delay(_options.HeadSettleDelay, _time, cancellationToken).ConfigureAwait(false);
            if (block is not null)
                return block;
            lastError = error;
        }
        throw new BrymenMeterException(BrymenErrorCodes.TransmissionTimeout, $"{MeterMessages.TransmissionTimeout} ({lastError})");
    }

    // null = recomeçar do 88.
    private async Task<byte[]?> ReadNextAsync(CancellationToken cancellationToken)
    {
        byte command = Bm52xCommands.MemoryNext;
        // O 8A (P ← P − 24; lê; P ← P + 24) é idempotente: depois que o 89 foi processado, qualquer número de 8A relê o mesmo bloco.
        bool pointerAdvanced = false;
        for (int attempt = 1; attempt <= _options.MaxBlockAttempts; attempt++)
        {
            var (block, _, processed) = await TryReadBlockAsync(command, cancellationToken).ConfigureAwait(false);
            if (block is not null)
                return block.Data;

            pointerAdvanced |= command == Bm52xCommands.MemoryNext && processed;
            // Sem nova tentativa (ponteiro indefinido ou 8A esgotados), quem recomeça do 88 faz o reset e a espera.
            if (!pointerAdvanced || attempt == _options.MaxBlockAttempts)
                return null;
            _retries++;
            await _transport.ResetAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_options.RetryDelay, _time, cancellationToken).ConfigureAwait(false);
            command = Bm52xCommands.MemoryCurrent;
        }
        return null;
    }

    /// <summary>Lê um bloco; devolve null em falha, com o motivo e se o medidor certamente processou o comando.</summary>
    private async Task<(MemoryBlock? Block, string? Error, bool Processed)> TryReadBlockAsync(byte command, CancellationToken cancellationToken)
    {
        var result = await _transport.ExecuteAsync(command, Bm52xCommands.MemoryReportCount, cancellationToken).ConfigureAwait(false);
        if (result.Frame is not { } frame)
            return (null, result.Error?.Message ?? "sem resposta", result.ReportsReceived > 0);

        MemoryBlock block;
        try
        {
            block = Bm52xMemory.ParseBlock(frame);
        }
        catch (Bm52xFrameException ex)
        {
            return (null, ex.Message, true);
        }
        if (!block.IsChecksumValid)
            return (null, string.Create(CultureInfo.InvariantCulture, $"checksum {block.ChecksumReceived:X4}h, esperado {block.ChecksumComputed:X4}h"), true);
        return (block, null, true);
    }

    private void Compare(List<byte[]> blocks, int index, byte[] data)
    {
        if (blocks[index].AsSpan().SequenceEqual(data))
            return;
        _warnings.Add(string.Create(CultureInfo.InvariantCulture, $"O bloco {index} veio diferente na releitura; usando a releitura."));
        blocks[index] = data;
    }

    private void Report(int blocksRead, int blocksTotal, int totalBytes)
        => _progress?.Report(new LoggerReadProgress(blocksRead, blocksTotal, totalBytes, _retries, _restarts));
}
