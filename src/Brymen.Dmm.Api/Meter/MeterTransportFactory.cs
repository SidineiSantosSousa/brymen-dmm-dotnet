using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Hid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Brymen.Dmm.Api.Meter;

/// <summary>Escolhe o <see cref="IHidDeviceProvider"/> conforme <c>Meter:Transport</c>.</summary>
internal static class MeterTransportFactory
{
    public static IHidDeviceProvider Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MeterOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MeterTransportFactory));
        switch (options.Transport)
        {
            case MeterTransport.Simulated:
                logger.LogInformation("Transporte Simulated: {Model} reproduzido a partir das capturas reais.", Describe(options.SimulatedModel));
                return CreateSimulated(services.GetRequiredService<SimulatedBm52xMeter>(), options.SimulatedLatency);
            case MeterTransport.Hid:
                return HidDeviceProviders.CreateForCurrentPlatform();
            default:
                try
                {
                    return HidDeviceProviders.CreateForCurrentPlatform();
                }
                catch (PlatformNotSupportedException ex)
                {
                    logger.LogWarning(ex, "Sem suporte a HID nesta plataforma; usando o transporte Simulated ({Model}).", Describe(options.SimulatedModel));
                    return CreateSimulated(services.GetRequiredService<SimulatedBm52xMeter>(), options.SimulatedLatency);
                }
        }
    }

    /// <summary>
    /// O multímetro simulado de <c>Meter:SimulatedModel</c>: o BM525s com as capturas reais (tempo real e memória, com o
    /// <c>8A</c> que, logo depois do <c>88</c>, avança como um <c>89</c>) ou o ET-2517A com a varredura real e o descarte
    /// de pedidos de tempo real medido no hardware (<see cref="SimulatedBm52xMeter.FromEt2517aCaptures"/>).
    /// </summary>
    public static SimulatedBm52xMeter CreateSimulatedMeter(SimulatedMeterModel model, TimeProvider time) => model switch
    {
        SimulatedMeterModel.Et2517a => SimulatedBm52xMeter.FromEt2517aCaptures(time),
        _ => SimulatedBm52xMeter.FromCaptures(time),
    };

    private static string Describe(SimulatedMeterModel model) => model switch
    {
        SimulatedMeterModel.Et2517a => "Minipa ET-2517A (BM82x, sem datalogger, com descarte de pedidos)",
        _ => "Brymen BM525s (BM52x, com datalogger)",
    };

    /// <summary>
    /// Um BC-86X simulado (<see cref="SimulatedHidDevice.CreateBm52x"/>, que aceita qualquer <see cref="SimulatedBm52xMeter"/>,
    /// BM52x ou BM82x) ligado ao medidor simulado com as capturas reais. O dispositivo usa o relógio do sistema: os testes
    /// com relógio falso não precisam avançá-lo a cada relatório.
    /// </summary>
    public static SimulatedHidDeviceProvider CreateSimulated(SimulatedBm52xMeter meter, TimeSpan latency)
    {
        var device = SimulatedHidDevice.CreateBm52x(meter);
        device.ResponseLatency = latency;
        // O cabo real entrega os relatórios de uma resposta a cada ≈ 8 ms; aqui, proporcional à latência.
        device.InterReportDelay = latency / 8;
        return new SimulatedHidDeviceProvider(device);
    }
}

/// <summary><c>/health/ready</c>: pronto quando o cabo está aberto ou o transporte é simulado.</summary>
internal sealed class MeterReadyHealthCheck(MeterSession session) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = session.GetSnapshot();
        var data = new Dictionary<string, object>
        {
            ["transport"] = session.TransportName,
            ["state"] = snapshot.State.ToString(),
        };
        if (session.IsSimulated)
            return Task.FromResult(HealthCheckResult.Healthy("Transporte simulado.", data));
        if (snapshot.DeviceOpen)
            return Task.FromResult(HealthCheckResult.Healthy("Cabo BC-86X aberto.", data));
        string reason = snapshot.LastError is { } error ? $"[{error.Code}] {error.Message}" : "Cabo BC-86X fechado.";
        return Task.FromResult(HealthCheckResult.Unhealthy(reason, data: data));
    }
}
