using System.Diagnostics;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Brymen.Dmm.Extensions.Hosting.Tests;

/// <summary>Um BC-86X simulado, sem latência, ligado a um BM52x simulado com as capturas reais.</summary>
internal sealed class SimulatedCable
{
    public SimulatedCable(string? path = null)
    {
        Meter = SimulatedBm52xMeter.FromCaptures();
        Device = new SimulatedHidDevice(Meter.Respond, path is null ? null : SimulatedHidDevice.DefaultInfo with { Path = path });
        Provider = new SimulatedHidDeviceProvider([Device]);
    }

    public SimulatedBm52xMeter Meter { get; }

    public SimulatedHidDevice Device { get; }

    public SimulatedHidDeviceProvider Provider { get; }

    /// <summary>Opções rápidas para o simulado: descarte instantâneo, timeouts curtos e reconexão em 50 ms.</summary>
    public void Configure(BrymenMeterOptions options)
    {
        options.DeviceProvider = Provider;
        options.PollInterval = TimeSpan.FromMilliseconds(10);
        options.ReportTimeout = TimeSpan.FromMilliseconds(150);
        options.RealTimeAfterMemoryTimeout = TimeSpan.FromMilliseconds(300);
        options.ReconnectDelay = TimeSpan.FromMilliseconds(50);
        options.DrainTimeout = TimeSpan.Zero;
        options.RecoveryDrainTimeout = TimeSpan.Zero;
    }

    public static string Hex(int index) => Convert.ToHexString(SimulatedBm52xMeter.CapturedRealTimeFrames[index]);
}

/// <summary>Um Generic Host com o AddBrymenMeter sobre um <see cref="SimulatedCable"/>, sem as fontes padrão de configuração.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    public TestHost(SimulatedCable? cable = null, Action<BrymenMeterOptions>? configure = null)
    {
        Cable = cable ?? new SimulatedCable();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(Logs);
        builder.Services.AddBrymenMeter(options =>
        {
            Cable.Configure(options);
            configure?.Invoke(options);
        });
        Host = builder.Build();
        Accessor = Host.Services.GetRequiredService<IBrymenMeterAccessor>();
        Events = new AccessorEvents(Accessor);
    }

    public SimulatedCable Cable { get; }

    public IHost Host { get; }

    public IBrymenMeterAccessor Accessor { get; }

    /// <summary>O <see cref="BrymenMeter"/> do serviço (o acessor só expõe a fachada).</summary>
    public BrymenMeter? OwnedMeter => ((BrymenMeterAccessor)Accessor).CurrentMeter;

    public AccessorEvents Events { get; }

    public LogCollector Logs { get; } = new();

    public Task StartAsync() => Host.StartAsync();

    public Task StopAsync() => Host.StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (Host is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            Host.Dispose();
    }
}

/// <summary>Coleta os eventos de um <see cref="IBrymenMeterAccessor"/>, de forma thread-safe.</summary>
internal sealed class AccessorEvents
{
    private readonly Lock _lock = new();
    private readonly List<DmmReading> _readings = [];
    private readonly List<MeterStateChangedEventArgs> _states = [];
    private readonly List<BrymenMeterError> _errors = [];
    private readonly List<object?> _senders = [];

    public AccessorEvents(IBrymenMeterAccessor accessor)
    {
        accessor.ReadingReceived += (s, r) => { lock (_lock) { _readings.Add(r); _senders.Add(s); } };
        accessor.StateChanged += (s, e) => { lock (_lock) { _states.Add(e); _senders.Add(s); } };
        accessor.ErrorOccurred += (s, e) => { lock (_lock) { _errors.Add(e); _senders.Add(s); } };
    }

    public IReadOnlyList<DmmReading> Readings
    {
        get { lock (_lock) return [.. _readings]; }
    }

    public IReadOnlyList<MeterStateChangedEventArgs> States
    {
        get { lock (_lock) return [.. _states]; }
    }

    public IReadOnlyList<BrymenMeterError> Errors
    {
        get { lock (_lock) return [.. _errors]; }
    }

    public IReadOnlyList<object?> Senders
    {
        get { lock (_lock) return [.. _senders]; }
    }
}

/// <summary>Uma mensagem de log gravada pelo <see cref="LogCollector"/>.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message, Exception? Exception);

/// <summary>Provider de log em memória, thread-safe.</summary>
internal sealed class LogCollector : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return [.. _entries]; }
    }

    public IReadOnlyList<LogEntry> Of<T>() => [.. Entries.Where(e => e.Category.StartsWith("Brymen.Dmm.Extensions.Hosting.", StringComparison.Ordinal) && e.Category.EndsWith(typeof(T).Name, StringComparison.Ordinal))];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_lock)
            _entries.Add(entry);
    }

    private sealed class Logger(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception), exception));
    }
}

internal static class TestWait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    /// <summary>Espera <paramref name="condition"/> ficar verdadeira, verificando a cada 5 ms; falha depois de <paramref name="timeout"/>.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Default;
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit)
                throw new TimeoutException($"Tempo esgotado esperando {what}.");
            await Task.Delay(5);
        }
    }
}
