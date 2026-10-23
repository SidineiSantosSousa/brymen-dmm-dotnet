using System.Globalization;
using System.Text.RegularExpressions;

namespace Brymen.Dmm.Api.Acquisition;

/// <summary>
/// Backup contínuo da aquisição, equivalente ao <c>~BDMDLn.TMP</c> do original: um arquivo por gravação iniciada,
/// com cada amostra acrescentada e descarregada no disco na hora. Usa o CSV estendido, que carrega o timestamp.
/// Ficam no máximo <c>Acquisition:BackupRetention</c> arquivos; os de menor número (os mais antigos) são apagados.
/// </summary>
internal sealed partial class CsvBackupWriter : IDisposable
{
    private readonly StreamWriter _writer;

    private CsvBackupWriter(string path, StreamWriter writer)
    {
        FilePath = path;
        _writer = writer;
    }

    public string FilePath { get; }

    /// <summary>
    /// Cria <c>~BDMDL&lt;n&gt;.csv</c> com o próximo n (o original sobrescrevia um só arquivo) e depois apaga os
    /// backups mais antigos até restarem <paramref name="retention"/> arquivos, contando o novo.
    /// </summary>
    public static CsvBackupWriter Create(string directory, int retention, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, 1);
        Directory.CreateDirectory(directory);
        var existing = new List<(int Number, string Path)>();
        foreach (var file in Directory.EnumerateFiles(directory, "~BDMDL*.csv"))
        {
            var match = BackupName().Match(Path.GetFileName(file));
            if (match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                existing.Add((n, file));
        }
        int next = existing.Count == 0 ? 1 : existing.Max(e => e.Number) + 1;
        string path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"~BDMDL{next}.csv"));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, CsvFormat.Encoding) { NewLine = CsvFormat.NewLine, AutoFlush = true };
        writer.WriteLine(AcquisitionCsv.ExtendedHeader);

        // Só depois de criar o novo: uma falha na criação não apaga backup nenhum.
        existing.Sort((a, b) => a.Number.CompareTo(b.Number));
        for (int i = 0; i < existing.Count + 1 - retention; i++)
            Delete(existing[i].Path, logger);
        return new CsvBackupWriter(path, writer);
    }

    private static void Delete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
            logger.LogInformation("Backup antigo apagado pela retenção: {Path}.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Não foi possível apagar o backup antigo {Path}.", path);
        }
    }

    public void Append(AcquisitionSample sample) => _writer.WriteLine(AcquisitionCsv.Row(sample, extended: true));

    public void Dispose() => _writer.Dispose();

    [GeneratedRegex(@"^~BDMDL(\d{1,9})\.csv$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}
