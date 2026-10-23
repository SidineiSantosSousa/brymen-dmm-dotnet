using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Acquisition;

/// <summary>
/// Faixa observada de um display (painéis Max/Min do original), em unidades SI. Vale para uma unidade e um acoplamento:
/// quando a chave de função muda (de ACV para Ω, por exemplo), a faixa recomeça.
/// </summary>
internal readonly record struct ValueRange(double Min, double Max, string Unit, MeasurementMode Mode);

/// <summary>Fotografia do estado do gravador.</summary>
internal sealed record AcquisitionStatus(
    AcquisitionState State,
    AcquisitionMode Mode,
    int IntervalSeconds,
    int SampleCount,
    int MaxSamples,
    bool BufferFull,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt,
    DateTimeOffset? FirstSampleAt,
    DateTimeOffset? LastSampleAt,
    string? BackupFile,
    ApiError? BackupError,
    ValueRange? MainRange,
    ValueRange? SecondaryRange);

/// <summary>
/// Graphical Recorder / Data List: buffer em memória limitado por <c>Acquisition:MaxSamples</c>, modos
/// <c>fullSpeed</c>, <c>interval</c> e <c>manual</c>, e backup contínuo em CSV. Cada amostra gravada também vai para
/// os assinantes de <see cref="SubscribeSamples"/> (evento <c>acquisitionSample</c> do SignalR).
/// </summary>
internal sealed class Recorder : IDisposable
{
    public const int MaxIntervalSeconds = 86_400;

    /// <summary>Capacidade do canal de cada assinante de amostras; com o leitor atrasado, a mais antiga é descartada.</summary>
    public const int SampleSubscriberCapacity = 64;

    private readonly AcquisitionOptions _options;
    private readonly string _backupDirectory;
    private readonly ReadingBroadcaster _broadcaster;
    private readonly MeterSession _session;
    private readonly TimeProvider _time;
    private readonly ILogger<Recorder> _logger;
    private readonly Lock _lock = new();
    private readonly List<AcquisitionSample> _samples = [];
    private readonly SubscriberList<AcquisitionSample> _sampleSubscribers = new();

    private AcquisitionMode _mode = AcquisitionMode.FullSpeed;
    private int _intervalSeconds = 1;
    private bool _recording;
    private bool _bufferFull;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stoppedAt;
    private long _lastSequence;
    private long _originTimestamp;
    private CsvBackupWriter? _backup;
    private string? _backupFile;
    private ApiError? _backupError;
    private CancellationTokenSource? _intervalCts;
    private ValueRange? _mainRange;
    private ValueRange? _secondaryRange;

    public Recorder(
        IOptions<AcquisitionOptions> options,
        DataDirectory dataDirectory,
        IHostEnvironment environment,
        ReadingBroadcaster broadcaster,
        MeterSession session,
        TimeProvider time,
        ILogger<Recorder> logger)
    {
        _options = options.Value;
        _backupDirectory = _options.BackupDirectory is { } dir
            ? DataDirectory.Resolve(dir, environment.ContentRootPath)
            : dataDirectory.DefaultBackupDirectory;
        _broadcaster = broadcaster;
        _session = session;
        _time = time;
        _logger = logger;
    }

    public AcquisitionStatus GetStatus()
    {
        lock (_lock)
        {
            return new AcquisitionStatus(
                _recording ? AcquisitionState.Recording : AcquisitionState.Idle,
                _mode, _intervalSeconds, _samples.Count, _options.MaxSamples, _bufferFull,
                _startedAt, _stoppedAt,
                _samples.Count > 0 ? _samples[0].Timestamp : null,
                _samples.Count > 0 ? _samples[^1].Timestamp : null,
                _backupFile, _backupError, _mainRange, _secondaryRange);
        }
    }

    /// <summary>
    /// Altera o modo e o intervalo (null mantém o valor atual, resolvido sob o lock: dois PUT parciais concorrentes não se
    /// desfazem); com a gravação em andamento, é preciso pará-la antes. Um intervalo informado é sempre validado
    /// (0 a <see cref="MaxIntervalSeconds"/>; 0 vale como Full Speed no original e só é aceito fora do modo interval).
    /// </summary>
    public ApiError? UpdateSettings(AcquisitionMode? mode, int? intervalSeconds)
    {
        if (mode is { } requested && !Enum.IsDefined(requested))
            return new ApiError(ErrorCodes.Validation, "Modo inválido: use fullSpeed, interval ou manual.");
        if (intervalSeconds is < 0 or > MaxIntervalSeconds)
            return new ApiError(ErrorCodes.InvalidValue, $"O intervalo deve ser um número inteiro de segundos entre 1 e {MaxIntervalSeconds}.");

        lock (_lock)
        {
            if (_recording)
                return new ApiError(ErrorCodes.Conflict, "Pare a aquisição antes de alterar o modo ou o intervalo.");
            var next = mode ?? _mode;
            if (next == AcquisitionMode.Interval && (intervalSeconds ?? _intervalSeconds) < 1)
                return new ApiError(ErrorCodes.InvalidValue, $"O intervalo deve ser um número inteiro de segundos entre 1 e {MaxIntervalSeconds}.");
            _mode = next;
            if (intervalSeconds is >= 1)
                _intervalSeconds = intervalSeconds.Value;
        }
        return null;
    }

    /// <summary>Inicia a gravação (as amostras se somam às do buffer). Exige o link ativo, como o original (E9002).</summary>
    public ApiError? Start()
    {
        if (!_session.GetSnapshot().LinkEnabled)
            return new ApiError(ErrorCodes.NotLinked, Messages.NotLinked);

        lock (_lock)
        {
            if (_recording)
                return null;
            if (_samples.Count >= _options.MaxSamples)
                return new ApiError(ErrorCodes.Conflict, $"O buffer está cheio ({_options.MaxSamples} amostras); exporte e limpe as amostras antes de gravar.");

            _recording = true;
            _bufferFull = false;
            _startedAt = _time.GetUtcNow();
            _stoppedAt = null;
            OpenBackup();

            if (_mode == AcquisitionMode.Interval)
            {
                _intervalCts = new CancellationTokenSource();
                _ = RunIntervalAsync(TimeSpan.FromSeconds(_intervalSeconds), _intervalCts.Token);
            }
        }
        _logger.LogInformation("Aquisição iniciada no modo {Mode}.", _mode);
        return null;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_recording)
                return;
            StopCore();
        }
        _logger.LogInformation("Aquisição parada.");
    }

    /// <summary>Apaga o buffer. Se a gravação estiver em andamento, ela continua num novo arquivo de backup.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _samples.Clear();
            _bufferFull = false;
            _mainRange = null;
            _secondaryRange = null;
            _lastSequence = 0;
            if (_recording)
                OpenBackup();
        }
    }

    /// <summary>Chamado pelo polling a cada leitura; no modo <c>fullSpeed</c> toda leitura vira amostra.</summary>
    public void OnReading(LiveReading reading)
    {
        lock (_lock)
        {
            if (_recording && _mode == AcquisitionMode.FullSpeed)
                AddCore(reading);
        }
    }

    /// <summary>Amostra manual: a leitura atual entra no buffer.</summary>
    public (AcquisitionSample? Sample, ApiError? Error) AddManual()
    {
        var latest = _broadcaster.Latest;
        var meter = _session.GetSnapshot();
        lock (_lock)
        {
            if (!_recording || _mode != AcquisitionMode.Manual)
                return (null, new ApiError(ErrorCodes.Conflict, "A amostra manual exige o modo manual com a aquisição iniciada."));
            if (meter.Importing)
                return (null, new ApiError(ErrorCodes.Conflict, Messages.ImportInProgress));
            if (latest is null || meter.State != MeterConnectionState.Connected)
                return (null, new ApiError(ErrorCodes.NoDataReceived, Messages.NoDataReceived));
            var sample = AddCore(latest);
            return sample is null
                ? (null, new ApiError(ErrorCodes.Conflict, $"O buffer está cheio ({_options.MaxSamples} amostras)."))
                : (sample, null);
        }
    }

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    /// <summary>Assina as próximas amostras gravadas, na ordem do buffer, sem bloquear a gravação.</summary>
    public Subscription<AcquisitionSample> SubscribeSamples(int capacity = SampleSubscriberCapacity)
        => _sampleSubscribers.Subscribe(capacity);

    public (IReadOnlyList<AcquisitionSample> Items, int Total) GetPage(int offset, int limit)
    {
        lock (_lock)
        {
            int total = _samples.Count;
            if (offset >= total)
                return ([], total);
            return (_samples.GetRange(offset, Math.Min(limit, total - offset)), total);
        }
    }

    /// <summary>Cópia do buffer para exportação (as amostras são imutáveis).</summary>
    public IReadOnlyList<AcquisitionSample> Snapshot()
    {
        lock (_lock)
            return [.. _samples];
    }

    // Chamado sob _lock.
    private AcquisitionSample? AddCore(LiveReading reading)
    {
        if (_samples.Count >= _options.MaxSamples)
        {
            if (!_bufferFull)
                _logger.LogWarning("Buffer de aquisição cheio ({Max} amostras); gravação parada.", _options.MaxSamples);
            _bufferFull = true;
            StopCore();
            return null;
        }

        // Relógio monotônico: o de parede pode saltar (NTP) e daria tempo negativo ou de horas no CSV.
        if (_samples.Count == 0)
            _originTimestamp = reading.MonotonicTimestamp;
        var elapsed = _time.GetElapsedTime(_originTimestamp, reading.MonotonicTimestamp);
        var sample = new AcquisitionSample(_samples.Count, elapsed, Compact(reading.Reading), reading.Comparison);
        _samples.Add(sample);
        _lastSequence = reading.Sequence;
        _mainRange = Extend(_mainRange, reading.Reading.Main);
        _secondaryRange = Extend(_secondaryRange, reading.Reading.Secondary);

        if (_backup is not null)
        {
            try
            {
                _backup.Append(sample);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Falha ao gravar o backup {Path}.", _backup.FilePath);
                _backupError = new ApiError(ErrorCodes.BackupFile, $"{Messages.BackupFile} {ex.Message}");
                _backup.Dispose();
                _backup = null;
            }
        }

        // Sob _lock: os assinantes recebem as amostras na ordem do buffer.
        _sampleSubscribers.Publish(sample);

        if (_samples.Count >= _options.MaxSamples)
        {
            _bufferFull = true;
            StopCore();
        }
        return sample;
    }

    // Com 1 milhão de amostras o buffer precisa caber num Raspberry Pi: o quadro bruto e os diagnósticos não entram
    // nas amostras, e displays iguais aos da amostra anterior (o caso comum) reaproveitam os mesmos objetos.
    // Chamado sob _lock.
    private DmmReading Compact(DmmReading reading)
    {
        var previous = _samples.Count > 0 ? _samples[^1].Reading : null;
        return reading with
        {
            Main = Reuse(reading.Main, previous?.Main),
            Secondary = Reuse(reading.Secondary, previous?.Secondary),
            RawHex = "",
            Diagnostics = null,
        };
    }

    private static DisplayReading? Reuse(DisplayReading? current, DisplayReading? previous)
        => current is not null && current.Equals(previous) ? previous : current;

    // Uma leitura de outra unidade ou outro acoplamento recomeça a faixa: misturar V e Ω (ou AC e DC) daria um Max/Min
    // sem sentido. Overload e displays sem valor não mudam a faixa.
    private static ValueRange? Extend(ValueRange? range, DisplayReading? display)
    {
        if (display?.SiValue is not { } v || !double.IsFinite(v))
            return range;
        if (range is { } r && r.Unit == display.Unit && r.Mode == display.Mode)
            return r with { Min = Math.Min(r.Min, v), Max = Math.Max(r.Max, v) };
        return new ValueRange(v, v, display.Unit, display.Mode);
    }

    // Chamado sob _lock.
    private void StopCore()
    {
        _recording = false;
        _stoppedAt = _time.GetUtcNow();
        _intervalCts?.Cancel();
        _intervalCts?.Dispose();
        _intervalCts = null;
        _backup?.Dispose();
        _backup = null;
    }

    // Chamado sob _lock.
    private void OpenBackup()
    {
        _backup?.Dispose();
        _backup = null;
        _backupError = null;
        if (!_options.BackupEnabled)
            return;
        try
        {
            _backup = CsvBackupWriter.Create(_backupDirectory, _options.BackupRetention, _logger);
            _backupFile = _backup.FilePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Não foi possível criar o backup em {Directory}.", _backupDirectory);
            _backupError = new ApiError(ErrorCodes.BackupFile, $"{Messages.BackupFile} {ex.Message}");
        }
    }

    // O original só amostra com "dado válido": a mesma leitura não entra duas vezes nem com o cabo em falha.
    private async Task RunIntervalAsync(TimeSpan period, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(period, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var latest = _broadcaster.Latest;
                var meter = _session.GetSnapshot();
                if (latest is null || meter.State != MeterConnectionState.Connected || meter.Importing)
                    continue;
                lock (_lock)
                {
                    if (_recording && _mode == AcquisitionMode.Interval && latest.Sequence != _lastSequence)
                        AddCore(latest);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_recording)
                StopCore();
        }
        _sampleSubscribers.Complete();
    }
}
