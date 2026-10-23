using Microsoft.Extensions.Configuration;

namespace Brymen.Dmm.Extensions.Hosting;

/// <summary>
/// As opções de <see cref="BrymenMeterOptions"/> que vêm da configuração. O binding vai para esta classe, e não direto
/// para <see cref="BrymenMeterOptions"/>: o gerador do binding não aceita as propriedades
/// <see cref="BrymenMeterOptions.DeviceProvider"/> (interface) e <see cref="BrymenMeterOptions.TimeProvider"/> (classe
/// abstrata). Tudo é anulável: só as chaves presentes mudam as opções.
/// </summary>
internal sealed class BrymenMeterSettings
{
    public string? DevicePath { get; set; }

    public MeterFamilySelection? Family { get; set; }

    public TimeSpan? PollInterval { get; set; }

    public TimeSpan? ReportTimeout { get; set; }

    public TimeSpan? RealTimeAfterMemoryTimeout { get; set; }

    public TimeSpan? RealTimeResendTimeout { get; set; }

    public TimeSpan? RealTimeResendBudget { get; set; }

    public TimeSpan? ReconnectDelay { get; set; }

    public bool? AutoReconnect { get; set; }

    public TimeSpan? DrainTimeout { get; set; }

    public TimeSpan? RecoveryDrainTimeout { get; set; }

    public LoggerReadSettings? LoggerRead { get; set; }

    /// <summary>Lê <paramref name="configuration"/> e aplica as chaves presentes em <paramref name="options"/>.</summary>
    /// <exception cref="InvalidOperationException">Um valor não pôde ser convertido (por exemplo, um intervalo inválido).</exception>
    public static void Bind(IConfiguration configuration, BrymenMeterOptions options)
    {
        var settings = new BrymenMeterSettings();
        // Interceptado pelo gerador do binding (EnableConfigurationBindingGenerator): sem reflexão.
        configuration.Bind(settings);
        settings.ApplyTo(options);
    }

    public void ApplyTo(BrymenMeterOptions options)
    {
        if (DevicePath is not null)
            options.DevicePath = DevicePath;
        if (Family is { } family)
            options.Family = family;
        if (PollInterval is { } pollInterval)
            options.PollInterval = pollInterval;
        if (ReportTimeout is { } reportTimeout)
            options.ReportTimeout = reportTimeout;
        if (RealTimeAfterMemoryTimeout is { } realTimeAfterMemoryTimeout)
            options.RealTimeAfterMemoryTimeout = realTimeAfterMemoryTimeout;
        if (RealTimeResendTimeout is { } realTimeResendTimeout)
            options.RealTimeResendTimeout = realTimeResendTimeout;
        if (RealTimeResendBudget is { } realTimeResendBudget)
            options.RealTimeResendBudget = realTimeResendBudget;
        if (ReconnectDelay is { } reconnectDelay)
            options.ReconnectDelay = reconnectDelay;
        if (AutoReconnect is { } autoReconnect)
            options.AutoReconnect = autoReconnect;
        if (DrainTimeout is { } drainTimeout)
            options.DrainTimeout = drainTimeout;
        if (RecoveryDrainTimeout is { } recoveryDrainTimeout)
            options.RecoveryDrainTimeout = recoveryDrainTimeout;
        LoggerRead?.ApplyTo(options.LoggerRead ??= new LoggerReadOptions());
    }
}

/// <summary>As opções de <see cref="LoggerReadOptions"/> que vêm da configuração (seção <c>LoggerRead</c>).</summary>
internal sealed class LoggerReadSettings
{
    public bool? VerifyModel { get; set; }

    public TimeSpan? HeadSettleDelay { get; set; }

    public TimeSpan? RetryDelay { get; set; }

    public int? MaxHeadAttempts { get; set; }

    public int? MaxBlockAttempts { get; set; }

    public int? MaxRestarts { get; set; }

    public void ApplyTo(LoggerReadOptions options)
    {
        if (VerifyModel is { } verifyModel)
            options.VerifyModel = verifyModel;
        if (HeadSettleDelay is { } headSettleDelay)
            options.HeadSettleDelay = headSettleDelay;
        if (RetryDelay is { } retryDelay)
            options.RetryDelay = retryDelay;
        if (MaxHeadAttempts is { } maxHeadAttempts)
            options.MaxHeadAttempts = maxHeadAttempts;
        if (MaxBlockAttempts is { } maxBlockAttempts)
            options.MaxBlockAttempts = maxBlockAttempts;
        if (MaxRestarts is { } maxRestarts)
            options.MaxRestarts = maxRestarts;
    }
}
