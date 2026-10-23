using System.Net;
using System.Text.Json;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Importação do datalogger (88h + N×89h, checksum, 8Ah e recomeço) com a memória real do dump-1.</summary>
public sealed class LoggerImportTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OriginalHeader = "#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc";
    private const int DumpBlocks = 95;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Responder.FaultInjector = null;
        await TestHelpers.WaitForReadingAsync(_client);
    }

    public Task DisposeAsync()
    {
        factory.Responder.FaultInjector = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Import_reads_one_session_with_375_samples()
    {
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            return SimulatedFault.None;
        };

        using var post = await _client.PostAsync("/api/v1/logger/imports", null);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
        string id = created.GetProperty("id").GetString()!;
        Assert.Equal($"/api/v1/logger/imports/{id}", post.Headers.Location?.OriginalString);

        var job = await WaitForJobAsync(id);
        factory.Responder.FaultInjector = null;

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(1, job.GetProperty("sessionCount").GetInt32());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, job.GetProperty("modelId").GetInt32());
        var progress = job.GetProperty("progress");
        Assert.Equal(DumpBlocks, progress.GetProperty("blocksRead").GetInt32());
        Assert.Equal(DumpBlocks, progress.GetProperty("blocksTotal").GetInt32());
        Assert.Equal(2272, progress.GetProperty("totalBytes").GetInt32());
        Assert.Equal(100, progress.GetProperty("percent").GetDouble());
        Assert.Equal(0, progress.GetProperty("retries").GetInt32());
        Assert.Equal(0, progress.GetProperty("restarts").GetInt32());

        // Polling pausado: entre o 88h e o último 89h não houve nenhum 66h; exatamente 1 × 88h + 94 × 89h.
        byte[] sequence;
        lock (commands)
            sequence = [.. commands];
        int head = Array.IndexOf(sequence, Bm52xCommands.MemoryHead);
        int lastNext = Array.LastIndexOf(sequence, Bm52xCommands.MemoryNext);
        Assert.True(head >= 0 && lastNext > head);
        var during = sequence[head..(lastNext + 1)];
        Assert.DoesNotContain(Bm52xCommands.RealTime, during);
        Assert.Equal(DumpBlocks - 1, during.Count(c => c == Bm52xCommands.MemoryNext));
        Assert.Single(sequence, c => c == Bm52xCommands.MemoryHead);
        Assert.DoesNotContain(Bm52xCommands.MemoryCurrent, sequence);

        // Sessões.
        var sessions = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}/sessions");
        var session = Assert.Single(sessions.EnumerateArray());
        Assert.Equal(1, session.GetProperty("number").GetInt32());
        Assert.Equal("A0_0080", session.GetProperty("headAddress").GetString());
        Assert.True(session.GetProperty("isDual").GetBoolean());
        Assert.Equal(0.05, session.GetProperty("intervalSeconds").GetDouble(), 9);
        Assert.Equal("ACV", session.GetProperty("mainFunction").GetString());
        Assert.Equal("Hz", session.GetProperty("secondaryFunction").GetString());
        Assert.Equal(375, session.GetProperty("sampleCount").GetInt32());
        Assert.Equal(2250, session.GetProperty("lengthBytes").GetInt32());

        // Amostras paginadas.
        var page = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}/sessions/1/samples?offset=0&limit=1000");
        Assert.Equal(375, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(375, items.Count);
        Assert.Equal(1, items[0].GetProperty("item").GetInt32());
        Assert.Equal("0.013", items[0].GetProperty("main").GetProperty("text").GetString());
        Assert.Equal("V", items[0].GetProperty("main").GetProperty("unit").GetString());
        Assert.Equal("AC", items[0].GetProperty("main").GetProperty("mode").GetString());
        Assert.Equal("0.000", items[0].GetProperty("secondary").GetProperty("text").GetString());
        Assert.Equal("0.012", items[22].GetProperty("main").GetProperty("text").GetString());
        Assert.Equal("0.006", items[374].GetProperty("main").GetProperty("text").GetString());
        Assert.Equal(18.7, items[374].GetProperty("elapsedSeconds").GetDouble(), 6);

        var tail = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}/sessions/1/samples?offset=370&limit=10");
        Assert.Equal(5, tail.GetProperty("items").GetArrayLength());

        // CSV da sessão no formato do original.
        string csv = await _client.GetStringAsync($"/api/v1/logger/imports/{id}/sessions/1/samples.csv");
        string[] lines = csv[..^2].Split("\r\n");
        Assert.Equal(OriginalHeader, lines[0]);
        Assert.Equal(376, lines.Length);
        Assert.Equal("1,  0:00:00.0,0.013,V,AC,0.000,Hz,", lines[1]);
        Assert.Equal("375,  0:00:18.7,0.006,V,AC,0.000,Hz,", lines[375]);
        string extended = await _client.GetStringAsync($"/api/v1/logger/imports/{id}/sessions/1/samples.csv?format=extended");
        string[] extLines = extended[..^2].Split("\r\n");
        Assert.Equal(OriginalHeader + ",ElapsedSeconds,MainSiValue,SubSiValue,Flags", extLines[0]);
        string[] ext = extLines[1].Split(',');
        string[] expectedFields = ["1", "  0:00:00.0", "0.013", "V", "AC", "0.000", "Hz", "", "0"];
        Assert.Equal(expectedFields, ext[..9]);
        Assert.Equal(0.013, double.Parse(ext[9], System.Globalization.CultureInfo.InvariantCulture), 9);
        Assert.Equal("0", ext[10]);
        Assert.Equal("", ext[11]);
        Assert.Equal("18.7", extLines[375].Split(',')[8]);

        // Imagem bruta: 95 blocos de 24 bytes em DataDirectory/imports/{id}.bin.
        string bin = Path.Combine(factory.DataDirectory, "imports", id + ".bin");
        Assert.True(File.Exists(bin));
        byte[] image = await File.ReadAllBytesAsync(bin);
        Assert.Equal(DumpBlocks * 24, image.Length);
        Assert.Equal(ExpectedImage(), image);
        Assert.Equal(image, await _client.GetByteArrayAsync($"/api/v1/logger/imports/{id}/image"));

        // Listagem, 404 e polling de volta.
        var list = await _client.GetJsonAsync("/api/v1/logger/imports");
        Assert.Contains(id, list.EnumerateArray().Select(j => j.GetProperty("id").GetString()));
        using (var missing = await _client.GetAsync("/api/v1/logger/imports/0123456789abcdef0123456789abcdef"))
            await TestHelpers.AssertProblemAsync(missing, HttpStatusCode.NotFound, "NOT_FOUND");
        using (var noSession = await _client.GetAsync($"/api/v1/logger/imports/{id}/sessions/2/samples"))
            await TestHelpers.AssertProblemAsync(noSession, HttpStatusCode.NotFound, "NOT_FOUND");
        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.False(meter.GetProperty("importing").GetBoolean());
        long count = meter.GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > count);

        // Apagar remove o job e a imagem.
        using var delete = await _client.DeleteAsync($"/api/v1/logger/imports/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(File.Exists(bin));
    }

    [Fact]
    public async Task Bad_checksum_is_recovered_with_8A()
    {
        int nextCount = 0;
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            return command == Bm52xCommands.MemoryNext && Interlocked.Increment(ref nextCount) == 10
                ? SimulatedFault.CorruptChecksum
                : SimulatedFault.None;
        };

        var job = await ImportAsync();

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, job.GetProperty("progress").GetProperty("retries").GetInt32());
        Assert.Equal(0, job.GetProperty("progress").GetProperty("restarts").GetInt32());
        lock (commands)
            Assert.Single(commands, c => c == Bm52xCommands.MemoryCurrent);
        await AssertImageAsync(job);
    }

    [Fact]
    public async Task Truncated_answer_is_recovered_with_8A()
    {
        int nextCount = 0;
        factory.Responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.MemoryNext && Interlocked.Increment(ref nextCount) == 30 ? SimulatedFault.Truncate : SimulatedFault.None;

        var job = await ImportAsync();

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        Assert.Equal(0, job.GetProperty("progress").GetProperty("restarts").GetInt32());
        Assert.True(job.GetProperty("progress").GetProperty("retries").GetInt32() >= 1);
        await AssertImageAsync(job);
    }

    [Fact]
    public async Task Lost_89_without_any_report_restarts_from_88_instead_of_using_8A()
    {
        // O 89 se perde antes do medidor (ponteiro parado): um 8A aqui devolveria o bloco anterior.
        int nextCount = 0;
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            return command == Bm52xCommands.MemoryNext && Interlocked.Increment(ref nextCount) == 20 ? SimulatedFault.Drop : SimulatedFault.None;
        };

        var job = await ImportAsync();

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, job.GetProperty("progress").GetProperty("restarts").GetInt32());
        Assert.Contains(job.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("88h", StringComparison.Ordinal));
        lock (commands)
        {
            Assert.DoesNotContain(Bm52xCommands.MemoryCurrent, commands);
            Assert.Equal(2, commands.Count(c => c == Bm52xCommands.MemoryHead));
        }
        await AssertImageAsync(job);
    }

    [Theory]
    [InlineData(nameof(SimulatedFault.CorruptChecksum))]
    [InlineData(nameof(SimulatedFault.Truncate))]
    [InlineData(nameof(SimulatedFault.Drop))]
    public async Task Failed_88_is_retried_with_88_and_the_next_command_waits_200_ms(string faultName)
    {
        // O transporte Simulated reproduz o medidor real: um 8A logo depois do 88 avançaria como um 89.
        Assert.Equal(TimeSpan.FromMilliseconds(200), factory.Responder.CurrentAfterHeadWindow);
        var fault = Enum.Parse<SimulatedFault>(faultName);
        int heads = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var commands = new List<(byte Command, TimeSpan At)>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add((command, clock.Elapsed));
            return command == Bm52xCommands.MemoryHead && Interlocked.Increment(ref heads) == 1 ? fault : SimulatedFault.None;
        };

        var job = await ImportAsync();

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, job.GetProperty("progress").GetProperty("retries").GetInt32());
        Assert.Equal(0, job.GetProperty("progress").GetProperty("restarts").GetInt32());
        await AssertImageAsync(job);
        lock (commands)
        {
            var memory = commands.Where(c => c.Command != Bm52xCommands.RealTime).ToList();
            byte[] expected = [Bm52xCommands.MemoryHead, Bm52xCommands.MemoryHead, .. Enumerable.Repeat(Bm52xCommands.MemoryNext, DumpBlocks - 1)];
            Assert.Equal(expected, memory.Select(c => c.Command));
            // Depois de cada 88 (o que falhou e o que deu certo), 200 ms antes do comando seguinte.
            Assert.True(memory[1].At - memory[0].At >= TimeSpan.FromMilliseconds(195), $"88 → 88 em {memory[1].At - memory[0].At}");
            Assert.True(memory[2].At - memory[1].At >= TimeSpan.FromMilliseconds(195), $"88 → 89 em {memory[2].At - memory[1].At}");
        }
    }

    [Fact]
    public async Task Meter_that_stops_answering_fails_with_E9016()
    {
        int nextCount = 0;
        factory.Responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.MemoryNext && Interlocked.Increment(ref nextCount) > 3 ? SimulatedFault.Drop : SimulatedFault.None;

        var job = await ImportAsync();

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Equal("E9016", job.GetProperty("error").GetProperty("code").GetString());
        string id = job.GetProperty("id").GetString()!;
        using var sessions = await _client.GetAsync($"/api/v1/logger/imports/{id}/sessions");
        await TestHelpers.AssertProblemAsync(sessions, HttpStatusCode.Conflict, "CONFLICT");
    }

    [Fact]
    public async Task Cable_removed_during_the_import_fails_the_job_with_E9015()
    {
        // O cabo sai junto com o 10º 89: o leitor recomeça do 88 e a reabertura não acha o cabo.
        var device = ((SimulatedHidDeviceProvider)factory.Services.GetRequiredService<IHidDeviceProvider>()).Devices[0];
        int nextCount = 0;
        factory.Responder.FaultInjector = (command, _) =>
        {
            if (command != Bm52xCommands.MemoryNext || Interlocked.Increment(ref nextCount) != 10)
                return SimulatedFault.None;
            device.Disconnect();
            return SimulatedFault.Drop;
        };
        try
        {
            var job = await ImportAsync();

            Assert.Equal("failed", job.GetProperty("state").GetString());
            var error = job.GetProperty("error");
            Assert.Equal("E9015", error.GetProperty("code").GetString());
            Assert.Contains("não encontrado", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            device.Reconnect();
        }
        await TestHelpers.WaitForReadingAsync(_client);
    }

    [Fact]
    public async Task Second_import_while_running_is_a_conflict()
    {
        // Atrasa a leitura para manter o primeiro job em andamento.
        factory.Responder.FaultInjector = (command, _) =>
        {
            if (command == Bm52xCommands.MemoryNext)
                Thread.Sleep(20);
            return SimulatedFault.None;
        };

        using var first = await _client.PostAsync("/api/v1/logger/imports", null);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using (var second = await _client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(second, HttpStatusCode.Conflict, "CONFLICT");

        var running = await _client.GetJsonAsync(first.Headers.Location!.OriginalString);
        Assert.Equal("running", running.GetProperty("state").GetString());
        Assert.True((await _client.GetJsonAsync("/api/v1/meter")).GetProperty("importing").GetBoolean());
        using (var early = await _client.GetAsync(first.Headers.Location.OriginalString + "/sessions"))
            await TestHelpers.AssertProblemAsync(early, HttpStatusCode.Conflict, "CONFLICT");

        // DELETE durante a leitura cancela o job.
        using var cancel = await _client.DeleteAsync(first.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var canceled = await WaitForJobAsync(running.GetProperty("id").GetString()!);
        Assert.Equal("canceled", canceled.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Latest_after_an_import_does_not_report_a_fault_already_recovered_from()
    {
        // Um W8011 isolado, seguido de leituras boas: fica no histórico (lastError), mas já não é o estado atual.
        int corrupt = 1;
        factory.Responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Exchange(ref corrupt, 0) == 1 ? SimulatedFault.CorruptModel : SimulatedFault.None;
        await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await _client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "W8011" ? "ok" : null;
        }, "o W8011 registrado");
        long before = (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > before);

        // Depois da importação o medidor fica mudo: ainda não há leitura atual, e o motivo não pode ser o W8011 antigo.
        bool silent = false;
        factory.Responder.FaultInjector = (command, _) =>
        {
            if (command == Bm52xCommands.MemoryHead)
                Volatile.Write(ref silent, true);
            return command == Bm52xCommands.RealTime && Volatile.Read(ref silent) ? SimulatedFault.Drop : SimulatedFault.None;
        };
        using (var post = await _client.PostAsync("/api/v1/logger/imports", null))
        {
            var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
            Assert.Equal("completed", (await WaitForJobAsync(created.GetProperty("id").GetString()!)).GetProperty("state").GetString());
        }

        using var latest = await _client.GetAsync("/api/v1/readings/latest");
        string body = await latest.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, latest.StatusCode);
        Assert.Contains(JsonDocument.Parse(body).RootElement.GetProperty("code").GetString(), new[] { "W7012", "E9016" });
    }

    private async Task<JsonElement> ImportAsync()
    {
        using var post = await _client.PostAsync("/api/v1/logger/imports", null);
        var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
        var job = await WaitForJobAsync(created.GetProperty("id").GetString()!);
        factory.Responder.FaultInjector = null;
        return job;
    }

    private async Task<JsonElement> WaitForJobAsync(string id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var job = await _client.GetJsonAsync($"/api/v1/logger/imports/{id}");
            if (job.GetProperty("state").GetString() != "running")
                return job;
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"A importação {id} não terminou.");
            await Task.Delay(50);
        }
    }

    private async Task AssertImageAsync(JsonElement job)
    {
        string id = job.GetProperty("id").GetString()!;
        Assert.Equal(ExpectedImage(), await File.ReadAllBytesAsync(Path.Combine(factory.DataDirectory, "imports", id + ".bin")));
    }

    // Os 24 bytes úteis de cada bloco do dump real, na ordem do ponteiro.
    private static byte[] ExpectedImage() => SimulatedBm52xMeter.LoadDefaultMemoryImage();
}
