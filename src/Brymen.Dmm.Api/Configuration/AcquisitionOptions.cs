using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Configuration;

/// <summary>Seção <c>Acquisition</c> do appsettings (Graphical Recorder / Data List).</summary>
public sealed class AcquisitionOptions
{
    public const string SectionName = "Acquisition";

    /// <summary>Capacidade do buffer em memória; ao enchê-lo, a gravação para.</summary>
    public int MaxSamples { get; set; } = 1_000_000;

    /// <summary>Pasta do backup contínuo em CSV (equivale ao <c>~BDMDLn.TMP</c>); null usa <c>&lt;DataDirectory&gt;/backup</c>.</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Grava cada amostra também no backup em disco.</summary>
    public bool BackupEnabled { get; set; } = true;

    /// <summary>Número máximo de arquivos <c>~BDMDLn.csv</c> guardados; ao criar um novo, os mais antigos são apagados.</summary>
    public int BackupRetention { get; set; } = 20;
}

internal sealed class AcquisitionOptionsValidator : IValidateOptions<AcquisitionOptions>
{
    public const int MaxSamplesLimit = 100_000_000;
    public const int MaxBackupRetention = 100_000;

    public ValidateOptionsResult Validate(string? name, AcquisitionOptions options)
    {
        List<string> errors = [];
        if (options.MaxSamples is < 1 or > MaxSamplesLimit)
            errors.Add($"Acquisition:MaxSamples deve estar entre 1 e {MaxSamplesLimit} (valor: {options.MaxSamples}).");
        if (options.BackupRetention is < 1 or > MaxBackupRetention)
            errors.Add($"Acquisition:BackupRetention deve estar entre 1 e {MaxBackupRetention} (valor: {options.BackupRetention}).");
        if (options.BackupDirectory is { } dir && dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            errors.Add("Acquisition:BackupDirectory não é um caminho válido.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
