namespace Brymen.Dmm.Api.Configuration;

/// <summary>
/// Pasta de dados do serviço (chave <c>DataDirectory</c>; padrão <c>&lt;ContentRoot&gt;/data</c>): <c>settings.json</c>
/// (equivale ao <c>BTCDL.ini</c>), imagens brutas das importações e backup da aquisição.
/// </summary>
public sealed class DataDirectory
{
    public const string ConfigurationKey = "DataDirectory";

    public DataDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (root.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException($"DataDirectory não é um caminho válido: '{root}'.", nameof(root));
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ImportsDirectory => Path.Combine(Root, "imports");

    public string DefaultBackupDirectory => Path.Combine(Root, "backup");

    /// <summary>Resolve um caminho configurado: relativo à raiz do conteúdo ou absoluto.</summary>
    public static string Resolve(string path, string contentRoot) => Path.GetFullPath(path, contentRoot);

    public static DataDirectory FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        string? configured = configuration[ConfigurationKey];
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : Resolve(configured, environment.ContentRootPath);
        return new DataDirectory(root);
    }
}
