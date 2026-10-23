using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Recuperação do polling: cabo removido e recolocado, medidor mudo e quadros inválidos.</summary>
public sealed class ReconnectTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private SimulatedHidDevice Device => ((SimulatedHidDeviceProvider)factory.Services.GetRequiredService<IHidDeviceProvider>()).Devices[0];

    public async Task InitializeAsync()
    {
        factory.Responder.FaultInjector = null;
        await TestHelpers.WaitForReadingAsync(_client);
    }

    public Task DisposeAsync()
    {
        factory.Responder.FaultInjector = null;
        Device.Reconnect();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Unplugged_cable_reports_E9015_and_recovers_when_plugged_back()
    {
        Device.Disconnect();

        var meter = await WaitForMeterAsync(m => m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e
            && e.GetProperty("code").GetString() == "E9015" && m.GetProperty("state").GetString() == "reconnecting");
        Assert.True(meter.GetProperty("linkEnabled").GetBoolean());
        Assert.False(meter.GetProperty("deviceOpen").GetBoolean());
        await AssertLatestUnavailableAsync("E9015");
        using (var ready = await _client.GetAsync("/health/ready"))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode); // o transporte simulado está sempre pronto

        long before = meter.GetProperty("readingCount").GetInt64();
        Device.Reconnect();

        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > before);
        var recovered = await _client.GetJsonAsync("/api/v1/meter");
        Assert.Equal("connected", recovered.GetProperty("state").GetString());
        Assert.True(recovered.GetProperty("deviceOpen").GetBoolean());
    }

    [Fact]
    public async Task Silent_meter_reports_E9016_on_the_third_failure_and_recovers()
    {
        bool silent = true;
        factory.Responder.FaultInjector = (command, _) =>
            Volatile.Read(ref silent) && command == Bm52xCommands.RealTime ? SimulatedFault.Drop : SimulatedFault.None;

        var meter = await WaitForMeterAsync(m => m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e
            && e.GetProperty("code").GetString() == "E9016");
        Assert.Equal("reconnecting", meter.GetProperty("state").GetString());
        // Sem leitura atual: nada de servir a última leitura boa como se fosse de agora.
        await AssertLatestUnavailableAsync("E9016", "W7012");

        long before = meter.GetProperty("readingCount").GetInt64();
        Volatile.Write(ref silent, false);

        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > before);
        Assert.Equal("connected", (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Polling_failures_keep_the_api_messages_with_the_cause()
    {
        // A W7012 da API avisa da nova tentativa automática; a causa é a do Bu86xLink, sem a mensagem da biblioteca.
        const string cause = "(Sem resposta do medidor ao comando 52 66.)";
        bool silent = true;
        factory.Responder.FaultInjector = (command, _) =>
            Volatile.Read(ref silent) && command == Bm52xCommands.RealTime ? SimulatedFault.Drop : SimulatedFault.None;
        try
        {
            var noData = await WaitForMeterAsync(m => LastErrorCode(m) == "W7012");
            Assert.Equal($"{Errors.Messages.NoDataReceived} {cause}", noData.GetProperty("lastError").GetProperty("message").GetString());
            var timeout = await WaitForMeterAsync(m => LastErrorCode(m) == "E9016");
            Assert.Equal($"{Errors.Messages.TransmissionTimeout} {cause}", timeout.GetProperty("lastError").GetProperty("message").GetString());
        }
        finally
        {
            Volatile.Write(ref silent, false);
        }
        await TestHelpers.WaitForReadingAsync(_client);

        bool corrupt = true;
        factory.Responder.FaultInjector = (command, _) =>
            Volatile.Read(ref corrupt) && command == Bm52xCommands.RealTime ? SimulatedFault.CorruptModel : SimulatedFault.None;
        try
        {
            var invalid = await WaitForMeterAsync(m => LastErrorCode(m) == "W8011");
            string? message = invalid.GetProperty("lastError").GetProperty("message").GetString();
            Assert.StartsWith("Quadro inválido recebido do medidor: ", message, StringComparison.Ordinal);
            Assert.True(message!.Length > "Quadro inválido recebido do medidor: ".Length, message);
        }
        finally
        {
            Volatile.Write(ref corrupt, false);
        }
    }

    [Fact]
    public async Task Persistent_invalid_frames_stop_serving_the_last_good_reading()
    {
        bool corrupt = true;
        factory.Responder.FaultInjector = (command, _) =>
            Volatile.Read(ref corrupt) && command == Bm52xCommands.RealTime ? SimulatedFault.CorruptModel : SimulatedFault.None;

        var meter = await WaitForMeterAsync(m => m.GetProperty("state").GetString() == "reconnecting"
            && m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "W8011");
        Assert.True(meter.GetProperty("deviceOpen").GetBoolean());
        await AssertLatestUnavailableAsync("W8011");

        long before = meter.GetProperty("readingCount").GetInt64();
        Volatile.Write(ref corrupt, false);

        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > before);
        Assert.Equal("connected", (await _client.GetJsonAsync("/api/v1/meter")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_single_invalid_frame_keeps_the_meter_connected()
    {
        int count = 0;
        factory.Responder.FaultInjector = (command, _) =>
            command == Bm52xCommands.RealTime && Interlocked.Increment(ref count) == 1 ? SimulatedFault.CorruptModel : SimulatedFault.None;

        await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await _client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "W8011" ? "ok" : null;
        }, "o W8011 registrado");

        var meter = await _client.GetJsonAsync("/api/v1/meter");
        Assert.Equal("connected", meter.GetProperty("state").GetString());
        long before = meter.GetProperty("readingCount").GetInt64();
        await TestHelpers.WaitForReadingAsync(_client, r => r.GetProperty("sequence").GetInt64() > before);
    }

    [Fact]
    public async Task Deliberate_unlink_after_a_recovered_cable_fault_answers_409_E9002()
    {
        Device.Disconnect();
        await WaitForMeterAsync(m => m.GetProperty("state").GetString() == "reconnecting"
            && m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "E9015");
        Device.Reconnect();
        await TestHelpers.WaitForReadingAsync(_client);

        var unlinked = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
        try
        {
            // O histórico continua em lastError, mas o motivo atual é o link desativado pelo usuário.
            Assert.Equal("E9015", unlinked.GetProperty("lastError").GetProperty("code").GetString());
            using var latest = await _client.GetAsync("/api/v1/readings/latest");
            await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.Conflict, "E9002");
        }
        finally
        {
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        }
    }

    [Fact]
    public async Task Unlink_without_the_cable_answers_409_E9002_and_a_link_that_fails_answers_503_E9015()
    {
        Device.Disconnect();
        try
        {
            await WaitForMeterAsync(m => m.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } e && e.GetProperty("code").GetString() == "E9015");
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = false });
            using (var latest = await _client.GetAsync("/api/v1/readings/latest"))
                await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.Conflict, "E9002");

            // Uma importação sem o cabo também registra E9015, mas o link continua desativado pelo usuário.
            using (var import = await _client.PostAsync("/api/v1/logger/imports", null))
                await TestHelpers.AssertProblemAsync(import, HttpStatusCode.ServiceUnavailable, "E9015");
            using (var latest = await _client.GetAsync("/api/v1/readings/latest"))
                await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.Conflict, "E9002");

            // O Link sem o cabo falha e deixa o link desativado por falta do cabo: aí o motivo é E9015.
            using (var link = await _client.PutAsJsonAsync("/api/v1/meter/link", new { enabled = true }))
                await TestHelpers.AssertProblemAsync(link, HttpStatusCode.ServiceUnavailable, "E9015");
            using (var latest = await _client.GetAsync("/api/v1/readings/latest"))
                await TestHelpers.AssertProblemAsync(latest, HttpStatusCode.ServiceUnavailable, "E9015");
        }
        finally
        {
            Device.Reconnect();
            await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/meter/link", new { enabled = true });
        }
        await TestHelpers.WaitForReadingAsync(_client);
    }

    private async Task<JsonElement> WaitForMeterAsync(Func<JsonElement, bool> predicate)
    {
        object found = await TestHelpers.EventuallyAsync(async () =>
        {
            var meter = await _client.GetJsonAsync("/api/v1/meter");
            return predicate(meter) ? (object)meter : null;
        }, "o estado esperado do medidor");
        return (JsonElement)found;
    }

    private static string? LastErrorCode(JsonElement meter)
        => meter.GetProperty("lastError") is { ValueKind: JsonValueKind.Object } error ? error.GetProperty("code").GetString() : null;

    private async Task AssertLatestUnavailableAsync(params string[] codes)
    {
        string body = await TestHelpers.EventuallyAsync(async () =>
        {
            using var response = await _client.GetAsync("/api/v1/readings/latest");
            string text = await response.Content.ReadAsStringAsync();
            return response.StatusCode == HttpStatusCode.ServiceUnavailable ? text : null;
        }, "503 em readings/latest");
        string? code = JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();
        Assert.Contains(code, codes);
    }
}
