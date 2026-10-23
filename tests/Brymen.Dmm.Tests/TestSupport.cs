using System.Diagnostics;
using System.Globalization;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Tests;

/// <summary>
/// Teste com o cabo BC-86X real pelo transporte nativo (hidraw no Linux, hid.dll no Windows): só roda com
/// <c>BRYMEN_HW_TESTS=1</c>, com o medidor ligado (BM52x ou BM82x). Só usa os comandos <c>82 66</c> e <c>52 66</c> e, no
/// BM52x, <c>52 88</c>, <c>52 89</c> e <c>52 8A</c>.
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Skip = "Teste de hardware do BC-86X: só roda no Linux e no Windows.";
        else if (Environment.GetEnvironmentVariable("BRYMEN_HW_TESTS") != "1")
            Skip = "Teste de hardware do BC-86X desativado: defina BRYMEN_HW_TESTS=1, com o cabo conectado e o medidor ligado.";
    }
}

/// <summary>Um BC-86X simulado, sem latência, ligado a um BM52x simulado, e as opções para abri-lo.</summary>
internal sealed class SimulatedSetup
{
    public SimulatedSetup(SimulatedBm52xMeter? meter = null, TimeProvider? time = null, Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?>? responder = null)
    {
        Time = time ?? TimeProvider.System;
        Meter = meter ?? SimulatedBm52xMeter.FromCaptures(Time);
        Device = new SimulatedHidDevice(responder ?? Meter.Respond, timeProvider: Time);
        Provider = new SimulatedHidDeviceProvider(Device);
        // Descarte instantâneo e timeouts curtos: o simulado responde na hora, e os testes não esperam à toa.
        Options = new BrymenMeterOptions
        {
            DeviceProvider = Provider,
            TimeProvider = Time,
            PollInterval = TimeSpan.FromMilliseconds(10),
            ReportTimeout = TimeSpan.FromMilliseconds(150),
            RealTimeAfterMemoryTimeout = TimeSpan.FromMilliseconds(300),
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            DrainTimeout = TimeSpan.Zero,
            RecoveryDrainTimeout = TimeSpan.Zero,
        };
    }

    public TimeProvider Time { get; }

    public SimulatedBm52xMeter Meter { get; }

    public SimulatedHidDevice Device { get; }

    public SimulatedHidDeviceProvider Provider { get; }

    public BrymenMeterOptions Options { get; }

    /// <summary>Comandos recebidos pelo cabo simulado (o 3º byte de <c>00 52 cmd</c>), em hexadecimal.</summary>
    public IReadOnlyList<string> Commands => [.. Device.Writes.Select(w => w.Data[2].ToString("X2", CultureInfo.InvariantCulture))];

    /// <summary>Pedidos recebidos com a família (o 2º e o 3º bytes de <c>00 fam cmd</c>, como <c>"8266"</c>), em hexadecimal.</summary>
    public IReadOnlyList<string> Requests => [.. Device.Writes.Select(w => Convert.ToHexString(w.Data, 1, 2))];

    public Task<BrymenMeter> OpenAsync() => BrymenMeter.OpenAsync(Options);
}

/// <summary>Coleta os eventos de um <see cref="BrymenMeter"/>, de forma thread-safe.</summary>
internal sealed class EventRecorder
{
    private readonly Lock _lock = new();
    private readonly List<DmmReading> _readings = [];
    private readonly List<MeterStateChangedEventArgs> _states = [];
    private readonly List<BrymenMeterError> _errors = [];

    public EventRecorder(BrymenMeter meter)
    {
        meter.ReadingReceived += (_, r) => { lock (_lock) _readings.Add(r); };
        meter.StateChanged += (_, e) => { lock (_lock) _states.Add(e); };
        meter.ErrorOccurred += (_, e) => { lock (_lock) _errors.Add(e); };
    }

    public IReadOnlyList<DmmReading> Readings
    {
        get { lock (_lock) return [.. _readings]; }
    }

    public IReadOnlyList<MeterState> States
    {
        get { lock (_lock) return [.. _states.Select(s => s.Current)]; }
    }

    public IReadOnlyList<BrymenMeterError> Errors
    {
        get { lock (_lock) return [.. _errors]; }
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

    /// <summary>
    /// Avança o relógio falso em passos de <paramref name="step"/> até <paramref name="task"/> terminar, dando tempo às
    /// continuações entre um passo e outro; falha se passar de <paramref name="limit"/> no relógio falso.
    /// </summary>
    public static async Task<T> PumpAsync<T>(FakeTimeProvider time, Task<T> task, TimeSpan step, TimeSpan limit)
    {
        var advanced = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (advanced > limit || sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"A tarefa não terminou depois de {advanced.TotalMilliseconds} ms no relógio falso.");
            // Deixa as continuações pendentes (fila do pool) registrarem os seus timers antes de avançar.
            for (int i = 0; i < 20 && !task.IsCompleted; i++)
                await Task.Yield();
            if (task.IsCompleted)
                break;
            await Task.Delay(1);
            if (task.IsCompleted)
                break;
            time.Advance(step);
            advanced += step;
        }
        return await task;
    }

    public static Task PumpAsync(FakeTimeProvider time, Task task, TimeSpan step, TimeSpan limit)
        => PumpAsync(time, task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), step, limit);
}

/// <summary>
/// Transporte do <see cref="LoggerMemoryReader"/> direto sobre o medidor simulado, sem HID nem esperas: monta o quadro
/// com os report IDs e registra os comandos e os resets.
/// </summary>
internal sealed class ResponderTransport(SimulatedBm52xMeter meter) : ILoggerMemoryTransport
{
    private readonly Lock _lock = new();
    private readonly List<string> _commands = [];
    private int _resets;

    public IReadOnlyList<string> Commands
    {
        get { lock (_lock) return [.. _commands]; }
    }

    public int Resets => Volatile.Read(ref _resets);

    public Task<MeterCommandResult> ExecuteAsync(byte command, int reportCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            _commands.Add(command.ToString("X2", CultureInfo.InvariantCulture));
        var reports = meter.Respond(0, new byte[] { 0x00, Bm52xCommands.Family, command });
        if (reports is null || reports.Count < reportCount)
            return Task.FromResult(new MeterCommandResult(null, new TimeoutException("sem resposta"), reports?.Count ?? 0));
        return Task.FromResult(new MeterCommandResult(Join(reports), null, reportCount));
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resets);
        return Task.CompletedTask;
    }

    public static byte[] Join(IReadOnlyList<byte[]> reports) => [.. reports.SelectMany(r => r.Prepend((byte)0x00))];
}

/// <summary>Progresso síncrono (o <see cref="Progress{T}"/> entregaria depois, no pool).</summary>
internal sealed class ProgressRecorder : IProgress<LoggerReadProgress>
{
    private readonly Lock _lock = new();
    private readonly List<LoggerReadProgress> _items = [];

    public IReadOnlyList<LoggerReadProgress> Items
    {
        get { lock (_lock) return [.. _items]; }
    }

    public void Report(LoggerReadProgress value)
    {
        lock (_lock)
            _items.Add(value);
    }
}

/// <summary>Decorador que registra o timeout pedido em cada leitura de relatório (inclusive as do descarte).</summary>
internal sealed class TimeoutRecordingDevice(IHidDevice inner) : IHidDevice
{
    private readonly Lock _lock = new();
    private readonly List<(byte Command, TimeSpan Timeout)> _firstReads = [];
    private byte? _pendingCommand;

    public HidDeviceInfo Info => inner.Info;

    public int InputReportLength => inner.InputReportLength;

    public int OutputReportLength => inner.OutputReportLength;

    /// <summary>Timeout da 1ª leitura depois de cada comando (a do 1º relatório da resposta).</summary>
    public IReadOnlyList<(byte Command, TimeSpan Timeout)> FirstReportTimeouts
    {
        get { lock (_lock) return [.. _firstReads]; }
    }

    public async ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await inner.WriteReportAsync(reportId, data, cancellationToken);
        lock (_lock)
            _pendingCommand = data.Span[2];
    }

    public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_pendingCommand is { } command)
            {
                _firstReads.Add((command, timeout));
                _pendingCommand = null;
            }
        }
        return inner.ReadReportAsync(buffer, timeout, cancellationToken);
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Provider que embrulha os dispositivos abertos num <see cref="TimeoutRecordingDevice"/> (o último fica em <see cref="Current"/>).</summary>
internal sealed class TimeoutRecordingProvider(IHidDeviceProvider inner) : IHidDeviceProvider
{
    private readonly List<TimeoutRecordingDevice> _opened = [];

    public IReadOnlyList<(byte Command, TimeSpan Timeout)> FirstReportTimeouts
    {
        get
        {
            lock (_opened)
                return [.. _opened.SelectMany(d => d.FirstReportTimeouts)];
        }
    }

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => inner.Enumerate(vendorId, productId);

    public IHidDevice Open(HidDeviceInfo device)
    {
        var recording = new TimeoutRecordingDevice(inner.Open(device));
        lock (_opened)
            _opened.Add(recording);
        return recording;
    }
}

/// <summary>
/// Provider que conta as aberturas do cabo e registra cada pedido escrito (<c>"8266"</c>, <c>"5288"</c>...), em qualquer
/// dispositivo aberto por ele. Serve para os testes com o cabo real.
/// </summary>
internal sealed class RequestRecordingProvider(IHidDeviceProvider inner) : IHidDeviceProvider
{
    private readonly Lock _lock = new();
    private readonly List<string> _requests = [];
    private int _opens;

    /// <summary>Aberturas do cabo por este provider.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>Os bytes 2 e 3 de cada relatório de saída (“Command 2” e “Command 3”), na ordem.</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => inner.Enumerate(vendorId, productId);

    public IHidDevice Open(HidDeviceInfo device)
    {
        var opened = inner.Open(device);
        Interlocked.Increment(ref _opens);
        return new Device(opened, this);
    }

    private void Add(ReadOnlySpan<byte> data)
    {
        string request = Convert.ToHexString(data[1..Math.Min(3, data.Length)]);
        lock (_lock)
            _requests.Add(request);
    }

    private sealed class Device(IHidDevice inner, RequestRecordingProvider owner) : IHidDevice
    {
        public HidDeviceInfo Info => inner.Info;

        public int InputReportLength => inner.InputReportLength;

        public int OutputReportLength => inner.OutputReportLength;

        public async ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await inner.WriteReportAsync(reportId, data, cancellationToken);
            owner.Add(data.Span);
        }

        public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.ReadReportAsync(buffer, timeout, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}

internal static class TestData
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);
}
