using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Comparator;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Hubs;
using Brymen.Dmm.Api.Meter;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Security;
using Brymen.Dmm.Hid;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api;

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra a configuração tipada, a sessão do medidor, o polling, o gravador, o comparador, a importação, o hub
    /// SignalR e o CORS opcional.
    /// </summary>
    public static IServiceCollection AddBrymenDmm(this IServiceCollection services, IConfiguration configuration)
    {
        // Um "null" no appsettings.json pode chegar como "": os dois significam "não configurado".
        services.AddOptions<MeterOptions>()
            .Bind(configuration.GetSection(MeterOptions.SectionName))
            .PostConfigure(o => o.DevicePath = NullIfBlank(o.DevicePath))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MeterOptions>, MeterOptionsValidator>();
        services.AddOptions<AcquisitionOptions>()
            .Bind(configuration.GetSection(AcquisitionOptions.SectionName))
            .PostConfigure(o => o.BackupDirectory = NullIfBlank(o.BackupDirectory))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AcquisitionOptions>, AcquisitionOptionsValidator>();
        services.AddOptions<LoggerOptions>().Bind(configuration.GetSection(LoggerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LoggerOptions>, LoggerOptionsValidator>();

        services.AddSingleton(sp => DataDirectory.FromConfiguration(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton(TimeProvider.System);

        // Medidor simulado com as capturas reais (Brymen.Dmm.Hid); reproduz também o 8A que, logo depois do 88, avança
        // como um 89.
        services.AddSingleton(sp => SimulatedBm52xMeter.FromCaptures(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHidDeviceProvider>(MeterTransportFactory.Create);
        services.AddSingleton<MeterSession>();
        services.AddSingleton(sp =>
        {
            // SSE e SignalR terminam no início do desligamento, sem esperar o ShutdownTimeout do Kestrel.
            var broadcaster = new ReadingBroadcaster();
            sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(broadcaster.Complete);
            return broadcaster;
        });
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ComparatorService>();
        services.AddSingleton<Recorder>();
        services.AddSingleton<LoggerImportService>();
        services.AddRealtime(configuration);
        // Depois do SignalRForwarder: ele assina o broadcaster na partida, antes da primeira leitura.
        services.AddHostedService<MeterPollingService>();

        services.AddHealthChecks().AddCheck<MeterReadyHealthCheck>("meter", tags: ["ready"]);
        return services;
    }

    /// <summary>Hub <c>/hubs/meter</c> (seção <c>SignalR</c>), heartbeat do SSE (seção <c>Sse</c>) e CORS (seção <c>Cors</c>).</summary>
    private static void AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<RealtimeOptionsValidator>();
        services.AddOptions<SignalRSettings>().Bind(configuration.GetSection(SignalRSettings.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SignalRSettings>>(sp => sp.GetRequiredService<RealtimeOptionsValidator>());
        services.AddOptions<SseSettings>().Bind(configuration.GetSection(SseSettings.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SseSettings>>(sp => sp.GetRequiredService<RealtimeOptionsValidator>());
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .PostConfigure(o => o.AllowedOrigins = RealtimeOptionsValidator.NormalizeOrigins(o.AllowedOrigins))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CorsSettings>>(sp => sp.GetRequiredService<RealtimeOptionsValidator>());

        // Hub não tipado e protocolo JSON só com os tipos do contexto gerado: compatível com trimming/Native AOT.
        services.AddSignalR()
            .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
        services.AddOptions<HubOptions>().Configure<IOptions<SignalRSettings>>((hub, settings) =>
        {
            hub.EnableDetailedErrors = settings.Value.EnableDetailedErrors;
            hub.KeepAliveInterval = settings.Value.KeepAliveInterval;
        });
        services.AddSingleton<MeterHubConnections>();
        services.AddHostedService<SignalRForwarder>();

        // Sem origens configuradas não há política, e o Program não liga o middleware. O CrossSiteRequestGuard vale
        // sempre: o CORS não impede que uma página de outra origem dispare um POST simples ou abra o WebSocket do hub.
        services.AddSingleton<CrossSiteRequestGuard>();
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<CorsSettings>>((cors, settings) =>
        {
            string[] origins = settings.Value.AllowedOrigins;
            if (origins.Length > 0)
                cors.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
        });
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
