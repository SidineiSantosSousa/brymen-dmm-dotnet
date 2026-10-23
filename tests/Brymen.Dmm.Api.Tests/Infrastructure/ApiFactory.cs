using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Brymen.Dmm.Hid;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Brymen.Dmm.Api.Tests.Infrastructure;

/// <summary>
/// Host da API com o transporte Simulated (capturas reais), polling rápido e um DataDirectory temporário
/// exclusivo, apagado no descarte. Nunca usa o cabo real.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "brymen-dmm-api-tests", Guid.NewGuid().ToString("N"));

    internal SimulatedBm52xMeter Responder => Services.GetRequiredService<SimulatedBm52xMeter>();

    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new("DataDirectory", DataDirectory),
        new("Meter:Transport", "Simulated"),
        new("Meter:AutoLink", "true"),
        new("Meter:PollInterval", "00:00:00.040"),
        new("Meter:ReportTimeout", "00:00:00.250"),
        new("Meter:ReconnectDelay", "00:00:00.200"),
        new("Meter:SimulatedLatency", "00:00:00.002"),
    ];

    /// <summary>Ambiente do host; Production desliga a página de exceção do desenvolvedor.</summary>
    protected virtual string EnvironmentName => "Development";

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            // Sem o fallback por reflexão, como no publish trimmed/AOT: um tipo fora do JsonSerializerContext falha aqui
            // também, tanto na API REST quanto no protocolo JSON do SignalR.
            services.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => RemoveReflection(options.SerializerOptions));
            services.PostConfigure<JsonHubProtocolOptions>(options => RemoveReflection(options.PayloadSerializerOptions));
            ConfigureTestServices(services);
        });
    }

    private static void RemoveReflection(JsonSerializerOptions options)
    {
        var chain = options.TypeInfoResolverChain;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i] is DefaultJsonTypeInfoResolver)
                chain.RemoveAt(i);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDataDirectory();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DeleteDataDirectory();
    }

    private void DeleteDataDirectory()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
