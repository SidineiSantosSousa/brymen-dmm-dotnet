using System.Net;
using System.Text.Json;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Tests;

/// <summary>
/// O transporte Simulated com o Minipa ET-2517A (família BM82x, Model ID 82h, sem datalogger), com o descarte de pedidos
/// de tempo real medido no hardware, e o polling no padrão de 200 ms, em que ≈ 27 % dos pedidos se perdem.
/// </summary>
public class Et2517aFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        .. base.Settings.Where(s => s.Key is not ("Meter:PollInterval" or "Meter:AutoLink")),
        new("Meter:SimulatedModel", "Et2517a"),
        new("Meter:PollInterval", "00:00:00.200"),
        new("Meter:AutoLink", AutoLink ? "true" : "false"),
        .. ExtraSettings,
    ];

    protected virtual bool AutoLink => true;

    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings => [];

    internal SimulatedHidDevice Device => ((SimulatedHidDeviceProvider)Services.GetRequiredService<IHidDeviceProvider>()).Devices[0];
}

/// <summary>ET-2517A simulado com o link desativado na partida: a família continua desconhecida até a 1ª consulta.</summary>
public sealed class Et2517aUnlinkedFactory : Et2517aFactory
{
    protected override bool AutoLink => false;
}

/// <summary>ET-2517A simulado com <c>Meter:Family = Bm82x</c> e a janela e o orçamento dos reenvios configurados.</summary>
public sealed class FixedBm82xFactory : Et2517aFactory
{
    protected override bool AutoLink => false;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings =>
    [
        new("Meter:Family", "Bm82x"),
        new("Meter:RealTimeResendTimeout", "00:00:00.250"),
        new("Meter:RealTimeResendBudget", "00:00:01.500"),
    ];
}

/// <summary>BM525s simulado (o padrão) com o link desativado na partida.</summary>
public sealed class Bm525sUnlinkedFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [.. base.Settings.Where(s => s.Key != "Meter:AutoLink"), new("Meter:AutoLink", "false")];

    internal SimulatedHidDevice Device => ((SimulatedHidDeviceProvider)Services.GetRequiredService<IHidDeviceProvider>()).Devices[0];
}

internal static class ModelAssertions
{
    public const string Bm52xName = "BM52x (BM525s / Minipa ET-2615A)";
    public const string Bm82xName = "BM82x (Minipa ET-2517A)";

    /// <summary>Espera até <c>GET /api/v1/meter</c> ter o objeto <c>model</c>.</summary>
    public static async Task<JsonElement> WaitForModelAsync(HttpClient client)
    {
        object meter = await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("model").ValueKind == JsonValueKind.Object ? (object)m : null;
        }, "a detecção do modelo");
        return (JsonElement)meter;
    }

    public static void AssertModel(JsonElement model, string family, string modelId, string name, bool supportsDataLogger)
    {
        Assert.Equal(JsonValueKind.Object, model.ValueKind);
        Assert.Equal(family, model.GetProperty("family").GetString());
        Assert.Equal(modelId, model.GetProperty("modelId").GetString());
        Assert.Equal(name, model.GetProperty("name").GetString());
        Assert.Equal(supportsDataLogger, model.GetProperty("supportsDataLogger").GetBoolean());
    }

    public static JsonElement Statistics(JsonElement meter) => meter.GetProperty("realTimeStatistics");

    public static long Stat(JsonElement meter, string name) => Statistics(meter).GetProperty(name).GetInt64();

    /// <summary>Os pedidos de tempo real escritos no cabo simulado, como "8266" ou "5266".</summary>
    public static List<string> RealTimeWrites(SimulatedHidDevice device)
        => [.. device.Writes.Where(w => w.Data.Length >= 3 && w.Data[2] == Bm52xCommands.RealTime).Select(w => Convert.ToHexString(w.Data.AsSpan(1, 2)))];

    /// <summary>Os comandos de memória (88h, 89h, 8Ah) escritos no cabo simulado.</summary>
    public static int MemoryWrites(SimulatedHidDevice device)
        => device.Writes.Count(w => w.Data.Length >= 3 && w.Data[2] is Bm52xCommands.MemoryHead or Bm52xCommands.MemoryNext or Bm52xCommands.MemoryCurrent);
}

/// <summary><c>model</c> e estatísticas do tempo real com o BM525s simulado (família BM52x, com datalogger).</summary>
public sealed class Bm525sModelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private SimulatedHidDevice Device => ((SimulatedHidDeviceProvider)factory.Services.GetRequiredService<IHidDeviceProvider>()).Devices[0];

    [Fact]
    public async Task Meter_reports_the_detected_BM52x_model_with_data_logger()
    {
        await TestHelpers.WaitForReadingAsync(_client);

        var meter = await ModelAssertions.WaitForModelAsync(_client);

        ModelAssertions.AssertModel(meter.GetProperty("model"), "BM52x", "0x52", ModelAssertions.Bm52xName, supportsDataLogger: true);
        // O BM525s nunca descarta pedidos: nenhum reenvio, nenhuma perda.
        Assert.True(ModelAssertions.Stat(meter, "queries") > 0);
        Assert.True(ModelAssertions.Stat(meter, "requests") >= ModelAssertions.Stat(meter, "queries"));
        Assert.Equal(0, ModelAssertions.Stat(meter, "resends"));
        Assert.Equal(0, ModelAssertions.Stat(meter, "failedQueries"));
        // A detecção é o 82 66, que o BM525s também responde; depois, o 52 66 validado no hardware.
        var writes = ModelAssertions.RealTimeWrites(Device);
        Assert.Equal("8266", writes[0]);
        Assert.Equal("5266", writes[^1]);
        Assert.DoesNotContain("8266", writes.Skip(1));
    }

    [Fact]
    public async Task Panel_reads_the_model_from_the_meter_status()
    {
        string html = await _client.GetStringAsync("/");
        string script = await _client.GetStringAsync("/js/app.js");

        // O cabeçalho mostra o modelo detectado, e não mais o "Brymen BM525s" fixo.
        Assert.Contains("id=\"meter-model\"", html);
        Assert.DoesNotContain("Brymen BM525s", html);
        Assert.Contains("id=\"log-unsupported\"", html);
        Assert.Contains("status.model", script);
        Assert.Contains("model.name", script);
        Assert.Contains("supportsDataLogger", script);
        Assert.Contains("$(\"log-import\").hidden", script);
        Assert.Contains("$(\"meter-model\")", script);
    }

    [Fact]
    public async Task OpenApi_describes_the_model_and_the_real_time_statistics()
    {
        var document = await _client.GetJsonAsync("/openapi/v1.json");

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var status = schemas.GetProperty("MeterStatusDto").GetProperty("properties");
        Assert.True(status.TryGetProperty("model", out var model), "MeterStatusDto sem \"model\".");
        // Anulável: null antes da 1ª detecção.
        Assert.Contains(model.GetProperty("oneOf").EnumerateArray(), s => s.TryGetProperty("type", out var t) && t.GetString() == "null");
        Assert.Contains(model.GetProperty("oneOf").EnumerateArray(), s => s.TryGetProperty("$ref", out var r) && r.GetString() == "#/components/schemas/MeterModelDto");
        Assert.True(status.TryGetProperty("realTimeStatistics", out _), "MeterStatusDto sem \"realTimeStatistics\".");

        var modelSchema = schemas.GetProperty("MeterModelDto");
        Assert.False(string.IsNullOrWhiteSpace(modelSchema.GetProperty("description").GetString()));
        var properties = modelSchema.GetProperty("properties");
        Assert.Equal(["BM52x", "BM82x"], properties.GetProperty("family").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["0x52", "0x82"], properties.GetProperty("modelId").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("string", properties.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("supportsDataLogger").GetProperty("type").GetString());
        string[] required = [.. modelSchema.GetProperty("required").EnumerateArray().Select(v => v.GetString()!)];
        Assert.Equal(["family", "modelId", "name", "supportsDataLogger"], required.Order(StringComparer.Ordinal));

        var statistics = schemas.GetProperty("RealTimeStatisticsDto").GetProperty("properties");
        foreach (string name in new[] { "queries", "requests", "resends", "lostRequests", "recoveredQueries", "failedQueries" })
            Assert.Equal("integer", statistics.GetProperty(name).GetProperty("type").GetString());

        // A importação documenta o 409 (CONFLICT e E9014) e o código E9014 no campo "code".
        var post = document.GetProperty("paths").GetProperty("/api/v1/logger/imports").GetProperty("post");
        Assert.True(post.GetProperty("responses").GetProperty("409").GetProperty("content").TryGetProperty("application/problem+json", out _));
        Assert.Contains("E9014", post.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("E9014", schemas.GetProperty("ProblemDetails").GetProperty("properties").GetProperty("code").GetProperty("description").GetString(), StringComparison.Ordinal);
    }
}

/// <summary>O ET-2517A simulado com o link ativo na partida: detecção, recusa da importação e leitura contínua com descarte.</summary>
public sealed class Et2517aModelTests(Et2517aFactory factory) : IClassFixture<Et2517aFactory>, IAsyncLifetime
{
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
    public async Task Meter_reports_the_detected_BM82x_model_without_data_logger()
    {
        var meter = await ModelAssertions.WaitForModelAsync(_client);

        ModelAssertions.AssertModel(meter.GetProperty("model"), "BM82x", "0x82", ModelAssertions.Bm82xName, supportsDataLogger: false);
        Assert.Equal("connected", meter.GetProperty("state").GetString());
        Assert.False(factory.Responder.HasDataLogger);

        // Os quadros da varredura do ET-2517A, com o Model ID 82h nos bytes 20–23; o tempo real é sempre o 82 66.
        var reading = await TestHelpers.WaitForReadingAsync(_client);
        Assert.Equal("82828282", reading.GetProperty("rawHex").GetString()![38..46]);
        var writes = ModelAssertions.RealTimeWrites(factory.Device);
        Assert.All(writes.TakeLast(10), w => Assert.Equal("8266", w));
    }

    [Fact]
    public async Task Import_is_refused_with_409_E9014_without_a_job_or_memory_commands()
    {
        await ModelAssertions.WaitForModelAsync(_client);
        var commands = new List<byte>();
        factory.Responder.FaultInjector = (command, _) =>
        {
            lock (commands)
                commands.Add(command);
            return SimulatedFault.None;
        };
        int memoryBefore = ModelAssertions.MemoryWrites(factory.Device);
        var before = await _client.GetJsonAsync("/api/v1/meter");

        using (var post = await _client.PostAsync("/api/v1/logger/imports", null))
        {
            var problem = await TestHelpers.AssertProblemAsync(post, HttpStatusCode.Conflict, "E9014");
            Assert.Null(post.Headers.Location);
            Assert.Equal("Medidor sem datalogger", problem.GetProperty("title").GetString());
            Assert.Contains("ET-2517A", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        // Nenhum job, nenhum comando de memória, o polling não foi pausado e o E9014 não vira o último erro do medidor.
        Assert.Equal(0, (await _client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength());
        Assert.Equal(memoryBefore, ModelAssertions.MemoryWrites(factory.Device));
        lock (commands)
            Assert.All(commands, c => Assert.Equal(Bm52xCommands.RealTime, c));
        var after = await _client.GetJsonAsync("/api/v1/meter");
        Assert.False(after.GetProperty("importing").GetBoolean());
        Assert.Equal(before.GetProperty("lastError").ToString(), after.GetProperty("lastError").ToString());
        long count = after.GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > count);
    }

    [Fact]
    public async Task Continuous_reading_with_dropped_requests_has_no_W7012_nor_E9016()
    {
        var start = await ModelAssertions.WaitForModelAsync(_client);
        var firstReadingAt = (await TestHelpers.WaitForReadingAsync(_client)).GetProperty("timestamp").GetDateTimeOffset();
        start = await _client.GetJsonAsync("/api/v1/meter");
        long readings = start.GetProperty("readingCount").GetInt64();
        long dropped = factory.Responder.DroppedRequestCount;

        // ≈ 25 leituras a 200 ms: com a curva do ET-2517A, ≈ 1 em 4 pedidos se perde e é reenviado depois de 300 ms.
        var end = (JsonElement)await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await _client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("readingCount").GetInt64() >= readings + 25 ? (object)m : null;
        }, "25 leituras do ET-2517A simulado", TimeSpan.FromSeconds(60));

        Assert.True(factory.Responder.DroppedRequestCount > dropped, "O ET-2517A simulado não descartou nenhum pedido.");
        Assert.True(ModelAssertions.Stat(end, "resends") > ModelAssertions.Stat(start, "resends"));
        Assert.True(ModelAssertions.Stat(end, "recoveredQueries") > ModelAssertions.Stat(start, "recoveredQueries"));
        Assert.True(ModelAssertions.Stat(end, "lostRequests") >= ModelAssertions.Stat(end, "resends"));
        // As perdas recuperadas por reenvio não são falhas: nenhuma consulta sem resposta, nenhum W7012 e nenhum E9016.
        Assert.Equal(ModelAssertions.Stat(start, "failedQueries"), ModelAssertions.Stat(end, "failedQueries"));
        Assert.Equal("connected", end.GetProperty("state").GetString());
        var error = end.GetProperty("lastError");
        Assert.True(error.ValueKind == JsonValueKind.Null || error.GetProperty("timestamp").GetDateTimeOffset() < firstReadingAt,
            $"Erro durante a leitura contínua: {error}");
    }

    [Fact]
    public async Task Every_query_losing_three_requests_still_reads_without_W7012_nor_E9016()
    {
        // Cada consulta perde os 3 primeiros envios e só o 4º (≈ 0,9 s depois, dentro do orçamento de 2 s) é atendido. Sem o
        // reenvio, seriam 3 W7012 seguidos e um E9016.
        await ModelAssertions.WaitForModelAsync(_client);
        var start = await _client.GetJsonAsync("/api/v1/meter");
        factory.Responder.RequestDropper = (_, consecutiveDrops) => consecutiveDrops < 3;
        try
        {
            long readings = (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("readingCount").GetInt64();
            var started = DateTimeOffset.UtcNow;

            var end = (JsonElement)await TestHelpers.EventuallyAsync(async () =>
            {
                var m = await _client.GetJsonAsync("/api/v1/meter");
                return m.GetProperty("readingCount").GetInt64() >= readings + 4 ? (object)m : null;
            }, "4 leituras com 3 perdas por consulta", TimeSpan.FromSeconds(60));

            Assert.True(ModelAssertions.Stat(end, "resends") - ModelAssertions.Stat(start, "resends") >= 3 * 3);
            Assert.Equal(ModelAssertions.Stat(start, "failedQueries"), ModelAssertions.Stat(end, "failedQueries"));
            Assert.Equal("connected", end.GetProperty("state").GetString());
            var error = end.GetProperty("lastError");
            Assert.True(error.ValueKind == JsonValueKind.Null || error.GetProperty("timestamp").GetDateTimeOffset() < started,
                $"Erro com perdas recuperadas: {error}");
        }
        finally
        {
            factory.Responder.RequestDropper = SimulatedBm52xMeter.CreateEt2517aRequestDropper();
        }
    }
}

/// <summary>Importação pedida com a família ainda desconhecida (link desativado desde a partida).</summary>
public sealed class Et2517aImportDetectionTests(Et2517aUnlinkedFactory factory) : IClassFixture<Et2517aUnlinkedFactory>
{
    [Fact]
    public async Task Import_with_unknown_family_detects_the_BM82x_and_is_refused_without_a_job()
    {
        var client = factory.CreateClient();
        var meter = await client.GetJsonAsync("/api/v1/meter");
        Assert.False(meter.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, meter.GetProperty("model").ValueKind);

        // A detecção é feita no POST, com o polling pausado: 82 66 (com reenvios), e nenhum comando de memória.
        using (var post = await client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(post, HttpStatusCode.Conflict, "E9014");

        Assert.Equal(0, (await client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength());
        Assert.Equal(0, ModelAssertions.MemoryWrites(factory.Device));
        Assert.NotEmpty(ModelAssertions.RealTimeWrites(factory.Device));
        Assert.Contains("8266", ModelAssertions.RealTimeWrites(factory.Device));
        var after = await client.GetJsonAsync("/api/v1/meter");
        ModelAssertions.AssertModel(after.GetProperty("model"), "BM82x", "0x82", ModelAssertions.Bm82xName, supportsDataLogger: false);
        Assert.False(after.GetProperty("importing").GetBoolean());
        Assert.False(after.GetProperty("linkEnabled").GetBoolean());
        // Com o link desativado, o cabo aberto só para a detecção é fechado de novo.
        Assert.False(after.GetProperty("deviceOpen").GetBoolean());

        // A família já conhecida é recusada na hora, sem nenhum comando.
        int writes = factory.Device.Writes.Count;
        using (var again = await client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(again, HttpStatusCode.Conflict, "E9014");
        Assert.Equal(writes, factory.Device.Writes.Count);
    }
}

/// <summary>Evento <c>status</c> do SignalR quando a família é detectada.</summary>
public sealed class Et2517aStatusEventTests(Et2517aUnlinkedFactory factory) : IClassFixture<Et2517aUnlinkedFactory>
{
    [Fact]
    public async Task Status_event_carries_the_model_once_it_is_detected()
    {
        var client = factory.CreateClient();
        await using var hub = await HubClient.ConnectAsync(factory.Server);
        var initial = await hub.Connection.InvokeAsync<JsonElement>("GetStatus");
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("model").ValueKind);
        Assert.Equal(0, initial.GetProperty("realTimeStatistics").GetProperty("queries").GetInt64());

        await client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });

        var status = (await HubClient.TakeAsync(hub.Statuses, 1, s => s.GetProperty("model").ValueKind == JsonValueKind.Object))[0];
        ModelAssertions.AssertModel(status.GetProperty("model"), "BM82x", "0x82", ModelAssertions.Bm82xName, supportsDataLogger: false);
        Assert.True(status.GetProperty("realTimeStatistics").GetProperty("queries").GetInt64() >= 1);
        var viaHub = await hub.Connection.InvokeAsync<JsonElement>("GetStatus");
        ModelAssertions.AssertModel(viaHub.GetProperty("model"), "BM82x", "0x82", ModelAssertions.Bm82xName, supportsDataLogger: false);
    }
}

/// <summary>Importação do BM525s com a família ainda desconhecida: a detecção não atrapalha a leitura da memória.</summary>
public sealed class Bm525sImportDetectionTests(Bm525sUnlinkedFactory factory) : IClassFixture<Bm525sUnlinkedFactory>
{
    [Fact]
    public async Task Import_with_unknown_family_detects_the_BM52x_and_reads_the_memory()
    {
        var client = factory.CreateClient();
        Assert.Equal(JsonValueKind.Null, (await client.GetJsonAsync("/api/v1/meter")).GetProperty("model").ValueKind);

        using var post = await client.PostAsync("/api/v1/logger/imports", null);
        var created = await TestHelpers.ReadJsonAsync(post, HttpStatusCode.Accepted);
        string id = created.GetProperty("id").GetString()!;
        var job = (JsonElement)await TestHelpers.EventuallyAsync(async () =>
        {
            var j = await client.GetJsonAsync($"/api/v1/logger/imports/{id}");
            return j.GetProperty("state").GetString() == "running" ? null : (object)j;
        }, "o fim da importação", TimeSpan.FromSeconds(60));

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        var meter = await client.GetJsonAsync("/api/v1/meter");
        ModelAssertions.AssertModel(meter.GetProperty("model"), "BM52x", "0x52", ModelAssertions.Bm52xName, supportsDataLogger: true);
        // A detecção (82 66) e a conferência do modelo (52 66, a família já conhecida) antes do 1º 88; os comandos de
        // memória são sempre da família 52h.
        var writes = factory.Device.Writes.Select(w => Convert.ToHexString(w.Data.AsSpan(1, 2))).ToList();
        int head = writes.IndexOf("5288");
        Assert.True(head > 0, string.Join(' ', writes));
        Assert.Equal(["8266", "5266"], writes[..head]);
        Assert.DoesNotContain(writes, w => w.StartsWith("82", StringComparison.Ordinal) && w != "8266");
    }
}

/// <summary><c>Meter:Family</c> fixa e as opções do reenvio vindas da configuração.</summary>
public sealed class FixedFamilyTests(FixedBm82xFactory factory) : IClassFixture<FixedBm82xFactory>
{
    [Fact]
    public async Task Fixed_BM82x_family_is_reported_before_any_query_and_refuses_the_import_without_commands()
    {
        var client = factory.CreateClient();

        var meter = await client.GetJsonAsync("/api/v1/meter");
        ModelAssertions.AssertModel(meter.GetProperty("model"), "BM82x", "0x82", ModelAssertions.Bm82xName, supportsDataLogger: false);
        using (var post = await client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(post, HttpStatusCode.Conflict, "E9014");
        Assert.Empty(factory.Device.Writes);
        Assert.Equal(0, (await client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength());

        var options = factory.Services.GetRequiredService<IOptions<MeterOptions>>().Value;
        Assert.Equal(MeterFamilySelection.Bm82x, options.Family);
        Assert.Equal(SimulatedMeterModel.Et2517a, options.SimulatedModel);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RealTimeResendTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), options.RealTimeResendBudget);
    }
}

/// <summary>
/// Cabo simulado com o medidor trocável, como o ET-2517A trocado pelo BM525s no mesmo BC-86X, e o link desativado na
/// partida: a família só é detectada pelas importações.
/// </summary>
public sealed class SwappableMeterFactory : ApiFactory
{
    private SimulatedBm52xMeter _current = SimulatedBm52xMeter.CreateBm82x();
    private SimulatedHidDevice? _device;

    internal SimulatedBm52xMeter Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    internal SimulatedHidDevice OpenedDevice => _device ?? throw new InvalidOperationException("O cabo simulado ainda não foi criado.");

    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [.. base.Settings.Where(s => s.Key != "Meter:AutoLink"), new("Meter:AutoLink", "false")];

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        _device = new SimulatedHidDevice((id, data) => Current.Respond(id, data))
        {
            ResponseLatency = TimeSpan.FromMilliseconds(2),
        };
        services.AddSingleton<IHidDeviceProvider>(new SimulatedHidDeviceProvider([_device]));
    }
}

/// <summary>A família guardada não recusa a importação depois que o medidor pode ter sido trocado no cabo.</summary>
public sealed class SwappedMeterImportTests(SwappableMeterFactory factory) : IClassFixture<SwappableMeterFactory>
{
    [Fact]
    public async Task A_BM82x_family_known_for_a_while_is_confirmed_before_E9014_and_a_swapped_BM525s_is_imported()
    {
        var client = factory.CreateClient();

        // ET-2517A: a detecção do POST acha o BM82x, e a 2ª tentativa, logo depois, é recusada na hora, sem comando.
        using (var post = await client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(post, HttpStatusCode.Conflict, "E9014");
        int writes = factory.OpenedDevice.Writes.Count;
        using (var again = await client.PostAsync("/api/v1/logger/imports", null))
            await TestHelpers.AssertProblemAsync(again, HttpStatusCode.Conflict, "E9014");
        Assert.Equal(writes, factory.OpenedDevice.Writes.Count);

        // O BM525s toma o lugar do ET-2517A, sem polling (o link continua desativado). Depois da janela de confirmação, a
        // importação confere a família com um 82 66 (a família guardada) e lê a memória.
        factory.Current = SimulatedBm52xMeter.FromCaptures();
        await Task.Delay(MeterConnection.FamilyConfirmationWindow + TimeSpan.FromMilliseconds(200));
        using var created = await client.PostAsync("/api/v1/logger/imports", null);
        string id = (await TestHelpers.ReadJsonAsync(created, HttpStatusCode.Accepted)).GetProperty("id").GetString()!;
        var job = (JsonElement)await TestHelpers.EventuallyAsync(async () =>
        {
            var j = await client.GetJsonAsync($"/api/v1/logger/imports/{id}");
            return j.GetProperty("state").GetString() == "running" ? null : (object)j;
        }, "o fim da importação", TimeSpan.FromSeconds(60));

        Assert.Equal("completed", job.GetProperty("state").GetString());
        Assert.Equal(375, job.GetProperty("sampleCount").GetInt32());
        var meter = await client.GetJsonAsync("/api/v1/meter");
        ModelAssertions.AssertModel(meter.GetProperty("model"), "BM52x", "0x52", ModelAssertions.Bm52xName, supportsDataLogger: true);
        var requests = factory.OpenedDevice.Writes.Skip(writes).Select(w => Convert.ToHexString(w.Data.AsSpan(1, 2))).ToList();
        int head = requests.IndexOf("5288");
        Assert.True(head > 0, string.Join(' ', requests));
        // A confirmação (82 66, que o BM525s responde com o Model ID 52h) e a conferência do modelo (52 66).
        Assert.Equal(["8266", "5266"], requests[..head]);
    }
}
