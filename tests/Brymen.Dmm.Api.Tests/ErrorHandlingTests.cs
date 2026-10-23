using System.Net;
using System.Text;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Tests.Infrastructure;
using Brymen.Dmm.Hid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Brymen.Dmm.Api.Tests;

/// <summary>ProblemDetails com <c>code</c> em todas as respostas de erro, validação de entrada e ids/caminhos maliciosos.</summary>
public sealed class ErrorHandlingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Unknown_route_is_a_problem_with_code()
    {
        using var response = await _client.GetAsync("/api/v1/nao-existe");

        var problem = await TestHelpers.AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal("Recurso não encontrado", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Wrong_method_is_a_problem_with_code()
    {
        using var response = await _client.DeleteAsync("/api/v1/meter");

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
    }

    [Theory]
    [InlineData("{\"high\": \"abc\"}")]
    [InlineData("{\"high\": 1.5")]
    [InlineData("[]")]
    public async Task Malformed_body_is_a_validation_problem(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.PutAsync("/api/v1/comparator", content);

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
        Assert.Equal(200, (await _client.GetJsonAsync("/api/v1/comparator")).GetProperty("high").GetDouble());
    }

    [Theory]
    [InlineData("/api/v1/acquisition/samples?offset=-1")]
    [InlineData("/api/v1/acquisition/samples?limit=10001")]
    [InlineData("/api/v1/acquisition/samples?offset=abc")]
    [InlineData("/api/v1/acquisition/samples?limit=1.5")]
    [InlineData("/api/v1/logger/imports/0123456789abcdef0123456789abcdef/sessions/1/samples?limit=0")]
    public async Task Invalid_pagination_is_a_validation_problem(string url)
    {
        using var response = await _client.GetAsync(url);

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
    }

    [Fact]
    public async Task Interval_out_of_range_is_rejected_in_every_mode()
    {
        var before = await _client.GetJsonAsync("/api/v1/acquisition/settings");

        using (var tooLong = await _client.PutAsync("/api/v1/acquisition/settings", Json("{\"mode\":\"fullSpeed\",\"intervalSeconds\":100000}")))
            await TestHelpers.AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "E9007");
        using (var negative = await _client.PutAsync("/api/v1/acquisition/settings", Json("{\"mode\":\"manual\",\"intervalSeconds\":-5}")))
            await TestHelpers.AssertProblemAsync(negative, HttpStatusCode.BadRequest, "E9007");
        using (var badMode = await _client.PutAsync("/api/v1/acquisition/settings", Json("{\"mode\":\"turbo\"}")))
            await TestHelpers.AssertProblemAsync(badMode, HttpStatusCode.BadRequest, "VALIDATION");

        var after = await _client.GetJsonAsync("/api/v1/acquisition/settings");
        Assert.Equal(before.GetProperty("intervalSeconds").GetInt32(), after.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(before.GetProperty("mode").GetString(), after.GetProperty("mode").GetString());

        // 0 é o "Full Speed" do original: aceito fora do modo interval, sem mudar o intervalo guardado.
        var zero = await _client.SendJsonAsync(HttpMethod.Put, "/api/v1/acquisition/settings", new { mode = "fullSpeed", intervalSeconds = 0 });
        Assert.Equal(before.GetProperty("intervalSeconds").GetInt32(), zero.GetProperty("intervalSeconds").GetInt32());
    }

    [Theory]
    [InlineData("/api/v1/logger/imports/0123456789abcdef0123456789abcdef%0A")]
    [InlineData("/api/v1/logger/imports/..%2F..%2Fsettings.json")]
    [InlineData("/api/v1/logger/imports/..%5C..%5Csettings.json/image")]
    [InlineData("/api/v1/logger/imports/%2E%2E/sessions")]
    public async Task Malicious_import_ids_are_not_found(string url)
    {
        using var response = await _client.GetAsync(url);

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Delete_with_malicious_id_touches_nothing()
    {
        string settings = Path.Combine(factory.DataDirectory, "settings.json");
        await File.WriteAllTextAsync(settings, "{}");

        using var response = await _client.DeleteAsync("/api/v1/logger/imports/..%2Fsettings");

        await TestHelpers.AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.True(File.Exists(settings));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef\n", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("../0123456789abcdef0123456789abcd", false)]
    [InlineData("", false)]
    public void Job_id_validation_is_anchored(string id, bool valid)
        => Assert.Equal(valid, LoggerImportService.IsValidId(id));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}

/// <summary>Host em Production, com um provider HID que falha de um jeito inesperado.</summary>
public sealed class ProductionFactory : ApiFactory
{
    public const string Secret = "detalhe-interno-que-nao-pode-vazar";

    protected override string EnvironmentName => "Production";

    protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [.. base.Settings.Where(s => s.Key is not ("Meter:Transport" or "Meter:AutoLink")), new("Meter:Transport", "Hid"), new("Meter:AutoLink", "false")];

    protected override void ConfigureTestServices(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IHidDeviceProvider>(new BrokenHidDeviceProvider()));

    private sealed class BrokenHidDeviceProvider : IHidDeviceProvider
    {
        public IReadOnlyList<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId) => throw new InvalidOperationException(Secret);

        public IHidDevice Open(HidDeviceInfo device) => throw new InvalidOperationException(Secret);
    }
}

/// <summary>Em produção, erros não expõem exceção nem stack trace.</summary>
public sealed class ProductionErrorTests(ProductionFactory factory) : IClassFixture<ProductionFactory>
{
    [Fact]
    public async Task Unhandled_exception_is_a_generic_problem_without_details()
    {
        var client = factory.CreateClient();

        using var response = await client.PutAsync("/api/v1/meter/link", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json"));

        var problem = await TestHelpers.AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL");
        string body = problem.GetRawText();
        Assert.DoesNotContain(ProductionFactory.Secret, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain(" at ", body);
        Assert.False(problem.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task Bad_request_is_a_validation_problem_without_details()
    {
        var client = factory.CreateClient();

        using var response = await client.PutAsync("/api/v1/comparator", new StringContent("{\"high\":", Encoding.UTF8, "application/json"));

        var problem = await TestHelpers.AssertProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION");
        Assert.DoesNotContain(" at ", problem.GetRawText());
        Assert.False(problem.TryGetProperty("exception", out _));
    }
}
