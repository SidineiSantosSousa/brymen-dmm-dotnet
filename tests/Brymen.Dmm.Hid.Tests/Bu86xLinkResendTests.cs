using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Brymen.Dmm.Hid.Tests;

/// <summary>O código de família no relatório de saída e o reenvio do pedido sem o 1º relatório (§8.5 da especificação).</summary>
public class Bu86xLinkResendTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private static readonly Bu86xResendPolicy Policy = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));

    /// <summary>Relatórios [envio, índice, família, cmd, 1..4]: o número do envio identifica a resposta.</summary>
    private static byte[][] Reports(int send, ReadOnlyMemory<byte> data, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new byte[] { (byte)send, (byte)i, data.Span[1], data.Span[2], 1, 2, 3, 4 })];

    private static (SimulatedHidDevice Device, Bu86xLink Link) Create(
        FakeTimeProvider time, Func<int, ReadOnlyMemory<byte>, IReadOnlyList<byte[]>?> respond, TimeSpan? latency = null)
    {
        int sends = 0;
        var device = new SimulatedHidDevice((_, data) => respond(Interlocked.Increment(ref sends), data), timeProvider: time)
        {
            ResponseLatency = latency ?? TimeSpan.FromMilliseconds(50),
        };
        var link = new Bu86xLink(device, new Bu86xLinkOptions
        {
            ReportTimeout = TimeSpan.FromMilliseconds(200),
            DrainTimeout = TimeSpan.FromMilliseconds(5),
            RecoveryDrainTimeout = TimeSpan.FromMilliseconds(100),
            TimeProvider = time,
        });
        return (device, link);
    }

    [Fact]
    public async Task The_family_code_is_the_second_byte_of_the_output_report()
    {
        var time = new FakeTimeProvider();
        var (device, link) = Create(time, (n, data) => Reports(n, data, 3), TimeSpan.Zero);
        using (link)
        {
            byte[] bm82x = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3), Step);
            await FakeClock.PumpAsync(time, link.ExecuteAsync(0x66, 3), Step);
            await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, TimeSpan.FromSeconds(1)), Step);
            await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, Policy), Step);

            Assert.Equal(0x52, Bu86xLink.DefaultFamily);
            Assert.Equal(0x82, bm82x[3]);
            Assert.Equal(
                [[0x00, 0x82, 0x66], [0x00, 0x52, 0x66], [0x00, 0x82, 0x66], [0x00, 0x82, 0x66]],
                device.Writes.Select(w => w.Data));
            Assert.Equal(1, link.LastCommandAttempts);
            Assert.Equal(3, link.LastCommandReportsReceived);
        }
    }

    [Fact]
    public async Task Failures_name_the_family_of_the_command()
    {
        var time = new FakeTimeProvider();
        var (_, silent) = Create(time, (_, _) => null);
        var (_, shortReport) = Create(time, (n, data) =>
        {
            var reports = Reports(n, data, 3);
            reports[1] = reports[1][..5];
            return reports;
        });
        using (silent)
        using (shortReport)
        {
            var timeout = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => FakeClock.PumpAsync(time, silent.ExecuteAsync(0x82, 0x66, 3), Step));
            var legacy = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => FakeClock.PumpAsync(time, silent.ExecuteAsync(0x66, 3), Step));
            var truncated = await Assert.ThrowsAsync<Bu86xShortReportException>(() => FakeClock.PumpAsync(time, shortReport.ExecuteAsync(0x82, 0x66, 3), Step));

            Assert.Equal("Sem resposta do medidor ao comando 82 66.", timeout.Message);
            Assert.Equal((0x82, 0x66, 0, 1), (timeout.Family, timeout.Command, timeout.ReportsReceived, timeout.Attempts));
            Assert.Equal("Sem resposta do medidor ao comando 52 66.", legacy.Message);
            Assert.Equal(0x52, legacy.Family);
            Assert.Equal("Relatório 2/3 curto do BC-86X no comando 82 66: 5 bytes (esperado 8).", truncated.Message);
            Assert.Equal((0x82, 2), (truncated.Family, truncated.ReportsReceived));
        }

        // Os construtores antigos continuam com a família BM52x e um envio.
        var old = new Bu86xTimeoutException(0x88, 1, 4);
        Assert.Equal((0x52, 1, "Resposta incompleta do medidor ao comando 52 88: 1 de 4 relatórios."), (old.Family, old.Attempts, old.Message));
        Assert.Equal(0x52, new Bu86xShortReportException(0x89, 1, 4, 2).Family);
    }

    [Fact]
    public async Task Lost_requests_are_resent_without_closing_the_cable()
    {
        var time = new FakeTimeProvider();
        // Os 2 primeiros pedidos se perdem; o 3º, enviado 600 ms depois do 1º, é respondido em 50 ms.
        var (device, link) = Create(time, (n, data) => n <= 2 ? null : Reports(n, data, 3));
        using (link)
        {
            long start = time.GetTimestamp();
            byte[] frame = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, Policy), Step);

            Assert.Equal(3, frame[1]);
            Assert.Equal(3, link.LastCommandAttempts);
            Assert.Equal(3, link.LastCommandReportsReceived);
            // 100 ms do descarte inicial (o link começa com a recuperação pendente), 600 ms até o 3º envio e 50 ms de latência.
            Assert.InRange(time.GetElapsedTime(start), TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(800));
            Assert.All(device.Writes, w => Assert.Equal(new byte[] { 0x00, 0x82, 0x66 }, w.Data));
            Assert.Equal(3, device.Writes.Count);
            Assert.True(device.IsOpen);
        }
    }

    [Fact]
    public async Task The_budget_ends_the_exchange_with_the_number_of_sends()
    {
        var time = new FakeTimeProvider();
        var (device, link) = Create(time, (_, _) => null);
        using (link)
        {
            long start = time.GetTimestamp();
            var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() =>
                FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, new Bu86xResendPolicy(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1))), Step));

            // Envios em 0, 300, 600 e 900 ms; o último espera só os 100 ms que sobram do orçamento.
            Assert.Equal(4, ex.Attempts);
            Assert.Equal((0x82, 0x66, 0, 3), (ex.Family, ex.Command, ex.ReportsReceived, ex.ReportsExpected));
            Assert.Equal("Sem resposta do medidor ao comando 82 66 em 4 envios.", ex.Message);
            Assert.IsType<TimeoutException>(ex.InnerException);
            Assert.Equal(4, link.LastCommandAttempts);
            Assert.Equal(0, link.LastCommandReportsReceived);
            // O orçamento conta da 1ª escrita, depois dos 100 ms do descarte inicial.
            Assert.Equal(TimeSpan.FromMilliseconds(1100), time.GetElapsedTime(start));
            Assert.Equal(4, device.Writes.Count);
        }
    }

    [Fact]
    public async Task A_late_answer_to_an_earlier_send_is_accepted_and_the_duplicate_is_drained()
    {
        var time = new FakeTimeProvider();
        SimulatedHidDevice device = null!;
        // O 1º pedido é respondido tarde (350 ms, depois do reenvio dos 300 ms); o 2º, 100 ms depois de enviado.
        (device, var link) = Create(time, (n, data) =>
        {
            if (n != 1)
                return Reports(n, data, 3);
            foreach (var report in Reports(n, data, 3))
                device.EnqueueReport(report, TimeSpan.FromMilliseconds(350));
            return null;
        }, TimeSpan.FromMilliseconds(100));
        using (link)
        {
            byte[] first = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, Policy), Step);
            Assert.Equal(1, first[1]);
            Assert.Equal(2, link.LastCommandAttempts);

            // A resposta duplicada (a do 2º envio) chega depois; o próximo comando a descarta (100 ms de silêncio).
            byte[] next = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3), Step);

            Assert.Equal(new byte[] { 3, 3, 3 }, new[] { next[1], next[10], next[19] });
            Assert.Equal(new byte[] { 0, 1, 2 }, new[] { next[2], next[11], next[20] });
            Assert.Equal(1, link.LastCommandAttempts);
            Assert.Equal(0, device.PendingReportCount);
        }
    }

    [Fact]
    public async Task An_incomplete_answer_is_drained_and_the_request_resent()
    {
        var time = new FakeTimeProvider();
        // A 1ª resposta perde o 3º relatório; a 2ª chega inteira.
        var (device, link) = Create(time, (n, data) => n == 1 ? Reports(n, data, 3)[..2] : Reports(n, data, 3), TimeSpan.Zero);
        using (link)
        {
            byte[] frame = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, Policy), Step);

            Assert.Equal(new byte[] { 2, 2, 2 }, new[] { frame[1], frame[10], frame[19] });
            Assert.Equal(2, link.LastCommandAttempts);
            Assert.Equal(3, link.LastCommandReportsReceived);
            Assert.Equal(2, device.Writes.Count);
        }
    }

    [Fact]
    public async Task An_incomplete_answer_at_the_end_of_the_budget_keeps_the_reports_received()
    {
        var time = new FakeTimeProvider();
        var (_, link) = Create(time, (n, data) => Reports(n, data, 3)[..2], TimeSpan.Zero);
        using (link)
        {
            var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() =>
                FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3, new Bu86xResendPolicy(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500))), Step));

            Assert.Equal(2, ex.ReportsReceived);
            Assert.Equal(2, link.LastCommandReportsReceived);
            Assert.True(ex.Attempts >= 2);
            Assert.StartsWith("Resposta incompleta do medidor ao comando 82 66 em ", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Invalid_policies_and_arguments_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bu86xResendPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bu86xResendPolicy(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(-1)));
        Assert.Equal((TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2)), (Bu86xResendPolicy.Default.FirstReportWindow, Bu86xResendPolicy.Default.Budget));

        using var link = new Bu86xLink(new SimulatedHidDevice((_, _) => null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => link.ExecuteAsync(0x82, 0x66, 3, (Bu86xResendPolicy)null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.ExecuteAsync(0x82, 0x66, 0, Policy));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => link.ExecuteAsync(0x82, 0x66, 3, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_concurrent_caller_does_not_reset_the_sends_of_the_exchange_in_progress()
    {
        var time = new FakeTimeProvider();
        SimulatedHidDevice device = null!;
        // Como no teste da resposta atrasada: o 1º envio é respondido a 350 ms, depois do reenvio, e a resposta do 2º
        // fica a caminho. Um 2º chamador entra (e zera os contadores publicados) enquanto a troca do 1º está em curso.
        (device, var link) = Create(time, (n, data) =>
        {
            if (n != 1)
                return Reports(n, data, 3);
            foreach (var report in Reports(n, data, 3))
                device.EnqueueReport(report, TimeSpan.FromMilliseconds(350));
            return null;
        }, TimeSpan.FromMilliseconds(100));
        using (link)
        {
            var first = link.ExecuteAsync(0x82, 0x66, 3, Policy);
            await TestWait.UntilAsync(() =>
            {
                time.Advance(Step);
                return device.Writes.Count == 1;
            }, "o 1º envio");
            var second = link.ExecuteAsync(0x82, 0x66, 3);

            byte[] frame = await FakeClock.PumpAsync(time, first, Step);
            byte[] next = await FakeClock.PumpAsync(time, second, Step);

            // A troca do 1º contou os seus 2 envios: o 2º chamador fez o descarte de recuperação, que levou a resposta
            // duplicada, e recebeu a resposta do seu próprio envio, o 3º.
            Assert.Equal(1, frame[1]);
            Assert.Equal(new byte[] { 3, 3, 3 }, new[] { next[1], next[10], next[19] });
            Assert.Equal(3, device.Writes.Count);
            Assert.Equal(0, device.PendingReportCount);
        }
    }

    [Fact]
    public async Task The_numbers_of_a_failed_exchange_are_its_own_with_a_concurrent_caller()
    {
        var time = new FakeTimeProvider();
        var (device, link) = Create(time, (n, data) => n <= 4 ? null : Reports(n, data, 3));
        using (link)
        {
            var failing = link.ExecuteAsync(0x82, 0x66, 3, new Bu86xResendPolicy(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)));
            await TestWait.UntilAsync(() =>
            {
                time.Advance(Step);
                return device.Writes.Count == 2;
            }, "o 2º envio");
            var waiting = link.ExecuteAsync(0x82, 0x66, 3);

            var ex = await Assert.ThrowsAsync<Bu86xTimeoutException>(() => FakeClock.PumpAsync(time, failing, Step));
            await FakeClock.PumpAsync(time, waiting, Step);

            // Os 4 envios do orçamento de 1 s, mesmo com o 2º chamador zerando os contadores publicados no meio da troca.
            Assert.Equal(4, ex.Attempts);
            Assert.Equal("Sem resposta do medidor ao comando 82 66 em 4 envios.", ex.Message);
            Assert.Equal(1, link.LastCommandAttempts);
            Assert.Equal(3, link.LastCommandReportsReceived);
        }
    }

    [Fact]
    public async Task An_interrupted_exchange_with_resends_makes_the_next_command_wait_for_the_first_report_window()
    {
        var time = new FakeTimeProvider();
        // O ET-2517A responde em até ≈ 260 ms, mais que os 100 ms do RecoveryDrainTimeout (aqui, em 180 ms, dentro do
        // ReportTimeout de 200 ms): a resposta do pedido cancelado chega durante o descarte do comando seguinte, que exige o
        // silêncio da janela do reenvio (300 ms).
        var (device, link) = Create(time, (n, data) => Reports(n, data, 3), TimeSpan.FromMilliseconds(180));
        using (link)
        {
            await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3), Step);
            using var cts = new CancellationTokenSource();
            var cancelled = link.ExecuteAsync(0x82, 0x66, 3, Policy, cts.Token);
            await TestWait.UntilAsync(() =>
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
                return device.Writes.Count == 2;
            }, "o envio que será cancelado");
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));

            long start = time.GetTimestamp();
            byte[] next = await FakeClock.PumpAsync(time, link.ExecuteAsync(0x82, 0x66, 3), Step);

            Assert.Equal(new byte[] { 3, 3, 3 }, new[] { next[1], next[10], next[19] });
            Assert.True(time.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(300 + 180));
            Assert.Equal(0, device.PendingReportCount);
        }
    }

    [Fact]
    public async Task Cancellation_stops_the_resends()
    {
        var time = new FakeTimeProvider();
        var (device, link) = Create(time, (_, _) => null);
        using (link)
        {
            using var cts = new CancellationTokenSource();
            var exchange = link.ExecuteAsync(0x82, 0x66, 3, Policy, cts.Token);
            // O descarte inicial (100 ms no relógio falso) vem antes do 1º envio.
            await TestWait.UntilAsync(() =>
            {
                time.Advance(Step);
                return device.Writes.Count == 1;
            }, "o 1º envio");
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(device.Writes);
            Assert.Equal(1, link.LastCommandAttempts);
        }
    }
}

/// <summary>Avança um <see cref="FakeTimeProvider"/> até a tarefa terminar.</summary>
internal static class FakeClock
{
    /// <summary>
    /// Avança o relógio falso em passos de <paramref name="step"/> até <paramref name="task"/> terminar, dando tempo às
    /// continuações entre um passo e outro; falha se passar de 60 s no relógio falso.
    /// </summary>
    public static async Task<T> PumpAsync<T>(FakeTimeProvider time, Task<T> task, TimeSpan step)
    {
        var advanced = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (advanced > TimeSpan.FromSeconds(60) || sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"A tarefa não terminou depois de {advanced.TotalMilliseconds} ms no relógio falso.");
            // Deixa as continuações pendentes (fila do pool) registrarem os seus timers antes de avançar.
            for (int i = 0; i < 20 && !task.IsCompleted; i++)
                await Task.Yield();
            if (task.IsCompleted)
                break;
            await Task.Delay(1);
            if (task.IsCompleted)
                break;
            time.Advance(step);
            advanced += step;
        }
        return await task;
    }
}
