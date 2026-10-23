namespace Brymen.Dmm.Hid.Tests;

/// <summary>
/// Teste com o cabo BC-86X real pelo transporte nativo (hidraw no Linux, hid.dll no Windows): só roda com
/// <c>BRYMEN_HW_TESTS=1</c>; caso contrário, é pulado com o motivo. No Linux o usuário precisa de acesso ao
/// /dev/hidrawN do cabo (regra udev deploy/linux/99-brymen-bu86x.rules).
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

/// <summary>Como <see cref="HardwareFactAttribute"/>, só no Linux (hidraw).</summary>
public sealed class LinuxHardwareFactAttribute : FactAttribute
{
    public LinuxHardwareFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Teste de hardware do BC-86X pelo hidraw: só roda no Linux.";
        else if (Environment.GetEnvironmentVariable("BRYMEN_HW_TESTS") != "1")
            Skip = "Teste de hardware do BC-86X desativado: defina BRYMEN_HW_TESTS=1, com o cabo conectado e o medidor ligado.";
    }
}

/// <summary>Teste que precisa criar links simbólicos (no Windows exige Modo de Desenvolvedor ou privilégio); pulado se não for possível.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(Probe);

    public SymlinkFactAttribute()
    {
        if (!Supported.Value)
            Skip = "Este sistema não permite criar links simbólicos para a árvore sysfs falsa.";
    }

    private static bool Probe()
    {
        using var temp = new TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, "target"));
            Directory.CreateSymbolicLink(Path.Combine(temp.Path, "link"), "target");
            return Directory.Exists(Path.Combine(temp.Path, "link"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "brymen-hid-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            // Links são apagados sem seguir o alvo.
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestData
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>Linhas "&lt;cmd&gt; &lt;hex&gt;" de um arquivo de captura (comentários e linhas vazias ignorados).</summary>
    public static IReadOnlyList<(string Command, byte[] Frame)> ReadCapture(string name) =>
    [
        .. File.ReadLines(PathOf(name))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && !p[0].StartsWith('#'))
            .Select(p => (p[0], Convert.FromHexString(p[1]))),
    ];

    /// <summary>Checksum do bloco de memória: soma dos bytes 1..27 (LE nos bytes 29 e 30).</summary>
    public static bool HasValidChecksum(byte[] frame)
    {
        int sum = 0;
        for (int i = 0; i < 27; i++)
            sum += frame[i];
        return (ushort)sum == (frame[28] | frame[29] << 8);
    }
}

/// <summary>Decorador que registra a sequência de operações concluídas, para verificar a serialização do <see cref="Bu86xLink"/>.</summary>
internal sealed class RecordingHidDevice(IHidDevice inner) : IHidDevice
{
    private readonly Lock _lock = new();
    private readonly List<string> _events = [];
    private readonly List<TimeSpan> _readTimeouts = [];
    private int _active;
    private int _maxActive;
    private int _disposeCount;

    public HidDeviceInfo Info => inner.Info;

    public int InputReportLength => inner.InputReportLength;

    public int OutputReportLength => inner.OutputReportLength;

    public IReadOnlyList<string> Events
    {
        get { lock (_lock) return [.. _events]; }
    }

    /// <summary>Timeout pedido em cada leitura, inclusive nas que expiraram (descarte).</summary>
    public IReadOnlyList<TimeSpan> ReadTimeouts
    {
        get { lock (_lock) return [.. _readTimeouts]; }
    }

    /// <summary>Maior número de operações simultâneas observado.</summary>
    public int MaxConcurrentOperations => Volatile.Read(ref _maxActive);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask WriteReportAsync(byte reportId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            await inner.WriteReportAsync(reportId, data, cancellationToken);
            Record($"W{data.Span[2]:X2}");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async ValueTask<int> ReadReportAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Enter();
        lock (_lock)
            _readTimeouts.Add(timeout);
        try
        {
            int n = await inner.ReadReportAsync(buffer, timeout, cancellationToken);
            Record($"R{buffer.Span[0]:X2}");
            return n;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        inner.Dispose();
    }

    private void Enter()
    {
        int active = Interlocked.Increment(ref _active);
        int max;
        while (active > (max = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, max) != max)
        {
        }
    }

    private void Record(string e)
    {
        lock (_lock)
            _events.Add(e);
    }
}

internal static class TestWait
{
    /// <summary>Espera <paramref name="condition"/> ficar verdadeira, verificando a cada 5 ms; falha depois de <paramref name="timeout"/> (padrão 5 s).</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit)
                throw new TimeoutException($"Tempo esgotado esperando {what}.");
            await Task.Delay(5);
        }
    }
}
