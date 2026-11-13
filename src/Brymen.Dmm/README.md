# Brymen.Dmm

**English** · Versão em português mais abaixo, na seção [Português](#português).

## English

.NET library for the **Brymen BM525s** multimeter (sold in Brazil as the **Minipa ET-2615A**) and other BM52x models with
a data logger, and for the **BM82x** family (such as the **Minipa ET-2517A**, without a data logger), connected through
the **BC-86X** USB optical cable (**BU-86X** kit, generic HID VID `0820h`, PID `0001h`). It runs on **Windows** (hid.dll)
and **Linux** (hidraw, x64 and arm64/Raspberry Pi), with no driver, no native dependencies and no package dependencies.

- Real-time reading (`52 66` on the BM52x, `82 66` on the BM82x): one-off, continuous (`IAsyncEnumerable`) or through
  events, with automatic reconnection. The family is detected on the 1st reading, with no extra query.
- Import of the BM52x data logger memory (`88`/`89`/`8A`), with checksums, error recovery and progress.
- Simulated meters built from real BM525s and ET-2517A captures, for demos and tests without hardware.
- Thread-safe, trimming and Native AOT compatible. The async methods never do the I/O on the caller's thread: they can
  be called from a UI thread.

> Independent project: not affiliated with Brymen or Minipa.

### Installation

```bash
dotnet add package Brymen.Dmm
```

A single package: `Brymen.Dmm.dll` and its building blocks `Brymen.Dmm.Protocol.dll` (decoding) and `Brymen.Dmm.Hid.dll`
(HID transport and simulator) ship together, with the XML documentation. For the Generic Host (worker services,
ASP.NET Core) there is the optional `Brymen.Dmm.Extensions.Hosting` package, with `AddBrymenMeter` and a hosted service.

**Targets:** `net8.0` (LTS) and `net10.0`. **Systems:** Windows and Linux with glibc (x64 and arm64); on other systems,
only the simulated meter or your own `IHidDeviceProvider`.

### One-off reading

```csharp
using Brymen.Dmm;
using Brymen.Dmm.Protocol;

await using var meter = await BrymenMeter.OpenAsync();   // the first cable; BrymenMeterException E9015 without one

DmmReading reading = await meter.ReadAsync();
Console.WriteLine($"{reading.Main?.Text} {reading.Main?.UnitSymbol} {reading.Main?.Mode}");   // 0.287 V AC
```

A specific cable: `BrymenMeter.OpenAsync(new BrymenMeterOptions { DevicePath = "/dev/hidraw0" })`. The connected
cables: `BrymenMeter.FindDevices()`.

### Without hardware

```csharp
await using var meter = await BrymenMeter.OpenSimulatedAsync();   // 14 real ACV readings and 1 session of 375 samples
```

The same simulated cable comes with `new SimulatedHidDeviceProvider()` without arguments, for
`BrymenMeterOptions.DeviceProvider` (in the Generic Host, for example); `new SimulatedHidDeviceProvider([])` is a missing
cable. The simulated ET-2517A is `SimulatedBm52xMeter.FromEt2517aCaptures()` (the 43 real frames of the sweep and the
measured request drops; without the drops, `CreateBm82x()`):
`BrymenMeter.OpenSimulatedAsync(SimulatedBm52xMeter.FromEt2517aCaptures())`. The simulator accepts fault injection
(`SimulatedBm52xMeter.FaultInjector`) and a switched-off meter (`IsPoweredOn`):

```csharp
using Brymen.Dmm.Hid;

var simulated = SimulatedBm52xMeter.FromCaptures();
simulated.FaultInjector = (command, n) => command == 0x89 && n % 10 == 0 ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
await using var meter = await BrymenMeter.OpenSimulatedAsync(simulated);
```

### Continuous reading

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    await foreach (var r in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(500), cts.Token))
        Console.WriteLine($"{r.Timestamp:HH:mm:ss.f} {r.Main?.Text} {r.Main?.UnitSymbol}");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // The 10 s are over: cancellation ends the enumeration with OperationCanceledException.
}
```

Cancelling the token ends the enumeration with `OperationCanceledException`; to stop after a number of readings, use
`break`. Each enumeration makes its own queries (two at the same time double the traffic on the cable): for several
consumers of the same readings, use the events below. Without an interval, `PollInterval` (200 ms) applies. With
`AutoReconnect` (the default), a removed cable or a switched-off meter does not end the enumeration: the failures go to
`LastError` and to the `ErrorOccurred` event, and the cable is reopened after `ReconnectDelay`. Without it, the
enumeration ends with the persistent failure: `E9016` on the 3rd failure in a row, or `E9015` without the cable.

### BM52x and BM82x families

The same cable serves both families, with the same LCD map. The family comes from the Model ID (bytes 20–23) of the
real-time frame and is in `meter.Family` and in `DmmReading.Family`; `meter.ModelDescription` describes it
(`"Minipa ET-2615A (Brymen BM525s, BM52x)"` or `"Minipa ET-2517A (Brymen BM827s, BM82x)"`), and
`meter.SupportsDataLogger` says whether there is a memory.

- **Detection** (`BrymenMeterOptions.Family = MeterFamilySelection.Auto`, the default): the 1st reading after opening
  sends `82 66`, which both families accept, and is already a reading; without any report, it tries `52 66`. After
  that, the BM52x is read with `52 66` and the BM82x with `82 66`. A meter swapped on the cable is detected again (by
  the Model ID, or after 3 queries without an answer). With `Bm52x` or `Bm82x`, the family is fixed; `Bm52x` reproduces
  the real-time behavior of version 0.1.0.
- **Dropped requests**: the ET-2517A drops part of the real-time requests, more so the longer the interval between them
  (on the real meter, 0 of 40 at 0 ms, 11 of 40 at 200 ms and 19 of 40 at 500 ms), and waiting longer does not help.
  Each BM82x query resends the same request, without closing the cable, after `RealTimeResendTimeout` (300 ms) without
  an answer, up to `RealTimeResendBudget` (3.5 s): only an exhausted budget is `W7012`. Resends raise no error or
  event, and are counted in `meter.RealTimeStatistics` (the cable discards the ones that arrive while it waits for the
  optical answer: the counters measure slow queries, not requests lost by the meter). A shorter `PollInterval` loses
  fewer requests.
- **Slow functions**: in logic Hz without a signal and in capacitance, the meter only answers at the end of its
  measurement cycle (up to ≈ 2.8 s with 1000 µF). The wait for the 1st report is therefore 3.5 s in both families
  (`RealTimeFirstReportTimeout` on the BM52x, `RealTimeResendBudget` on the BM82x).
- **Memory**: only the BM52x has a data logger. On the BM82x, `ReadLoggerMemoryAsync` and `ReadLoggerImageAsync` fail
  with `E9014`, without any memory command: right away, with the family fixed or confirmed by a reading less than 2 s
  old; with it unknown or older, after a real-time query that confirms it (the meter may have been swapped).

```csharp
DmmReading r = await meter.ReadAsync();
Console.WriteLine($"{meter.ModelDescription}: {r.Main?.Text} {r.Main?.UnitSymbol}");   // Minipa ET-2517A (Brymen BM827s, BM82x): ...

RealTimeStatistics s = meter.RealTimeStatistics;
Console.WriteLine($"{s.Queries} queries, {s.Resends} resends, {s.RecoveredQueries} recovered, {s.FailedQueries} unanswered");
```

The Model ID identifies the family, not the model. The BM525s (= ET-2615A) and the ET-2517A were validated on hardware;
the other models of both families (BM521s; BM821s, BM822s, BM827s and BM829s) are likely to work, but were not tested.

### Events

```csharp
meter.ReadingReceived += (_, r) => Console.WriteLine(r.Main?.Text);
meter.StateChanged += (_, e) => Console.WriteLine($"{e.Previous} -> {e.Current}");
meter.ErrorOccurred += (_, e) => Console.WriteLine($"[{e.Code}] {e.Message}");

meter.Start();          // background loop, every PollInterval
// ...
await meter.StopAsync();
```

Events are raised outside the internal locks, each handler on its own: an exception in a handler does not bring the
loop down (it becomes `ErrorOccurred` with the `EVENT_HANDLER` code). Do not block (`.Result`, `Wait()`) on a meter
operation inside a handler.

### Data logger

```csharp
var progress = new Progress<LoggerReadProgress>(p => Console.Write($"\r{p.Percent:0}%"));
LoggerMemory memory = await meter.ReadLoggerMemoryAsync(progress);
foreach (var session in memory.Sessions)
    Console.WriteLine($"Session {session.Number}: {session.MainFunction}, {session.Samples.Count} samples every {session.Interval.TotalSeconds} s");

// The raw image (24-byte blocks), with the retries and restarts:
LoggerMemoryImage image = await meter.ReadLoggerImageAsync();
File.WriteAllBytes("memory.bin", image.ToArray());
```

Continuous reading (`Start` or `ReadContinuouslyAsync`) is paused during the import and resumes on its own at the end.
The algorithm follows the original software: a 200 ms wait after `88`, `88` always recovered with another `88`, `8A`
only after an `89` the meter processed, a checksum per block and a limit on attempts (`LoggerReadOptions`).

### Options

| `BrymenMeterOptions` | Default | Description |
|---|---|---|
| `DevicePath` | `null` | Cable path (`/dev/hidraw0`, `/dev/brymen-bu86x`, `\\?\hid#vid_0820...`); `null` = the first VID 0820/PID 0001. |
| `PollInterval` | 200 ms | Interval of the continuous reading. |
| `ReportTimeout` | 1 s | Wait for each 8-byte report (memory, and the ones after the 1st in real time). |
| `RealTimeFirstReportTimeout` | 3.5 s | BM52x: wait for the 1st report of each real-time query; covers logic Hz and capacitance, where the meter only answers at the end of its cycle. |
| `Family` | `Auto` | Meter family: `Auto` (detected on the 1st reading), `Bm52x` or `Bm82x`. |
| `RealTimeAfterMemoryTimeout` | 3 s | Deadline of the 1st `66` after opening and after reading the memory (the BM525s takes ≈ 2 s): a single send on a known BM52x; with resends during detection and on the BM82x. |
| `RealTimeResendTimeout`, `RealTimeResendBudget` | 300 ms, 3.5 s | BM82x: wait for the 1st report before resending the request, and the total time of the query with the resends. |
| `ReconnectDelay` | 2 s | Wait before reopening the cable after a persistent failure. |
| `AutoReconnect` | `true` | Continuous reading: reopens the cable instead of ending. |
| `DrainTimeout`, `RecoveryDrainTimeout` | 5 ms, 100 ms | Discarding of late reports before each command and after an interrupted exchange or one with resends (after an interrupted query with resends, at least `RealTimeResendTimeout`). |
| `DeviceProvider` | platform | `IHidDeviceProvider` (hidraw, hid.dll or `SimulatedHidDeviceProvider`). |
| `TimeProvider` | `TimeProvider.System` | Clock of the waits and of the timestamps (a `FakeTimeProvider` in tests). |
| `LoggerRead` | — | Memory: `VerifyModel`, `HeadSettleDelay` (200 ms), `RetryDelay`, `MaxHeadAttempts`, `MaxBlockAttempts`, `MaxRestarts`. |

### Errors

Failures are `BrymenMeterException`, with a `Code` equal to the one of Brymen's original software:

| Code | Meaning |
|---|---|
| `E9015` | USB cable not found (or no permission on `/dev/hidrawN`). |
| `E9016` | Timeout: 3rd failure in a row of the continuous reading, or an unsuccessful memory read. |
| `E9017` | Meter switched off or without the DATA LOGGING function. |
| `E9014` | Family without a data logger (BM82x): the memory read fails without memory commands (specific to this library). |
| `W7012` | No data received in a real-time query (on the BM82x, after the resends run out). |
| `W8011` | Invalid frame received from the meter. |
| `INTERNAL` | Unexpected failure (a bug, or your own `IHidDeviceProvider`/`IHidDevice` throwing outside its contract), with the original exception in `InnerException`. |

On the real BM525s, the 1st command after a long time without communication once went unanswered (`W7012`), with the
following ones normal: handle the `W7012` of `ReadAsync` with a retry, or use the continuous reading, which tolerates 2
failures in a row.

### Linux

The user needs read and write access to the cable's `/dev/hidrawN`; without it, opening fails with `E9015`. A udev rule
solves it, for example in `/etc/udev/rules.d/70-brymen-bu86x.rules`:

```text
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0820", ATTRS{idProduct}=="0001", MODE="0660", GROUP="plugdev", TAG+="uaccess", SYMLINK+="brymen-bu86x"
```

```bash
sudo udevadm control --reload
# reapplies the rules only to the cable's hidraw (or unplug and replug the cable), not to every hidraw on the machine
for n in /sys/class/hidraw/hidraw*; do
    grep -qix 'HID_ID=0003:00000820:00000001' "$n/device/uevent" &&
        sudo udevadm trigger --action=change --subsystem-match=hidraw --sysname-match="${n##*/}"
done
sudo usermod -aG plugdev "$USER"     # and log in again
```

`TAG+="uaccess"` gives access to the local session's user (the file must come before `73-seat-late.rules`, hence the
`70-`); the group covers services and remote sessions. The `SYMLINK` creates the fixed path `/dev/brymen-bu86x`, which
can go in `DevicePath`. The `plugdev` group exists on Debian, Ubuntu and Raspberry Pi OS; on other distributions, use a
group of your own. In a container, pass the node through (`--device /dev/hidraw3:/dev/bu86x`) and use that path in
`DevicePath`: it is identified by the node itself (`HIDIOCGRAWINFO`), without sysfs.

### Documentation

The full guide (public API, threading, errors, timeouts, families and simulation) is in the repository, in
`docs/library.md` (in Portuguese), and the changes of each version are in `CHANGELOG.md`.

### License

MIT. Source code: https://github.com/SidineiSantosSousa/brymen-dmm-dotnet

## Português

Biblioteca .NET para o multímetro **Brymen BM525s** (vendido no Brasil como **Minipa ET-2615A**) e outros BM52x com
datalogger, e para a família **BM82x** (como o **Minipa ET-2517A**, sem datalogger), ligados pelo cabo óptico USB
**BC-86X** (kit **BU-86X**, HID genérico VID `0820h`, PID `0001h`). Funciona no **Windows** (hid.dll) e no **Linux**
(hidraw, x64 e arm64/Raspberry Pi), sem driver, sem dependências nativas e sem dependências de pacotes.

- Leitura do tempo real (`52 66` no BM52x, `82 66` no BM82x): avulsa, contínua (`IAsyncEnumerable`) ou por eventos, com
  reconexão automática. A família é detectada na 1ª leitura, sem consulta extra.
- Importação da memória do datalogger do BM52x (`88`/`89`/`8A`), com checksum, recuperação de erros e progresso.
- Medidores simulados com as capturas reais do BM525s e do ET-2517A, para demonstrações e testes sem hardware.
- Thread-safe, compatível com trimming e Native AOT. Os métodos assíncronos nunca fazem a E/S na thread de quem chama:
  podem ser chamados da thread de uma interface gráfica.

> Projeto independente: não é afiliado à Brymen nem à Minipa.

### Instalação

```bash
dotnet add package Brymen.Dmm
```

Um pacote só: o `Brymen.Dmm.dll` e os blocos `Brymen.Dmm.Protocol.dll` (decodificação) e `Brymen.Dmm.Hid.dll`
(transporte HID e simulador) vão juntos, com a documentação XML. Para o Generic Host (worker services, ASP.NET Core),
há o pacote opcional `Brymen.Dmm.Extensions.Hosting`, com `AddBrymenMeter` e um serviço hospedado.

**Alvos:** `net8.0` (LTS) e `net10.0`. **Sistemas:** Windows e Linux com glibc (x64 e arm64); em outros sistemas, só o
medidor simulado ou um `IHidDeviceProvider` próprio.

### Leitura avulsa

```csharp
using Brymen.Dmm;
using Brymen.Dmm.Protocol;

await using var meter = await BrymenMeter.OpenAsync();   // o primeiro cabo; BrymenMeterException E9015 sem ele

DmmReading reading = await meter.ReadAsync();
Console.WriteLine($"{reading.Main?.Text} {reading.Main?.UnitSymbol} {reading.Main?.Mode}");   // 0.287 V AC
```

Um cabo específico: `BrymenMeter.OpenAsync(new BrymenMeterOptions { DevicePath = "/dev/hidraw0" })`. Os cabos
conectados: `BrymenMeter.FindDevices()`.

### Sem hardware

```csharp
await using var meter = await BrymenMeter.OpenSimulatedAsync();   // 14 leituras reais em ACV e 1 sessão de 375 amostras
```

O mesmo cabo simulado vem no `new SimulatedHidDeviceProvider()` sem argumentos, para `BrymenMeterOptions.DeviceProvider`
(no Generic Host, por exemplo); `new SimulatedHidDeviceProvider([])` é o cabo ausente. O ET-2517A simulado é o
`SimulatedBm52xMeter.FromEt2517aCaptures()` (os 43 quadros reais da varredura e o descarte de pedidos medido; sem o
descarte, `CreateBm82x()`): `BrymenMeter.OpenSimulatedAsync(SimulatedBm52xMeter.FromEt2517aCaptures())`. O simulado
aceita injeção de falhas (`SimulatedBm52xMeter.FaultInjector`) e o medidor desligado (`IsPoweredOn`):

```csharp
using Brymen.Dmm.Hid;

var simulated = SimulatedBm52xMeter.FromCaptures();
simulated.FaultInjector = (command, n) => command == 0x89 && n % 10 == 0 ? SimulatedFault.CorruptChecksum : SimulatedFault.None;
await using var meter = await BrymenMeter.OpenSimulatedAsync(simulated);
```

### Leitura contínua

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    await foreach (var r in meter.ReadContinuouslyAsync(TimeSpan.FromMilliseconds(500), cts.Token))
        Console.WriteLine($"{r.Timestamp:HH:mm:ss.f} {r.Main?.Text} {r.Main?.UnitSymbol}");
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // Os 10 s acabaram: o cancelamento termina a enumeração com OperationCanceledException.
}
```

O cancelamento do token termina a enumeração com `OperationCanceledException`; para parar por contagem, use `break`.
Cada enumeração faz as suas próprias consultas (duas ao mesmo tempo dobram o tráfego no cabo): para vários consumidores
das mesmas leituras, use os eventos abaixo. Sem intervalo, vale `PollInterval` (200 ms). Com `AutoReconnect` (padrão),
um cabo removido ou um medidor desligado não termina a enumeração: as falhas vão para `LastError` e para o evento
`ErrorOccurred`, e o cabo é reaberto depois de `ReconnectDelay`. Sem ela, a enumeração termina com a falha persistente:
`E9016` na 3ª falha seguida, ou `E9015` sem o cabo.

### Famílias BM52x e BM82x

O mesmo cabo serve às duas famílias, com o mesmo mapa de LCD. A família vem do Model ID (bytes 20–23) do quadro de
tempo real e fica em `meter.Family` e em `DmmReading.Family`; `meter.ModelDescription` a descreve (`"Minipa ET-2615A
(Brymen BM525s, BM52x)"` ou `"Minipa ET-2517A (Brymen BM827s, BM82x)"`), e `meter.SupportsDataLogger` diz se há memória.

- **Detecção** (`BrymenMeterOptions.Family = MeterFamilySelection.Auto`, o padrão): a 1ª leitura depois da abertura
  envia o `82 66`, que as duas famílias aceitam, e já é uma leitura; sem nenhum relatório, ela tenta o `52 66`. Depois,
  o BM52x é lido com `52 66` e o BM82x com `82 66`. Um medidor trocado no cabo é detectado de novo (pelo Model ID, ou
  depois de 3 consultas sem resposta). Com `Bm52x` ou `Bm82x`, a família é fixa; `Bm52x` reproduz o tempo real da
  versão 0.1.0.
- **Pedidos descartados**: o ET-2517A descarta parte dos pedidos de tempo real, tanto mais quanto maior o intervalo
  entre eles (no medidor real, 0 de 40 a 0 ms, 11 de 40 a 200 ms e 19 de 40 a 500 ms), e esperar mais não adianta. Cada
  consulta do BM82x reenvia o mesmo pedido sem fechar o cabo depois de `RealTimeResendTimeout` (300 ms) sem resposta, até
  `RealTimeResendBudget` (3,5 s): só o orçamento esgotado é `W7012`. Os reenvios não geram erro nem evento, e ficam
  contados em `meter.RealTimeStatistics` (o cabo descarta os que chegam enquanto ele espera a resposta óptica: os
  contadores medem consultas lentas, não pedidos perdidos pelo medidor). Um `PollInterval` menor perde menos pedidos.
- **Funções lentas**: no Hz lógico sem sinal e na capacitância, o medidor só responde no fim do ciclo de medição (até
  ≈ 2,8 s com 1000 µF). Por isso a espera pelo 1º relatório é de 3,5 s nas duas famílias
  (`RealTimeFirstReportTimeout` no BM52x, `RealTimeResendBudget` no BM82x).
- **Memória**: só o BM52x tem datalogger. No BM82x, `ReadLoggerMemoryAsync` e `ReadLoggerImageAsync` falham com
  `E9014`, sem nenhum comando de memória: na hora, com a família fixa ou confirmada por uma leitura há menos de 2 s; com
  ela desconhecida ou mais antiga, depois de uma consulta de tempo real que a confirma (o medidor pode ter sido trocado).

```csharp
DmmReading r = await meter.ReadAsync();
Console.WriteLine($"{meter.ModelDescription}: {r.Main?.Text} {r.Main?.UnitSymbol}");   // Minipa ET-2517A (Brymen BM827s, BM82x): ...

RealTimeStatistics s = meter.RealTimeStatistics;
Console.WriteLine($"{s.Queries} consultas, {s.Resends} reenvios, {s.RecoveredQueries} recuperadas, {s.FailedQueries} sem resposta");
```

O Model ID identifica a família, e não o modelo. Foram validados no hardware o BM525s (= ET-2615A) e o ET-2517A; os
outros modelos das duas famílias (BM521s; BM821s, BM822s, BM827s e BM829s) são prováveis, mas não foram testados. A
versão 0.2.0, com a detecção e o reenvio, foi validada com o ET-2517A real no Windows; com o BM525s, só com o medidor
simulado.

### Eventos

```csharp
meter.ReadingReceived += (_, r) => Console.WriteLine(r.Main?.Text);
meter.StateChanged += (_, e) => Console.WriteLine($"{e.Previous} -> {e.Current}");
meter.ErrorOccurred += (_, e) => Console.WriteLine($"[{e.Code}] {e.Message}");

meter.Start();          // laço em segundo plano, a cada PollInterval
// ...
await meter.StopAsync();
```

Os eventos são disparados fora dos locks internos, cada handler à parte: uma exceção num handler não derruba o laço
(ela vira `ErrorOccurred` com o código `EVENT_HANDLER`). Não espere de forma bloqueante (`.Result`, `Wait()`) por uma
operação do medidor dentro de um handler.

### Datalogger

```csharp
var progress = new Progress<LoggerReadProgress>(p => Console.Write($"\r{p.Percent:0}%"));
LoggerMemory memory = await meter.ReadLoggerMemoryAsync(progress);
foreach (var session in memory.Sessions)
    Console.WriteLine($"Sessão {session.Number}: {session.MainFunction}, {session.Samples.Count} amostras a cada {session.Interval.TotalSeconds} s");

// A imagem bruta (blocos de 24 bytes), com as retentativas e os recomeços:
LoggerMemoryImage image = await meter.ReadLoggerImageAsync();
File.WriteAllBytes("memoria.bin", image.ToArray());
```

A leitura contínua (`Start` ou `ReadContinuouslyAsync`) fica pausada durante a importação e volta sozinha no fim. O
algoritmo segue o software original: espera de 200 ms depois do `88`, recuperação do `88` sempre com outro `88`, `8A` só
depois de um `89` que o medidor processou, checksum por bloco e limite de tentativas (`LoggerReadOptions`).

### Opções

| `BrymenMeterOptions` | Padrão | Descrição |
|---|---|---|
| `DevicePath` | `null` | Caminho do cabo (`/dev/hidraw0`, `/dev/brymen-bu86x`, `\\?\hid#vid_0820...`); `null` = o primeiro VID 0820/PID 0001. |
| `PollInterval` | 200 ms | Intervalo da leitura contínua. |
| `ReportTimeout` | 1 s | Espera por cada relatório de 8 bytes (memória, e os seguintes ao 1º no tempo real). |
| `RealTimeFirstReportTimeout` | 3,5 s | BM52x: espera pelo 1º relatório de cada consulta de tempo real; cobre o Hz lógico e a capacitância, em que o medidor só responde no fim do ciclo. |
| `Family` | `Auto` | Família do medidor: `Auto` (detectada na 1ª leitura), `Bm52x` ou `Bm82x`. |
| `RealTimeAfterMemoryTimeout` | 3 s | Prazo do 1º `66` depois da abertura e da leitura da memória (o BM525s leva ≈ 2 s): um envio só no BM52x conhecido; com reenvios na detecção e no BM82x. |
| `RealTimeResendTimeout`, `RealTimeResendBudget` | 300 ms, 3,5 s | BM82x: espera pelo 1º relatório antes de reenviar o pedido, e o tempo total da consulta com os reenvios. |
| `ReconnectDelay` | 2 s | Espera antes de reabrir o cabo depois de uma falha persistente. |
| `AutoReconnect` | `true` | Leitura contínua: reabre o cabo em vez de terminar. |
| `DrainTimeout`, `RecoveryDrainTimeout` | 5 ms, 100 ms | Descarte de relatórios atrasados antes de cada comando e depois de uma troca interrompida ou com reenvios (depois de uma consulta com reenvios interrompida, pelo menos `RealTimeResendTimeout`). |
| `DeviceProvider` | plataforma | `IHidDeviceProvider` (hidraw, hid.dll ou `SimulatedHidDeviceProvider`). |
| `TimeProvider` | `TimeProvider.System` | Relógio das esperas e dos instantes (um `FakeTimeProvider` nos testes). |
| `LoggerRead` | — | Memória: `VerifyModel`, `HeadSettleDelay` (200 ms), `RetryDelay`, `MaxHeadAttempts`, `MaxBlockAttempts`, `MaxRestarts`. |

### Erros

As falhas são `BrymenMeterException`, com `Code` igual ao do software original da Brymen:

| Código | Significado |
|---|---|
| `E9015` | Cabo USB não encontrado (ou sem permissão no `/dev/hidrawN`). |
| `E9016` | Tempo esgotado: 3ª falha seguida da leitura contínua, ou leitura da memória sem sucesso. |
| `E9017` | Medidor desligado ou sem a função DATA LOGGING. |
| `E9014` | Família sem datalogger (BM82x): a leitura da memória falha sem comandos de memória (próprio da biblioteca). |
| `W7012` | Nenhum dado recebido numa consulta de tempo real (no BM82x, depois de esgotados os reenvios). |
| `W8011` | Quadro inválido recebido do medidor. |
| `INTERNAL` | Falha inesperada (um defeito, ou um `IHidDeviceProvider`/`IHidDevice` próprio que lançou fora do contrato), com a exceção original em `InnerException`. |

No BM525s real, o 1º comando depois de muito tempo sem comunicação já ficou sem resposta (`W7012`) uma vez, com os
seguintes normais: trate o `W7012` de `ReadAsync` com uma nova tentativa, ou use a leitura contínua, que tolera 2 falhas
seguidas.

### Linux

O usuário precisa de leitura e escrita no `/dev/hidrawN` do cabo; sem isso, a abertura dá `E9015`. Uma regra udev
resolve, por exemplo em `/etc/udev/rules.d/70-brymen-bu86x.rules`:

```text
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0820", ATTRS{idProduct}=="0001", MODE="0660", GROUP="plugdev", TAG+="uaccess", SYMLINK+="brymen-bu86x"
```

```bash
sudo udevadm control --reload
# reaplica as regras só ao hidraw do cabo (ou desligue e religue o cabo), e não a todos os hidraw da máquina
for n in /sys/class/hidraw/hidraw*; do
    grep -qix 'HID_ID=0003:00000820:00000001' "$n/device/uevent" &&
        sudo udevadm trigger --action=change --subsystem-match=hidraw --sysname-match="${n##*/}"
done
sudo usermod -aG plugdev "$USER"     # e entre de novo na sessão
```

O `TAG+="uaccess"` dá acesso ao usuário da sessão local (o arquivo precisa vir antes do `73-seat-late.rules`, daí o
`70-`); o grupo cobre serviços e sessões remotas. O `SYMLINK` cria o caminho fixo `/dev/brymen-bu86x`, que pode ir em
`DevicePath`. O grupo `plugdev` existe no Debian, no Ubuntu e no Raspberry Pi OS; em outras distribuições, use um grupo
próprio. Num contêiner, repasse o nó (`--device /dev/hidraw3:/dev/bu86x`) e use o caminho em `DevicePath`: ele é
identificado pelo próprio nó (`HIDIOCGRAWINFO`), sem o sysfs.

### Documentação

O guia completo (API pública, threads, erros, tempos de espera, famílias e simulação) está no repositório, em
`docs/library.md`, e as mudanças de cada versão, em `CHANGELOG.md`.

### Licença

MIT. Código-fonte: https://github.com/SidineiSantosSousa/brymen-dmm-dotnet
