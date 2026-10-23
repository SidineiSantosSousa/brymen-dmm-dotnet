using System.Net;
using System.Text.RegularExpressions;
using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Graphical Recorder / Data List: modos, buffer, backup e export CSV.</summary>
public sealed partial class AcquisitionTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OriginalHeader = "#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc";

    private readonly HttpClient _client = factory.CreateClient();

    // Cada teste começa parado, com o buffer vazio e no modo fullSpeed.
    public async Task InitializeAsync()
    {
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        await _client.SendJsonAsync(HttpMethod.Delete, "/api/v1/acquisition/samples", expected: HttpStatusCode.NoContent);
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed" });
        await TestHelpers.WaitForReadingAsync(_client);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Csv_header_is_exactly_the_original_one()
    {
        using var response = await _client.GetAsync("/api/v1/acquisition/samples.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OriginalHeader + "\r\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Full_speed_records_every_reading_and_exports_csv()
    {
        var started = await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        Assert.Equal("recording", started.GetProperty("state").GetString());
        Assert.Equal("fullSpeed", started.GetProperty("mode").GetString());

        await TestHelpers.EventuallyAsync(async () =>
        {
            var status = await _client.GetJsonAsync("/api/v1/acquisition");
            return status.GetProperty("sampleCount").GetInt32() >= 5 ? "ok" : null;
        }, "5 amostras no modo fullSpeed");
        var stopped = await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");
        Assert.Equal("idle", stopped.GetProperty("state").GetString());
        int count = stopped.GetProperty("sampleCount").GetInt32();
        Assert.True(count >= 5);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, stopped.GetProperty("mainRange").ValueKind);

        // Paginação.
        var page = await _client.GetJsonAsync("/api/v1/acquisition/samples?offset=1&limit=2");
        Assert.Equal(1, page.GetProperty("offset").GetInt32());
        Assert.Equal(2, page.GetProperty("limit").GetInt32());
        Assert.Equal(count, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items[0].GetProperty("item").GetInt32());
        Assert.Equal("V", items[0].GetProperty("main").GetProperty("unit").GetString());
        Assert.Matches(TimeFormat(), items[0].GetProperty("time").GetString()!);

        // CSV no formato do original.
        string csv = await _client.GetStringAsync("/api/v1/acquisition/samples.csv");
        Assert.EndsWith("\r\n", csv);
        string[] lines = csv[..^2].Split("\r\n");
        Assert.Equal(OriginalHeader, lines[0]);
        Assert.Equal(count + 1, lines.Length);
        Assert.Matches(@"^1,  0:00:00\.0,0\.(287|156|090),V,AC,0\.000,Hz,$", lines[1]);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] fields = lines[i].Split(',');
            Assert.Equal(8, fields.Length);
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[0]);
            Assert.Matches(TimeFormat(), fields[1]);
        }

        // CSV estendido: ISO-8601, SI e flags.
        string extended = await _client.GetStringAsync("/api/v1/acquisition/samples.csv?format=extended");
        string[] extLines = extended[..^2].Split("\r\n");
        Assert.Equal(OriginalHeader + ",Timestamp,MainSiValue,SubSiValue,Flags,Comparator", extLines[0]);
        string[] ext = extLines[1].Split(',');
        Assert.Equal(13, ext.Length);
        Assert.True(DateTimeOffset.TryParse(ext[8], System.Globalization.CultureInfo.InvariantCulture, out _), ext[8]);
        Assert.Matches(@"^0\.(287|156|09)$", ext[9]);
        Assert.Equal("0", ext[10]);
        Assert.Contains("AUTO", ext[11].Split('|'));
        Assert.Equal("", ext[12]);

        // Backup contínuo equivalente ao ~BDMDLn.TMP.
        string? backup = stopped.GetProperty("backupFile").GetString();
        Assert.NotNull(backup);
        Assert.StartsWith(Path.Combine(factory.DataDirectory, "backup"), backup);
        string[] backupLines = (await File.ReadAllTextAsync(backup))[..^2].Split("\r\n");
        Assert.Equal(extLines[0], backupLines[0]);
        Assert.Equal(count + 1, backupLines.Length);

        // Buffer compacto: sem o quadro bruto, e displays repetidos compartilham o mesmo objeto.
        var buffer = factory.Services.GetRequiredService<Recorder>().Snapshot();
        Assert.All(buffer, s => Assert.Equal("", s.Reading.RawHex));
        Assert.Contains(buffer.Zip(buffer.Skip(1)), p => ReferenceEquals(p.First.Reading.Secondary, p.Second.Reading.Secondary));

        // Limpar.
        using var clear = await _client.DeleteAsync("/api/v1/acquisition/samples");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        Assert.Equal(0, (await _client.GetJsonAsync("/api/v1/acquisition")).GetProperty("sampleCount").GetInt32());
    }

    [Fact]
    public async Task Manual_mode_adds_one_sample_per_post()
    {
        await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "manual" });
        using (var notRunning = await _client.PostAsync("/api/v1/acquisition/samples", null))
            await TestHelpers.AssertProblemAsync(notRunning, HttpStatusCode.Conflict, "CONFLICT");

        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        var first = await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/samples", expected: HttpStatusCode.Created);
        var second = await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/samples", expected: HttpStatusCode.Created);
        await Task.Delay(200);
        var status = await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");

        Assert.Equal(1, first.GetProperty("item").GetInt32());
        Assert.Equal(2, second.GetProperty("item").GetInt32());
        Assert.Equal("  0:00:00.0", first.GetProperty("time").GetString());
        Assert.Equal(2, status.GetProperty("sampleCount").GetInt32());
        Assert.Equal("manual", status.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Interval_mode_samples_every_n_seconds()
    {
        var settings = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "interval", intervalSeconds = 1 });
        Assert.Equal("interval", settings.GetProperty("mode").GetString());
        Assert.Equal(1, settings.GetProperty("intervalSeconds").GetInt32());

        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        await TestHelpers.EventuallyAsync(async () =>
        {
            var s = await _client.GetJsonAsync("/api/v1/acquisition");
            return s.GetProperty("sampleCount").GetInt32() >= 2 ? "ok" : null;
        }, "2 amostras no modo interval");
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");

        var page = await _client.GetJsonAsync("/api/v1/acquisition/samples");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        double gap = items[1].GetProperty("elapsedSeconds").GetDouble() - items[0].GetProperty("elapsedSeconds").GetDouble();
        Assert.InRange(gap, 0.7, 1.6);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected()
    {
        using (var zero = await _client.PutAsync("/api/v1/acquisition/settings", Json(new { mode = "interval", intervalSeconds = 0 })))
            await TestHelpers.AssertProblemAsync(zero, HttpStatusCode.BadRequest, "E9007");
        using (var negative = await _client.PutAsync("/api/v1/acquisition/settings", Json(new { mode = "fullSpeed", intervalSeconds = -1 })))
            await TestHelpers.AssertProblemAsync(negative, HttpStatusCode.BadRequest, "E9007");
        using (var badPage = await _client.GetAsync("/api/v1/acquisition/samples?limit=0"))
            await TestHelpers.AssertProblemAsync(badPage, HttpStatusCode.BadRequest, "VALIDATION");
        using (var badFormat = await _client.GetAsync("/api/v1/acquisition/samples.csv?format=xml"))
            await TestHelpers.AssertProblemAsync(badFormat, HttpStatusCode.BadRequest, "VALIDATION");

        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/start");
        using (var whileRecording = await _client.PutAsync("/api/v1/acquisition/settings", Json(new { mode = "manual" })))
            await TestHelpers.AssertProblemAsync(whileRecording, HttpStatusCode.Conflict, "CONFLICT");
        await _client.SendJsonAsync(HttpMethod.Post, "/api/v1/acquisition/stop");

        var current = await _client.GetJsonAsync("/api/v1/acquisition/settings");
        Assert.Equal("fullSpeed", current.GetProperty("mode").GetString());
    }

    private static System.Net.Http.Json.JsonContent Json(object value) => System.Net.Http.Json.JsonContent.Create(value);

    [GeneratedRegex(@"^ {0,2}\d{1,}:\d{2}:\d{2}\.\d$")]
    private static partial Regex TimeFormat();
}
