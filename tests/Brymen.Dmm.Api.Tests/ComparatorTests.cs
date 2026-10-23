using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Tests.Infrastructure;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Comparator Hi/Lo: limites, persistência em settings.json e aplicação a leituras e amostras.</summary>
public sealed class ComparatorTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Comparator_flow_validates_persists_and_applies_limits()
    {
        var client = factory.CreateClient();

        // Padrões do BTCDL.ini distribuído (Hi 200, Lo 100), desligado.
        var defaults = await client.GetJsonAsync("/api/v1/comparator");
        Assert.False(defaults.GetProperty("enabled").GetBoolean());
        Assert.Equal(200, defaults.GetProperty("high").GetDouble());
        Assert.Equal(100, defaults.GetProperty("low").GetDouble());
        Assert.False(defaults.GetProperty("alarm").GetBoolean());

        // Hi ≤ Lo é recusado (W7010) e não altera nada.
        using (var invalid = await client.PutAsJsonAsync("/api/v1/comparator", new { high = 0.1, low = 0.1 }))
            await TestHelpers.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "W7010");
        Assert.Equal(200, (await client.GetJsonAsync("/api/v1/comparator")).GetProperty("high").GetDouble());

        // 0.287 V > Hi → High; 0.156 V → Pass; 0.090 V < Lo → Low.
        var updated = await client.SendJsonAsync(HttpMethod.Put, "/api/v1/comparator", new { enabled = true, high = 0.2, low = 0.1, alarm = true });
        Assert.True(updated.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.2, updated.GetProperty("high").GetDouble());

        string settingsFile = Path.Combine(factory.DataDirectory, "settings.json");
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsFile)))
        {
            var persisted = json.RootElement.GetProperty("comparator");
            Assert.True(persisted.GetProperty("enabled").GetBoolean());
            Assert.Equal(0.2, persisted.GetProperty("high").GetDouble());
            Assert.Equal(0.1, persisted.GetProperty("low").GetDouble());
            Assert.True(persisted.GetProperty("alarm").GetBoolean());
        }

        var seen = new Dictionary<string, double>();
        await TestHelpers.ReadEventsAsync(client, "/api/v1/readings/stream", events =>
        {
            var reading = events[^1].Data;
            if (reading.GetProperty("comparator").ValueKind == JsonValueKind.String)
                seen[reading.GetProperty("comparator").GetString()!] = reading.GetProperty("main").GetProperty("siValue").GetDouble();
            return seen.Count == 3;
        });
        Assert.Equal(0.287, seen["High"], 9);
        Assert.Equal(0.156, seen["Pass"], 9);
        Assert.Equal(0.09, seen["Low"], 9);

        // As amostras carregam o resultado do comparador da leitura.
        await client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        await TestHelpers.EventuallyAsync(async () =>
            (await client.GetJsonAsync("/api/v1/acquisition")).GetProperty("sampleCount").GetInt32() >= 3 ? "ok" : null, "3 amostras");
        await client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        var samples = await client.GetJsonAsync("/api/v1/acquisition/samples?limit=50");
        Assert.All(samples.GetProperty("items").EnumerateArray(), s =>
        {
            double v = s.GetProperty("main").GetProperty("siValue").GetDouble();
            string expected = v > 0.2 ? "High" : v < 0.1 ? "Low" : "Pass";
            Assert.Equal(expected, s.GetProperty("comparator").GetString());
        });
        string csv = await client.GetStringAsync("/api/v1/acquisition/samples.csv?format=extended");
        Assert.Matches(@",(High|Pass|Low)\r\n", csv);
    }

    [Theory]
    [InlineData(0.2, "Pass")]  // v == Hi não é High (comparação estrita, como o original)
    [InlineData(0.1, "Pass")]  // v == Lo não é Low
    [InlineData(0.2000001, "High")]
    [InlineData(0.0999999, "Low")]
    [InlineData(0.15, "Pass")]
    public void Evaluate_uses_strict_comparisons(double value, string expected)
    {
        var settings = new ComparatorSettings(Enabled: true, High: 0.2, Low: 0.1, Alarm: false);

        Assert.Equal(Enum.Parse<ComparatorResult>(expected), ComparatorService.Evaluate(settings, value));
    }

    [Fact]
    public void Evaluate_returns_null_when_disabled_or_not_numeric()
    {
        var enabled = new ComparatorSettings(true, 0.2, 0.1, false);

        Assert.Null(ComparatorService.Evaluate(enabled with { Enabled = false }, 0.5));
        Assert.Null(ComparatorService.Evaluate(enabled, null));
        Assert.Null(ComparatorService.Evaluate(enabled, double.NaN));
    }
}
