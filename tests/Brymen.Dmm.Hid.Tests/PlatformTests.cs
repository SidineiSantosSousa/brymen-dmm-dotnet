namespace Brymen.Dmm.Hid.Tests;

public class PlatformTests
{
    [Fact]
    public void CreateForCurrentPlatformPicksTheNativeTransport()
    {
        if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxHidrawDeviceProvider>(HidDeviceProviders.CreateForCurrentPlatform());
        else if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsHidDeviceProvider>(HidDeviceProviders.CreateForCurrentPlatform());
        else
            Assert.Throws<PlatformNotSupportedException>(() => HidDeviceProviders.CreateForCurrentPlatform());
    }

    [Fact]
    public void PermissionDeniedPointsToTheUdevRule()
    {
        var ex = Assert.IsType<HidException>(HidrawErrors.ForOpen(HidrawErrors.EACCES, "/dev/hidraw0"));

        Assert.Contains("deploy/linux/99-brymen-bu86x.rules", ex.Message);
        Assert.Contains("EACCES", ex.Message);
        Assert.Equal(HidrawErrors.EACCES, ex.NativeError);
    }

    [Fact]
    public void DeviceCgroupDenialPointsToTheServiceRestart()
    {
        // EPERM é o controlador de dispositivos do systemd (DeviceAllow resolvido sem o hidraw em /proc/devices).
        var ex = Assert.IsType<HidException>(HidrawErrors.ForOpen(HidrawErrors.EPERM, "/dev/hidraw0"));

        Assert.Contains("EPERM", ex.Message);
        Assert.Contains("/proc/devices", ex.Message);
        Assert.Contains("systemctl restart brymen-dmm", ex.Message);
        Assert.Contains("/etc/modules-load.d/brymen-dmm.conf", ex.Message);
        Assert.Equal(HidrawErrors.EPERM, ex.NativeError);
    }

    [Theory]
    [InlineData(HidrawErrors.ENODEV)]
    [InlineData(HidrawErrors.EIO)]
    [InlineData(HidrawErrors.ESHUTDOWN)]
    public void DeviceGoneMeansDisconnected(int errno)
    {
        var ex = Assert.IsType<HidException>(HidrawErrors.ForIo(errno, "read", "/dev/hidraw0"));

        Assert.Contains("desconectado", ex.Message);
        Assert.Equal(errno, ex.NativeError);
    }

    [Fact]
    public void OtherErrnoValuesAreReported()
    {
        Assert.IsType<TimeoutException>(HidrawErrors.ForIo(HidrawErrors.ETIMEDOUT, "write", "/dev/hidraw0"));
        var ex = Assert.IsType<HidException>(HidrawErrors.ForIo(HidrawErrors.EINVAL, "write", "/dev/hidraw0"));
        Assert.Contains("EINVAL", ex.Message);
        Assert.Contains("errno 99", HidrawErrors.ForOpen(99, "/dev/hidraw0").Message);
        Assert.Contains("não encontrado", HidrawErrors.ForOpen(HidrawErrors.ENOENT, "/dev/hidraw0").Message);
    }

    [Fact]
    public void CleanNormalizesDescriptorStrings()
    {
        Assert.Equal("Brymen", HidStrings.Clean("Brymen\0lixo"));
        Assert.Equal("£⑉", HidStrings.Clean("£⑉\n"));
        Assert.Equal("a�b", HidStrings.Clean("a\uD800b"));
        Assert.Equal("\U0001F600", HidStrings.Clean("\U0001F600"));
        Assert.Null(HidStrings.Clean(" \r\n\0"));
    }

    [SymlinkFact]
    public void RealPathResolvesDotDotPhysicallyAfterLinks()
    {
        using var temp = new TempDirectory();
        string deep = Path.Combine(temp.Path, "a", "b", "c");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(temp.Path, "x"));
        // x/link → ../a/b/c; x/link/.. deve ser a/b, não x.
        Directory.CreateSymbolicLink(Path.Combine(temp.Path, "x", "link"), Path.Combine("..", "a", "b", "c"));

        string real = PathResolver.GetRealPath(Path.Combine(temp.Path, "x", "link", "..", "c"));

        Assert.Equal(PathResolver.GetRealPath(deep), real);
        Assert.EndsWith(Path.Combine("a", "b", "c"), real);
    }

    [Fact]
    public void RealPathKeepsMissingComponents()
    {
        using var temp = new TempDirectory();
        string missing = Path.Combine(temp.Path, "nao", "existe");

        Assert.Equal(Path.GetFullPath(missing), PathResolver.GetRealPath(missing));
    }
}
