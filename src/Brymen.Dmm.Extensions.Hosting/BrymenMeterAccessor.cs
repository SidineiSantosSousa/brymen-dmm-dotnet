using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Brymen.Dmm.Extensions.Hosting;

/// <summary>
/// Implementação de <see cref="IBrymenMeterAccessor"/>: guarda o medidor do <see cref="BrymenMeterHostedService"/>,
/// expõe só as operações dele (<see cref="MeterHandle"/>), repassa os eventos e as leituras e anuncia as transições do
/// próprio serviço (abertura, falha da abertura, desistência e parada).
/// </summary>
internal sealed partial class BrymenMeterAccessor : IBrymenMeterAccessor, IDisposable
{
    /// <summary>Leituras guardadas por enumeração de <see cref="ReadingsAsync"/> enquanto o consumidor não as lê.</summary>
    internal const int ReadingsCapacity = 64;

    private const string StoppedMessage = "O serviço do multímetro parou (ou ainda não partiu de novo): o medidor só volta numa nova partida do host.";

    private readonly Func<BrymenMeterOptions> _options;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();

    // Protegidos por _sync.
    private BrymenMeter? _meter;
    private MeterHandle? _handle;
    private MeterState _state = MeterState.Closed;
    private DmmReading? _lastReading;
    private BrymenMeterError? _lastError;
    private TaskCompletionSource<IBrymenMeter> _available = NewAvailability();
    // Por que o medidor não vai aparecer (serviço parado, abertura abandonada, partida que falhou, contêiner descartado);
    // null enquanto ele pode aparecer.
    private Exception? _unavailable;
    // As enumerações de ReadingsAsync: copiado a cada mudança, para o laço de leitura percorrê-lo fora do lock.
    private ChannelWriter<DmmReading>[] _subscribers = [];
    // Com Ended, as enumerações novas terminam na hora (com Error, se houver).
    private (bool Ended, Exception? Error) _readingsEnd;
    private bool _disposed;

    /// <param name="options">As opções do medidor (as do nome do registro), lidas só quando preciso.</param>
    /// <param name="logger">Log dos handlers de <see cref="ErrorOccurred"/> que lançam.</param>
    public BrymenMeterAccessor(Func<BrymenMeterOptions> options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<DmmReading>? ReadingReceived;

    public event EventHandler<MeterStateChangedEventArgs>? StateChanged;

    public event EventHandler<BrymenMeterError>? ErrorOccurred;

    public IBrymenMeter? Meter
    {
        get { lock (_sync) return _handle; }
    }

    /// <summary>O medidor que o serviço abriu, ou <see langword="null"/> (para o serviço e os testes).</summary>
    internal BrymenMeter? CurrentMeter
    {
        get { lock (_sync) return _meter; }
    }

    public MeterState State
    {
        get
        {
            BrymenMeter? meter;
            lock (_sync)
            {
                meter = _meter;
                if (meter is null)
                    return _state;
            }
            // O estado do medidor é sempre o atual. Quem usa o acessor não tem como descartá-lo: Disposed só aparece numa
            // corrida com a parada do serviço, que é o Closed.
            var state = meter.State;
            return state == MeterState.Disposed ? MeterState.Closed : state;
        }
    }

    public DmmReading? LastReading
    {
        get { lock (_sync) return _lastReading; }
    }

    public BrymenMeterError? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    public Task<IBrymenMeter> WaitForMeterAsync(CancellationToken cancellationToken = default)
    {
        Task<IBrymenMeter> available;
        lock (_sync)
        {
            if (_unavailable is { } reason)
                return Task.FromException<IBrymenMeter>(reason);
            available = _available.Task;
        }
        return available.IsCompleted ? available : available.WaitAsync(cancellationToken);
    }

    public async IAsyncEnumerable<DmmReading> ReadingsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<DmmReading>(new BoundedChannelOptions(ReadingsCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        Subscribe(channel.Writer);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var reading))
                    yield return reading;
            }
        }
        finally
        {
            Unsubscribe(channel.Writer);
        }
    }

    /// <summary>Uma nova partida do serviço: o medidor volta a poder aparecer.</summary>
    internal void OnStarting()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _unavailable = null;
            _readingsEnd = default;
            if (_available.Task.IsCompleted)
                _available = NewAvailability();
        }
    }

    /// <summary>O serviço vai tentar abrir o cabo.</summary>
    internal void OnConnecting()
    {
        MeterStateChangedEventArgs? change;
        lock (_sync)
            change = _meter is null ? SetOwnState(MeterState.Connecting) : null;
        RaiseStateChanged(change);
    }

    /// <summary>A abertura falhou (cabo ausente ou sem acesso); o serviço tenta de novo, ou não, conforme as opções.</summary>
    internal void OnOpenFailed(BrymenMeterError error)
    {
        MeterStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_meter is not null)
                return;
            _lastError = error;
            change = SetOwnState(MeterState.Faulted);
        }
        RaiseStateChanged(change);
        Invoke(ErrorOccurred, error, reportFailures: false);
    }

    /// <summary>
    /// O serviço desistiu de abrir o medidor (sem reconexão automática): quem espera em <see cref="WaitForMeterAsync"/>
    /// e as enumerações de <see cref="ReadingsAsync"/> recebem <paramref name="error"/>.
    /// </summary>
    internal void OnGaveUp(Exception error)
    {
        TaskCompletionSource<IBrymenMeter> pending;
        ChannelWriter<DmmReading>[] subscribers;
        lock (_sync)
        {
            if (_disposed || _meter is not null)
                return;
            _unavailable = error;
            pending = ReplaceAvailability();
            subscribers = EndReadings(error);
        }
        Fail(pending, error);
        Complete(subscribers, error);
    }

    /// <summary>
    /// O medidor foi aberto: passa a ser <see cref="Meter"/> (pela fachada), com os eventos repassados. Chamado antes de
    /// <see cref="BrymenMeter.Start"/>, para não perder a 1ª leitura.
    /// </summary>
    internal void Attach(BrymenMeter meter)
    {
        meter.ReadingReceived += OnMeterReading;
        meter.StateChanged += OnMeterStateChanged;
        meter.ErrorOccurred += OnMeterError;

        // Lido fora do lock, como em Detach: nada de fora (nem o lock do medidor) é chamado com este lock.
        var current = meter.State;
        var handle = new MeterHandle(meter);
        MeterStateChangedEventArgs? change;
        TaskCompletionSource<IBrymenMeter> available;
        lock (_sync)
        {
            _meter = meter;
            _handle = handle;
            change = _state == current ? null : new MeterStateChangedEventArgs(_state, current);
            _state = current;
            _unavailable = null;
            if (_available.Task.IsCompleted)
                _available = NewAvailability();
            available = _available;
        }
        available.TrySetResult(handle);
        RaiseStateChanged(change);
    }

    /// <summary>
    /// O serviço parou, ou a partida falhou: <see cref="Meter"/> volta a <see langword="null"/>, os eventos do medidor
    /// deixam de ser repassados (o descarte, logo em seguida, não aparece aqui), as enumerações de
    /// <see cref="ReadingsAsync"/> terminam, e quem espera em <see cref="WaitForMeterAsync"/> recebe
    /// <paramref name="reason"/> (padrão: <see cref="InvalidOperationException"/>, serviço parado). Também chamado sem
    /// medidor, se a parada veio antes da abertura.
    /// </summary>
    internal void Detach(BrymenMeter? meter, Exception? reason = null)
    {
        if (meter is not null)
        {
            meter.ReadingReceived -= OnMeterReading;
            meter.StateChanged -= OnMeterStateChanged;
            meter.ErrorOccurred -= OnMeterError;
        }

        // Lido fora do lock: nada de fora (nem o lock do medidor) é chamado com o lock deste acessor.
        MeterState? meterState = meter?.State;
        MeterState previous;
        TaskCompletionSource<IBrymenMeter> pending;
        ChannelWriter<DmmReading>[] subscribers;
        Exception unavailable;
        lock (_sync)
        {
            if (meter is not null && !ReferenceEquals(_meter, meter))
                return;
            previous = meterState ?? _state;
            _meter = null;
            _handle = null;
            _state = MeterState.Closed;
            if (!_disposed)
                _unavailable = reason ?? new InvalidOperationException(StoppedMessage);
            unavailable = _unavailable!;
            pending = ReplaceAvailability();
            subscribers = EndReadings(null);
        }
        Fail(pending, unavailable);
        Complete(subscribers, null);
        if (previous is not MeterState.Closed and not MeterState.Disposed)
            RaiseStateChanged(new MeterStateChangedEventArgs(previous, MeterState.Closed));
    }

    /// <summary>Uma falha do próprio serviço (um defeito), anunciada como as do medidor.</summary>
    internal void OnServiceFailure(BrymenMeterError error)
    {
        lock (_sync)
            _lastError = error;
        Invoke(ErrorOccurred, error, reportFailures: false);
    }

    /// <summary>
    /// Descarte do contêiner: quem espera em <see cref="WaitForMeterAsync"/> recebe <see cref="ObjectDisposedException"/>,
    /// e as enumerações de <see cref="ReadingsAsync"/> terminam.
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource<IBrymenMeter> pending;
        ChannelWriter<DmmReading>[] subscribers;
        Exception disposed = new ObjectDisposedException(nameof(IBrymenMeterAccessor));
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _unavailable = disposed;
            pending = _available;
            subscribers = EndReadings(null);
        }
        Fail(pending, disposed);
        Complete(subscribers, null);
    }

    private DateTimeOffset Now() => (_options().TimeProvider ?? TimeProvider.System).GetUtcNow();

    private static TaskCompletionSource<IBrymenMeter> NewAvailability() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Chamado sob _sync: devolve o TaskCompletionSource atual, para quem chama concluí-lo fora do lock, e põe um novo no
    // lugar, para a próxima partida.
    private TaskCompletionSource<IBrymenMeter> ReplaceAvailability()
    {
        var current = _available;
        if (!_disposed)
            _available = NewAvailability();
        return current;
    }

    // Uma espera que ainda não terminou recebe a falha; marcada como observada, ela não vira UnobservedTaskException
    // quando ninguém está esperando.
    private static void Fail(TaskCompletionSource<IBrymenMeter> source, Exception error)
    {
        if (source.TrySetException(error))
            _ = source.Task.Exception;
    }

    // Chamado sob _sync: as enumerações atuais terminam, e as novas também, até a próxima partida.
    private ChannelWriter<DmmReading>[] EndReadings(Exception? error)
    {
        var subscribers = _subscribers;
        _subscribers = [];
        _readingsEnd = (true, error);
        return subscribers;
    }

    private static void Complete(ChannelWriter<DmmReading>[] subscribers, Exception? error)
    {
        foreach (var writer in subscribers)
            writer.TryComplete(error);
    }

    private void Subscribe(ChannelWriter<DmmReading> writer)
    {
        (bool Ended, Exception? Error) end;
        lock (_sync)
        {
            end = _readingsEnd;
            if (!end.Ended)
            {
                _subscribers = [.. _subscribers, writer];
                return;
            }
        }
        writer.TryComplete(end.Error);
    }

    private void Unsubscribe(ChannelWriter<DmmReading> writer)
    {
        lock (_sync)
            _subscribers = Array.FindAll(_subscribers, w => !ReferenceEquals(w, writer));
    }

    // Chamado sob _sync, sem medidor.
    private MeterStateChangedEventArgs? SetOwnState(MeterState state)
    {
        if (_state == state)
            return null;
        var change = new MeterStateChangedEventArgs(_state, state);
        _state = state;
        return change;
    }

    private bool IsCurrent(object? sender)
    {
        lock (_sync)
            return sender is not null && ReferenceEquals(sender, _meter);
    }

    private void OnMeterReading(object? sender, DmmReading reading)
    {
        ChannelWriter<DmmReading>[] subscribers;
        lock (_sync)
        {
            if (sender is null || !ReferenceEquals(sender, _meter))
                return;
            _lastReading = reading;
            subscribers = _subscribers;
        }
        // Canal limitado com DropOldest: a escrita nunca espera, e um consumidor atrasado não atrasa o laço.
        foreach (var writer in subscribers)
            writer.TryWrite(reading);
        Invoke(ReadingReceived, reading, reportFailures: true);
    }

    private void OnMeterStateChanged(object? sender, MeterStateChangedEventArgs e)
    {
        // O descarte é do serviço, que anuncia Closed no lugar de Disposed.
        if (e.Current != MeterState.Disposed && IsCurrent(sender))
            RaiseStateChanged(e);
    }

    private void OnMeterError(object? sender, BrymenMeterError error)
    {
        lock (_sync)
        {
            if (sender is null || !ReferenceEquals(sender, _meter))
                return;
            if (error.Code != BrymenErrorCodes.EventHandlerFailed)
                _lastError = error;
        }
        Invoke(ErrorOccurred, error, reportFailures: false);
    }

    private void RaiseStateChanged(MeterStateChangedEventArgs? change)
    {
        if (change is not null)
            Invoke(StateChanged, change, reportFailures: true);
    }

    // Cada handler é chamado à parte: um que lança não impede os outros nem chega ao laço de leitura do medidor.
    private void Invoke<T>(EventHandler<T>? handlers, T args, bool reportFailures)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)handler)(this, args);
            }
            catch (Exception ex)
            {
                if (reportFailures)
                {
                    var error = new BrymenMeterError(BrymenErrorCodes.EventHandlerFailed,
                        $"Um handler de evento do IBrymenMeterAccessor lançou {ex.GetType().Name}: {ex.Message}", ex, Now());
                    Invoke(ErrorOccurred, error, reportFailures: false);
                }
                else
                {
                    // Exceções dos handlers de ErrorOccurred: anunciá-las lá de novo poderia não ter fim.
                    LogErrorHandlerFailed(_logger, ex);
                }
            }
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Um handler de IBrymenMeterAccessor.ErrorOccurred lançou uma exceção (ignorada).")]
    private static partial void LogErrorHandlerFailed(ILogger logger, Exception exception);

    /// <summary>
    /// A fachada de <see cref="Meter"/>: só as operações do medidor, sem o descarte, o laço de leitura nem os eventos (que
    /// o acessor repassa). Quem a recebe não tem como desligar o serviço por engano, nem com um <c>await using</c>.
    /// </summary>
    private sealed class MeterHandle(BrymenMeter meter) : IBrymenMeter
    {
        public HidDeviceInfo Device => meter.Device;

        public MeterState State => meter.State;

        public BrymenMeterError? LastError => meter.LastError;

        public Task<DmmReading> ReadAsync(CancellationToken cancellationToken = default) => meter.ReadAsync(cancellationToken);

        public Task<LoggerMemory> ReadLoggerMemoryAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
            => meter.ReadLoggerMemoryAsync(progress, cancellationToken);

        public Task<LoggerMemoryImage> ReadLoggerImageAsync(IProgress<LoggerReadProgress>? progress = null, CancellationToken cancellationToken = default)
            => meter.ReadLoggerImageAsync(progress, cancellationToken);

        public override string ToString() => $"IBrymenMeter ({meter.Device.Path})";
    }
}
