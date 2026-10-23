using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static Brymen.Dmm.Hid.WindowsNative;

namespace Brymen.Dmm.Hid;

/// <summary>
/// Dispositivos HID do Windows: enumeração pela SetupAPI (interfaces da classe HID) e E/S overlapped pela hid.dll.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHidDeviceProvider : IHidDeviceProvider
{
    // Strings USB têm no máximo 126 caracteres UTF-16; o buffer inclui o terminador.
    private const int StringBufferChars = 256;

    /// <inheritdoc/>
    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId)
    {
        var found = new List<HidDeviceInfo>();
        foreach (string path in EnumerateInterfacePaths())
        {
            // Acesso 0 basta para atributos e strings e funciona mesmo com teclados e mouses abertos em modo exclusivo.
            using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
                continue;
            if (TryGetAttributes(handle, out var attributes)
                && attributes.VendorId == vendorId
                && attributes.ProductId == productId)
            {
                found.Add(CreateInfo(path, handle, attributes));
            }
        }
        return found;
    }

    /// <inheritdoc/>
    public IHidDevice Open(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return WindowsHidDevice.Open(device);
    }

    internal static HidDeviceInfo CreateInfo(string path, SafeFileHandle handle, HiddAttributes attributes) => new(
        path,
        attributes.VendorId,
        attributes.ProductId,
        attributes.VersionNumber,
        GetString(handle, HidString.Manufacturer),
        GetString(handle, HidString.Product),
        GetString(handle, HidString.SerialNumber));

    internal static unsafe bool TryGetAttributes(SafeFileHandle handle, out HiddAttributes attributes)
    {
        attributes = new HiddAttributes { Size = (uint)sizeof(HiddAttributes) };
        return HidD_GetAttributes(handle, ref attributes);
    }

    private static unsafe List<string> EnumerateInterfacePaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        nint set = SetupDiGetClassDevs(ref hidGuid, 0, 0, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandleValue)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new HidException($"SetupDiGetClassDevs falhou (erro {error}).", nativeError: error);
        }

        var paths = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)sizeof(SpDeviceInterfaceData) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref data))
                    break; // ERROR_NO_MORE_ITEMS ou falha: em ambos os casos não há mais interfaces legíveis.
                if (GetInterfacePath(set, ref data) is { } path)
                    paths.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    private static unsafe string? GetInterfacePath(nint set, ref SpDeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, null, 0, out uint required, 0);
        if (required < sizeof(uint) + sizeof(char))
            return null;

        var buffer = new byte[required + sizeof(char)];
        fixed (byte* p = buffer)
        {
            *(uint*)p = DeviceInterfaceDetailDataCbSize;
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, p, required, out _, 0))
                return null;
            var chars = new ReadOnlySpan<char>(p + sizeof(uint), (buffer.Length - sizeof(uint)) / sizeof(char));
            int nul = chars.IndexOf('\0');
            return nul > 0 ? new string(chars[..nul]) : null;
        }
    }

    internal enum HidString { Manufacturer, Product, SerialNumber }

    internal static unsafe string? GetString(SafeFileHandle handle, HidString kind)
    {
        char* buffer = stackalloc char[StringBufferChars];
        new Span<char>(buffer, StringBufferChars).Clear();
        const uint bytes = StringBufferChars * sizeof(char);
        bool ok = kind switch
        {
            HidString.Manufacturer => HidD_GetManufacturerString(handle, buffer, bytes),
            HidString.Product => HidD_GetProductString(handle, buffer, bytes),
            _ => HidD_GetSerialNumberString(handle, buffer, bytes),
        };
        return ok ? HidStrings.Clean(new ReadOnlySpan<char>(buffer, StringBufferChars)) : null;
    }
}
