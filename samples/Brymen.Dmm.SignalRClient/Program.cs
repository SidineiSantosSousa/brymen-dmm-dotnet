// Cliente de exemplo do hub SignalR /hubs/meter do Brymen DMM REST.
//
// Uso: dotnet run --project samples/Brymen.Dmm.SignalRClient -- http://host:5080
// (o endereço do hub, http://host:5080/hubs/meter, também serve)
//
// Conecta com reconexão automática (sem desistir), chama GetStatus e GetLatest e imprime uma linha por evento
// "reading" (hora, display principal e secundário com unidade, anunciadores) e cada evento "status" (com o medidor
// detectado: BM52x ou BM82x).
// Ctrl+C encerra.
using System.Globalization;
using System.Text;
using Brymen.Dmm.SignalRClient;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

const string HubPath = "hubs/meter";

if (args.Length > 1 || (args.Length == 1 && args[0] is "-h" or "--help" or "/?"))
{
    Console.Error.WriteLine("Uso: Brymen.Dmm.SignalRClient [http://host:5080]");
    return 2;
}
string address = args.Length == 1 ? args[0] : "http://localhost:5080";
if (!Uri.TryCreate(address, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
{
    Console.Error.WriteLine($"Endereço inválido: '{address}'. Use, por exemplo, http://raspberrypi.local:5080.");
    return 2;
}
// "http://host:5080/base" e "http://host:5080/base/" levam ao mesmo hub; o endereço do próprio hub
// ("http://host:5080/hubs/meter") também é aceito.
string trimmed = baseUri.AbsoluteUri.TrimEnd('/');
var hubUri = trimmed.EndsWith("/" + HubPath, StringComparison.OrdinalIgnoreCase)
    ? new Uri(trimmed)
    : new Uri(new Uri(trimmed + "/"), HubPath);

Console.OutputEncoding = Encoding.UTF8;
using var exit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exit.Cancel();
};

await using var connection = new HubConnectionBuilder()
    .WithUrl(hubUri)
    .WithAutomaticReconnect(new ForeverRetryPolicy())
    .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ClientJsonContext.Default))
    .Build();

connection.On<ReadingMessage>("reading", reading => Console.WriteLine(Format.Reading(reading)));
connection.On<StatusMessage>("status", status => Console.WriteLine(Format.Status("status", status)));
connection.Reconnecting += error =>
{
    Console.WriteLine($"{Format.Now()} conexão perdida ({error?.Message ?? "sem detalhe"}); reconectando...");
    return Task.CompletedTask;
};
connection.Reconnected += async _ =>
{
    Console.WriteLine($"{Format.Now()} reconectado.");
    await ShowSnapshotAsync(connection, exit.Token);
};
connection.Closed += error =>
{
    if (!exit.IsCancellationRequested)
        Console.WriteLine($"{Format.Now()} conexão encerrada: {error?.Message ?? "pelo servidor"}.");
    return Task.CompletedTask;
};

Console.WriteLine($"Conectando a {hubUri} (Ctrl+C encerra)...");
try
{
    await StartAsync(connection, exit.Token);
    await ShowSnapshotAsync(connection, exit.Token);
    await Task.Delay(Timeout.Infinite, exit.Token);
}
catch (OperationCanceledException) when (exit.IsCancellationRequested)
{
}
Console.WriteLine("Encerrando.");
return 0;

// A reconexão automática só vale depois da 1ª conexão; até lá, tenta de novo a cada 5 s (o serviço pode estar subindo).
static async Task StartAsync(HubConnection connection, CancellationToken cancellationToken)
{
    while (true)
    {
        try
        {
            await connection.StartAsync(cancellationToken);
            Console.WriteLine($"{Format.Now()} conectado ({connection.ConnectionId}).");
            return;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"{Format.Now()} falha ao conectar: {ex.Message} Nova tentativa em 5 s.");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}

static async Task ShowSnapshotAsync(HubConnection connection, CancellationToken cancellationToken)
{
    try
    {
        var status = await connection.InvokeAsync<StatusMessage>("GetStatus", cancellationToken);
        Console.WriteLine(Format.Status("GetStatus", status));
        var latest = await connection.InvokeAsync<ReadingMessage?>("GetLatest", cancellationToken);
        Console.WriteLine(latest is null ? $"{Format.Now()} [GetLatest] sem leitura atual." : Format.Reading(latest, "GetLatest"));
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        // A conexão pode cair entre o evento e a chamada; a reconexão chama de novo.
        Console.WriteLine($"{Format.Now()} falha ao consultar o estado: {ex.Message}");
    }
}

/// <summary>Reconexão sem desistir: 0, 2, 5 e depois 10 s entre as tentativas.</summary>
internal sealed class ForeverRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delays[(int)Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1)];
}

/// <summary>Linhas impressas pelo exemplo (formatação invariante, hora local).</summary>
internal static class Format
{
    public static string Now() => DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Ex.: <c>14:03:27.481 #1532  0.287 V AC | 0.000 Hz  AUTO,AC</c>.</summary>
    public static string Reading(ReadingMessage reading, string? source = null)
    {
        var line = new StringBuilder();
        line.Append(reading.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        if (source is not null)
            line.Append(" [").Append(source).Append(']');
        line.Append(CultureInfo.InvariantCulture, $" #{reading.Sequence,-6} ");
        line.Append(Display(reading.Main).PadLeft(14)).Append(" | ").Append(Display(reading.Secondary).PadRight(14));
        line.Append("  ").Append(reading.Flags.Count > 0 ? string.Join(',', reading.Flags) : "-");
        if (reading.Comparator is { } comparator)
            line.Append("  comparador: ").Append(comparator);
        return line.ToString();
    }

    public static string Status(string source, StatusMessage status)
    {
        var line = new StringBuilder();
        line.Append(Now()).Append(" [").Append(source).Append("] ").Append(status.State);
        line.Append(status.LinkEnabled ? ", link ativo" : ", link desativado");
        line.Append(status.DeviceOpen ? ", cabo aberto" : ", cabo fechado");
        if (status.Device is { } device)
            line.Append(" (").Append(device.Path).Append(')');
        line.Append(", transporte ").Append(status.Transport);
        // O evento "status" também chega quando a família detectada muda (o medidor trocado no cabo).
        if (status.Model is { } model)
            line.Append(", medidor ").Append(model.Name).Append(model.SupportsDataLogger ? "" : " sem datalogger");
        if (status.Importing)
            line.Append(", importando o datalogger");
        line.Append(CultureInfo.InvariantCulture, $", {status.ReadingCount} leituras");
        if (status.LastError is { } error)
            line.Append(CultureInfo.InvariantCulture, $"; último erro {error.Code} às {error.Timestamp.ToLocalTime():HH:mm:ss}: {error.Message}");
        return line.ToString();
    }

    // "0.287 V AC", "0.L MΩ"; display apagado vira "-".
    private static string Display(DisplayMessage? display)
    {
        if (display is null)
            return "-";
        string text = display.Text.Trim();
        if (display.UnitSymbol.Length > 0)
            text += " " + display.UnitSymbol;
        if (display.Mode is not ("NONE" or ""))
            text += " " + display.Mode;
        return text;
    }
}
