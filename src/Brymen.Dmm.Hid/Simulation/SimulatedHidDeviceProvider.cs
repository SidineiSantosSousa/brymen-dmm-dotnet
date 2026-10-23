namespace Brymen.Dmm.Hid;

/// <summary>Provider em memória que expõe um ou mais <see cref="SimulatedHidDevice"/>.</summary>
public sealed class SimulatedHidDeviceProvider : IHidDeviceProvider
{
    /// <summary>
    /// Um BC-86X simulado em <see cref="SimulatedHidDevice.DefaultInfo"/>, o mesmo do <c>BrymenMeter.OpenSimulatedAsync</c>:
    /// o BM52x de <see cref="SimulatedBm52xMeter.FromCaptures"/> (as capturas reais e o <c>8A</c> que avança como um
    /// <c>89</c> logo depois do <c>88</c>), com <see cref="SimulatedHidDevice.TypicalResponseLatency"/> por resposta. Sem
    /// nenhum cabo (<c>E9015</c>), passe uma coleção vazia: <c>new SimulatedHidDeviceProvider([])</c>.
    /// </summary>
    public SimulatedHidDeviceProvider()
        : this(SimulatedHidDevice.CreateTypicalBm52x())
    {
    }

    /// <summary>Provider com os dispositivos simulados <paramref name="devices"/> (nenhum = nenhum cabo conectado).</summary>
    /// <param name="devices">Dispositivos, cada um com um caminho diferente.</param>
    /// <exception cref="ArgumentException">Dois dispositivos com o mesmo caminho.</exception>
    public SimulatedHidDeviceProvider(params IEnumerable<SimulatedHidDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        Devices = [.. devices];
        var duplicated = Devices.GroupBy(d => d.Info.Path, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
            throw new ArgumentException($"Caminho de dispositivo simulado repetido: {duplicated.Key}.", nameof(devices));
    }

    /// <summary>Os dispositivos simulados, conectados ou não.</summary>
    public IReadOnlyList<SimulatedHidDevice> Devices { get; }

    /// <summary>Dispositivos conectados (veja <see cref="SimulatedHidDevice.Disconnect"/>) com o VID/PID pedido.</summary>
    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) =>
        [.. Devices.Where(d => d.IsConnected && d.Info.VendorId == vendorId && d.Info.ProductId == productId).Select(d => d.Info)];

    /// <summary>Reabre o dispositivo simulado (fila vazia) e o devolve; abrir de novo um handle aberto devolve a mesma instância.</summary>
    public IHidDevice Open(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var simulated = Devices.FirstOrDefault(d => string.Equals(d.Info.Path, device.Path, StringComparison.Ordinal))
            ?? throw new HidException($"Dispositivo simulado {device.Path} não encontrado.");
        simulated.Reopen();
        return simulated;
    }
}
