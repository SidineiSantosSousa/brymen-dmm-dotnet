using Brymen.Dmm.Hid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Extensions.Hosting.Tests;

/// <summary>Registro de <see cref="BrymenMeterServiceCollectionExtensions.AddBrymenMeter(IServiceCollection, Action{BrymenMeterOptions}?)"/> e binding da configuração.</summary>
public sealed class ServiceCollectionTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void AddBrymenMeter_registers_one_hosted_service_and_a_singleton_accessor()
    {
        var services = new ServiceCollection();
        services.AddBrymenMeter();
        services.AddBrymenMeter(o => o.PollInterval = TimeSpan.FromSeconds(1));
        services.AddBrymenMeter(Configuration(("ReportTimeout", "00:00:02")));

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        var accessor = provider.GetRequiredService<IBrymenMeterAccessor>();
        var options = provider.GetRequiredService<IOptions<BrymenMeterOptions>>().Value;

        Assert.IsType<BrymenMeterHostedService>(Assert.Single(hosted));
        Assert.Same(accessor, provider.GetRequiredService<IBrymenMeterAccessor>());
        Assert.Same(accessor, provider.GetRequiredService<BrymenMeterAccessor>());
        Assert.Null(accessor.Meter);
        Assert.Null(accessor.LastReading);
        Assert.Null(accessor.LastError);
        Assert.Equal(MeterState.Closed, accessor.State);
        // As configurações se acumulam, na ordem do registro.
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ReportTimeout);
    }

    [Fact]
    public void AddBrymenMeter_binds_every_key_of_the_configuration_section()
    {
        var root = Configuration(
            ("Brymen:DevicePath", "/dev/brymen-bu86x"),
            ("Brymen:PollInterval", "00:00:00.500"),
            ("Brymen:ReportTimeout", "00:00:02"),
            ("Brymen:RealTimeAfterMemoryTimeout", "00:00:04"),
            ("Brymen:ReconnectDelay", "00:00:05"),
            ("Brymen:AutoReconnect", "false"),
            ("Brymen:DrainTimeout", "00:00:00.010"),
            ("Brymen:RecoveryDrainTimeout", "00:00:00.050"),
            ("Brymen:LoggerRead:VerifyModel", "false"),
            ("Brymen:LoggerRead:HeadSettleDelay", "00:00:00.300"),
            ("Brymen:LoggerRead:RetryDelay", "00:00:00.020"),
            ("Brymen:LoggerRead:MaxHeadAttempts", "5"),
            ("Brymen:LoggerRead:MaxBlockAttempts", "6"),
            ("Brymen:LoggerRead:MaxRestarts", "0"));
        var services = new ServiceCollection();
        services.AddBrymenMeter(root.GetSection("Brymen"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<BrymenMeterOptions>>().Value;

        Assert.Equal("/dev/brymen-bu86x", options.DevicePath);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ReportTimeout);
        Assert.Equal(TimeSpan.FromSeconds(4), options.RealTimeAfterMemoryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReconnectDelay);
        Assert.False(options.AutoReconnect);
        Assert.Equal(TimeSpan.FromMilliseconds(10), options.DrainTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.RecoveryDrainTimeout);
        Assert.False(options.LoggerRead.VerifyModel);
        Assert.Equal(TimeSpan.FromMilliseconds(300), options.LoggerRead.HeadSettleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(20), options.LoggerRead.RetryDelay);
        Assert.Equal(5, options.LoggerRead.MaxHeadAttempts);
        Assert.Equal(6, options.LoggerRead.MaxBlockAttempts);
        Assert.Equal(0, options.LoggerRead.MaxRestarts);
    }

    [Fact]
    public void Missing_keys_keep_the_defaults_and_code_can_complete_the_configuration()
    {
        var provider = new SimulatedHidDeviceProvider();
        var services = new ServiceCollection();
        services.AddBrymenMeter(Configuration(("PollInterval", "00:00:01"), ("LoggerRead:MaxRestarts", "1")));
        services.AddBrymenMeter(o => o.DeviceProvider = provider);

        using var container = services.BuildServiceProvider();
        var options = container.GetRequiredService<IOptions<BrymenMeterOptions>>().Value;
        var defaults = new BrymenMeterOptions();

        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
        Assert.Same(provider, options.DeviceProvider);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.Null(options.DevicePath);
        Assert.Equal(defaults.ReportTimeout, options.ReportTimeout);
        Assert.Equal(defaults.RealTimeAfterMemoryTimeout, options.RealTimeAfterMemoryTimeout);
        Assert.Equal(defaults.ReconnectDelay, options.ReconnectDelay);
        Assert.True(options.AutoReconnect);
        Assert.Equal(1, options.LoggerRead.MaxRestarts);
        Assert.Equal(defaults.LoggerRead.MaxBlockAttempts, options.LoggerRead.MaxBlockAttempts);
        Assert.Equal(defaults.LoggerRead.HeadSettleDelay, options.LoggerRead.HeadSettleDelay);
    }

    [Fact]
    public void An_invalid_value_fails_when_the_options_are_read()
    {
        var services = new ServiceCollection();
        services.AddBrymenMeter(Configuration(("PollInterval", "rápido")));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<BrymenMeterOptions>>().Value);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => BrymenMeterServiceCollectionExtensions.AddBrymenMeter(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddBrymenMeter((IConfiguration)null!));
    }
}
