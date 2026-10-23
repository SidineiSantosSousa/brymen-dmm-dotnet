using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Brymen.Dmm.Api.Tests.Infrastructure;

internal static class TestHelpers
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Relatórios de 8 bytes de uma resposta do medidor simulado no formato das capturas (report ID 00h na frente de cada um).</summary>
    public static byte[] JoinReports(IReadOnlyList<byte[]> reports) => [.. reports.SelectMany(r => r.Prepend((byte)0x00))];

    /// <summary>Repete <paramref name="probe"/> até devolver um valor não nulo ou estourar o tempo.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, string because, TimeSpan? timeout = null)
        where T : class
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        while (true)
        {
            if (await probe() is { } value)
                return value;
            if (cts.IsCancellationRequested)
                throw new TimeoutException($"Tempo esgotado esperando: {because}.");
            await Task.Delay(25);
        }
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(url);
        return await ReadJsonAsync(response, expected);
    }

    public static async Task<JsonElement> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        return await ReadJsonAsync(response, expected);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Esperado {(int)expected}, recebido {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Confere que a resposta é um ProblemDetails (RFC 9457) com o código esperado em <c>code</c>.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Esperado {(int)status}, recebido {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
        return json;
    }

    /// <summary>Espera até <c>GET /api/v1/readings/latest</c> responder 200 (e satisfazer <paramref name="predicate"/>).</summary>
    public static async Task<JsonElement> WaitForReadingAsync(HttpClient client, Func<JsonElement, bool>? predicate = null)
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        while (true)
        {
            using (var response = await client.GetAsync("/api/v1/readings/latest"))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var reading = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
                    if (predicate is null || predicate(reading))
                        return reading;
                }
            }
            if (cts.IsCancellationRequested)
                throw new TimeoutException("Tempo esgotado esperando uma leitura do medidor simulado.");
            await Task.Delay(25);
        }
    }

    /// <summary>Lê eventos SSE (<c>event:</c>/<c>data:</c>) até <paramref name="stop"/> devolver verdadeiro.</summary>
    public static async Task<List<(string Event, JsonElement Data)>> ReadEventsAsync(
        HttpClient client, string url, Func<List<(string Event, JsonElement Data)>, bool> stop, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<(string, JsonElement)>();
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    events.Add((eventName ?? "message", JsonDocument.Parse(data.ToString()).RootElement.Clone()));
                    if (stop(events))
                        return events;
                }
                eventName = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line["data:".Length..].TrimStart());
            }
        }
        return events;
    }
}
