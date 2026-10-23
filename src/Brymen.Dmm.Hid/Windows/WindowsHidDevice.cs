using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static Brymen.Dmm.Hid.WindowsNative;

namespace Brymen.Dmm.Hid;

/// <summary>
/// Dispositivo HID aberto com <c>FILE_FLAG_OVERLAPPED</c> e lido por um <see cref="FileStream"/> assíncrono.
/// No Windows o buffer de cada relatório começa com o report ID (00h no BC-86X): o write leva
/// <c>OutputReportByteLength</c> bytes e o read devolve <c>InputReportByteLength</c> − 1 bytes de dados.
/// Aceita um leitor e um escritor simultâneos.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHidDevice : IHidDevice
{
    private readonly FileStream _stream;
    private readonly byte[] _inputBuffer;
    private readonly int _outputReportByteLength;
    private volatile bool _disposed;

    private WindowsHidDevice(HidDeviceInfo info, FileStream stream, in HidpCaps caps)
    {
        Info = info;
        _stream = stream;
        _inputBuffer = new byte[Math.Max(1, (int)caps.InputReportByteLength)];
        _outputReportByteLength = caps.OutputReportByteLength;
        InputReportLength = Math.Max(0, caps.InputReportByteLength - 1);
        OutputReportLength = Math.Max(0, caps.OutputReportByteLength - 1);
        UsagePage = caps.UsagePage;
        Usage = caps.Usage;
    }

    /// <inheritdoc/>
    public HidDeviceInfo Info { get; }

    /// <inheritdoc/>
    public int InputReportLength { get; }

    /// <inheritdoc/>
    public int OutputReportLength { get; }

    /// <summary>Usage page da coleção de topo (0xFF00 no BC-86X).</summary>
    public ushort UsagePage { get; }

    /// <summary>Usage da coleção de topo (0x0001 no BC-86X).</summary>
    public ushort Usage { get; }

    /// <summary>Tempo máximo para o driver aceitar um relatório de saída (o original usa 2000 ms).</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal static WindowsHidDevice Open(HidDeviceInfo info)
    {
        var handle = CreateFile(info.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw WindowsHidErrors.ForOpen(error, info.Path);
        }

        try
        {
            var caps = GetCaps(handle, info.Path);
            // bufferSize 0: sem buffer do FileStream; cada ReadAsync/WriteAsync é um ReadFile/WriteFile overlapped.
            var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
            return new WindowsHidDevice(info, stream, caps);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static HidpCaps GetCaps(SafeFileHandle handle, string path)
    {
        if (!HidD_GetPreparsedData(handle, out nint preparsed))
            throw new HidException($"HidD_GetPreparsedData falhou para {path}.");
        try
        {
            int status = HidP_GetCaps(preparsed, out var caps);
            if (status != HIDP_STATUS_SUCCESS)
                throw new HidException(string.Create(CultureInfo.InvariantCulture, $"HidP_GetCaps falhou para {path} (status 0x{status:X8})."), nativeError: status);
            return caps;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_outputReportByteLength == 0)
            throw new HidException($"O dispositivo {Info.Path} não tem relatório de saída.");
        if (data.Length > OutputReportLength)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"O relatório de saída tem no máximo {OutputReportLength} bytes; recebidos {data.Length}."), nameof(data));

        var report = new byte[_outputReportByteLength];
        report[0] = reportId;
        data.Span.CopyTo(report.AsSpan(1));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var write = _stream.WriteAsync(report, timeoutCts.Token);
            if (!write.IsCompleted)
                timeoutCts.CancelAfter(WriteTimeout);
            await write.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposedDuringIo();
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"O relatório de saída não foi aceito por {Info.Path} em {WriteTimeout.TotalMilliseconds:0} ms."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThrowIfDisposedDuringIo();
            throw WindowsHidErrors.ForIo(ex, Info.Path);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length < InputReportLength)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"O buffer precisa ter ao menos {InputReportLength} bytes."), nameof(buffer));
        if (timeout != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int n;
        try
        {
            // O prazo só começa a contar depois de o ReadFile ter sido emitido. Armado antes, um atraso da thread
            // (GC, pool ocupado) faria o FileStream achar o token já cancelado e desistir sem consultar o driver,
            // deixando relatórios na fila, e o descarte do Bu86xLink terminaria cedo. Com o ReadFile emitido, o
            // prazo faz CancelIoEx só dele: um relatório que chegue depois fica na fila do driver HID para a próxima
            // leitura e, se o ReadFile já tiver concluído, os dados são devolvidos. Vale também para timeout 0.
            var read = _stream.ReadAsync(_inputBuffer, timeoutCts.Token);
            if (!read.IsCompleted && timeout != Timeout.InfiniteTimeSpan)
                timeoutCts.CancelAfter(timeout);
            n = await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // O CloseHandle do Dispose aborta o ReadFile pendente com ERROR_OPERATION_ABORTED, como um prazo esgotado
            // (≈ 4 ms no BC-86X real).
            ThrowIfDisposedDuringIo();
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"Nenhum relatório de entrada de {Info.Path} em {timeout.TotalMilliseconds:0} ms."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThrowIfDisposedDuringIo();
            throw WindowsHidErrors.ForIo(ex, Info.Path);
        }

        if (n == 0)
            throw WindowsHidErrors.Disconnected(Info.Path);
        int count = Math.Min(n - 1, buffer.Length);
        _inputBuffer.AsSpan(1, count).CopyTo(buffer.Span);
        return count;
    }

    /// <summary>Fecha o handle; uma leitura ou escrita pendente é abortada e falha com <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _disposed = true;
        _stream.Dispose();
    }

    private void ThrowIfDisposedDuringIo()
    {
        if (_disposed)
            throw new ObjectDisposedException($"O dispositivo {Info.Path} foi fechado durante a operação.");
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsHidErrors
{
    public static HidException ForOpen(int error, string path) => error switch
    {
        WindowsNative.ERROR_ACCESS_DENIED or WindowsNative.ERROR_SHARING_VIOLATION => new HidException(
            string.Create(CultureInfo.InvariantCulture, $"Acesso negado a {path} (erro {error}): o cabo pode estar aberto por outro programa, como o Bs82-52x original."),
            nativeError: error),
        WindowsNative.ERROR_FILE_NOT_FOUND or WindowsNative.ERROR_PATH_NOT_FOUND or WindowsNative.ERROR_DEVICE_NOT_CONNECTED or WindowsNative.ERROR_NO_SUCH_DEVICE =>
            new HidException(string.Create(CultureInfo.InvariantCulture, $"Dispositivo {path} não encontrado ou desconectado (erro {error})."), nativeError: error),
        _ => new HidException(string.Create(CultureInfo.InvariantCulture, $"Falha ao abrir {path} (erro {error})."), nativeError: error),
    };

    /// <summary>Traduz a exceção do <see cref="FileStream"/>: IOException ou, para ERROR_ACCESS_DENIED, UnauthorizedAccessException.</summary>
    public static HidException ForIo(Exception ex, string path)
    {
        // HRESULT_FROM_WIN32: 0x8007xxxx, com o código Win32 nos 16 bits baixos.
        int? error = ex is UnauthorizedAccessException
            ? WindowsNative.ERROR_ACCESS_DENIED
            : unchecked((uint)ex.HResult) >> 16 == 0x8007 ? ex.HResult & 0xFFFF : null;
        return error switch
        {
            WindowsNative.ERROR_DEVICE_NOT_CONNECTED or WindowsNative.ERROR_GEN_FAILURE or WindowsNative.ERROR_INVALID_HANDLE
                or WindowsNative.ERROR_NO_SUCH_DEVICE or WindowsNative.ERROR_NOT_READY or WindowsNative.ERROR_FILE_NOT_FOUND =>
                new HidException(string.Create(CultureInfo.InvariantCulture, $"Dispositivo {path} desconectado (erro {error})."), ex, error),
            _ => new HidException($"Falha de E/S em {path}: {ex.Message}", ex, error),
        };
    }

    public static HidException Disconnected(string path) => new($"Dispositivo {path} desconectado.");
}
