#:project ../../../src/Brymen.Dmm.Hid/Brymen.Dmm.Hid.csproj
#:project ../../../src/Brymen.Dmm.Protocol/Brymen.Dmm.Protocol.csproj
// Gravador de varredura: consulta 00 | 00 82 66 a cada ~150 ms e registra cada quadro diferente do anterior,
// com a decodificação atual (bytes de modelo trocados para 52 só para o decodificador aceitar o quadro).
using System.Diagnostics;
using Brymen.Dmm.Hid;
using Brymen.Dmm.Protocol;

int seconds = args.Length > 0 ? int.Parse(args[0]) : 300;
string outFile = args.Length > 1 ? args[1] : "sweep.txt";

var provider = HidDeviceProviders.CreateForCurrentPlatform();
var info = provider.Enumerate(0x0820, 0x0001).FirstOrDefault() ?? throw new InvalidOperationException("BC-86X não encontrado");
using var dev = provider.Open(info);
using var log = new StreamWriter(outFile, append: false) { AutoFlush = true };
log.WriteLine($"# varredura 82 66, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {info.Path}");
Console.WriteLine($"gravando por {seconds} s em {outFile}");

var buf = new byte[Math.Max(dev.InputReportLength, 8)];
string? last = null;
int total = 0, timeouts = 0, changes = 0;
var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds)
{
    // descarta relatórios atrasados
    try { while (true) await dev.ReadReportAsync(buf, TimeSpan.FromMilliseconds(5)); } catch (TimeoutException) { }
    await dev.WriteReportAsync(0, new byte[] { 0x00, 0x82, 0x66 });
    var frame = new byte[27];
    bool ok = true;
    for (int i = 0; i < 3 && ok; i++)
    {
        try
        {
            int n = await dev.ReadReportAsync(buf, TimeSpan.FromMilliseconds(2500));
            buf.AsSpan(0, 8).CopyTo(frame.AsSpan(i * 9 + 1));
        }
        catch (TimeoutException) { ok = false; timeouts++; }
    }
    if (!ok)
    {
        if (last != "TIMEOUT") { log.WriteLine($"{sw.Elapsed.TotalSeconds,7:0.0}s TIMEOUT"); Console.WriteLine($"{sw.Elapsed.TotalSeconds,7:0.0}s TIMEOUT"); last = "TIMEOUT"; }
        continue;
    }
    total++;
    string hex = Convert.ToHexString(frame);
    if (hex != last)
    {
        changes++;
        last = hex;
        var patched = (byte[])frame.Clone();
        for (int i = 19; i < 23; i++) if (patched[i] == 0x82) patched[i] = 0x52;
        string decoded = Bm52xRealtime.TryDecode(patched, DateTimeOffset.Now, out var r, out var err)
            ? $"{Show(r!.Main)} | {Show(r.Secondary)} | {string.Join(",", DmmFlagNames.ToNames(r.Flags))}"
            : $"INVÁLIDO: {err}";
        string line = $"{sw.Elapsed.TotalSeconds,7:0.0}s {hex} {decoded}";
        log.WriteLine(line);
        Console.WriteLine(line);
    }
    await Task.Delay(120);
}
log.WriteLine($"# fim: {total} quadros, {changes} mudanças, {timeouts} timeouts");
Console.WriteLine($"fim: {total} quadros, {changes} mudanças, {timeouts} timeouts");
return 0;

static string Show(DisplayReading? d) => d is null ? "(apagado)" : $"{d.Text} {d.UnitSymbol} {d.Mode}{(d.Overload ? " OL" : "")}";
