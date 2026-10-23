using System.Runtime.InteropServices;

namespace Brymen.Dmm.Hid;

/// <summary>Seleção do transporte HID nativo da plataforma.</summary>
public static class HidDeviceProviders
{
    /// <summary>Linux → hidraw; Windows → hid.dll. Lança <see cref="PlatformNotSupportedException"/> nos demais sistemas.</summary>
    public static IHidDeviceProvider CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsLinux())
            return new LinuxHidrawDeviceProvider();
        if (OperatingSystem.IsWindows())
            return new WindowsHidDeviceProvider();
        throw new PlatformNotSupportedException(
            $"Transporte HID não suportado em {RuntimeInformation.OSDescription}: use Linux (hidraw) ou Windows, ou o transporte simulado.");
    }
}
