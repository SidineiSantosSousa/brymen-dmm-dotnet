using System.Net;
using System.Net.Http.Json;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Brymen.Dmm.Api.Tests;

/// <summary>Transporte HID sem nenhum cabo conectado (provider falso; o cabo real nunca é tocado).</summary>
public sealed class NoDeviceFactory : ApiFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [.. base.Settings.Where(s => s.Key != "Meter:Transport"), new("Meter:Transport", "Hid")];

    protected override void ConfigureTestServices(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IHidDeviceProvider>(new EmptyHidDeviceProvider()));

    private sealed class EmptyHidDeviceProvider : IHidDeviceProvider
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => [];

        public IHidDevice Open(HidDeviceInfo device) => throw new HidException($"Dispositivo {device.Path} ausente.");
    }
}

/// <summary>
/// <c>Meter:DevicePath</c> fora da enumeração, num nó sem permissão: a consulta direta do provider
/// (<see cref="IHidDevicePathProbe"/>, como o hidraw de um contêiner) falha com a dica da regra udev.
/// </summary>
public sealed class ForbiddenDevicePathFactory : ApiFactory
{
    public const string DevicePath = "/dev/bu86x";
    public const string ProbeMessage = "Sem permissão para abrir /dev/bu86x (EACCES). Instale a regra udev.";

    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [.. base.Settings.Where(s => s.Key != "Meter:Transport"), new("Meter:Transport", "Hid"), new("Meter:DevicePath", DevicePath)];

    protected override void ConfigureTestServices(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IHidDeviceProvider>(new ForbiddenProbeProvider()));

    private sealed class ForbiddenProbeProvider : IHidDeviceProvider, IHidDevicePathProbe
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => [];

        public IHidDevice Open(HidDeviceInfo device) => throw new HidException($"Dispositivo {device.Path} ausente.");

        public HidDeviceInfo? Probe(string devicePath) => throw new HidException(ProbeMessage, nativeError: 13);
    }
}

public sealed class ForbiddenDevicePathTests(ForbiddenDevicePathFactory factory) : IClassFixture<ForbiddenDevicePathFactory>
{
    [Fact]
    public async Task Link_answers_503_E9015_with_the_probe_error()
    {
        var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/meter/link", new { enabled = true });

        var problem = await TestHelpers.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "E9015");
        Assert.Equal($"Cabo adaptador USB não encontrado. {ForbiddenDevicePathFactory.ProbeMessage}", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Polling_records_the_probe_error_in_lastError()
    {
        var client = factory.CreateClient();

        var message = await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("lastError") is { ValueKind: System.Text.Json.JsonValueKind.Object } error
                ? error.GetProperty("message").GetString()
                : null;
        }, "o erro do polling");

        Assert.Contains(ForbiddenDevicePathFactory.ProbeMessage, message);
    }
}

public sealed class NoDeviceTests(NoDeviceFactory factory) : IClassFixture<NoDeviceFactory>
{
    [Fact]
    public async Task Link_answers_503_E9015()
    {
        var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/v1/meter/link", new { enabled = true });

        var problem = await TestHelpers.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "E9015");
        Assert.Contains("não encontrado", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Meter_status_records_E9015()
    {
        var client = factory.CreateClient();

        var meter = await TestHelpers.EventuallyAsync(async () =>
        {
            var m = await client.GetJsonAsync("/api/v1/meter");
            return m.GetProperty("lastError").ValueKind == System.Text.Json.JsonValueKind.Object ? (object)m : null;
        }, "o erro E9015 do polling");

        var status = (System.Text.Json.JsonElement)meter;
        Assert.Equal("hid", status.GetProperty("transport").GetString());
        Assert.Equal("E9015", status.GetProperty("lastError").GetProperty("code").GetString());
        Assert.False(status.GetProperty("deviceOpen").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, status.GetProperty("device").ValueKind);
    }

    [Fact]
    public async Task Latest_reading_answers_503_E9015()
    {
        var client = factory.CreateClient();

        var problem = await TestHelpers.EventuallyAsync(async () =>
        {
            using var response = await client.GetAsync("/api/v1/readings/latest");
            string body = await response.Content.ReadAsStringAsync();
            return response.StatusCode == HttpStatusCode.ServiceUnavailable && body.Contains("E9015", StringComparison.Ordinal) ? body : null;
        }, "503 E9015 em readings/latest");

        Assert.Contains("\"code\":\"E9015\"", problem);
    }

    [Fact]
    public async Task Logger_import_answers_503_E9015()
    {
        var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/logger/imports", null);

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "E9015");
        Assert.Equal(0, (await client.GetJsonAsync("/api/v1/logger/imports")).GetArrayLength());
    }

    [Fact]
    public async Task Readiness_is_unhealthy_without_cable()
    {
        var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready");
        using var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
