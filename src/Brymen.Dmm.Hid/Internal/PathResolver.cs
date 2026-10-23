namespace Brymen.Dmm.Hid;

internal static class PathResolver
{
    private const int MaxLinks = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Equivalente gerenciado de <c>realpath(3)</c>: resolve os links simbólicos componente a componente e
    /// trata <c>..</c> fisicamente. É necessário no sysfs, em que <c>class/hidraw/hidrawN</c> e <c>device</c> são
    /// links relativos e <see cref="Path.GetFullPath(string)"/> resolveria <c>..</c> de forma léxica (errada).
    /// Componentes inexistentes são mantidos como estão.
    /// </summary>
    public static string GetRealPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Sem Path.GetFullPath: ele colapsaria "link/.." lexicamente antes de o link ser resolvido.
        string full = Path.IsPathRooted(path) ? path : Path.Join(Environment.CurrentDirectory, path);
        string resolved = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Stack<string>();
        PushComponents(pending, full[resolved.Length..]);

        int links = 0;
        while (pending.TryPop(out var part))
        {
            if (part is "." or "")
                continue;
            if (part == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            string candidate = Path.Join(resolved, part);
            string? target = GetLinkTarget(candidate);
            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (++links > MaxLinks)
                throw new IOException($"Excesso de links simbólicos ao resolver {path}.");
            if (Path.IsPathRooted(target))
            {
                resolved = Path.GetPathRoot(target) ?? string.Empty;
                target = target[resolved.Length..];
            }
            PushComponents(pending, target);
        }
        return resolved;
    }

    /// <summary>Como <see cref="GetRealPath(string)"/>, mas devolve o caminho original em caso de erro de E/S.</summary>
    public static string TryGetRealPath(string path)
    {
        try
        {
            return GetRealPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static void PushComponents(Stack<string> pending, string relative)
    {
        var parts = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
            pending.Push(parts[i]);
    }

    private static string? GetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
