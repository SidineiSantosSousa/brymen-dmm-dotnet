using System.Globalization;
using System.Text;
using Brymen.Dmm;
using Brymen.Dmm.Protocol;

// Exemplo de console do pacote Brymen.Dmm: leituras ao vivo do multímetro e importação da memória do datalogger.
//
//   dotnet run -- --simulated --count 10          10 leituras do medidor simulado (capturas reais do BM525s)
//   dotnet run -- --device /dev/hidraw0           leituras do cabo indicado, até o Ctrl+C
//   dotnet run -- --import memoria.csv            importa o datalogger e salva em CSV

Console.OutputEncoding = Encoding.UTF8;

if (!CommandLine.TryParse(args, out var command, out string? problem))
{
    if (problem is not null)
        Console.Error.WriteLine(problem);
    Console.Error.WriteLine(CommandLine.Usage);
    return problem is null ? 0 : 2;
}

if (command.List)
    return ListCables();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // O Ctrl+C encerra a leitura com calma: o medidor é descartado e o cabo, fechado.
    e.Cancel = true;
    cts.Cancel();
};

var options = new BrymenMeterOptions { DevicePath = command.DevicePath };
BrymenMeter meter;
try
{
    meter = command.Simulated
        ? await BrymenMeter.OpenSimulatedAsync(options: options, cancellationToken: cts.Token)
        : await BrymenMeter.OpenAsync(options, cts.Token);
}
catch (BrymenMeterException ex)
{
    Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
    Console.Error.WriteLine("Confira o cabo (--list mostra os encontrados) ou rode com --simulated.");
    return 1;
}
catch (PlatformNotSupportedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

await using (meter)
{
    var device = meter.Device;
    Console.WriteLine($"Cabo: {device.Path} ({device.Manufacturer} {device.Product})");
    // Com a reconexão automática (padrão), as falhas não interrompem a leitura contínua: elas chegam por aqui.
    meter.ErrorOccurred += (_, error) => Console.Error.WriteLine($"[{error.Code}] {error.Message}");

    try
    {
        if (command.ImportPath is { } path)
        {
            await ImportAsync(meter, path, cts.Token);
            if (command.Count is null)
                return 0;
        }
        await PrintReadingsAsync(meter, command.Count, cts.Token);
        return 0;
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("Interrompido.");
        return 0;
    }
    catch (BrymenMeterException ex)
    {
        Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
        return 1;
    }
}

static int ListCables()
{
    try
    {
        var cables = BrymenMeter.FindDevices();
        if (cables.Count == 0)
            Console.WriteLine("Nenhum cabo BC-86X (VID 0820h/PID 0001h) encontrado.");
        foreach (var cable in cables)
            Console.WriteLine($"{cable.Path}  {cable.Manufacturer} {cable.Product} {cable.SerialNumber}".TrimEnd());
        return 0;
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task PrintReadingsAsync(BrymenMeter meter, int? count, CancellationToken cancellationToken)
{
    Console.WriteLine(count is { } n ? $"{n} leituras (Ctrl+C interrompe):" : "Leituras ao vivo (Ctrl+C encerra):");
    int received = 0;
    await foreach (var reading in meter.ReadContinuouslyAsync(cancellationToken: cancellationToken))
    {
        Console.WriteLine(Format(reading));
        if (++received == count)
            break;
    }
}

static async Task ImportAsync(BrymenMeter meter, string path, CancellationToken cancellationToken)
{
    LoggerReadProgress last = default;
    // Com a saída de erro num terminal, o progresso é reescrito na mesma linha; redirecionada, só o resultado aparece.
    bool interactive = !Console.IsErrorRedirected;
    // IProgress síncrono: o Progress<T> postaria os relatórios no pool, fora de ordem com o fim da importação.
    var progress = new SyncProgress<LoggerReadProgress>(p =>
    {
        last = p;
        if (interactive)
            Console.Error.Write(string.Create(CultureInfo.InvariantCulture, $"\rImportando o datalogger: bloco {p.BlocksRead}/{p.BlocksTotal} ({p.Percent:0}%)"));
    });
    if (!interactive)
        Console.Error.WriteLine("Importando o datalogger...");
    var memory = await meter.ReadLoggerMemoryAsync(progress, cancellationToken);
    if (interactive)
        Console.Error.WriteLine();

    int samples = CsvExport.Write(path, memory);
    int sessions = memory.Sessions.Count;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{sessions} {(sessions == 1 ? "sessão" : "sessões")} e {samples} {(samples == 1 ? "amostra" : "amostras")} ({last.BlocksRead} blocos, {last.Retries} retentativas, {last.Restarts} recomeços), salvas em {Path.GetFullPath(path)}"));
    foreach (var warning in memory.Warnings)
        Console.Error.WriteLine($"Aviso: {warning}");
}

static string Format(DmmReading reading)
{
    var line = new StringBuilder();
    line.Append(CultureInfo.InvariantCulture, $"{reading.Timestamp.ToLocalTime():HH:mm:ss.fff}  {Display(reading.Main),-16}");
    if (reading.Secondary is not null)
        line.Append(CultureInfo.InvariantCulture, $" | {Display(reading.Secondary),-16}");
    var flags = DmmFlagNames.ToNames(reading.Flags);
    if (flags.Count > 0)
        line.Append(CultureInfo.InvariantCulture, $" [{string.Join(' ', flags)}]");
    return line.ToString();
}

static string Display(DisplayReading? display)
{
    if (display is null)
        return "----";
    string mode = CsvExport.ModeName(display.Mode);
    return $"{display.Text} {display.UnitSymbol} {mode}".TrimEnd();
}

/// <summary>Argumentos da linha de comando.</summary>
internal sealed record CommandLine(bool Simulated, string? DevicePath, string? ImportPath, int? Count, bool List)
{
    public const string DefaultImportPath = "datalogger.csv";

    public const string Usage = """
        Uso: Brymen.Dmm.ConsoleSample [opções]

          --simulated          usa o medidor simulado (capturas reais do BM525s), sem hardware
          --device <caminho>   cabo a abrir (ex.: /dev/hidraw0, /dev/brymen-bu86x); padrão: o primeiro encontrado
          --count <N>          para depois de N leituras ao vivo; padrão: até o Ctrl+C
          --import [arquivo]   lê a memória do datalogger e a salva em CSV (padrão: datalogger.csv); sem --count,
                               termina depois da importação
          --list               lista os cabos BC-86X conectados
          --help               mostra esta ajuda
        """;

    /// <summary>Lê <paramref name="args"/>; devolve <see langword="false"/> com a ajuda pedida (sem problema) ou com o problema encontrado.</summary>
    public static bool TryParse(string[] args, out CommandLine command, out string? problem)
    {
        command = new CommandLine(false, null, null, null, false);
        problem = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--simulated":
                    command = command with { Simulated = true };
                    break;
                case "--device" when i + 1 < args.Length:
                    command = command with { DevicePath = args[++i] };
                    break;
                case "--count" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
                    {
                        problem = $"--count precisa de um número inteiro positivo, não \"{args[i]}\".";
                        return false;
                    }
                    command = command with { Count = count };
                    break;
                case "--import":
                    // O arquivo é opcional: o próximo argumento, se não for outra opção.
                    bool hasPath = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    command = command with { ImportPath = hasPath ? args[++i] : DefaultImportPath };
                    break;
                case "--list":
                    command = command with { List = true };
                    break;
                case "--help" or "-h" or "/?":
                    return false;
                default:
                    problem = args[i] is "--device" or "--count"
                        ? $"{args[i]} precisa de um valor."
                        : $"Opção desconhecida: {args[i]}";
                    return false;
            }
        }
        if (command.Simulated && command.DevicePath is not null)
            Console.Error.WriteLine("--device é ignorado com --simulated.");
        return true;
    }
}

/// <summary>Um <see cref="IProgress{T}"/> que chama o handler na thread de quem informa o progresso.</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
