using Brymen.Dmm.Extensions.Hosting;
using Brymen.Dmm.Protocol;

namespace Brymen.Dmm.WorkerSample;

/// <summary>
/// Registra no <see cref="ILogger"/> cada leitura e cada mudança de estado do multímetro, pelos eventos do
/// <see cref="IBrymenMeterAccessor"/>. As falhas já vão para o log pelo próprio serviço do AddBrymenMeter.
/// </summary>
internal sealed partial class ReadingLogger(IBrymenMeterAccessor meter, ILogger<ReadingLogger> logger) : IHostedService
{
    // O medidor já foi aberto (ou já falhou) no StartingAsync do serviço do AddBrymenMeter, antes deste StartAsync; a
    // leitura contínua só começa depois de todos os StartAsync, então nenhuma leitura se perde.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        meter.ReadingReceived += OnReading;
        meter.StateChanged += OnStateChanged;
        LogStarted(logger, meter.State, meter.Meter?.Device.Path ?? "-");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        meter.ReadingReceived -= OnReading;
        meter.StateChanged -= OnStateChanged;
        return Task.CompletedTask;
    }

    // Chamado na thread do laço de leitura: um handler lento atrasa a próxima consulta ao medidor.
    private void OnReading(object? sender, DmmReading reading)
    {
        var main = reading.Main;
        var secondary = reading.Secondary;
        LogReading(logger, main?.Text ?? "----", main?.UnitSymbol ?? "", ModeName(main), secondary?.Text ?? "----",
            secondary?.UnitSymbol ?? "", string.Join(' ', DmmFlagNames.ToNames(reading.Flags)));
    }

    // Sem o cabo, cada tentativa de abertura passa por Connecting e volta a Faulted: essas vão para Debug, e só a
    // conexão e a perda dela, para Information.
    private void OnStateChanged(object? sender, MeterStateChangedEventArgs e)
    {
        var level = e.Current == MeterState.Connected || e.Previous == MeterState.Connected ? LogLevel.Information : LogLevel.Debug;
        LogStateChanged(logger, level, e.Previous, e.Current);
    }

    private static string ModeName(DisplayReading? display) => display?.Mode switch
    {
        MeasurementMode.AC => "AC",
        MeasurementMode.DC => "DC",
        MeasurementMode.AcDc => "AC+DC",
        _ => "",
    };

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Leitura: {Main} {Unit} {Mode} | {Secondary} {SecondaryUnit} [{Flags}]")]
    private static partial void LogReading(ILogger logger, string main, string unit, string mode, string secondary, string secondaryUnit, string flags);

    [LoggerMessage(EventId = 101, Message = "Multímetro: {Previous} -> {Current}")]
    private static partial void LogStateChanged(ILogger logger, LogLevel level, MeterState previous, MeterState current);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Multímetro na partida: {State} (cabo {DevicePath})")]
    private static partial void LogStarted(ILogger logger, MeterState state, string devicePath);
}
