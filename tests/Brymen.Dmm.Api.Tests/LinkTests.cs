using System.Net;
using System.Net.Http.Json;
using Brymen.Dmm.Api.Tests.Infrastructure;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Link/Unlink (<c>PUT /api/v1/meter/link</c>) e as regras que dependem dele.</summary>
public sealed class LinkTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Unlink_stops_readings_and_link_resumes_them()
    {
        var client = factory.CreateClient();
        await TestHelpers.WaitForReadingAsync(client);

        var unlinked = await client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        Assert.False(unlinked.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal("unlinked", unlinked.GetProperty("state").GetString());
        Assert.False(unlinked.GetProperty("deviceOpen").GetBoolean());

        using (var latest = await client.GetAsync("/api/v1/readings/latest"))
            await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.Conflict, "E9002");

        var linked = await client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        Assert.True(linked.GetProperty("linkEnabled").GetBoolean());
        Assert.Equal("connected", linked.GetProperty("state").GetString());
        Assert.True(linked.GetProperty("deviceOpen").GetBoolean());
        Assert.Equal("Simulated".ToLowerInvariant(), linked.GetProperty("transport").GetString());

        long before = linked.GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(client, r => r.GetProperty("sequence").GetInt64() > before);
    }

    [Fact]
    public async Task Acquisition_start_requires_link()
    {
        var client = factory.CreateClient();
        await client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        try
        {
            using var start = await client.PostAsync("/api/v1/acquisition/start", null);
            await TestHelpers.AssertProblemAsync(start, HttpStatusCode.Conflict, "E9002");
        }
        finally
        {
            await client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        }
    }

    [Fact]
    public async Task Link_request_without_enabled_is_rejected()
    {
        var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/meter/link", new { });

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }
}
