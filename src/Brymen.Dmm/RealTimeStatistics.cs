namespace Brymen.Dmm;

/// <summary>
/// Contadores das consultas de tempo real de uma conexão (<see cref="MeterConnection.RealTimeStatistics"/>,
/// <see cref="BrymenMeter.RealTimeStatistics"/>), desde a criação dela. Os reenvios não são falhas: não geram
/// <see cref="BrymenErrorCodes.NoDataReceived"/> nem <see cref="BrymenMeter.ErrorOccurred"/>, e só aparecem aqui. Os
/// contadores são lidos um a um, sem um instantâneo atômico de todos.
/// </summary>
/// <remarks>
/// Os contadores veem só o lado USB: um pedido "perdido" é um <c>66</c> sem o 1º relatório dentro de
/// <see cref="BrymenMeterOptions.RealTimeResendTimeout"/>, e não um pedido que o medidor perdeu. O cabo BC-86X (o original
/// e o BrymenIR) faz ele mesmo 2 pedidos ópticos e escuta ≈ 3 s depois do 2º, e descarta todo comando USB que chega
/// nesse tempo. Assim, no BM82x e na detecção da família:
/// <list type="bullet">
/// <item>nas funções em que o medidor só responde no fim de um ciclo mais longo que a janela (AC+DC ≈ 0,78 s, Hz lógico
/// ≈ 1,03 s, capacitância), cada consulta conta reenvios, pedidos perdidos e uma consulta recuperada, sem nenhuma perda
/// no medidor: em 20 s de AC+DC no ET-2517A, 50 pedidos "perdidos" e 25 consultas "recuperadas";</item>
/// <item>quando o ET-2517A descarta um pedido (§8.5 da especificação), quem recupera a leitura é o 2º pedido óptico do
/// próprio cabo, e não o reenvio, que chega com o cabo ocupado.</item>
/// </list>
/// <see cref="Resends"/>, <see cref="LostRequests"/> e <see cref="RecoveredQueries"/> medem, portanto, quantas consultas
/// demoraram mais que a janela; só <see cref="FailedQueries"/> mede leituras que faltaram. No BM52x, que não reenvia,
/// <see cref="LostRequests"/> é igual a <see cref="FailedQueries"/> (mais o <c>52 66</c> de reserva da detecção).
/// </remarks>
/// <param name="Queries">Consultas de tempo real terminadas, com ou sem resposta (as canceladas não contam).</param>
/// <param name="Requests">Pedidos <c>66</c> enviados ao cabo: o 1º de cada consulta, os reenvios e o <c>52 66</c> de reserva da detecção.</param>
/// <param name="Resends">Reenvios do mesmo pedido depois da janela do 1º relatório (<see cref="BrymenMeterOptions.RealTimeResendTimeout"/>), inclusive os que o cabo descartou por estar ocupado.</param>
/// <param name="LostRequests">Pedidos sem o 1º relatório dentro da janela: os que foram reenviados e os das consultas que falharam. Não são necessariamente pedidos perdidos pelo medidor (veja as observações).</param>
/// <param name="RecoveredQueries">Consultas cujo quadro chegou depois de ao menos um reenvio: medidor lento ou pedido óptico repetido pelo cabo.</param>
/// <param name="FailedQueries">Consultas sem resposta completa (cada uma é um <see cref="BrymenErrorCodes.NoDataReceived"/>).</param>
public readonly record struct RealTimeStatistics(
    long Queries,
    long Requests,
    long Resends,
    long LostRequests,
    long RecoveredQueries,
    long FailedQueries);
