using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Brymen.Dmm.Api.Tests.Infrastructure;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Medidor, leituras (JSON e SSE), health checks, OpenAPI e painel, com o transporte Simulated.</summary>
public sealed class MeterEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] CapturedMainTexts = ["0.287", "0.156", "0.090"];

    [Fact]
    public async Task Meter_status_reports_simulated_cable()
    {
        var client = factory.CreateClient();
        await TestHelpers.WaitForReadingAsync(client);

        var meter = await client.GetJsonAsync("/api/v1/meter");

        Assert.True(meter.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal("connected", meter.GetProperty("state").GetString());
        Assert.Equal("simulated", meter.GetProperty("transport").GetString());
        Assert.True(meter.GetProperty("deviceOpen").GetBoolean());
        Assert.False(meter.GetProperty("importing").GetBoolean());
        Assert.True(meter.GetProperty("readingCount").GetInt64() > 0);
        var device = meter.GetProperty("device");
        Assert.Equal("0x0820", device.GetProperty("vendorId").GetString());
        Assert.Equal("0x0001", device.GetProperty("productId").GetString());
        Assert.Equal("Brymen", device.GetProperty("manufacturer").GetString());
    }

    [Fact]
    public async Task Latest_reading_decodes_the_captured_frames()
    {
        var client = factory.CreateClient();

        var reading = await TestHelpers.WaitForReadingAsync(client);

        Assert.True(reading.GetProperty("sequence").GetInt64() > 0);
        Assert.Equal(54, reading.GetProperty("rawHex").GetString()!.Length);
        var main = reading.GetProperty("main");
        Assert.Contains(main.GetProperty("text").GetString(), CapturedMainTexts);
        Assert.Equal("V", main.GetProperty("unit").GetString());
        Assert.Equal("V", main.GetProperty("unitSymbol").GetString());
        Assert.Equal("AC", main.GetProperty("mode").GetString());
        Assert.False(main.GetProperty("overload").GetBoolean());
        Assert.Equal(main.GetProperty("value").GetDouble(), main.GetProperty("siValue").GetDouble(), 9);
        var secondary = reading.GetProperty("secondary");
        Assert.Equal("0.000", secondary.GetProperty("text").GetString());
        Assert.Equal("Hz", secondary.GetProperty("unit").GetString());
        Assert.Contains("AUTO", reading.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        // Comparador desligado por padrão.
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("comparator").ValueKind);
    }

    [Fact]
    public async Task Stream_sends_reading_events()
    {
        var client = factory.CreateClient();

        var events = await TestHelpers.ReadEventsAsync(client, "/api/v1/readings/stream", e => e.Count >= 3);

        Assert.True(events.Count >= 3);
        Assert.All(events, e => Assert.Equal("reading", e.Event));
        Assert.All(events, e => Assert.Contains(e.Data.GetProperty("main").GetProperty("text").GetString(), CapturedMainTexts));
        var sequences = events.Select(e => e.Data.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.Order().Distinct(), sequences);
    }

    [Fact]
    public async Task Health_checks_are_healthy_with_simulated_transport()
    {
        var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_document_describes_the_endpoints()
    {
        var client = factory.CreateClient();

        var document = await client.GetJsonAsync("/openapi/v1.json");

        Assert.StartsWith("3.", document.GetProperty("openapi").GetString());
        Assert.Equal("Brymen DMM REST", document.GetProperty("info").GetProperty("title").GetString());
        var paths = document.GetProperty("paths");
        string[] expected =
        [
            "/api/v1/meter", "/api/v1/meter/link", "/api/v1/readings/latest", "/api/v1/readings/stream",
            "/api/v1/acquisition", "/api/v1/acquisition/settings", "/api/v1/acquisition/start", "/api/v1/acquisition/stop",
            "/api/v1/acquisition/samples", "/api/v1/acquisition/samples.csv", "/api/v1/comparator",
            "/api/v1/logger/imports", "/api/v1/logger/imports/{id}", "/api/v1/logger/imports/{id}/sessions",
            "/api/v1/logger/imports/{id}/sessions/{number}/samples", "/api/v1/logger/imports/{id}/sessions/{number}/samples.csv",
        ];
        foreach (string path in expected)
            Assert.True(paths.TryGetProperty(path, out _), $"Caminho ausente na OpenAPI: {path}");

        var getMeter = paths.GetProperty("/api/v1/meter").GetProperty("get");
        Assert.Equal("Medidor", getMeter.GetProperty("tags")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(getMeter.GetProperty("summary").GetString()));
        var tags = document.GetProperty("tags").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(tags["Datalogger"].GetProperty("description").GetString()));

        // Erros documentados como ProblemDetails com o campo "code".
        var linkResponses = paths.GetProperty("/api/v1/meter/link").GetProperty("put").GetProperty("responses");
        Assert.True(linkResponses.TryGetProperty("200", out _));
        Assert.True(linkResponses.GetProperty("503").GetProperty("content").TryGetProperty("application/problem+json", out _));
        var problem = document.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails");
        Assert.True(problem.GetProperty("properties").TryGetProperty("code", out _));
        Assert.Equal(202, int.Parse(paths.GetProperty("/api/v1/logger/imports").GetProperty("post").GetProperty("responses").EnumerateObject().First().Name, System.Globalization.CultureInfo.InvariantCulture));

        // Números estritos: o limit da paginação é só "integer".
        var limit = paths.GetProperty("/api/v1/acquisition/samples").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Single(p => p.GetProperty("name").GetString() == "limit");
        Assert.Equal("integer", limit.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Panel_is_served_from_wwwroot()
    {
        var client = factory.CreateClient();

        using var page = await client.GetAsync("/");
        using var script = await client.GetAsync("/js/app.js");
        using var css = await client.GetAsync("/css/app.css");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"pt-BR\"", html);
        Assert.DoesNotContain("https://", html);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("readings/stream", await script.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
    }

    /// <summary>
    /// Os arquivos comprimidos do SDK (.gz no build; .gz e .br no publish) são servidos conforme o <c>Accept-Encoding</c>,
    /// pelo <c>MapStaticAssets</c>. Com o <c>UseStaticFiles</c>, a resposta vinha sempre sem compressão (Orange Pi 5,
    /// <c>Accept-Encoding: br</c> → 11 908 B do <c>app.js</c>), e os comprimidos do pacote não serviam para nada.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/js/app.js")]
    [InlineData("/css/app.css")]
    public async Task Panel_files_are_served_compressed_when_the_client_accepts_it(string path)
    {
        var client = factory.CreateClient();
        using var plain = await client.GetAsync(path);
        byte[] expected = await plain.Content.ReadAsByteArrayAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var compressed = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Equal(["gzip"], compressed.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
        Assert.NotNull(compressed.Headers.ETag);
        Assert.Equal(plain.Content.Headers.ContentType?.MediaType, compressed.Content.Headers.ContentType?.MediaType);
        await using var gzip = new GZipStream(await compressed.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed);
        Assert.Equal(expected, decompressed.ToArray());
    }
}
