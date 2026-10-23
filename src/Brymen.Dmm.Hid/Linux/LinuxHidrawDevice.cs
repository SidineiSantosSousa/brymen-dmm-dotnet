using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Brymen.Dmm.Hid;

/// <summary>
/// Dispositivo <c>/dev/hidrawN</c> aberto com <c>O_RDWR|O_CLOEXEC|O_NONBLOCK</c>. O <c>write</c> leva o report ID
/// na frente; o <c>read</c> de relatórios não numerados devolve só os dados (8 bytes no BC-86X).
/// Aceita um leitor e um escritor simultâneos.
/// </summary>
/// <remarks>
/// <see cref="ReadReportAsync"/> devolve na hora um relatório que já está na fila do kernel; senão, a espera (o
/// <c>poll()</c> em fatias de <see cref="PollSlice"/>) ocupa uma thread do pool, e não a de quem chama. O
/// <see cref="WriteReportAsync"/> é curto e termina na thread de quem chama.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxHidrawDevice : IHidDevice
{
    private readonly SafeFileDescriptor _fd;
    private readonly bool _numberedInput;
    private readonly byte[] _readScratch;
    private TimeSpan _pollSlice = TimeSpan.FromMilliseconds(50);

    private LinuxHidrawDevice(HidDeviceInfo info, SafeFileDescriptor fd, HidReportLayout layout)
    {
        Info = info;
        _fd = fd;
        InputReportLength = layout.InputReportLength;
        OutputReportLength = layout.OutputReportLength;
        _numberedInput = layout.UsesReportIds;
        // O hidraw trunca o relatório ao tamanho do buffer; a folga evita perder bytes se o descriptor mentir.
        _readScratch = new byte[Math.Max(64, InputReportLength + 1)];
    }

    /// <inheritdoc/>
    public HidDeviceInfo Info { get; }

    /// <inheritdoc/>
    public int InputReportLength { get; }

    /// <inheritdoc/>
    public int OutputReportLength { get; }

    /// <summary>
    /// Fatia máxima de cada <c>poll</c>; entre fatias o <see cref="CancellationToken"/> é verificado e um
    /// <see cref="Dispose"/> concorrente é percebido.
    /// </summary>
    public TimeSpan PollSlice
    {
        get => _pollSlice;
        set
        {
            // Precisa caber no int de milissegundos do poll(): um valor negativo lá é espera infinita.
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromMilliseconds(1));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue));
            _pollSlice = value;
        }
    }

    /// <summary>Limite para repetir um <c>write</c> que devolveu <c>EAGAIN</c>.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Abre o nó e lê os tamanhos dos relatórios do report descriptor do próprio nó (<c>HIDIOCGRDESC</c>), que vale para
    /// qualquer caminho, inclusive um nó com outro nome num contêiner; <paramref name="fallbackLayout"/> (o sysfs) só é
    /// usado se o ioctl falhar.
    /// </summary>
    internal static LinuxHidrawDevice Open(HidDeviceInfo info, Func<HidReportLayout> fallbackLayout)
    {
        int fd, errno;
        do
        {
            fd = LibC.Open(info.Path, LibC.O_RDWR | LibC.O_CLOEXEC | LibC.O_NONBLOCK);
            errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (fd < 0 && errno == HidrawErrors.EINTR);

        if (fd < 0)
            throw HidrawErrors.ForOpen(errno, info.Path);
        var handle = new SafeFileDescriptor(fd);
        try
        {
            // O descritor ainda não foi entregue a ninguém: o fd cru não corre o risco de um Dispose concorrente.
            var layout = HidrawNode.TryReadReportLayout(LibCHidrawSyscalls.Instance, fd) ?? fallbackLayout();
            return new LinuxHidrawDevice(info, handle, layout);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_fd.IsClosed, this);
        if (OutputReportLength == 0)
            throw new HidException($"O dispositivo {Info.Path} não tem relatório de saída.");
        if (data.Length > OutputReportLength)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"O relatório de saída tem no máximo {OutputReportLength} bytes; recebidos {data.Length}."), nameof(data));

        var report = new byte[OutputReportLength + 1];
        report[0] = reportId;
        data.Span.CopyTo(report.AsSpan(1));

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            // O write do hidraw é síncrono e curto: o kernel limita a transferência USB a 5 s.
            var (written, errno) = WriteCore(report);
            if (written == report.Length)
                return;
            if (written >= 0)
                throw new HidException(string.Create(CultureInfo.InvariantCulture, $"Escrita parcial em {Info.Path}: {written} de {report.Length} bytes."));
            if (errno == HidrawErrors.EINTR)
                continue;
            if (errno == HidrawErrors.EAGAIN && Stopwatch.GetElapsedTime(start) < WriteTimeout)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                continue;
            }
            throw HidrawErrors.ForIo(errno, "write", Info.Path);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Um relatório já na fila do kernel volta na hora, sem trocar de thread; senão, a espera roda numa thread do pool, e
    /// a tarefa volta em andamento para quem chamou (uma thread de interface gráfica não fica bloqueada).
    /// </remarks>
    public ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_fd.IsClosed, this);
            if (buffer.Length < InputReportLength)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"O buffer precisa ter ao menos {InputReportLength} bytes."), nameof(buffer));
            if (timeout != Timeout.InfiniteTimeSpan)
                ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
            cancellationToken.ThrowIfCancellationRequested();

            int ready = PollOnce(buffer.Span, waitMs: 0);
            if (ready >= 0)
                return ValueTask.FromResult(ready);
            if (timeout == TimeSpan.Zero)
                throw NoReport(timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }

        return new ValueTask<int>(Task.Run(() => ReadReport(buffer, timeout, cancellationToken), cancellationToken));
    }

    // Espera em fatias curtas de poll(), numa thread do pool: ela fica ocupada no máximo pelo timeout pedido, e o
    // cancelamento é percebido em até uma fatia. Um relatório que chega depois do timeout fica na fila do kernel para a
    // próxima leitura.
    private int ReadReport(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = infinite ? PollSlice : timeout - Stopwatch.GetElapsedTime(start);
            int waitMs = remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, PollSlice.TotalMilliseconds));

            int length = PollOnce(buffer.Span, waitMs);
            if (length >= 0)
                return length;
            if (waitMs == 0)
                throw NoReport(timeout);
        }
    }

    // Um poll() de até waitMs e, com dados, um read(): o tamanho do relatório copiado, ou -1 se nada chegou.
    private int PollOnce(Span<byte> buffer, int waitMs)
    {
        short revents = PollCore(waitMs);
        if ((revents & LibC.POLLIN) != 0)
        {
            // Relatórios já na fila continuam legíveis mesmo com POLLHUP; o read seguinte devolve EIO.
            var (n, errno) = ReadCore(_readScratch);
            if (n > 0)
                return CopyReport(_readScratch.AsSpan(0, (int)n), _numberedInput, InputReportLength, buffer);
            if (n == 0)
                throw HidrawErrors.Disconnected(Info.Path);
            if (errno is not (HidrawErrors.EAGAIN or HidrawErrors.EINTR))
                throw HidrawErrors.ForIo(errno, "read", Info.Path);
        }
        else if ((revents & (LibC.POLLERR | LibC.POLLHUP | LibC.POLLNVAL)) != 0)
        {
            throw HidrawErrors.Disconnected(Info.Path);
        }
        return -1;
    }

    private TimeoutException NoReport(TimeSpan timeout)
        => new(string.Create(CultureInfo.InvariantCulture, $"Nenhum relatório de entrada de {Info.Path} em {timeout.TotalMilliseconds:0} ms."));

    /// <summary>
    /// Copia um relatório lido do hidraw para <paramref name="buffer"/>, sem o report ID dos relatórios numerados.
    /// O hidraw entrega o relatório como veio do fio; um relatório curto é completado com zeros até
    /// <paramref name="inputReportLength"/>, como o ReadFile do Windows (sempre <c>InputReportByteLength</c> bytes)
    /// e o próprio núcleo HID do kernel (<c>hid_report_raw_event</c>) fazem.
    /// </summary>
    internal static int CopyReport(ReadOnlySpan<byte> report, bool numbered, int inputReportLength, Span<byte> buffer)
    {
        if (numbered && !report.IsEmpty)
            report = report[1..];
        int count = Math.Min(report.Length, buffer.Length);
        report[..count].CopyTo(buffer);
        int length = Math.Min(Math.Max(count, inputReportLength), buffer.Length);
        buffer[count..length].Clear();
        return length;
    }

    // O SafeHandle conta as referências: um Dispose concorrente só fecha o descritor depois da chamada em curso,
    // e o número nunca é reaproveitado pelo kernel para outro arquivo enquanto ela usa.
    private void AddRef(ref bool added)
    {
        try
        {
            _fd.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException ex)
        {
            throw new ObjectDisposedException($"O dispositivo {Info.Path} foi fechado.", ex);
        }
    }

    private unsafe short PollCore(int timeoutMs)
    {
        bool added = false;
        try
        {
            AddRef(ref added);
            var pfd = new LibC.PollFd { Fd = _fd.Fd, Events = LibC.POLLIN };
            int rc = LibC.Poll(&pfd, 1, timeoutMs);
            if (rc > 0)
                return pfd.Revents;
            if (rc == 0)
                return 0;
            int errno = Marshal.GetLastPInvokeError();
            if (errno == HidrawErrors.EINTR)
                return 0;
            throw HidrawErrors.ForIo(errno, "poll", Info.Path);
        }
        finally
        {
            if (added)
                _fd.DangerousRelease();
        }
    }

    private unsafe (nint Result, int Errno) ReadCore(byte[] scratch)
    {
        bool added = false;
        try
        {
            AddRef(ref added);
            fixed (byte* p = scratch)
            {
                nint n = LibC.Read(_fd.Fd, p, (nuint)scratch.Length);
                return (n, n < 0 ? Marshal.GetLastPInvokeError() : 0);
            }
        }
        finally
        {
            if (added)
                _fd.DangerousRelease();
        }
    }

    private unsafe (nint Result, int Errno) WriteCore(byte[] report)
    {
        bool added = false;
        try
        {
            AddRef(ref added);
            fixed (byte* p = report)
            {
                nint n = LibC.Write(_fd.Fd, p, (nuint)report.Length);
                return (n, n < 0 ? Marshal.GetLastPInvokeError() : 0);
            }
        }
        finally
        {
            if (added)
                _fd.DangerousRelease();
        }
    }

    /// <summary>Fecha o descritor; uma chamada em andamento termina antes do <c>close</c> (contagem de referências do SafeHandle).</summary>
    public void Dispose() => _fd.Dispose();
}
