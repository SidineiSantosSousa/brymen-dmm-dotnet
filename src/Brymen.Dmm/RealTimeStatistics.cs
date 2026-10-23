namespace Brymen.Dmm;

/// <summary>
/// Contadores das consultas de tempo real de uma conexão (<see cref="MeterConnection.RealTimeStatistics"/>,
/// <see cref="BrymenMeter.RealTimeStatistics"/>), desde a criação dela. Os pedidos perdidos e recuperados por reenvio (o
/// ET-2517A descarta parte deles, §8.5 da especificação) não são falhas: não geram
/// <see cref="BrymenErrorCodes.NoDataReceived"/> nem <see cref="BrymenMeter.ErrorOccurred"/>, e só aparecem aqui. Os
/// contadores são lidos um a um, sem um instantâneo atômico de todos.
/// </summary>
/// <param name="Queries">Consultas de tempo real terminadas, com ou sem resposta (as canceladas não contam).</param>
/// <param name="Requests">Pedidos <c>66</c> enviados: o 1º de cada consulta, os reenvios e o <c>52 66</c> de reserva da detecção.</param>
/// <param name="Resends">Reenvios do mesmo pedido depois da janela do 1º relatório (<see cref="BrymenMeterOptions.RealTimeResendTimeout"/>).</param>
/// <param name="LostRequests">Pedidos sem resposta dentro da janela: os que foram reenviados e os das consultas que falharam.</param>
/// <param name="RecoveredQueries">Consultas que só tiveram resposta depois de ao menos um reenvio.</param>
/// <param name="FailedQueries">Consultas sem resposta completa (cada uma é um <see cref="BrymenErrorCodes.NoDataReceived"/>).</param>
public readonly record struct RealTimeStatistics(
    long Queries,
    long Requests,
    long Resends,
    long LostRequests,
    long RecoveredQueries,
    long FailedQueries);
