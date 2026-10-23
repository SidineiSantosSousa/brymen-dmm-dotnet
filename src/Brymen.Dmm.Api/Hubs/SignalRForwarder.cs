using Brymen.Dmm.Api.Acquisition;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Meter;
using Microsoft.AspNetCore.SignalR;

namespace Brymen.Dmm.Api.Hubs;

/// <summary>
/// Encaminha ao <see cref="MeterHub"/>, para todos os clientes conectados:
/// <list type="bullet">
///   <item><description><c>reading</c>: cada leitura publicada no <see cref="ReadingBroadcaster"/>, a mesma fonte do SSE;</description></item>
///   <item><description><c>acquisitionSample</c>: cada amostra gravada pelo <see cref="Recorder"/>;</description></item>
///   <item><description><c>status</c>: cada mudança do link ou do cabo (estado, abertura, importação, erro registrado).</description></item>
/// </list>
/// É mais um assinante do broadcaster: o polling nunca espera pelo SignalR. Se os clientes não acompanharem, o canal
/// descarta o item mais antigo e a contagem vai para o log. As assinaturas são feitas em <see cref="StartAsync"/>,
/// antes do polling começar (o serviço é registrado antes dele), e terminam no <c>ApplicationStopping</c>.
/// </summary>
internal sealed class SignalRForwarder(
    IHubContext<MeterHub> hub,
    ReadingBroadcaster broadcaster,
    Recorder recorder,
    MeterSession session,
    IHostApplicationLifetime lifetime,
    ILogger<SignalRForwarder> logger) : BackgroundService
{
    /// <summary>
    /// Folga de cada fluxo. O envio a <c>Clients.All</c> espera a conexão mais lenta, e uma conexão morta só cai no
    /// <c>ClientTimeoutInterval</c> (30 s): 256 leituras cobrem cerca de 50 s com o PollInterval padrão.
    /// </summary>
    public const int Capacity = 256;

    private Subscription<LiveReading>? _readings;
    private Subscription<AcquisitionSample>? _samples;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _readings = broadcaster.Subscribe(Capacity);
        _samples = recorder.SubscribeSamples(Capacity);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var readings = _readings ?? throw new InvalidOperationException("StartAsync não foi chamado.");
        var samples = _samples ?? throw new InvalidOperationException("StartAsync não foi chamado.");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        try
        {
            await Task.WhenAll(
                ForwardAsync(readings, MeterHub.ReadingEvent, DtoMapper.ToDto, stopping.Token),
                ForwardAsync(samples, MeterHub.AcquisitionSampleEvent, DtoMapper.ToDto, stopping.Token),
                ForwardStatusAsync(stopping.Token)).ConfigureAwait(false);
        }
        finally
        {
            readings.Dispose();
            samples.Dispose();
        }
    }

    public override void Dispose()
    {
        _readings?.Dispose();
        _samples?.Dispose();
        base.Dispose();
    }

    // Um laço por fluxo, na ordem de publicação: uma leitura só sai depois que a anterior foi entregue aos clientes.
    private async Task ForwardAsync<TItem, TDto>(Subscription<TItem> subscription, string method, Func<TItem, TDto> map, CancellationToken cancellationToken)
    {
        var drops = new DropReporter(logger, $"SignalR \"{method}\"");
        var reader = subscription.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    drops.Collect(subscription);
                    await SendAsync(method, map(item), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            drops.Collect(subscription);
            drops.Flush();
        }
    }

    private async Task ForwardStatusAsync(CancellationToken cancellationToken)
    {
        StatusKey? last = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // O sinal é obtido antes da fotografia: uma mudança entre as duas completa o sinal e não se perde.
                var changed = session.Changed;
                var snapshot = session.GetSnapshot();
                var key = StatusKey.From(snapshot);
                if (last is { } previous && key != previous)
                    await SendAsync(MeterHub.StatusEvent, DtoMapper.ToDto(snapshot, session, broadcaster), cancellationToken).ConfigureAwait(false);
                last = key;
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync<TDto>(string method, TDto payload, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.All.SendAsync(method, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Falha de um envio não derruba o encaminhamento; o SignalR já aborta a conexão com problema.
            logger.LogWarning(ex, "SignalR: falha ao enviar o evento \"{Method}\".", method);
        }
    }

    /// <summary>O que, no <see cref="MeterSnapshot"/>, gera um evento <c>status</c>; o contador de leituras não entra.</summary>
    private readonly record struct StatusKey(
        bool LinkEnabled, MeterConnectionState State, bool DeviceOpen, string? DevicePath, bool Importing, MeterError? LastError)
    {
        public static StatusKey From(MeterSnapshot s) => new(s.LinkEnabled, s.State, s.DeviceOpen, s.Device?.Path, s.Importing, s.LastError);
    }
}
