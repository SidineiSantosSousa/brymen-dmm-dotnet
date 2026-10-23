using System.Net;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Importações guardadas limitadas por <c>Logger:ImportRetention</c>, na partida e a cada importação nova.</summary>
public sealed class LoggerRetentionTests
{
    private class RetentionFactory : ApiFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings => [.. base.Settings, new("Logger:ImportRetention", "2")];
    }

    /// <summary>
    /// Fotografa as importações (id e estado, na ordem da lista) a cada log "Importação ..." do
    /// <see cref="LoggerImportService"/>, na hora e na thread em que ele é gravado: o "apagada" sai da própria retenção, e o
    /// "concluída", logo depois do estado <c>completed</c>.
    /// </summary>
    private sealed class LogSnapshotFactory : RetentionFactory
    {
        private readonly Lock _lock = new();
        private readonly List<(string Message, (string Id, ImportJobState State)[] Jobs)> _snapshots = [];

        public (string Message, (string Id, ImportJobState State)[] Jobs)[] Snapshots
        {
            get { lock (_lock) return [.. _snapshots]; }
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.AddSingleton<ILoggerProvider>(new SnapshotLogger(this));
            services.AddLogging(logging => logging.AddFilter<SnapshotLogger>(typeof(LoggerImportService).FullName, LogLevel.Information));
        }

        private void OnLog(string message)
        {
            // O prefixo vem antes do Services: os logs da partida ("Imagem ... apagada") saem durante a construção do host.
            if (!message.StartsWith("Importação ", StringComparison.Ordinal))
                return;
            (string, ImportJobState)[] jobs = [.. Services.GetRequiredService<LoggerImportService>().List().Select(j => (j.Id, j.GetSnapshot().State))];
            lock (_lock)
                _snapshots.Add((message, jobs));
        }

        private sealed class SnapshotLogger(LogSnapshotFactory factory) : ILoggerProvider, ILogger
        {
            public ILogger CreateLogger(string categoryName) => this;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => factory.OnLog(formatter(state, exception));

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task Only_the_newest_imports_are_kept_on_disk_and_in_memory()
    {
        await using var factory = new RetentionFactory();
        // Três imagens de execuções anteriores (ilegíveis: viram jobs "failed") e um temporário de uma gravação interrompida.
        string imports = Path.Combine(factory.DataDirectory, "imports");
        string[] ids = await WriteOldImagesAsync(imports, 3);
        string partial = Path.Combine(imports, new string('f', 32) + ".bin.tmp");
        await File.WriteAllBytesAsync(partial, [0x01]);

        var client = factory.CreateClient();

        // Partida: só as duas mais novas são carregadas; a mais antiga e o temporário saem do disco.
        Assert.Equal([ids[2], ids[1]], await ListAsync(client));
        Assert.False(File.Exists(Path.Combine(imports, ids[0] + ".bin")));
        Assert.False(File.Exists(partial));

        // Uma importação nova empurra para fora a mais antiga das que restaram, antes de aparecer como "completed".
        string id = await ImportAsync(client);

        Assert.Equal([id, ids[2]], await ListAsync(client));
        Assert.False(File.Exists(Path.Combine(imports, ids[1] + ".bin")));
        Assert.Equal(
            new[] { ids[2] + ".bin", id + ".bin" }.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(imports).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        using var gone = await client.GetAsync($"/api/v1/logger/imports/{ids[1]}");
        await TestHelpers.AssertProblemAsync(gone, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    /// <summary>
    /// A retenção vem antes do estado final: quando a importação mais antiga é apagada, a nova ainda está
    /// <c>running</c>, e quando a nova fica <c>completed</c>, a lista já foi podada. Com a ordem antiga (<c>Complete</c> e,
    /// só no <c>finally</c>, a retenção), quem visse <c>completed</c> e listasse logo em seguida podia encontrar uma
    /// importação a mais, e o teste acima falhava de vez em quando no Orange Pi 5.
    /// </summary>
    [Fact]
    public async Task The_retention_runs_before_the_import_becomes_completed()
    {
        await using var factory = new LogSnapshotFactory();
        string[] ids = await WriteOldImagesAsync(Path.Combine(factory.DataDirectory, "imports"), 2);
        var client = factory.CreateClient();
        Assert.Equal([ids[1], ids[0]], await ListAsync(client));

        string id = await ImportAsync(client);

        var snapshots = factory.Snapshots;
        var pruned = Assert.Single(snapshots, s => s.Message.Contains(" apagada", StringComparison.Ordinal));
        Assert.StartsWith($"Importação {ids[0]} apagada", pruned.Message, StringComparison.Ordinal);
        Assert.Equal([(id, ImportJobState.Running), (ids[1], ImportJobState.Failed)], pruned.Jobs);
        var completed = Assert.Single(snapshots, s => s.Message.Contains(" concluída", StringComparison.Ordinal));
        Assert.Equal([(id, ImportJobState.Completed), (ids[1], ImportJobState.Failed)], completed.Jobs);
    }

    // Imagens ilegíveis de execuções anteriores (viram jobs "failed"), da mais antiga para a mais nova.
    private static async Task<string[]> WriteOldImagesAsync(string imports, int count)
    {
        Directory.CreateDirectory(imports);
        string[] ids = [.. Enumerable.Range(1, count).Select(i => new string((char)('a' + i), 32))];
        for (int i = 0; i < ids.Length; i++)
        {
            string path = Path.Combine(imports, ids[i] + ".bin");
            await File.WriteAllBytesAsync(path, [0x01, 0x02, 0x03]);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }
        return ids;
    }

    private static async Task<string> ImportAsync(HttpClient client)
    {
        await TestHelpers.WaitForReadingAsync(client);
        using var post = await client.PostAsync("/api/v1/logger/imports", null);
        string id = (await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted)).GetProperty("id").GetString()!;
        await TestHelpers.EventuallyAsync(async () =>
            (await client.GetJsonAsync($"/api/v1/logger/imports/{id}")).GetProperty("state").GetString() == "completed" ? "ok" : null,
            "o fim da importação");
        return id;
    }

    private static async Task<string[]> ListAsync(HttpClient client)
        => [.. (await client.GetJsonAsync("/api/v1/logger/imports")).EnumerateArray().Select(j => j.GetProperty("id").GetString()!)];
}
