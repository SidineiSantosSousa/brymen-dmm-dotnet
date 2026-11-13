# Brymen.Dmm.Extensions.Hosting

**English** · Versão em português mais abaixo, na seção [Português](#português).

## English

Integration of the `Brymen.Dmm` package with the .NET Generic Host (`Microsoft.Extensions.Hosting`): worker services,
ASP.NET Core and any app with an `IServiceCollection`. For the **Brymen BM525s** multimeter (= **Minipa ET-2615A**) and
other BM52x models with a data logger, and for the **BM82x** family (such as the **Minipa ET-2517A**, without a data
logger), through the **BC-86X** USB optical cable (**BU-86X** kit).

- `AddBrymenMeter(...)`: options from code or from `IConfiguration` (binding generated at compile time, trimming and
  Native AOT compatible); with a name, one meter per cable.
- A hosted service that opens the meter at startup, keeps trying while the cable is missing, keeps the continuous
  reading going with automatic reconnection and closes the cable on shutdown.
- `IBrymenMeterAccessor`: the one-off operations of the current meter (`IBrymenMeter`, or `null`), the readings of the
  service's loop (`ReadingsAsync` and events), the latest reading, the latest failure and the meter family (`Family`;
  on `IBrymenMeter`, also `SupportsDataLogger`, `ModelDescription` and `RealTimeStatistics`).

Targets `net8.0` and `net10.0`. Dependencies: `Brymen.Dmm` and the `Microsoft.Extensions.*` packages
(Hosting.Abstractions, Options, Configuration.Binder and Logging.Abstractions), from 8.0.0 on the `net8.0` target and
from 10.0.0 on `net10.0`: any patch of those lines works.

> Independent project: not affiliated with Brymen or Minipa.

### Installation

```bash
dotnet add package Brymen.Dmm.Extensions.Hosting
```

### Usage

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

        // One-off operations on the open meter: the import pauses the continuous reading and resumes it at the end.
        // Only the BM52x has a data logger; on the BM82x (ET-2517A), the memory read fails with E9014, without any
        // memory command.
        IBrymenMeter meter = await accessor.WaitForMeterAsync(stoppingToken);
        try
        {
            var memory = await meter.ReadLoggerMemoryAsync(cancellationToken: stoppingToken);
            logger.LogInformation("{Sessions} sessions in the data logger", memory.Sessions.Count);
        }
        catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.DataLoggerNotSupported)
        {
            logger.LogInformation("{Model}: no data logger", meter.ModelDescription);
        }

        // The readings of the service's loop, without extra queries on the cable.
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

The accessor's `IBrymenMeter` only has the operations (`ReadAsync`, `ReadLoggerMemoryAsync`, `ReadLoggerImageAsync`,
`Device`, `State`, `LastError`, `Family`, `SupportsDataLogger`, `ModelDescription` and `RealTimeStatistics`): the meter
belongs to the service, and users of the accessor cannot dispose of it or stop its loop. `accessor.Family` is the family
of the open meter or, without one, that of the latest reading. `ReadingsAsync` shares the service's loop among as many
enumerations as there are; each one keeps up to 64 unconsumed readings (the oldest go first).

`appsettings.json` (every key is optional; intervals use the `TimeSpan` format):

```json
{
  "Brymen": {
    "DevicePath": null,
    "Family": "Auto",
    "PollInterval": "00:00:00.500",
    "ReportTimeout": "00:00:01",
    "RealTimeFirstReportTimeout": "00:00:03.500",
    "RealTimeResendTimeout": "00:00:00.300",
    "RealTimeResendBudget": "00:00:03.500",
    "ReconnectDelay": "00:00:02",
    "AutoReconnect": true,
    "LoggerRead": { "VerifyModel": true }
  }
}
```

`Family` accepts `Auto` (detected on the 1st reading), `Bm52x` and `Bm82x`; `RealTimeResendTimeout` and
`RealTimeResendBudget` tune the BM82x resends, and `RealTimeFirstReportTimeout` the wait for the 1st report on the
BM52x. `DeviceProvider` and `TimeProvider` do not come from configuration. To run without hardware, with the simulated
meter (the real BM525s captures, the same as `BrymenMeter.OpenSimulatedAsync`), set them in code; both overloads can be
called together:

```csharp
using Brymen.Dmm.Hid;

builder.Services.AddBrymenMeter(o => o.DeviceProvider = new SimulatedHidDeviceProvider());

// Or the simulated ET-2517A (BM82x family, no data logger, with the real meter's request drops):
builder.Services.AddBrymenMeter(o => o.DeviceProvider = new SimulatedHidDeviceProvider(
    [SimulatedHidDevice.CreateTypicalBm52x(SimulatedBm52xMeter.FromEt2517aCaptures())]));
```

More than one cable: one name per meter, each with its own `DevicePath`, and the accessor as a keyed service:

```csharp
builder.Services.AddBrymenMeter("bench1", builder.Configuration.GetSection("Brymen:Bench1"));
builder.Services.AddBrymenMeter("bench2", builder.Configuration.GetSection("Brymen:Bench2"));

sealed class Bench([FromKeyedServices("bench1")] IBrymenMeterAccessor meter) { /* ... */ }
```

### Behavior

- **Startup**: the 1st open happens in the service's `StartingAsync` (`IHostedLifecycleService`), before the
  `StartAsync` of every service, and the reading loop starts in `StartedAsync`, after all of them. A service can wait
  for the meter (`WaitForMeterAsync`) and subscribe to the events in its own `StartAsync`, in any registration order,
  without missing the 1st reading. An invalid option or a system without an HID transport (only Linux and Windows have
  one) stop the host from starting; without the cable (`E9015`), startup goes on and the service retries every
  `ReconnectDelay` (with `AutoReconnect`, the default).
- **Waiting**: `WaitForMeterAsync` fails when the meter is not going to show up anymore: the opening failure if the
  service gives up (without `AutoReconnect`), `InvalidOperationException` with the service stopped, the exception of a
  failed startup and `ObjectDisposedException` with the container disposed.
- **State** (`IBrymenMeterAccessor.State`): the meter's, when open; before that, `Connecting` or `Faulted` (see
  `LastError`); `Closed` before startup and with the service stopped.
- **Events**: `ReadingReceived`, `StateChanged` and `ErrorOccurred`, forwarded from the meter, with the accessor as
  `sender`. An exception in a handler does not interrupt the reading: it becomes `ErrorOccurred` with the
  `EVENT_HANDLER` code.
- **Logging**: opening, meter family (event 8, on the 1st reading and when it changes: "Família do multímetro: BM82x
  (Minipa ET-2517A), sem datalogger."), failures (a repeated failure goes to the Debug level until communication comes
  back) and recovery, in the `Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService` category (with a name, followed by
  `.{name}`). The log messages are in Portuguese. BM82x requests recovered by a resend are not failures and are not
  logged.
- **Shutdown**: the meter is disposed of, `Meter` goes back to `null`, and the `ReadingsAsync` enumerations end.

The library's full guide, with the API, the error codes, the families and the Linux udev rule, is in the repository, in
`docs/library.md` (in Portuguese), and the changes of each version are in `CHANGELOG.md`.

### License

MIT. Source code: https://github.com/SidineiSantosSousa/brymen-dmm-dotnet

## Português

Integração do pacote `Brymen.Dmm` com o Generic Host do .NET
(`Microsoft.Extensions.Hosting`): worker services, ASP.NET Core e qualquer app com `IServiceCollection`. Para o
multímetro **Brymen BM525s** (= **Minipa ET-2615A**) e outros BM52x com datalogger, e para a família **BM82x** (como o
**Minipa ET-2517A**, sem datalogger), pelo cabo óptico USB **BC-86X** (kit **BU-86X**).

- `AddBrymenMeter(...)`: opções por código ou por `IConfiguration` (binding gerado em tempo de compilação, compatível
  com trimming e Native AOT); com nome, um medidor por cabo.
- Um serviço hospedado que abre o medidor na partida, tenta de novo enquanto o cabo não aparece, mantém a leitura
  contínua com reconexão automática e fecha o cabo na parada.
- `IBrymenMeterAccessor`: as operações avulsas do medidor atual (`IBrymenMeter`, ou `null`), as leituras do laço do
  serviço (`ReadingsAsync` e eventos), a última leitura, a última falha e a família do medidor (`Family`; no
  `IBrymenMeter`, também `SupportsDataLogger`, `ModelDescription` e `RealTimeStatistics`).

Alvos `net8.0` e `net10.0`. Dependências: `Brymen.Dmm` e os pacotes `Microsoft.Extensions.*` (Hosting.Abstractions,
Options, Configuration.Binder e Logging.Abstractions), a partir da 8.0.0 no alvo `net8.0` e da 10.0.0 no `net10.0`:
qualquer patch dessas linhas serve.

> Projeto independente: não é afiliado à Brymen nem à Minipa.

### Instalação

```bash
dotnet add package Brymen.Dmm.Extensions.Hosting
```

### Uso

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

        // Operações avulsas no medidor aberto: a importação pausa a leitura contínua e a retoma no fim. Só o BM52x tem
        // datalogger; no BM82x (ET-2517A), a leitura da memória falha com E9014, sem nenhum comando de memória.
        IBrymenMeter meter = await accessor.WaitForMeterAsync(stoppingToken);
        try
        {
            var memory = await meter.ReadLoggerMemoryAsync(cancellationToken: stoppingToken);
            logger.LogInformation("{Sessions} sessões no datalogger", memory.Sessions.Count);
        }
        catch (BrymenMeterException ex) when (ex.Code == BrymenErrorCodes.DataLoggerNotSupported)
        {
            logger.LogInformation("{Model}: sem datalogger", meter.ModelDescription);
        }

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
`Device`, `State`, `LastError`, `Family`, `SupportsDataLogger`, `ModelDescription` e `RealTimeStatistics`): o medidor é
do serviço, e quem usa o acessor não tem como descartá-lo nem parar o laço dele. `accessor.Family` é a família do
medidor aberto ou, sem ele, a da última leitura. `ReadingsAsync` divide o laço do serviço entre quantas enumerações
houver; cada uma guarda até 64 leituras não consumidas (as mais antigas saem primeiro).

`appsettings.json` (todas as chaves são opcionais; os intervalos usam o formato do `TimeSpan`):

```json
{
  "Brymen": {
    "DevicePath": null,
    "Family": "Auto",
    "PollInterval": "00:00:00.500",
    "ReportTimeout": "00:00:01",
    "RealTimeFirstReportTimeout": "00:00:03.500",
    "RealTimeResendTimeout": "00:00:00.300",
    "RealTimeResendBudget": "00:00:03.500",
    "ReconnectDelay": "00:00:02",
    "AutoReconnect": true,
    "LoggerRead": { "VerifyModel": true }
  }
}
```

`Family` aceita `Auto` (detectada na 1ª leitura), `Bm52x` e `Bm82x`; `RealTimeResendTimeout` e `RealTimeResendBudget`
ajustam os reenvios do BM82x, e `RealTimeFirstReportTimeout`, a espera do 1º relatório no BM52x. `DeviceProvider` e
`TimeProvider` não vêm da configuração. Para rodar sem hardware, com o medidor simulado (as capturas reais do BM525s, o mesmo do `BrymenMeter.OpenSimulatedAsync`), ajuste-os por código; as
duas sobrecargas podem ser chamadas juntas:

```csharp
using Brymen.Dmm.Hid;

builder.Services.AddBrymenMeter(o => o.DeviceProvider = new SimulatedHidDeviceProvider());

// Ou o ET-2517A simulado (família BM82x, sem datalogger, com o descarte de pedidos do medidor real):
builder.Services.AddBrymenMeter(o => o.DeviceProvider = new SimulatedHidDeviceProvider(
    [SimulatedHidDevice.CreateTypicalBm52x(SimulatedBm52xMeter.FromEt2517aCaptures())]));
```

Mais de um cabo: um nome por medidor, cada um com o seu `DevicePath`, e o acessor como serviço com chave:

```csharp
builder.Services.AddBrymenMeter("bancada1", builder.Configuration.GetSection("Brymen:Bancada1"));
builder.Services.AddBrymenMeter("bancada2", builder.Configuration.GetSection("Brymen:Bancada2"));

sealed class Bancada([FromKeyedServices("bancada1")] IBrymenMeterAccessor meter) { /* ... */ }
```

### Comportamento

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
- **Log**: abertura, família do medidor (evento 8, na 1ª leitura e quando ela muda: “Família do multímetro: BM82x
  (Minipa ET-2517A), sem datalogger.”), falhas (uma falha repetida vai para o nível Debug até a comunicação voltar) e
  recuperação, na categoria `Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService` (com nome, seguida de `.{nome}`).
  Os pedidos do BM82x recuperados por reenvio não são falhas e não vão para o log.
- **Parada**: o medidor é descartado, `Meter` volta a `null`, e as enumerações de `ReadingsAsync` terminam.

O guia completo da biblioteca, com a API, os códigos de erro, as famílias e a regra udev do Linux, está no repositório,
em `docs/library.md`, e as mudanças de cada versão, em `CHANGELOG.md`.

### Licença

MIT. Código-fonte: https://github.com/SidineiSantosSousa/brymen-dmm-dotnet
