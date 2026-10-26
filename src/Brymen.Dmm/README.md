# Brymen.Dmm

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

## Instalação

```bash
dotnet add package Brymen.Dmm
```

Um pacote só: o `Brymen.Dmm.dll` e os blocos `Brymen.Dmm.Protocol.dll` (decodificação) e `Brymen.Dmm.Hid.dll`
(transporte HID e simulador) vão juntos, com a documentação XML. Para o Generic Host (worker services, ASP.NET Core),
há o pacote opcional `Brymen.Dmm.Extensions.Hosting`, com `AddBrymenMeter` e um serviço hospedado.

**Alvos:** `net8.0` (LTS) e `net10.0`. **Sistemas:** Windows e Linux com glibc (x64 e arm64); em outros sistemas, só o
medidor simulado ou um `IHidDeviceProvider` próprio.

## Leitura avulsa

```csharp
using Brymen.Dmm;
using Brymen.Dmm.Protocol;

await using var meter = await BrymenMeter.OpenAsync();   // o primeiro cabo; BrymenMeterException E9015 sem ele

DmmReading reading = await meter.ReadAsync();
Console.WriteLine($"{reading.Main?.Text} {reading.Main?.UnitSymbol} {reading.Main?.Mode}");   // 0.287 V AC
```

Um cabo específico: `BrymenMeter.OpenAsync(new BrymenMeterOptions { DevicePath = "/dev/hidraw0" })`. Os cabos
conectados: `BrymenMeter.FindDevices()`.

## Sem hardware

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

## Leitura contínua

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

## Famílias BM52x e BM82x

O mesmo cabo serve às duas famílias, com o mesmo mapa de LCD. A família vem do Model ID (bytes 20–23) do quadro de
tempo real e fica em `meter.Family` e em `DmmReading.Family`; `meter.ModelDescription` a descreve (`"BM52x (BM525s /
Minipa ET-2615A)"` ou `"Minipa ET-2517A (Brymen BM827s, BM82x)"`), e `meter.SupportsDataLogger` diz se há memória.

- **Detecção** (`BrymenMeterOptions.Family = MeterFamilySelection.Auto`, o padrão): a 1ª leitura depois da abertura
  envia o `82 66`, que as duas famílias aceitam, e já é uma leitura; sem nenhum relatório, ela tenta o `52 66`. Depois,
  o BM52x é lido com `52 66` e o BM82x com `82 66`. Um medidor trocado no cabo é detectado de novo (pelo Model ID, ou
  depois de 3 consultas sem resposta). Com `Bm52x` ou `Bm82x`, a família é fixa; `Bm52x` reproduz o tempo real da
  versão 0.1.0.
- **Pedidos descartados**: o ET-2517A descarta parte dos pedidos de tempo real, tanto mais quanto maior o intervalo
  entre eles (no medidor real, 0 de 40 a 0 ms, 11 de 40 a 200 ms e 19 de 40 a 500 ms), e esperar mais não adianta. Cada
  consulta do BM82x reenvia o mesmo pedido sem fechar o cabo depois de `RealTimeResendTimeout` (300 ms) sem resposta, até
  `RealTimeResendBudget` (2 s): só o orçamento esgotado é `W7012`. As perdas recuperadas não geram erro nem evento, e
  ficam contadas em `meter.RealTimeStatistics`. Um `PollInterval` menor perde menos pedidos.
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

## Eventos

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

## Datalogger

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

## Opções

| `BrymenMeterOptions` | Padrão | Descrição |
|---|---|---|
| `DevicePath` | `null` | Caminho do cabo (`/dev/hidraw0`, `/dev/brymen-bu86x`, `\\?\hid#vid_0820...`); `null` = o primeiro VID 0820/PID 0001. |
| `PollInterval` | 200 ms | Intervalo da leitura contínua. |
| `ReportTimeout` | 1 s | Espera por cada relatório de 8 bytes. |
| `Family` | `Auto` | Família do medidor: `Auto` (detectada na 1ª leitura), `Bm52x` ou `Bm82x`. |
| `RealTimeAfterMemoryTimeout` | 3 s | Prazo do 1º `66` depois da abertura e da leitura da memória (o BM525s leva ≈ 2 s): um envio só no BM52x conhecido; com reenvios na detecção e no BM82x. |
| `RealTimeResendTimeout`, `RealTimeResendBudget` | 300 ms, 2 s | BM82x: espera pelo 1º relatório antes de reenviar o pedido, e o tempo total da consulta com os reenvios. |
| `ReconnectDelay` | 2 s | Espera antes de reabrir o cabo depois de uma falha persistente. |
| `AutoReconnect` | `true` | Leitura contínua: reabre o cabo em vez de terminar. |
| `DrainTimeout`, `RecoveryDrainTimeout` | 5 ms, 100 ms | Descarte de relatórios atrasados antes de cada comando e depois de uma troca interrompida ou com reenvios (depois de uma consulta com reenvios interrompida, pelo menos `RealTimeResendTimeout`). |
| `DeviceProvider` | plataforma | `IHidDeviceProvider` (hidraw, hid.dll ou `SimulatedHidDeviceProvider`). |
| `TimeProvider` | `TimeProvider.System` | Relógio das esperas e dos instantes (um `FakeTimeProvider` nos testes). |
| `LoggerRead` | — | Memória: `VerifyModel`, `HeadSettleDelay` (200 ms), `RetryDelay`, `MaxHeadAttempts`, `MaxBlockAttempts`, `MaxRestarts`. |

## Erros

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

## Linux

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

## Documentação

O guia completo (API pública, threads, erros, tempos de espera, famílias e simulação) está no repositório, em
`docs/library.md`, e as mudanças de cada versão, em `CHANGELOG.md`.

## Licença

MIT. Código-fonte: https://github.com/SidineiSantosSousa/brymen-dmm-dotnet
