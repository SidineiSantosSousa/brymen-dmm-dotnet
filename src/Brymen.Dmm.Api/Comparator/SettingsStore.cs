using System.Text.Json;
using System.Text.Json.Serialization;
using Brymen.Dmm.Api.Configuration;

namespace Brymen.Dmm.Api.Comparator;

/// <summary>Conteúdo de <c>&lt;DataDirectory&gt;/settings.json</c> (equivale ao <c>BTCDL.ini</c> do original).</summary>
public sealed record PersistedSettings
{
    public ComparatorSettings? Comparator { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(PersistedSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Leitura e gravação atômica de <c>settings.json</c>.</summary>
internal sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile PersistedSettings _current;

    public SettingsStore(DataDirectory dataDirectory, ILogger<SettingsStore> logger)
    {
        _path = dataDirectory.SettingsFile;
        _logger = logger;
        _current = Load();
    }

    public PersistedSettings Current => _current;

    public string FilePath => _path;

    /// <summary>
    /// Aplica <paramref name="update"/> ao conteúdo atual e grava o resultado. A leitura do atual e a gravação ficam sob o
    /// mesmo gate: duas atualizações parciais concorrentes não se sobrescrevem. Null em <paramref name="update"/> = nada
    /// a gravar (por exemplo, valor inválido).
    /// </summary>
    public async Task UpdateAsync(Func<PersistedSettings, PersistedSettings?> update, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (update(_current) is not { } next)
                return;
            // Grava num temporário, força o conteúdo para o disco e troca: uma queda de energia (Raspberry Pi com
            // cartão SD) não deixa um settings.json vazio ou pela metade.
            string temp = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, next, SettingsJsonContext.Default.PersistedSettings, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            _current = next;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Não foi possível apagar {Path}.", path);
        }
    }

    private PersistedSettings Load()
    {
        if (!File.Exists(_path))
            return new PersistedSettings();
        try
        {
            using var stream = File.OpenRead(_path);
            var settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.PersistedSettings) ?? new PersistedSettings();
            if (settings.Comparator?.Validate() is { } error)
            {
                _logger.LogWarning("Comparador inválido em {Path} ({Message}); usando os padrões.", _path, error.Message);
                settings = settings with { Comparator = null };
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Não foi possível ler {Path}; usando os padrões.", _path);
            return new PersistedSettings();
        }
    }
}
