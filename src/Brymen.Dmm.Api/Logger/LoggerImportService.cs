using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Logger;

/// <summary>
/// Importação do datalogger em segundo plano: pausa o polling com o acesso exclusivo da <see cref="MeterSession"/> e lê
/// a memória com o <see cref="LoggerMemoryReader"/> da biblioteca Brymen.Dmm (<see cref="MeterExclusiveAccess.ReadLoggerImageAsync"/>),
/// que confere o modelo (E9017) antes do <c>88</c>; guarda a imagem bruta em <c>&lt;DataDirectory&gt;/imports/{id}.bin</c>
/// e decodifica com <see cref="Bm52xMemory"/>. As imagens já gravadas são recarregadas na construção, que o
/// <c>Program</c> faz na partida. Só as <c>Logger:ImportRetention</c> importações terminadas mais novas ficam guardadas.
/// </summary>
internal sealed partial class LoggerImportService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private const string TempSuffix = ".tmp";

    private readonly MeterSession _session;
    private readonly int _retention;
    private readonly string _importsDirectory;
    private readonly TimeProvider _time;
    private readonly ILogger<LoggerImportService> _logger;
    private readonly ConcurrentDictionary<string, ImportJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public LoggerImportService(
        MeterSession session, DataDirectory dataDirectory, IOptions<LoggerOptions> options, TimeProvider time, ILogger<LoggerImportService> logger)
    {
        _session = session;
        _retention = options.Value.ImportRetention;
        _importsDirectory = dataDirectory.ImportsDirectory;
        _time = time;
        _logger = logger;
        LoadExisting();
    }

    public static bool IsValidId(string id) => JobId().IsMatch(id);

    /// <summary>Inicia uma importação; falha na hora com E9015 sem cabo ou com conflito se outra estiver em andamento.</summary>
    public async Task<(ImportJob? Job, ApiError? Error)> StartAsync(CancellationToken cancellationToken)
    {
        MeterExclusiveAccess access;
        try
        {
            access = await _session.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            return (null, ex.ToError());
        }
        catch (BrymenMeterException ex)
        {
            return (null, ex.ToApiError());
        }

        var job = new ImportJob(Guid.NewGuid().ToString("N"), _time.GetUtcNow());
        _jobs[job.Id] = job;
        job.Completion = Task.Run(() => RunAsync(job, access));
        _logger.LogInformation("Importação {Id} iniciada.", job.Id);
        return (job, null);
    }

    public ImportJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IReadOnlyList<ImportJob> List() => [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];

    /// <summary>Cancela um job em andamento, ou apaga um job terminado e a sua imagem.</summary>
    public bool Delete(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return false;
        if (job.IsRunning)
        {
            job.Cancellation.Cancel();
            return true;
        }
        Remove(job);
        return true;
    }

    private void Remove(ImportJob job)
    {
        if (!_jobs.TryRemove(new KeyValuePair<string, ImportJob>(job.Id, job)))
            return;
        if (job.GetSnapshot().ImagePath is { } path)
            TryDelete(path);
    }

    // No fim de cada importação, antes de o estado final ficar visível: as terminadas mais antigas além de
    // Logger:ImportRetention saem da memória e do disco. A que está terminando conta como terminada e sempre fica, mesmo
    // que o relógio tenha voltado e alguma imagem recarregada pareça mais nova que ela.
    private void ApplyRetention(ImportJob finishing)
    {
        var expired = _jobs.Values.Where(j => j != finishing && !j.IsRunning)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id, StringComparer.Ordinal)
            .Skip(_retention - 1).ToList();
        foreach (var job in expired)
        {
            Remove(job);
            _logger.LogInformation("Importação {Id} apagada (Logger:ImportRetention = {Retention}).", job.Id, _retention);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Não foi possível apagar {Path}.", path);
        }
    }

    // A retenção vem antes do estado final (Complete, Fail ou MarkCanceled), e não depois: quem vê o job terminado e lista
    // as importações logo em seguida já encontra no máximo Logger:ImportRetention terminadas. O estado final é publicado
    // mesmo se a retenção falhar, para o job não ficar "running" para sempre.
    private void Finish(ImportJob job, Action publish)
    {
        try
        {
            ApplyRetention(job);
        }
        finally
        {
            publish();
        }
    }

    private async Task RunAsync(ImportJob job, MeterExclusiveAccess access)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, job.Cancellation.Token);
        var cancellationToken = cts.Token;
        string? imagePath = null;
        try
        {
            await using (access.ConfigureAwait(false))
            {
                var result = await access.ReadLoggerImageAsync(new JobProgress(job), cancellationToken).ConfigureAwait(false);

                // A imagem é gravada antes da decodificação, para não se perder se esta falhar.
                string savedPath = await SaveImageAsync(job.Id, result.Image, cancellationToken).ConfigureAwait(false);
                imagePath = savedPath;
                job.SetImagePath(savedPath);
                var memory = result.Decode();
                var progress = new LoggerReadProgress(result.BlockCount,
                    Math.Max(result.Header.BlocksNeeded, 1), result.Header.TotalBytes, result.Retries, result.Restarts);
                Finish(job, () => job.Complete(memory, savedPath, progress, result.Warnings, _time.GetUtcNow()));
                _logger.LogInformation("Importação {Id} concluída: {Sessions} sessão(ões), {Retries} retentativa(s), {Restarts} recomeço(s).",
                    job.Id, memory.Sessions.Count, result.Retries, result.Restarts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(job, () => job.MarkCanceled(_time.GetUtcNow()));
            _logger.LogInformation("Importação {Id} cancelada.", job.Id);
        }
        catch (BrymenMeterException ex)
        {
            Finish(job, () => job.Fail(ex.ToApiError(), _time.GetUtcNow(), imagePath));
            _logger.LogWarning("Importação {Id} falhou: [{Code}] {Message}", job.Id, ex.Code, ex.Message);
        }
        catch (Bm52xFrameException ex)
        {
            Finish(job, () => job.Fail(new ApiError(ErrorCodes.InvalidFrame, ex.Message), _time.GetUtcNow(), imagePath));
            _logger.LogWarning("Importação {Id} falhou: {Message}", job.Id, ex.Message);
        }
        catch (Exception ex)
        {
            Finish(job, () => job.Fail(new ApiError(ErrorCodes.Internal, $"Falha inesperada na importação: {ex.Message}"), _time.GetUtcNow(), imagePath));
            _logger.LogError(ex, "Importação {Id} falhou.", job.Id);
        }
    }

    // Num temporário, com o conteúdo forçado para o disco, e depois trocado: uma falha no meio (disco cheio, cancelamento
    // pelo DELETE ou pelo desligamento) ou uma queda de energia não deixa um .bin parcial, que o DELETE não apagaria e a
    // partida recarregaria como uma importação concluída. É o mesmo esquema do settings.json.
    private async Task<string> SaveImageAsync(string id, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_importsDirectory);
        string path = Path.Combine(_importsDirectory, id + ".bin");
        string temp = path + TempSuffix;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(image, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return path;
    }

    // Só as Logger:ImportRetention imagens mais novas são lidas e decodificadas; as mais antigas e os temporários
    // deixados por uma queda de energia são apagados.
    private void LoadExisting()
    {
        if (!Directory.Exists(_importsDirectory))
            return;
        foreach (string temp in Directory.EnumerateFiles(_importsDirectory, "*.bin" + TempSuffix))
            TryDelete(temp);

        var images = Directory.EnumerateFiles(_importsDirectory, "*.bin")
            .Where(path => Path.GetExtension(path) == ".bin" && IsValidId(Path.GetFileNameWithoutExtension(path)))
            .Select(path => (Path: path, WrittenAt: File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(image => image.WrittenAt).ThenByDescending(image => image.Path, StringComparer.Ordinal)
            .ToList();
        foreach (var (path, _) in images.Skip(_retention))
        {
            TryDelete(path);
            _logger.LogInformation("Imagem {File} apagada (Logger:ImportRetention = {Retention}).", Path.GetFileName(path), _retention);
        }

        foreach (var (path, writtenAt) in images.Take(_retention))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            var job = new ImportJob(id, new DateTimeOffset(writtenAt, TimeSpan.Zero));
            job.SetImagePath(path);
            try
            {
                byte[] image = File.ReadAllBytes(path);
                var memory = Bm52xMemory.Decode(image);
                int blocks = image.Length / Bm52xCommands.MemoryBlockDataLength;
                // Só o nome do arquivo: o caminho absoluto do servidor não vai para a API.
                job.Complete(memory, path, new LoggerReadProgress(blocks, blocks, memory.Header.TotalBytes, 0, 0),
                    ["Recarregada de imports/" + Path.GetFileName(path)], job.CreatedAt);
            }
            catch (Exception ex)
            {
                job.Fail(new ApiError(ErrorCodes.InvalidFrame, $"Imagem gravada ilegível: {ex.Message}"), job.CreatedAt, path);
                _logger.LogWarning(ex, "Imagem de importação ilegível: {Path}.", path);
            }
            _jobs[id] = job;
        }
    }

    /// <summary>Cancela a importação em andamento e espera até 5 s que ela libere o cabo.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var running = _jobs.Values.Select(j => j.Completion).Where(t => !t.IsCompleted).ToArray();
        await Task.WhenAll(running).WaitAsync(ShutdownTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        _shutdown.Cancel();
        var running = _jobs.Values.Select(j => j.Completion).Where(t => !t.IsCompleted).ToArray();
        try
        {
            Task.WaitAll(running, ShutdownTimeout);
        }
        catch (AggregateException)
        {
        }
    }

    // \z e não $: o $ aceitaria um "\n" no fim (ex.: ".../imports/{id}%0A").
    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JobId();

    /// <summary>Progresso síncrono no job (o <see cref="Progress{T}"/> o entregaria depois, fora de ordem).</summary>
    private sealed class JobProgress(ImportJob job) : IProgress<LoggerReadProgress>
    {
        public void Report(LoggerReadProgress value) => job.ReportProgress(value);
    }
}
