#:project ../../../src/Brymen.Dmm.Hid/Brymen.Dmm.Hid.csproj
// Taxa de pedidos sem resposta do 00 | 00 82 66 por intervalo entre consultas, e efeito de um reenvio imediato.
using System.Diagnostics;
using Brymen.Dmm.Hid;

int perInterval = args.Length > 0 ? int.Parse(args[0]) : 40;
int[] intervals = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : [0, 100, 200, 300, 500];
byte family = args.Length > 2 ? Convert.ToByte(args[2], 16) : (byte)0x82;

var provider = HidDeviceProviders.CreateForCurrentPlatform();
var info = provider.Enumerate(0x0820, 0x0001).First();
using var dev = provider.Open(info);
var buf = new byte[Math.Max(dev.InputReportLength, 8)];

async Task Drain() { try { while (true) await dev.ReadReportAsync(buf, TimeSpan.FromMilliseconds(5)); } catch (TimeoutException) { } }

// Uma troca: devolve a latência do 1º relatório em ms, ou -1 se não houve resposta em 1 s.
async Task<long> Exchange(int firstTimeoutMs = 1000)
{
    await Drain();
    var sw = Stopwatch.StartNew();
    await dev.WriteReportAsync(0, new byte[] { 0x00, family, 0x66 });
    long first = -1;
    for (int i = 0; i < 3; i++)
    {
        try { await dev.ReadReportAsync(buf, TimeSpan.FromMilliseconds(i == 0 ? firstTimeoutMs : 1000)); if (i == 0) first = sw.ElapsedMilliseconds; }
        catch (TimeoutException) { return -1; }
    }
    return first;
}

Console.WriteLine($"família {family:X2}, {perInterval} pedidos por intervalo");
foreach (int interval in intervals)
{
    // Estratégia da biblioteca: 1º relatório em até 300 ms; senão, reenvia (até 3 reenvios).
    int firstTry = 0, failed = 0; var resends = new int[4]; var total = new List<long>();
    for (int k = 0; k < perInterval; k++)
    {
        var sw2 = Stopwatch.StartNew(); int r = 0; long l;
        while ((l = await Exchange(300)) < 0 && r < 3) r++;
        if (l < 0) failed++; else { resends[r]++; total.Add(sw2.ElapsedMilliseconds); if (r == 0) firstTry++; }
        if (interval > 0) await Task.Delay(interval);
    }
    total.Sort();
    string p = total.Count == 0 ? "-" : $"mediana {total[total.Count / 2]} / máx {total[^1]} ms";
    Console.WriteLine($"intervalo {interval,4} ms: 1ª tentativa {firstTry}/{perInterval}; após 1/2/3 reenvios {resends[1]}/{resends[2]}/{resends[3]}; falhas {failed}; tempo total {p}");
}
return 0;
