using System.Globalization;

namespace Brymen.Dmm.Hid;

/// <summary>Relatório de saída recebido por um <see cref="SimulatedHidDevice"/>.</summary>
/// <param name="ReportId">Report ID do relatório (00h no BC-86X).</param>
/// <param name="Data">Dados completados com zeros até <see cref="IHidDevice.OutputReportLength"/>.</param>
public sealed record SimulatedHidWrite(byte ReportId, byte[] Data);

/// <summary>
/// Dispositivo HID em memória. Cada relatório de saída é entregue ao <em>responder</em>, que devolve os
/// relatórios de entrada a enfileirar (8 bytes no BC-86X) ou <see langword="null"/> para nenhuma resposta.
/// Simula latência, desconexão e relatórios atrasados; é seguro para uso concorrente. As esperas (latência e timeout
/// das leituras) seguem o <see cref="TimeProvider"/> do construtor, que pode ser um relógio falso nos testes.
/// </summary>
/// <remarks>
/// O objeto representa o aparelho e o handle ao mesmo tempo: <see cref="Dispose"/> fecha o handle e o
/// <see cref="SimulatedHidDeviceProvider"/> pode reabri-lo, com a fila vazia.
/// </remarks>
public sealed class SimulatedHidDevice : IHidDevice
{
    private readonly Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> _responder;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Queue<(byte[] Data, long DueTimestamp)> _queue = new();
    private readonly List<SimulatedHidWrite> _writes = [];
    private TaskCompletionSource _changed = NewSignal();
    private bool _open = true;
    private bool _connected = true;
    private bool _broken;

    /// <param name="responder">Recebe o report ID e os dados de cada relatório de saída e devolve os relatórios de entrada da resposta.</param>
    /// <param name="info">Identificação do dispositivo; padrão: <see cref="DefaultInfo"/>.</param>
    /// <param name="inputReportLength">Bytes de dados de um relatório de entrada (8 no BC-86X).</param>
    /// <param name="outputReportLength">Bytes de dados de um relatório de saída (3 no BC-86X).</param>
    /// <param name="timeProvider">Relógio da latência e dos timeouts; padrão: <see cref="TimeProvider.System"/>.</param>
    public SimulatedHidDevice(
        Func<byte, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> responder,
        HidDeviceInfo? info = null,
        int inputReportLength = Bu86xLink.ReportDataLength,
        int outputReportLength = 3,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputReportLength, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(outputReportLength);
        _responder = responder;
        _time = timeProvider ?? TimeProvider.System;
        Info = info ?? DefaultInfo;
        InputReportLength = inputReportLength;
        OutputReportLength = outputReportLength;
    }

    /// <summary>Identificação de um BC-86X simulado, com os valores do cabo real (bcdDevice 0x8603, "Brymen" / "Superior DMM").</summary>
    public static HidDeviceInfo DefaultInfo { get; } =
        new("sim://bu86x/0", Bu86xLink.VendorId, Bu86xLink.ProductId, 0x8603, "Brymen", "Superior DMM");

    /// <summary>
    /// Cria um BC-86X simulado, sem latência, ligado a um BM52x simulado: <paramref name="meter"/> ou o de
    /// <see cref="SimulatedBm52xMeter.FromCaptures"/>, com as capturas reais completas (os 14 quadros de tempo real, a
    /// memória e o <c>8A</c> que avança como um <c>89</c> logo depois do <c>88</c>). Para a latência do cabo real, ajuste
    /// <see cref="ResponseLatency"/> e <see cref="InterReportDelay"/>, ou use o <see cref="SimulatedHidDeviceProvider()"/>
    /// sem argumentos, que já traz o cabo do <c>BrymenMeter.OpenSimulatedAsync</c>.
    /// </summary>
    /// <param name="meter">Medidor que responde aos comandos; padrão: <see cref="SimulatedBm52xMeter.FromCaptures"/>.</param>
    /// <param name="info">Identificação do dispositivo; padrão: <see cref="DefaultInfo"/>.</param>
    /// <param name="timeProvider">Relógio da latência, dos timeouts e da janela do <c>8A</c> do medidor padrão; padrão: <see cref="TimeProvider.System"/>.</param>
    public static SimulatedHidDevice CreateBm52x(SimulatedBm52xMeter? meter = null, HidDeviceInfo? info = null, TimeProvider? timeProvider = null)
    {
        meter ??= SimulatedBm52xMeter.FromCaptures(timeProvider);
        return new SimulatedHidDevice(meter.Respond, info, timeProvider: timeProvider);
    }

    /// <summary>
    /// Latência de cada resposta do cabo simulado de <see cref="SimulatedHidDeviceProvider()"/> e do
    /// <c>BrymenMeter.OpenSimulatedAsync</c> (20 ms), com 1/8 dela entre os relatórios: o BC-86X real entrega os
    /// relatórios de uma resposta a cada ≈ 8 ms.
    /// </summary>
    public static TimeSpan TypicalResponseLatency { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// O cabo simulado de <see cref="SimulatedHidDeviceProvider()"/> e do <c>BrymenMeter.OpenSimulatedAsync</c>: o de
    /// <see cref="CreateBm52x"/>, com <see cref="TypicalResponseLatency"/> por resposta.
    /// </summary>
    /// <param name="meter">Medidor que responde aos comandos; padrão: <see cref="SimulatedBm52xMeter.FromCaptures"/>.</param>
    /// <param name="timeProvider">Relógio da latência, dos timeouts e da janela do <c>8A</c>; padrão: <see cref="TimeProvider.System"/>.</param>
    public static SimulatedHidDevice CreateTypicalBm52x(SimulatedBm52xMeter? meter = null, TimeProvider? timeProvider = null)
    {
        var device = CreateBm52x(meter, timeProvider: timeProvider);
        device.ResponseLatency = TypicalResponseLatency;
        device.InterReportDelay = TypicalResponseLatency / 8;
        return device;
    }

    /// <inheritdoc/>
    public HidDeviceInfo Info { get; }

    /// <inheritdoc/>
    public int InputReportLength { get; }

    /// <inheritdoc/>
    public int OutputReportLength { get; }

    /// <summary>Atraso entre a escrita de um comando e o 1º relatório da resposta (o BC-86X real leva ≈ 65 ms no <c>66</c>).</summary>
    public TimeSpan ResponseLatency { get; set; }

    /// <summary>Intervalo entre relatórios consecutivos de uma resposta (≈ 8 ms no BC-86X real).</summary>
    public TimeSpan InterReportDelay { get; set; }

    /// <summary>Se o handle está aberto (falso depois de <see cref="Dispose"/>).</summary>
    public bool IsOpen
    {
        get { lock (_lock) return _open; }
    }

    /// <summary>Se o aparelho está "plugado" (falso depois de <see cref="Disconnect"/>).</summary>
    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
    }

    /// <summary>Relatórios de entrada na fila, inclusive os que ainda não "chegaram".</summary>
    public int PendingReportCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>Relatórios de saída recebidos, em ordem.</summary>
    public IReadOnlyList<SimulatedHidWrite> Writes
    {
        get { lock (_lock) return [.. _writes]; }
    }

    /// <summary>Enfileira um relatório de entrada não solicitado (ex.: resposta atrasada de um comando anterior).</summary>
    public void EnqueueReport(ReadOnlySpan<byte> report, TimeSpan delay = default)
    {
        var data = report.ToArray();
        lock (_lock)
        {
            ThrowIfUnusable();
            long due = _time.GetTimestamp() + ToTimestampTicks(delay);
            _queue.Enqueue((data, Math.Max(due, LastDueTimestamp())));
            Signal();
        }
    }

    /// <summary>Simula a remoção do cabo: operações pendentes e futuras falham com <see cref="HidException"/> até reabrir.</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            _connected = false;
            _broken = true;
            _queue.Clear();
            Signal();
        }
    }

    /// <summary>Simula a reconexão do cabo; o handle antigo continua inválido até o provider reabrir o dispositivo.</summary>
    public void Reconnect()
    {
        lock (_lock)
            _connected = true;
    }

    /// <summary>Limpa o histórico de <see cref="Writes"/>.</summary>
    public void ClearWrites()
    {
        lock (_lock)
            _writes.Clear();
    }

    internal void Reopen()
    {
        lock (_lock)
        {
            if (!_connected)
                throw new HidException($"Dispositivo simulado {Info.Path} desconectado.");
            if (!_open || _broken)
            {
                _open = true;
                _broken = false;
                _queue.Clear();
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        try
        {
            if (data.Length > OutputReportLength)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"O relatório de saída tem no máximo {OutputReportLength} bytes; recebidos {data.Length}."), nameof(data));

            var padded = new byte[OutputReportLength];
            data.Span.CopyTo(padded);
            lock (_lock)
            {
                ThrowIfUnusable();
                _writes.Add(new SimulatedHidWrite(reportId, padded));
            }

            // Fora do lock: o responder pode chamar EnqueueReport ou outros membros do dispositivo.
            var responses = _responder(reportId, padded.ToArray());
            if (responses is { Count: > 0 })
            {
                lock (_lock)
                {
                    ThrowIfUnusable();
                    long due = Math.Max(_time.GetTimestamp() + ToTimestampTicks(ResponseLatency), LastDueTimestamp());
                    long step = ToTimestampTicks(InterReportDelay);
                    foreach (var report in responses)
                    {
                        _queue.Enqueue((report.ToArray(), due));
                        due += step;
                    }
                    Signal();
                }
            }
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        if (!infinite)
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        long start = _time.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDequeue(buffer.Span, out int count, out TimeSpan untilHead, out Task changed))
                return count;

            TimeSpan wait = untilHead;
            if (!infinite)
            {
                TimeSpan remaining = timeout - _time.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"Nenhum relatório de entrada de {Info.Path} em {timeout.TotalMilliseconds:0} ms."));
                if (wait == Timeout.InfiniteTimeSpan || remaining < wait)
                    wait = remaining;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(changed, Task.Delay(wait, _time, delayCts.Token)).ConfigureAwait(false);
            await delayCts.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Fecha o handle: as operações em andamento e as futuras falham com <see cref="ObjectDisposedException"/> até o provider reabrir o dispositivo.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _open = false;
            _queue.Clear();
            Signal();
        }
    }

    // Consome o 1º relatório já "chegado"; senão informa quanto falta para ele e o sinal de mudança da fila.
    private bool TryDequeue(Span<byte> buffer, out int count, out TimeSpan untilHead, out Task changed)
    {
        lock (_lock)
        {
            ThrowIfUnusable();
            changed = _changed.Task;
            count = 0;
            untilHead = Timeout.InfiniteTimeSpan;
            if (!_queue.TryPeek(out var head))
                return false;

            long now = _time.GetTimestamp();
            if (head.DueTimestamp > now)
            {
                untilHead = _time.GetElapsedTime(now, head.DueTimestamp);
                return false;
            }

            _queue.Dequeue();
            count = Math.Min(head.Data.Length, buffer.Length);
            head.Data.AsSpan(0, count).CopyTo(buffer);
            return true;
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(!_open, this);
        if (_broken)
            throw new HidException($"Dispositivo simulado {Info.Path} desconectado.");
    }

    private long LastDueTimestamp()
    {
        long last = long.MinValue;
        foreach (var item in _queue)
            last = item.DueTimestamp;
        return last;
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long ToTimestampTicks(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? 0 : (long)(delay.TotalSeconds * _time.TimestampFrequency);
}
