namespace Brymen.Dmm.Hid;

/// <summary>Identificação de um dispositivo HID encontrado na enumeração.</summary>
/// <param name="Path">Caminho específico da plataforma (ex.: <c>/dev/hidraw0</c> ou <c>\\?\hid#vid_0820...</c>).</param>
/// <param name="VendorId">Vendor ID USB.</param>
/// <param name="ProductId">Product ID USB.</param>
/// <param name="ReleaseNumber">bcdDevice (0x8603 no BC-86X), quando disponível.</param>
/// <param name="Manufacturer">String do fabricante (<c>Brymen</c> no BC-86X), quando disponível.</param>
/// <param name="Product">String do produto (<c>Superior DMM</c> no BC-86X), quando disponível.</param>
/// <param name="SerialNumber">String do número de série (no BC-86X, lixo não-ASCII: não use para identificar o cabo).</param>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort? ReleaseNumber = null,
    string? Manufacturer = null,
    string? Product = null,
    string? SerialNumber = null);

/// <summary>
/// Dispositivo HID aberto. Os relatórios trafegam sempre SEM o byte de report ID:
/// cada plataforma adiciona/remove esse byte internamente (no Windows ele faz parte do buffer
/// de ReadFile/WriteFile; no Linux hidraw o write leva o ID e o read de relatórios não numerados não).
/// Aceita um leitor e um escritor simultâneos. <see cref="IDisposable.Dispose"/> durante uma operação a faz
/// falhar com <see cref="ObjectDisposedException"/>; o cabo removido faz as operações falharem com <see cref="HidException"/>.
/// </summary>
public interface IHidDevice : IDisposable
{
    /// <summary>Identificação do dispositivo aberto.</summary>
    HidDeviceInfo Info { get; }

    /// <summary>Tamanho dos dados de um relatório de entrada, sem report ID (8 no BC-86X).</summary>
    int InputReportLength { get; }

    /// <summary>Tamanho dos dados de um relatório de saída, sem report ID (3 no BC-86X).</summary>
    int OutputReportLength { get; }

    /// <summary>Envia um relatório de saída. <paramref name="data"/> é completado com zeros até <see cref="OutputReportLength"/>.</summary>
    ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê um relatório de entrada para <paramref name="buffer"/> (≥ <see cref="InputReportLength"/>) e retorna o número de bytes de dados.
    /// Lança <see cref="TimeoutException"/> se nada chegar dentro de <paramref name="timeout"/>.
    /// </summary>
    ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Enumera e abre dispositivos HID numa plataforma.</summary>
public interface IHidDeviceProvider
{
    /// <summary>Dispositivos HID conectados com o VID/PID pedido, na ordem da enumeração da plataforma.</summary>
    /// <param name="vendorId">Vendor ID USB.</param>
    /// <param name="productId">Product ID USB.</param>
    IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId);

    /// <summary>Abre o dispositivo para leitura/escrita. Lança <see cref="HidException"/> em caso de falha (permissão, desconectado...).</summary>
    IHidDevice Open(HidDeviceInfo device);
}

/// <summary>
/// <see cref="IHidDeviceProvider"/> que identifica um dispositivo pelo caminho, consultando o próprio nó, sem
/// depender da enumeração. O <see cref="Bu86xLink.FindDevice"/> recorre a ele quando o caminho configurado não
/// aparece na enumeração (no Linux: um nó com outro nome num contêiner ou sem o <c>/sys/class/hidraw</c> visível).
/// </summary>
public interface IHidDevicePathProbe
{
    /// <summary>
    /// Identifica o dispositivo em <paramref name="devicePath"/>, que pode ser aberto depois com
    /// <see cref="IHidDeviceProvider.Open(HidDeviceInfo)"/>. Devolve <see langword="null"/> se o caminho não existir
    /// ou não for um dispositivo HID.
    /// </summary>
    /// <exception cref="HidException">O caminho existe mas não pôde ser consultado (permissão, por exemplo).</exception>
    HidDeviceInfo? Probe(string devicePath);
}

/// <summary>Falha de E/S ou de acesso a um dispositivo HID.</summary>
public class HidException(string message, Exception? inner = null, int? nativeError = null) : IOException(message, inner)
{
    /// <summary>errno (Linux) ou GetLastError (Windows), quando disponível.</summary>
    public int? NativeError { get; } = nativeError;
}
