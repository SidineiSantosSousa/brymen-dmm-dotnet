using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Extensions.Hosting;

/// <summary>Registro do multímetro Brymen BM52x no contêiner de serviços do Generic Host.</summary>
public static class BrymenMeterServiceCollectionExtensions
{
    /// <summary>
    /// Registra as opções do medidor (<see cref="BrymenMeterOptions"/>, por
    /// <see cref="IOptions{TOptions}"/>), o <see cref="IBrymenMeterAccessor"/> (singleton) e o serviço hospedado que abre
    /// o medidor na partida do host, com reconexão automática, inicia a leitura contínua (<see cref="BrymenMeter.Start"/>)
    /// e o descarta na parada.
    /// </summary>
    /// <remarks>
    /// Chamar de novo (inclusive com a sobrecarga de <see cref="IConfiguration"/>) só acrescenta configurações: o serviço e o
    /// acessor são registrados uma vez. As opções são lidas na partida do host; mudá-las depois não afeta o medidor aberto.
    /// Para mais de um cabo, use as sobrecargas com nome.
    /// </remarks>
    /// <param name="services">Contêiner de serviços.</param>
    /// <param name="configure">Ajusta as opções (por exemplo, <see cref="BrymenMeterOptions.DevicePath"/> ou um
    /// <see cref="BrymenMeterOptions.DeviceProvider"/> simulado); <see langword="null"/> usa os padrões.</param>
    /// <returns><paramref name="services"/>, para encadear chamadas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> é nulo.</exception>
    public static IServiceCollection AddBrymenMeter(this IServiceCollection services, Action<BrymenMeterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCore(services, Options.DefaultName);
        if (configure is not null)
            services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Como <see cref="AddBrymenMeter(IServiceCollection, Action{BrymenMeterOptions}?)"/>, com as opções lidas de
    /// <paramref name="configuration"/> (por exemplo, <c>builder.Configuration.GetSection("Brymen")</c>) por binding gerado
    /// em tempo de compilação, sem reflexão: compatível com trimming e Native AOT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chaves aceitas (as ausentes mantêm o valor atual): <c>DevicePath</c>, <c>PollInterval</c>, <c>ReportTimeout</c>,
    /// <c>RealTimeAfterMemoryTimeout</c>, <c>ReconnectDelay</c>, <c>AutoReconnect</c>, <c>DrainTimeout</c>,
    /// <c>RecoveryDrainTimeout</c> e a seção <c>LoggerRead</c> (<c>VerifyModel</c>, <c>HeadSettleDelay</c>,
    /// <c>RetryDelay</c>, <c>MaxHeadAttempts</c>, <c>MaxBlockAttempts</c>, <c>MaxRestarts</c>). Os intervalos usam o formato
    /// invariante do <see cref="TimeSpan"/> (<c>"00:00:00.200"</c> = 200 ms).
    /// </para>
    /// <para>
    /// <see cref="BrymenMeterOptions.DeviceProvider"/> e <see cref="BrymenMeterOptions.TimeProvider"/> não vêm da
    /// configuração: ajuste-os por código, chamando também a outra sobrecarga.
    /// </para>
    /// </remarks>
    /// <param name="services">Contêiner de serviços.</param>
    /// <param name="configuration">Seção com as opções.</param>
    /// <returns><paramref name="services"/>, para encadear chamadas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="configuration"/> é nulo.</exception>
    public static IServiceCollection AddBrymenMeter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddCore(services, Options.DefaultName);
        services.Configure<BrymenMeterOptions>(options => BrymenMeterSettings.Bind(configuration, options));
        return services;
    }

    /// <summary>
    /// Registra um medidor com nome, para mais de um cabo no mesmo host: as opções nomeadas (<paramref name="name"/>, por
    /// <see cref="IOptionsMonitor{TOptions}.Get(string)"/>), um <see cref="IBrymenMeterAccessor"/> com a chave
    /// <paramref name="name"/> (<c>GetRequiredKeyedService&lt;IBrymenMeterAccessor&gt;(name)</c> ou
    /// <c>[FromKeyedServices(name)]</c>) e um serviço hospedado só dele.
    /// </summary>
    /// <remarks>
    /// Cada nome tem o seu medidor, o seu laço de leitura e a sua reconexão; dê a cada um o seu
    /// <see cref="BrymenMeterOptions.DevicePath"/> (com dois cabos, o caminho padrão, o primeiro da enumeração, seria o
    /// mesmo para os dois). Chamar de novo com o mesmo nome só acrescenta configurações. O log vai para as categorias
    /// <c>Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService.{nome}</c> e
    /// <c>Brymen.Dmm.Extensions.Hosting.BrymenMeterAccessor.{nome}</c>. O registro sem nome é independente dos nomeados.
    /// </remarks>
    /// <param name="services">Contêiner de serviços.</param>
    /// <param name="name">Nome do medidor: a chave do acessor e o nome das opções.</param>
    /// <param name="configure">Ajusta as opções deste medidor; <see langword="null"/> usa os padrões.</param>
    /// <returns><paramref name="services"/>, para encadear chamadas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> ou <paramref name="name"/> é nulo.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> é vazio ou só tem espaços.</exception>
    public static IServiceCollection AddBrymenMeter(this IServiceCollection services, string name, Action<BrymenMeterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        AddCore(services, name);
        if (configure is not null)
            services.Configure(name, configure);
        return services;
    }

    /// <summary>
    /// Como <see cref="AddBrymenMeter(IServiceCollection, string, Action{BrymenMeterOptions}?)"/>, com as opções deste
    /// medidor lidas de <paramref name="configuration"/> (as mesmas chaves de
    /// <see cref="AddBrymenMeter(IServiceCollection, IConfiguration)"/>), por exemplo
    /// <c>builder.Configuration.GetSection("Brymen:Bancada1")</c>.
    /// </summary>
    /// <param name="services">Contêiner de serviços.</param>
    /// <param name="name">Nome do medidor: a chave do acessor e o nome das opções.</param>
    /// <param name="configuration">Seção com as opções.</param>
    /// <returns><paramref name="services"/>, para encadear chamadas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="name"/> ou <paramref name="configuration"/> é nulo.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> é vazio ou só tem espaços.</exception>
    public static IServiceCollection AddBrymenMeter(this IServiceCollection services, string name, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);
        AddCore(services, name);
        services.Configure<BrymenMeterOptions>(name, options => BrymenMeterSettings.Bind(configuration, options));
        return services;
    }

    // Um acessor e um serviço por nome, mesmo com várias chamadas. O vazio (Options.DefaultName) é o registro sem nome,
    // sem chave.
    private static void AddCore(IServiceCollection services, string name)
    {
        services.AddOptions();
        bool keyed = name.Length > 0;
        if (services.Any(d => d.ServiceType == typeof(BrymenMeterAccessor) && d.IsKeyedService == keyed && (!keyed || Equals(d.ServiceKey, name))))
            return;

        if (keyed)
        {
            services.AddKeyedSingleton(name, static (provider, key) => CreateAccessor(provider, (string)key!));
            services.AddKeyedSingleton<IBrymenMeterAccessor>(name, static (provider, key) => provider.GetRequiredKeyedService<BrymenMeterAccessor>(key));
            services.AddSingleton<IHostedService>(provider => CreateService(provider, provider.GetRequiredKeyedService<BrymenMeterAccessor>(name), name));
        }
        else
        {
            services.AddSingleton(static provider => CreateAccessor(provider, Options.DefaultName));
            services.AddSingleton<IBrymenMeterAccessor>(static provider => provider.GetRequiredService<BrymenMeterAccessor>());
            services.AddSingleton<IHostedService>(static provider => CreateService(provider, provider.GetRequiredService<BrymenMeterAccessor>(), Options.DefaultName));
        }
    }

    private static BrymenMeterAccessor CreateAccessor(IServiceProvider provider, string name)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<BrymenMeterOptions>>();
        return new BrymenMeterAccessor(() => options.Get(name), CreateLogger(provider, "Brymen.Dmm.Extensions.Hosting.BrymenMeterAccessor", name));
    }

    private static BrymenMeterHostedService CreateService(IServiceProvider provider, BrymenMeterAccessor accessor, string name)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<BrymenMeterOptions>>();
        return new BrymenMeterHostedService(accessor, () => options.Get(name), CreateLogger(provider, "Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService", name));
    }

    // A categoria do tipo, como a de um ILogger<T>; com nome, a do tipo seguida de ".{nome}", que os filtros da categoria do
    // tipo também pegam.
    private static ILogger CreateLogger(IServiceProvider provider, string category, string name)
    {
        var factory = provider.GetService<ILoggerFactory>();
        if (factory is null)
            return NullLogger.Instance;
        return factory.CreateLogger(name.Length == 0 ? category : $"{category}.{name}");
    }
}
