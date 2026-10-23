using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;

namespace Brymen.Dmm.Api.Tests;

/// <summary>
/// Páginas de outra origem não alteram o estado nem abrem o WebSocket do hub: o CORS só esconde a resposta, e um POST
/// simples (sem corpo, sem preflight) ou o upgrade para WebSocket chegam ao servidor de qualquer origem.
/// </summary>
public sealed class CrossSiteRequestTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Intruder = "http://intruso.test";

    // O TestServer atende em http://localhost: é a origem da própria API.
    private const string SameOrigin = "http://localhost";

    [Theory]
    [InlineData(Intruder, null)]
    [InlineData(null, "cross-site")]
    [InlineData(Intruder, "cross-site")]
    [InlineData("http://localhost:8080", "same-site")]
    [InlineData("null", null)]
    public async Task Simple_post_from_another_origin_is_refused_with_403(string? origin, string? fetchSite)
    {
        var client = factory.CreateClient();
        int before = (await client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength();

        using var response = await client.SendAsync(Post("/api/v1/logger/imports", origin, fetchSite));

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(before, (await client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength());
        Assert.False((await client.GetJsonAsync("/api/v1/meter")).GetProperty("importing").GetBoolean());
    }

    [Theory]
    [InlineData(null, null)]                 // curl, clientes .NET
    [InlineData(SameOrigin, null)]           // browser sem Fetch Metadata, na própria origem
    [InlineData(SameOrigin, "same-origin")]  // o painel em /
    [InlineData(Intruder, "same-origin")]    // proxy reverso que reescreve o Host: vale o Sec-Fetch-Site
    [InlineData(null, "none")]               // endereço digitado ou favorito
    public async Task Requests_of_the_same_origin_or_outside_the_browser_pass(string? origin, string? fetchSite)
    {
        var client = factory.CreateClient();

        using var response = await client.SendAsync(Post("/api/v1/acquisition/stop", origin, fetchSite));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Json_requests_and_deletes_are_also_checked_but_reads_are_not()
    {
        var client = factory.CreateClient();

        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/meter/link") { Content = JsonContent.Create(new { enabled = false }) })
        {
            put.Headers.Add("Origin", Intruder);
            using var response = await client.SendAsync(put);
            await TestHelpers.AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        }
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/acquisition/samples"))
        {
            delete.Headers.Add("Origin", Intruder);
            using var response = await client.SendAsync(delete);
            await TestHelpers.AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        }
        Assert.True((await client.GetJsonAsync("/api/v1/meter")).GetProperty("linkEnabled").GetBoolean());

        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meter");
        get.Headers.Add("Origin", Intruder);
        get.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var read = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Hub_websocket_from_another_origin_is_refused()
    {
        var hub = new Uri(factory.Server.BaseAddress, "hubs/meter");
        var intruder = factory.Server.CreateWebSocketClient();
        intruder.ConfigureRequest = request => request.Headers.Origin = Intruder;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => intruder.ConnectAsync(hub, CancellationToken.None));
        Assert.Contains("403", ex.Message);

        var panel = factory.Server.CreateWebSocketClient();
        panel.ConfigureRequest = request => request.Headers.Origin = SameOrigin;
        using var socket = await panel.ConnectAsync(hub, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    internal static HttpRequestMessage Post(string url, string? origin, string? fetchSite)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (fetchSite is not null)
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return request;
    }
}

/// <summary>Com <c>Cors:AllowedOrigins</c>, a página da origem liberada também pode alterar o estado.</summary>
public sealed class CrossSiteAllowedOriginTests(CorsFactory factory) : IClassFixture<CorsFactory>
{
    [Theory]
    [InlineData(CorsFactory.AllowedOrigin, "cross-site", HttpStatusCode.OK)]
    [InlineData(CorsFactory.AllowedOrigin, null, HttpStatusCode.OK)]
    [InlineData("https://outro.test", "cross-site", HttpStatusCode.OK)]
    [InlineData("http://intruso.test", "cross-site", HttpStatusCode.Forbidden)]
    public async Task Allowed_origins_pass_and_the_others_do_not(string origin, string? fetchSite, HttpStatusCode expected)
    {
        var client = factory.CreateClient();

        using var response = await client.SendAsync(CrossSiteRequestTests.Post("/api/v1/acquisition/stop", origin, fetchSite));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
