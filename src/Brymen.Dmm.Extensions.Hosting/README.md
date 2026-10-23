# Brymen.Dmm.Extensions.Hosting

Integração do pacote `Brymen.Dmm` com o Generic Host do .NET
(`Microsoft.Extensions.Hosting`): worker services, ASP.NET Core e qualquer app com `IServiceCollection`. Para o
multímetro **Brymen BM525s** (= **Minipa ET-2615A**) e outros BM52x com datalogger, pelo cabo óptico USB **BC-86X**
(kit **BU-86X**).

- `AddBrymenMeter(...)`: opções por código ou por `IConfiguration` (binding gerado em tempo de compilação, compatível
  com trimming e Native AOT); com nome, um medidor por cabo.
- Um serviço hospedado que abre o medidor na partida, tenta de novo enquanto o cabo não aparece, mantém a leitura
  contínua com reconexão automática e fecha o cabo na parada.
- `IBrymenMeterAccessor`: as operações avulsas do medidor atual (`IBrymenMeter`, ou `null`), as leituras do laço do
  serviço (`ReadingsAsync` e eventos), a última leitura e a última falha.

Alvos `net8.0` e `net10.0`. Dependências: `Brymen.Dmm` e os pacotes `Microsoft.Extensions.*` (Hosting.Abstractions,
Options, Configuration.Binder e Logging.Abstractions), a partir da 8.0.0 no alvo `net8.0` e da 10.0.0 no `net10.0`:
qualquer patch dessas linhas serve.

> Projeto independente: não é afiliado à Brymen nem à Minipa.

## Instalação

```bash
dotnet add package Brymen.Dmm.Extensions.Hosting
```

## Uso

```csharp
using Brymen.Dmm;
using Brymen.Dmm.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBrymenMeter(builder.Configuration.GetSection("Brymen"));
builder.Services.AddHostedService<MeterWorker>();
await builder.Build().RunAsync();

sealed class MeterWorker(IBrymenMeterAccessor accessor, ILogger<MeterWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        accessor.ErrorOccurred += (_, e) => logger.LogWarning("[{Code}] {Message}", e.Code, e.Message);

        // Operações avulsas no medidor aberto: a importação pausa a leitura contínua e a retoma no fim.
        IBrymenMeter meter = await accessor.WaitForMeterAsync(stoppingToken);
        var memory = await meter.ReadLoggerMemoryAsync(cancellationToken: stoppingToken);
        logger.LogInformation("{Sessions} sessões no datalogger", memory.Sessions.Count);

        // As leituras do laço do serviço, sem consultas a mais no cabo.
        try
        {
            await foreach (var r in accessor.ReadingsAsync(stoppingToken))
                logger.LogInformation("{Value} {Unit}", r.Main?.Text, r.Main?.UnitSymbol);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
```

O `IBrymenMeter` do acessor só tem as operações (`ReadAsync`, `ReadLoggerMemoryAsync`, `ReadLoggerImageAsync`,
`Device`, `State` e `LastError`): o medidor é do serviço, e quem usa o acessor não tem como descartá-lo nem parar o
laço dele. `ReadingsAsync` divide o laço do serviço entre quantas enumerações houver; cada uma guarda até 64 leituras
não consumidas (as mais antigas saem primeiro).

`appsettings.json` (todas as chaves são opcionais; os intervalos usam o formato do `TimeSpan`):

```json
{
  "Brymen": {
    "DevicePath": null,
    "PollInterval": "00:00:00.500",
    "ReportTimeout": "00:00:01",
    "ReconnectDelay": "00:00:02",
    "AutoReconnect": true,
    "LoggerRead": { "VerifyModel": true }
  }
}
```

`DeviceProvider` e `TimeProvider` não vêm da configuração. Para rodar sem hardware, com o medidor simulado (as
capturas reais do BM525s, o mesmo do `BrymenMeter.OpenSimulatedAsync`), ajuste-os por código; as duas sobrecargas
podem ser chamadas juntas:

```csharp
using Brymen.Dmm.Hid;

builder.Services.AddBrymenMeter(o => o.DeviceProvider = new SimulatedHidDeviceProvider());
```

Mais de um cabo: um nome por medidor, cada um com o seu `DevicePath`, e o acessor como serviço com chave:

```csharp
builder.Services.AddBrymenMeter("bancada1", builder.Configuration.GetSection("Brymen:Bancada1"));
builder.Services.AddBrymenMeter("bancada2", builder.Configuration.GetSection("Brymen:Bancada2"));

sealed class Bancada([FromKeyedServices("bancada1")] IBrymenMeterAccessor meter) { /* ... */ }
```

## Comportamento

- **Partida**: a 1ª abertura acontece no `StartingAsync` do serviço (`IHostedLifecycleService`), antes do `StartAsync`
  de todos os serviços, e o laço de leitura começa no `StartedAsync`, depois de todos. Um serviço pode esperar o medidor
  (`WaitForMeterAsync`) e assinar os eventos no próprio `StartAsync`, em qualquer ordem de registro, sem perder a 1ª
  leitura. Uma opção inválida ou um sistema sem transporte HID (só Linux e Windows têm) impedem a partida do host; sem o
  cabo (`E9015`), a partida segue e o serviço tenta de novo a cada `ReconnectDelay` (com `AutoReconnect`, o padrão).
- **Espera**: `WaitForMeterAsync` termina com falha quando o medidor não vai mais aparecer: a falha da abertura se o
  serviço desistir (sem `AutoReconnect`), `InvalidOperationException` com o serviço parado, a exceção da partida que
  falhou e `ObjectDisposedException` com o contêiner descartado.
- **Estado** (`IBrymenMeterAccessor.State`): o do medidor, quando aberto; antes disso, `Connecting` ou `Faulted` (veja
  `LastError`); `Closed` antes da partida e com o serviço parado.
- **Eventos**: `ReadingReceived`, `StateChanged` e `ErrorOccurred`, repassados do medidor, com o acessor como `sender`.
  Uma exceção num handler não interrompe a leitura: vira `ErrorOccurred` com o código `EVENT_HANDLER`.
- **Log**: abertura, falhas (uma falha repetida vai para o nível Debug até a comunicação voltar) e recuperação, na
  categoria `Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService` (com nome, seguida de `.{nome}`).
- **Parada**: o medidor é descartado, `Meter` volta a `null`, e as enumerações de `ReadingsAsync` terminam.

O guia completo da biblioteca, com a API, os códigos de erro e a regra udev do Linux, está no repositório, em
`docs/library.md`.

## Licença

Ainda não definida pelo autor: o pacote é publicado sem licença por enquanto.
