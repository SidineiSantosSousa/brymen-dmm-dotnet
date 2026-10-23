using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Brymen.Dmm.Extensions.Hosting;

/// <summary>
/// Serviço hospedado de <see cref="BrymenMeterServiceCollectionExtensions.AddBrymenMeter(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{BrymenMeterOptions}?)"/>:
/// abre o medidor na partida (tentando de novo enquanto o cabo não aparece, com
/// <see cref="BrymenMeterOptions.AutoReconnect"/>), inicia a leitura contínua e descarta o medidor na parada. O
/// <see cref="BrymenMeterAccessor"/> expõe as operações do medidor e repassa os eventos; este serviço os registra no log.
/// </summary>
/// <remarks>
/// Com o ciclo de vida do host (<see cref="IHostedLifecycleService"/>), a 1ª abertura acontece no
/// <see cref="StartingAsync"/>, antes do <c>StartAsync</c> de todos os serviços, e o laço de leitura (ou as novas
/// tentativas de abertura) começa no <see cref="StartedAsync"/>, depois de todos: um serviço registrado antes ou depois
/// deste pode esperar o medidor e assinar os eventos no próprio <c>StartAsync</c>. Sem o ciclo de vida (uma chamada
/// direta ao <see cref="StartAsync"/>), as duas coisas acontecem no <see cref="StartAsync"/>.
/// </remarks>
internal sealed partial class BrymenMeterHostedService : IHostedLifecycleService, IAsyncDisposable, IDisposable
{
    // Espera mínima entre tentativas de abertura, com ReconnectDelay zero (a mesma do BrymenMeter ao reabrir o cabo).
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly BrymenMeterAccessor _accessor;
    private readonly Func<BrymenMeterOptions> _options;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();

    // Protegidos por _sync.
    private CancellationTokenSource? _stopping;
    private BrymenMeterOptions? _runOptions;
    // A partida veio pelo StartingAsync: o laço espera o StartedAsync.
    private bool _deferRun;
    private bool _startCalled;
    private bool _running;
    // A falha da 1ª abertura, entregue a quem espera o medidor se o serviço desistir (sem AutoReconnect).
    private BrymenMeterException? _openFailure;
    private Task? _reconnect;
    private BrymenMeter? _meter;
    private string? _lastLoggedCode;
    private bool _disposed;

    /// <param name="accessor">O acessor deste medidor.</param>
    /// <param name="options">As opções (as do nome do registro), lidas na partida.</param>
    /// <param name="logger">Log da abertura, das falhas e da parada.</param>
    public BrymenMeterHostedService(BrymenMeterAccessor accessor, Func<BrymenMeterOptions> options, ILogger? logger = null)
    {
        _accessor = accessor;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Inscreve o log nos eventos do acessor e faz a 1ª tentativa de abertura: uma opção inválida
    /// (<see cref="ArgumentException"/>) ou um sistema sem transporte HID (<see cref="PlatformNotSupportedException"/>)
    /// impedem a partida do host. Sem o cabo (<see cref="BrymenErrorCodes.CableNotFound"/>), a partida segue, e as
    /// novas tentativas começam no <see cref="StartedAsync"/>.
    /// </summary>
    public Task StartingAsync(CancellationToken cancellationToken) => BeginAsync(deferRun: true, cancellationToken);

    /// <summary>
    /// Depois do <see cref="StartingAsync"/>, nada a fazer (o laço espera o <see cref="StartedAsync"/>). Sem ele, faz o
    /// mesmo que ele e já inicia a leitura contínua (ou as novas tentativas de abertura).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_stopping is not null && _deferRun && !_startCalled)
            {
                _startCalled = true;
                return;
            }
        }
        await BeginAsync(deferRun: false, cancellationToken).ConfigureAwait(false);
        lock (_sync)
            _startCalled = true;
        Run();
    }

    /// <summary>Todos os serviços partiram: inicia a leitura contínua, ou as novas tentativas de abertura.</summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        bool deferred;
        lock (_sync)
            deferred = _deferRun && _stopping is not null;
        if (deferred)
            Run();
        return Task.CompletedTask;
    }

    /// <summary>Nada a fazer: a parada acontece no <see cref="StopAsync"/>.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Para as tentativas de abertura e descarta o medidor (o que interrompe a consulta em andamento).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? stopping;
        Task? reconnect;
        lock (_sync)
        {
            stopping = _stopping;
            reconnect = _reconnect;
        }
        if (stopping is null)
            return;

        await stopping.CancelAsync().ConfigureAwait(false);
        if (reconnect is not null)
            await reconnect.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        BrymenMeter? meter;
        lock (_sync)
        {
            meter = _meter;
            _meter = null;
            _reconnect = null;
            _stopping = null;
            _runOptions = null;
            _openFailure = null;
            _deferRun = false;
            _startCalled = false;
            _running = false;
        }
        _accessor.Detach(meter);
        if (meter is not null)
        {
            // O Dispose (síncrono, dentro do DisposeAsync) fecha o cabo na hora; o token só limita a espera pelo laço.
            await meter.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            LogStopped(_logger);
        }
        _accessor.StateChanged -= OnStateChanged;
        _accessor.ErrorOccurred -= OnError;
        stopping.Dispose();
    }

    /// <summary>Nada a fazer: a parada acontece no <see cref="StopAsync"/>.</summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Descarte do contêiner sem <see cref="StopAsync"/>: fecha o medidor, se aberto, e espera o laço de leitura.</summary>
    public async ValueTask DisposeAsync()
    {
        var meter = BeginDispose();
        if (meter is not null)
            await meter.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Como <see cref="DisposeAsync"/>, sem esperar o laço de leitura (que termina logo depois, sem eventos).</summary>
    public void Dispose() => BeginDispose()?.Dispose();

    // A 1ª fase da partida: lê as opções, inscreve o log e faz a 1ª tentativa de abertura, sem iniciar o laço.
    private async Task BeginAsync(bool deferRun, CancellationToken cancellationToken)
    {
        var stopping = new CancellationTokenSource();
        lock (_sync)
        {
            if (_disposed || _stopping is not null)
            {
                stopping.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
                throw new InvalidOperationException("O serviço do multímetro já foi iniciado.");
            }
            _stopping = stopping;
            _deferRun = deferRun;
            _startCalled = false;
            _running = false;
            _openFailure = null;
        }

        try
        {
            // Lidas aqui: um valor da configuração que não converte também impede a partida.
            var options = _options();
            lock (_sync)
                _runOptions = options;
            _accessor.OnStarting();
            _accessor.StateChanged += OnStateChanged;
            _accessor.ErrorOccurred += OnError;
            await TryOpenAsync(options, firstAttempt: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _accessor.StateChanged -= OnStateChanged;
            _accessor.ErrorOccurred -= OnError;
            // Quem já espera o medidor recebe a falha da partida.
            _accessor.Detach(null, ex);
            lock (_sync)
            {
                _stopping = null;
                _runOptions = null;
                _deferRun = false;
            }
            stopping.Dispose();
            throw;
        }
    }

    // A 2ª fase da partida: o laço do medidor aberto, as novas tentativas de abertura ou, sem AutoReconnect e sem o
    // medidor, a desistência.
    private void Run()
    {
        BrymenMeter? meter;
        BrymenMeterOptions options;
        BrymenMeterException? failure;
        lock (_sync)
        {
            var stopping = _stopping;
            if (_running || stopping is null || stopping.IsCancellationRequested || _runOptions is null)
                return;
            _running = true;
            meter = _meter;
            options = _runOptions;
            failure = _openFailure;
            if (meter is null && options.AutoReconnect)
                _reconnect = Task.Run(() => ReconnectAsync(options, stopping.Token), CancellationToken.None);
        }

        if (meter is not null)
        {
            StartLoop(meter);
        }
        else if (!options.AutoReconnect)
        {
            LogNoAutoReconnect(_logger);
            _accessor.OnGaveUp(failure ?? new BrymenMeterException(BrymenErrorCodes.CableNotFound, "O multímetro não foi aberto."));
        }
    }

    // Interrompe as tentativas de abertura e tira o medidor do acessor; quem chama o descarta.
    private BrymenMeter? BeginDispose()
    {
        CancellationTokenSource? stopping;
        BrymenMeter? meter;
        lock (_sync)
        {
            if (_disposed)
                return null;
            _disposed = true;
            stopping = _stopping;
            meter = _meter;
            _meter = null;
        }
        if (stopping is not null)
        {
            try
            {
                stopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // StopAsync terminando noutra thread.
            }
        }
        _accessor.Detach(meter, new ObjectDisposedException(nameof(IBrymenMeterAccessor)));
        return meter;
    }

    private async Task ReconnectAsync(BrymenMeterOptions options, CancellationToken stoppingToken)
    {
        var delay = options.ReconnectDelay > MinRetryDelay ? options.ReconnectDelay : MinRetryDelay;
        try
        {
            do
            {
                await Task.Delay(delay, options.TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            while (!await TryOpenAsync(options, firstAttempt: false, stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada do host.
        }
    }

    // Uma tentativa de abertura. Devolve false se o cabo não foi encontrado (E9015), já anunciado pelo acessor. Na 1ª
    // tentativa, as demais exceções sobem (configuração inválida) e o laço fica para o Run; nas seguintes, viram uma
    // falha INTERNAL e uma nova tentativa, e o laço começa na hora.
    private async Task<bool> TryOpenAsync(BrymenMeterOptions options, bool firstAttempt, CancellationToken cancellationToken)
    {
        _accessor.OnConnecting();
        BrymenMeter meter;
        try
        {
            meter = await BrymenMeter.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (BrymenMeterException ex)
        {
            if (firstAttempt)
            {
                lock (_sync)
                    _openFailure = ex;
            }
            _accessor.OnOpenFailed(ex.ToError(options.TimeProvider.GetUtcNow()));
            return false;
        }
        catch (Exception ex) when (!firstAttempt && ex is not OperationCanceledException)
        {
            var error = new BrymenMeterError(BrymenErrorCodes.Internal, $"Falha inesperada ao abrir o multímetro: {ex.Message}", ex, options.TimeProvider.GetUtcNow());
            _accessor.OnServiceFailure(error);
            return false;
        }

        lock (_sync)
        {
            // A parada chegou durante a abertura: o medidor não chega a ser exposto.
            if (_disposed || _stopping is not { IsCancellationRequested: false })
            {
                meter.Dispose();
                return true;
            }
            _meter = meter;
            // A abertura em si já é anunciada no log; um "restabelecida" depois dela seria repetido.
            _lastLoggedCode = null;
        }
        LogOpened(_logger, meter.Device.Path, meter.Device.Product ?? "?");
        _accessor.Attach(meter);
        if (!firstAttempt)
            StartLoop(meter);
        return true;
    }

    private static void StartLoop(BrymenMeter meter)
    {
        try
        {
            meter.Start();
        }
        catch (ObjectDisposedException)
        {
            // A parada descartou o medidor entre a abertura e o início do laço.
        }
    }

    private void OnStateChanged(object? sender, MeterStateChangedEventArgs e)
    {
        if (e.Current != MeterState.Connected)
            return;
        string? lastCode;
        lock (_sync)
        {
            lastCode = _lastLoggedCode;
            _lastLoggedCode = null;
        }
        if (lastCode is not null)
            LogRecovered(_logger, lastCode);
    }

    // Uma falha que se repete (o cabo continua ausente, o medidor continua desligado) vai para o log uma vez como aviso e
    // depois só em Debug, até a comunicação voltar.
    private void OnError(object? sender, BrymenMeterError error)
    {
        if (error.Code is BrymenErrorCodes.EventHandlerFailed or BrymenErrorCodes.Internal)
        {
            LogUnexpectedFailure(_logger, error.Code, error.Message, error.Exception);
            return;
        }
        bool repeated;
        lock (_sync)
        {
            repeated = _lastLoggedCode == error.Code;
            _lastLoggedCode = error.Code;
        }
        if (repeated)
            LogRepeatedFailure(_logger, error.Code, error.Message);
        else
            LogFailure(_logger, error.Code, error.Message);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Multímetro aberto no cabo {DevicePath} ({Product}).")]
    private static partial void LogOpened(ILogger logger, string devicePath, string product);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Falha na comunicação com o multímetro [{Code}]: {Message}")]
    private static partial void LogFailure(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Falha repetida na comunicação com o multímetro [{Code}]: {Message}")]
    private static partial void LogRepeatedFailure(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Comunicação com o multímetro restabelecida (a última falha foi {Code}).")]
    private static partial void LogRecovered(ILogger logger, string code);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Falha inesperada no multímetro [{Code}]: {Message}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string code, string message, Exception? exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Multímetro não aberto, e a reconexão automática (AutoReconnect) está desligada: o serviço não vai tentar de novo.")]
    private static partial void LogNoAutoReconnect(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Multímetro fechado.")]
    private static partial void LogStopped(ILogger logger);
}
