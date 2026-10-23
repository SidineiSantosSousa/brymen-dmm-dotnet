# Changelog

As mudanças relevantes dos pacotes `Brymen.Dmm` e `Brymen.Dmm.Extensions.Hosting` e da API REST (`src/Brymen.Dmm.Api`).
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/), e a versão é a do
[`Directory.Build.props`](Directory.Build.props), a mesma para os pacotes e para a API.

## 0.2.0 - 2026-10-23

Suporte à família **BM82x**, validada no protocolo com o **Minipa ET-2517A** real, ao lado da família **BM52x** do
BM525s (= Minipa ET-2615A). Na API pública da biblioteca, só acréscimos: nada foi removido nem renomeado.

### Acrescentado

**Medidores** ([Medidores suportados](README.md#medidores-suportados)):

- Minipa ET-2517A: família BM82x, Model ID `82h`, sem datalogger, no mesmo cabo BC-86X. Ele não responde ao `52 66` e
  responde ao `82 66` com o mesmo mapa de LCD do BM52x.
- Detecção automática da família: a 1ª consulta de tempo real depois da abertura envia o `82 66`, que as duas famílias
  aceitam, e já é uma leitura; sem nenhum relatório, ela tenta o `52 66` de reserva.
- Reenvio dos pedidos de tempo real que o ET-2517A descarta: o mesmo pedido sai de novo, sem fechar o cabo, depois de
  300 ms sem o 1º relatório, até 2 s por consulta (3 s na espera longa do 1º `66`, também reenviada na detecção e no
  BM82x).

**`Brymen.Dmm.Protocol`**:

- `enum MeterFamily : byte { Bm52x = 0x52, Bm82x = 0x82 }` e `static class MeterFamilies`: `TryFromModelId(byte, out
  MeterFamily)` e as extensões `Code()`, `HasDataLogger()`, `Name()` (`"BM52x"`, `"BM82x"`) e `Describe()`
  (`"BM52x (BM525s / Minipa ET-2615A)"`, `"BM82x (Minipa ET-2517A)"`).
- `Bm52xCommands.Bm52xFamily` (`0x52`) e `Bm52xCommands.Bm82xFamily` (`0x82`); `Bm52xCommands.Family` (`0x52`) continua.
- `Bm52xRealtime.Decode(ReadOnlySpan<byte>, DateTimeOffset, MeterFamily expectedFamily)`,
  `TryDecode(ReadOnlySpan<byte>, DateTimeOffset, MeterFamily expectedFamily, out DmmReading?, out string?)` e
  `TryGetFamily(ReadOnlySpan<byte>, out MeterFamily)`.
- `DmmReading.Family` (padrão `MeterFamily.Bm52x`).

**`Brymen.Dmm.Hid`**:

- `Bu86xLink.ExecuteAsync(byte family, byte command, int reportCount, ...)`, em três sobrecargas: com o
  `ReportTimeout`, com `TimeSpan firstReportTimeout` e com `Bu86xResendPolicy resend`. As sobrecargas sem família
  continuam enviando `52` (`Bu86xLink.DefaultFamily`).
- `Bu86xLink.LastCommandAttempts` (envios da última chamada) e `Bu86xLinkOptions.TimeProvider` (relógio do orçamento
  dos reenvios).
- `sealed class Bu86xResendPolicy(TimeSpan firstReportWindow, TimeSpan budget)`, com `FirstReportWindow`, `Budget` e
  `Default` (300 ms e 2 s).
- `Bu86xTimeoutException.Family` e `Attempts`, e `Bu86xShortReportException.Family`, com construtores novos que recebem
  a família.
- Medidor BM82x simulado: `SimulatedBm52xMeter.CreateBm82x(...)` e `FromEt2517aCaptures(...)`, com os 43 quadros reais
  da varredura do ET-2517A (`CapturedEt2517aRealTimeFrames`, recurso embutido) e o descarte de pedidos medido no
  hardware (`RequestDropper`, `CreateEt2517aRequestDropper(seed, maxConsecutiveDrops)`, `Et2517aDropProbability(idle)`,
  `DefaultDropSeed`, `DroppedRequestCount`); `FamilyCode` e `HasDataLogger`.

**`Brymen.Dmm`**:

- `BrymenMeterOptions.Family` (`MeterFamilySelection`: `Auto`, o padrão, `Bm52x` ou `Bm82x`),
  `RealTimeResendTimeout` (300 ms) e `RealTimeResendBudget` (2 s).
- `BrymenMeter.Family`, `SupportsDataLogger`, `ModelDescription` e `RealTimeStatistics`; os mesmos membros no
  `IBrymenMeter`, com implementação padrão.
- `readonly record struct RealTimeStatistics(long Queries, long Requests, long Resends, long LostRequests, long
  RecoveredQueries, long FailedQueries)`.
- `MeterConnection.Family`, `SupportsDataLogger`, `RealTimeStatistics`, `ExecuteAsync(byte family, byte command, int
  reportCount, CancellationToken)`, `TryExecuteAsync(byte family, ...)`, `DefaultRealTimeResendTimeout`,
  `DefaultRealTimeResendBudget` e `FailuresBeforeRedetection` (3).
- `MeterConnection.IsFamilyConfirmed` e `MeterConnection.FamilyConfirmationWindow` (2 s): a família fixa, ou detectada
  por um quadro de tempo real há menos de 2 s, recusa a memória de um BM82x sem nenhum comando; conhecida há mais tempo,
  ela é confirmada antes pela conferência do modelo.
- `BrymenErrorCodes.DataLoggerNotSupported` (`E9014`, próprio da biblioteca): a leitura da memória de um BM82x falha sem
  nenhum comando de memória.

**`Brymen.Dmm.Extensions.Hosting`**:

- `IBrymenMeterAccessor.Family`, com implementação padrão; sem medidor aberto, o acessor devolve a família da última
  leitura.
- Chaves de configuração `Family`, `RealTimeResendTimeout` e `RealTimeResendBudget`.
- Evento de log 8 (Information): "Família do multímetro: ..., com/sem datalogger.", na 1ª leitura e a cada troca.

**API REST** (`src/Brymen.Dmm.Api`):

- `GET /api/v1/meter` ganhou `model` (`family` `"BM52x"`/`"BM82x"`, `modelId` `"0x52"`/`"0x82"`, `name` e
  `supportsDataLogger`; `null` antes da 1ª detecção) e `realTimeStatistics` (`queries`, `requests`, `resends`,
  `lostRequests`, `recoveredQueries` e `failedQueries`, desde a partida do serviço).
- `POST /api/v1/logger/imports` responde 409 com `E9014` (título "Medidor sem datalogger") num medidor da família BM82x,
  sem criar importação nem enviar comando de memória. Com a família ainda desconhecida, ou conhecida há mais de 2 s sem
  quadro de tempo real (o polling parado, por exemplo), uma consulta de tempo real a confirma antes: um BM525s posto no
  lugar do ET-2517A é importado normalmente.
- Configuração: `Meter:Family` (`Auto`, `Bm52x`, `Bm82x`), `Meter:RealTimeResendTimeout` (300 ms, de 10 ms a 30 s),
  `Meter:RealTimeResendBudget` (2 s, de 10 ms a 1 min) e `Meter:SimulatedModel` (`Bm525s`, o padrão, ou `Et2517a`, o
  ET-2517A simulado com o descarte de pedidos).
- O evento SignalR `status` também sai quando a família detectada muda.
- Painel: o título mostra o medidor detectado ("Brymen DMM" antes da 1ª leitura), com a família, o Model ID e os
  reenvios na dica; num medidor sem datalogger, o botão "Importar do medidor" dá lugar a uma nota.
- Descrições do documento OpenAPI com as duas famílias e o `E9014`.

**Exemplos** (`samples/`):

- ConsoleSample: `--simulated [bm525s|et2517a]`, `--family <auto|bm52x|bm82x>`, a linha "Medidor:" na 1ª leitura e
  os contadores do tempo real no fim.
- WorkerSample: `--simulated [bm525s|et2517a]` e a chave `Family` no `appsettings.json`.
- SignalRClient: o medidor detectado (`model`) nas linhas de estado.

**Documentação e dados**:

- §8 da [especificação](docs/protocol/bm52x-usb-protocol.md#8-família-bm82x-model-id-0x82-e-o-minipa-et-2517a): o
  `82 66`, o mapa do BM82x, quadros comentados do ET-2517A, as medições dos pedidos descartados, o reenvio e a detecção.
- Capturas [`et2517a-sweep-2026-10-23.txt`](docs/protocol/captures/et2517a-sweep-2026-10-23.txt) e
  [`et2517a-commands-2026-10-23.txt`](docs/protocol/captures/et2517a-commands-2026-10-23.txt), e os scripts
  [`docs/protocol/tools/`](docs/protocol/tools/) (`sweep.cs`, `rate.cs`, `rate2.cs`) que as geraram.
- [`test-vectors.json`](docs/protocol/test-vectors.json) passou de 14 para 28 vetores (13 quadros reais do ET-2517A e o
  exemplo do PDF-N corrigido).
- Este CHANGELOG.

### Alterado

- **1ª consulta de tempo real.** Com `Family = Auto` (o padrão), a 1ª consulta de cada conexão envia `82 66`. Depois
  dela, o BM52x volta ao `52 66` de sempre, e o BM82x continua com o `82 66`.
- **Família depois da detecção.** Um quadro válido com o Model ID da outra família troca a família na hora (o medidor
  trocado no cabo). Depois de 3 consultas seguidas sem resposta, a família é detectada de novo: um BM525s trocado pelo
  ET-2517A dá 3 falhas, e a 3ª é o `E9016`.
- **`W7012` no BM82x** só sai quando o orçamento dos reenvios (2 s) acaba. As perdas recuperadas não geram erro nem
  `ErrorOccurred`, e ficam em `RealTimeStatistics`. A política das 3 falhas seguidas (`E9016`) conta consultas com o
  orçamento esgotado, e não pedidos perdidos.
- **Leitura da memória num BM82x**: `E9014` sem comandos de memória e sem mudar `State` nem `LastError`: na hora, com a
  família fixa ou confirmada por um quadro de tempo real há menos de 2 s (a leitura contínua rodando); com a família
  desconhecida, ou conhecida há mais tempo (o medidor pode ter sido trocado no cabo), depois da conferência do modelo
  (`LoggerReadOptions.VerifyModel`), que envia uma consulta de tempo real e nenhum `88`.
- **Espera longa do 1º `66`** (depois da abertura e de comandos de memória): com a família desconhecida (a detecção) ou
  BM82x, o pedido é reenviado a cada `RealTimeResendTimeout` até o prazo de `RealTimeAfterMemoryTimeout` (3 s), como na
  §8.5.4: o 1º pedido depois da abertura é o que vem depois do maior tempo ocioso, e o que o ET-2517A mais descarta. Com
  a família BM52x conhecida, ou depois de um comando de memória respondido, continua um envio só.
- **Descarte de recuperação**: depois de uma troca com reenvios que não terminou com o quadro inteiro (cancelada ou com
  o orçamento esgotado), o próximo comando no mesmo link exige pelo menos a janela do reenvio (300 ms) de silêncio, que
  cobre a latência do ET-2517A (até ≈ 260 ms). O `Bu86xLink` decide o descarte e os números de `Bu86xTimeoutException`
  (`Attempts`, `ReportsReceived`) pela contagem da própria troca, e não pelos contadores publicados, que um chamador
  concorrente zera.
- **Decodificador.** `Bm52xRealtime.Decode` e `TryDecode` sem família aceitam os Model IDs `52525252` e `82828282`, e
  recusam bytes mistos e outros valores. A mensagem de erro do Model ID diz o esperado da família que a maioria dos bytes
  indica (`52525282` → "esperado 52525252"), ou as duas quando não dá para dizer.
- **Simulador.** O BM525s simulado também responde ao `82 66`, com o mesmo rodízio de quadros, como o medidor real.
- **`Bu86xLink.LastCommandReportsReceived`**: com reenvios, é o maior número de relatórios de uma mesma resposta.
- **Tempo sem resposta.** Com a família desconhecida, uma consulta sem resposta demora mais: até 3 s (a espera longa
  do 1º `66`) ou 2 s (o orçamento dos reenvios), mais o descarte de recuperação (300 ms) e 1 s do `52 66` de reserva.
- **API.** O polling usa a detecção e o reenvio da biblioteca. A importação num ET-2517A, que na 0.1.0 dava `E9017`
  (503) depois de um `52 66` sem resposta, agora dá `E9014` (409), sem nenhum comando de memória.
- Descrição e tags dos dois pacotes NuGet, com o BM82x e o ET-2517A.
- **Testes com o cabo real** (`BRYMEN_HW_TESTS=1`): detectam a família pelo `82 66` e servem para os dois medidores. No
  BM82x, a importação tem de dar `E9014` sem nenhum comando de memória; no BM52x, ela continua comparada com o
  `memory-dump-1.txt`. Novo teste de 10 s a 500 ms: nenhuma reabertura do cabo por perda recuperável. No BM82x, os
  testes contínuos aceitam `W7012` isolados (orçamento esgotado), mas nenhum `E9016`.

### Compatibilidade

- **Código e binário**: nenhum membro público foi removido, renomeado nem mudou de assinatura. Os membros novos das
  interfaces públicas (`IBrymenMeter`, `IBrymenMeterAccessor`) têm implementação padrão, e quem as implementa ou simula
  compila sem mudanças. Alvos (`net8.0` e `net10.0`) e dependências não mudaram.
- **BM525s**: o quadro do `82 66` é idêntico ao do `52 66` (só a 1ª consulta muda). Para o tempo real da 0.1.0
  (`52 66` sempre, um envio, sem detecção), use `BrymenMeterOptions.Family = MeterFamilySelection.Bm52x` (no hosting,
  `"Family": "Bm52x"`; na API, `Meter:Family=Bm52x`). Com a família fixa, um quadro da outra família vira `W8011`.
- **BM52x conhecido**: mantém a política da 0.1.0, um envio com `ReportTimeout`, sem a janela de 300 ms.
- **Quem chama `Bm52xRealtime.Decode` direto**: um quadro com Model ID `82h`, recusado na 0.1.0, agora é decodificado,
  com `DmmReading.Family = Bm82x`. Para recusá-lo, use a sobrecarga com `MeterFamily.Bm52x`.
- **Testes com o simulador**: um teste que contava com o BM525s simulado ignorando o `82 66` precisa ser ajustado.
- **Configuração**: as chaves novas são opcionais; as ausentes ficam com o padrão.
- **Clientes da API**: o JSON de `GET /api/v1/meter` (e do evento `status`) só ganhou campos; `ReadingDto` e as rotas
  não mudaram. Um cliente que trate o `E9017` da importação deve tratar também o `E9014` (409).

### Diferenças da especificação

A implementação difere de três pontos da §8 da especificação:

- a família continua depois de fechar e reabrir o cabo (a §8.6 detectava de novo a cada abertura): a nova detecção vem
  do Model ID da outra família ou de 3 consultas seguidas sem resposta, e a memória confirma a família conhecida há
  mais de 2 s;
- o BM52x conhecido não usa a janela de 300 ms (a §8.5.3 a aplicava às duas famílias), para manter o comportamento do
  BM525s validado no hardware;
- a memória de um BM82x dá `E9014` (409 na API), e não o `E9017` do original (§8.6 e §8.7): o medidor respondeu e é
  conhecido, só não tem datalogger (veja `docs/architecture.md`).

A espera longa do 1º `66` segue a §8.5.4: na 1ª validação da 0.2.0 ela ainda era um envio só, também com a família
desconhecida, e passou a reenviar o pedido na detecção e no BM82x na revisão
([§11.10 do relatório de validação](docs/validation-report.md#1110-revisão)).

### Não validado

- Perdas reais de pedidos com a 0.2.0. Com o ET-2517A real no Windows (§11 do
  [relatório de validação](docs/validation-report.md#11-minipa-et-2517a-família-bm82x)), a biblioteca, a API, os
  testes de hardware e os pacotes funcionaram: 3 min a 200 ms e 1 min a 500 ms sem `W7012`, `E9016` nem reabertura do
  cabo, e `E9014` na importação. Mas nenhum pedido se perdeu nessa sessão, e o reenvio só foi exercitado com descartes
  injetados no host. O efeito do orçamento de 2 s com perdas reais continua sem medição (§6, questão 13 da
  especificação).
- O BM525s real com o `82 66` da detecção e a espera longa com reenvios (Windows e Orange Pi 5), e a 0.2.0 num Linux
  real. Um descarte real na espera longa do 1º `66`: o reenvio nela só foi exercitado com descartes injetados.
- O modo EF do ET-2517A (sem capturas: o mapa não tem bit para ele), o efeito do REC (20 leituras/s no medidor) na
  perda de pedidos e o ET-2517A em APO ou desligado (§6, questão 17 da especificação).

## 0.1.0 - 2026-10-23

Primeira versão publicada: a port para .NET 10 do *Bs82-52x Data Recording System V6.0.0.9* para o Brymen BM525s
(= Minipa ET-2615A) com o cabo BC-86X.

- `Brymen.Dmm` (NuGet, `net8.0` e `net10.0`, sem dependências): `BrymenMeter` com leitura avulsa, contínua e por
  eventos, reconexão automática e importação do datalogger (`88`/`89`/`8A`, com checksum e recuperação), o
  `MeterConnection` e o `LoggerMemoryReader`; transporte HID pelo `hidraw` no Linux e pela `hid.dll` no Windows; medidor
  simulado com as capturas reais.
- `Brymen.Dmm.Extensions.Hosting`: `AddBrymenMeter`, o serviço hospedado e o `IBrymenMeterAccessor`.
- API REST: polling, aquisição, comparador, importação, CSV, SSE, SignalR, OpenAPI, health checks e o painel; deploy com
  systemd e udev, publish para linux-arm64 e linux-x64 e Docker.
- Validada com o BM525s real no Windows 11 e num Orange Pi 5 (Linux arm64)
  ([relatório de validação](docs/validation-report.md)).
