# Arquitetura da port .NET 10 do Bs82-52x

Port do *Bs82-52x Data Recording System V6.0.0.9* (Brymen, Windows/C++Builder) para um serviço
.NET 10 com API REST, que roda em **Linux arm64 e x64** (e também em Windows para desenvolvimento).
O medidor alvo é o Brymen BM525s (= Minipa ET-2615A), ligado pelo cabo óptico BC-86X (kit BU-86X).

Fontes da verdade:
- Protocolo: [`protocol/bm52x-usb-protocol.md`](protocol/bm52x-usb-protocol.md), [`protocol/test-vectors.json`](protocol/test-vectors.json) e [`protocol/captures/`](protocol/captures/).
- Comportamento do software original, levantado com o REA: [`re/bs8252x-reverse-engineering.md`](re/bs8252x-reverse-engineering.md).

## Projetos

| Projeto | Responsabilidade | Depende de |
|---|---|---|
| `src/Brymen.Dmm.Protocol` | Decodificação pura, sem E/S: quadro RTD de 27 bytes → `DmmReading`; blocos de memória de 36 bytes → `MemoryBlock`; imagem da memória → `LoggerMemory`. | — |
| `src/Brymen.Dmm.Hid` | Transporte HID multiplataforma, sem dependências nativas além da libc/hid.dll: `IHidDeviceProvider` / `IHidDevice` para Linux (hidraw) e Windows (hid.dll + overlapped I/O), dispositivo e medidor simulados (`SimulatedHidDevice`, `SimulatedBm52xMeter` com as capturas reais e injeção de falhas) e enquadramento do BC-86X (`Bu86xLink`). | — |
| `src/Brymen.Dmm` | Biblioteca para outros projetos .NET (pacote NuGet `Brymen.Dmm`, que leva o Protocol e o Hid dentro): `BrymenMeter` (alto nível), `MeterConnection` (abertura do cabo e política de timeout do tempo real) e `LoggerMemoryReader` (leitura robusta da memória). Ver [Biblioteca Brymen.Dmm](#biblioteca-brymendmm). | Protocol, Hid |
| `src/Brymen.Dmm.Extensions.Hosting` | Pacote NuGet opcional `Brymen.Dmm.Extensions.Hosting` para o Generic Host: `AddBrymenMeter` (opções por código ou por `IConfiguration`, com binding por source generator), um serviço hospedado que abre o medidor com reconexão e mantém a leitura contínua, e o `IBrymenMeterAccessor`. | Brymen.Dmm (pacote), `Microsoft.Extensions.*` |
| `src/Brymen.Dmm.Api` | ASP.NET Core minimal API: serviço de polling, aquisição (Graphical Recorder/Data List), comparador, importação do datalogger, export CSV, SSE, hub SignalR, OpenAPI, health checks e um painel web mínimo em `wwwroot/`. | Brymen.Dmm, Protocol, Hid |
| `tests/*` | xUnit: vetores do protocolo, enquadramento HID, hidraw real via `/dev/uhid` (opcional), a biblioteca e o pacote de hosting em `net10.0` e `net8.0`, testes de integração da API com o dispositivo simulado e testes com o cabo real (opcionais). | — |
| `samples/Brymen.Dmm.SignalRClient` | Cliente de console do hub `/hubs/meter`, com `Microsoft.AspNetCore.SignalR.Client`. | — |
| `samples/Brymen.Dmm.ConsoleSample`, `samples/Brymen.Dmm.WorkerSample` | Exemplos fora da solução que consomem os **pacotes** do feed local `artifacts/packages` (`samples/nuget.config`): console com leituras ao vivo e importação para CSV, e worker service com `AddBrymenMeter`. | pacotes Brymen.Dmm e Brymen.Dmm.Extensions.Hosting |
| `deploy/` | Regra udev, unidade systemd, scripts de publish (linux-arm64/x64). O Dockerfile multi-arquitetura fica na raiz. | — |

## Contratos fixos

Os nomes abaixo são compartilhados entre os projetos. Não os renomeie; adicionar membros é permitido.

- `Brymen.Dmm.Protocol`:
  - `Bm52xCommands` (constantes) e `Bm52xFrameException`;
  - `DmmReading`, `DisplayReading`, `DmmFlags`, `MeasurementMode`, `RealTimeDiagnostics`;
  - `Bm52xRealtime.Decode/TryDecode` e `DmmFlagNames.ToNames`;
  - `MemoryBlock`, `LoggerHeader`, `LoggerValue`, `LoggerSample`, `LoggerSession`, `LoggerMemory`;
  - `Bm52xMemory.ParseBlock/ParseHeader/Decode`.
- `Brymen.Dmm.Hid`:
  - `HidDeviceInfo`, `IHidDevice`, `IHidDeviceProvider`, `HidException`;
  - `Bu86xLink` e `Bu86xLinkOptions`;
  - `HidDeviceProviders.CreateForCurrentPlatform()`;
  - `SimulatedHidDevice` e `SimulatedHidDeviceProvider`;
  - `SimulatedBm52xMeter` (`FromCaptures`, `FaultInjector`, `CurrentAfterHeadWindow`) e `SimulatedFault`.
- `Brymen.Dmm` (pacote NuGet `Brymen.Dmm`):
  - `BrymenMeter`, `IBrymenMeter`, `BrymenMeterOptions`, `LoggerReadOptions`, `MeterState`, `MeterStateChangedEventArgs`;
  - `BrymenErrorCodes`, `BrymenMeterError`, `BrymenMeterException`;
  - `MeterConnection`, `MeterCommandResult`, `ILoggerMemoryTransport`, `LoggerMemoryReader`, `LoggerMemoryImage` e
    `LoggerReadProgress`.
- `Brymen.Dmm.Extensions.Hosting` (pacote NuGet `Brymen.Dmm.Extensions.Hosting`):
  - `BrymenMeterServiceCollectionExtensions.AddBrymenMeter` (por `Action<BrymenMeterOptions>` e por `IConfiguration`,
    sem nome ou com nome, para mais de um cabo);
  - `IBrymenMeterAccessor` (com `IBrymenMeter? Meter`, `WaitForMeterAsync` e `ReadingsAsync`).
- `Brymen.Dmm.Api` (contrato com os clientes):
  - rotas e DTOs da API REST ([Rotas](#rotas)) e códigos de erro ([Erros](#erros));
  - hub SignalR em `/hubs/meter`: eventos `reading`, `status` e `acquisitionSample` e métodos `GetLatest` e
    `GetStatus` ([Tempo real via SignalR](#tempo-real-via-signalr)).

Formato dos quadros entre camadas: sempre o formato das capturas no Windows, com o report ID 00h na
frente de cada relatório de 8 bytes (27 bytes para RTD e 36 para memória). É o `Bu86xLink` que monta
esse formato; no Linux o hidraw entrega 8 bytes por `read()`.

## Comportamento do serviço (paridade com o original)

| Original | Port |
|---|---|
| Link/Unlink | `PUT /api/v1/meter/link` `{ "enabled": true }`. Com o link ativo, o serviço consulta `52 66` continuamente; o original faz polling sem `Sleep`, e aqui o intervalo é `Meter:PollInterval`, padrão 200 ms. |
| Digital/Analog Meter | `GET /api/v1/readings/latest`, `GET /api/v1/readings/stream` (Server-Sent Events, evento `reading`) e o hub SignalR `/hubs/meter` (evento `reading`), além do painel em `/`. |
| Graphical Recorder / Data List | `/api/v1/acquisition`: modos `fullSpeed` (toda leitura), `interval` (N s inteiros, como o “Customization...”) e `manual` (`POST /api/v1/acquisition/samples`). Buffer em memória com backup contínuo em CSV (equivale ao `~BDMDLn.TMP`). As faixas Max/Min (`mainRange`, `secondaryRange`, com `unit` e `mode`) valem para uma unidade e um acoplamento e recomeçam quando a função do medidor muda. |
| Export → Text/Data (CSV) | `GET /api/v1/acquisition/samples.csv` com o cabeçalho exato do original, `#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc` (Ω escrito como `Ohm`, `#item` começando em 1, `Time` no formato `%3d:%02d:%02d.%1d` do tempo decorrido). `?format=extended` acrescenta colunas ISO-8601 e SI. |
| Comparator (Hi/Lo, alarme) | `GET`/`PUT /api/v1/comparator`. Comparações estritas: `v > Hi` dá `High` e `v < Lo` dá `Low`; caso contrário, `Pass`. O resultado vai em cada leitura e amostra. Persistido em `settings.json` no `DataDirectory` (equivale ao `BTCDL.ini`). |
| Import data from DMM | `POST /api/v1/logger/imports` responde 202 com `Location`. Confere o modelo com `52 66` (E9017) e lê a memória com `88` seguido de `89`... (checksum, recuperação com `8A`, reinício a partir do `88`; detalhes em [Importação do datalogger](#importação-do-datalogger)). O progresso fica em `GET /api/v1/logger/imports/{id}`, as sessões em `.../sessions` e as amostras em `.../sessions/{n}/samples` (JSON) ou `.../samples.csv`. O polling fica pausado durante a importação. |
| Mensagens W/E | `ProblemDetails` (RFC 9457) com `code` (`E9015`, `E9016`, `W7012`...) em `extensions`. Cabo ausente responde 503. |

Endpoints auxiliares: `GET /api/v1/meter` (estado do link, do dispositivo e último erro), `GET /health/live`,
`GET /health/ready` (pronto quando o cabo está aberto ou o transporte é simulado) e `GET /openapi/v1.json`.

## Divergências deliberadas em relação ao original

| Ponto | Original | Port | Motivo |
|---|---|---|---|
| Coluna `Time` do CSV | Hora local do relógio de parede (`GetLocalTime`), sem data, no formato `%3d:%02d:%02d.%1d` (RE §7.6). | **Tempo decorrido** desde a 1ª amostra do buffer, no mesmo formato, medido pelo relógio monotônico (`TimeProvider.GetTimestamp`). A hora absoluta vai na coluna `Timestamp` (ISO-8601) de `?format=extended` e no campo `timestamp` do JSON. | Um Raspberry Pi sem RTC acerta o relógio pelo NTP depois do boot, com saltos de horas ou meses; a hora do dia também se repete em gravações de mais de 24 h. O `%3d` comporta até 999 h. |
| `fullSpeed` | “Full Speed” é o `TimerLogger` de **500 ms**, que copia o último dado válido da thread (RE §9.2). | **Toda leitura** decodificada vira amostra, na cadência do polling (`Meter:PollInterval`, padrão 200 ms, cerca de 5 amostras/s). Para 500 ms como no original, use `Meter:PollInterval=00:00:00.500`. | A port já tem uma cadência mínima configurável no polling; um segundo timer só repetiria ou perderia leituras. Cada amostra corresponde a um quadro real. |
| Texto das amostras do datalogger | Tela de importação e CSV da sessão com `sprintf("%05.*f")` (`"%04.*f"` sem casas, `"%06.*f"` com sinal) e as casas da faixa: 4 dígitos com zeros à esquerda (287 com 2 casas → `02.87`, 13 com 1 casa → `001.3`, 6 sem casas → `0006`); temperatura com `"%03.0f"` (`025`) [BIN 0x431877..0x4319FA, 0x432015..0x43208B]. Overload sai como `.OL`, `O.L` ou `OL.` conforme a faixa [BIN 0x431A14..0x431AF9]. | `MainData`/`Sub Data` do CSV da sessão como o original, com os zeros à esquerda. O `text` do JSON (`LoggerValueDto`) segue o LCD e o tempo real, sem zeros à esquerda (`2.87`). Overload fica `OL` (`----` em temperatura) nos dois. | O CSV é o formato de troca com o original; o JSON fica coerente com o `text` do tempo real. O texto do overload por faixa só foi lido para V/mV. |
| Falhas seguidas no polling | 2 reaberturas silenciosas; na **3ª falha seguida**, MessageBox “No data received.” com caption `Error[E9015]`, e a thread termina, desfazendo o link (RE §6.5, §12). | Falhas 1 e 2: `W7012` em `lastError`, reabertura do cabo após 10 ms. **3ª falha seguida: `E9016`** (tempo esgotado), a última leitura deixa de ser servida e o estado vai a `reconnecting`, com nova tentativa a cada `Meter:ReconnectDelay`. O link continua ativo. Quadro inválido (`W8011`) conta como falha. | Na port, `E9015` quer dizer só “cabo não encontrado” (enumeração), como no Link e na importação do original. Um serviço sem operador precisa se recuperar sozinho quando o medidor volta. |

## Biblioteca Brymen.Dmm

Pedido do usuário: “faça o pacote/biblioteca/classe standalone para a integração em outros projetos .NET”.

- **Pacote único**, sem dependências: `src/Brymen.Dmm` referencia o Protocol e o Hid com `PrivateAssets="all"`, e o
  target `IncludeReferencedProjectsInPackage` (em `TargetsForTfmSpecificBuildOutput`) põe as três DLLs e os XML docs em
  `lib/<tfm>/` do `.nupkg`; os PDBs vão para o `.snupkg`. O nuspec só tem grupos de dependência vazios por TFM. A
  versão é a do `Directory.Build.props`. Licença e repositório ainda não foram definidos.
- **Alvos** `net8.0` e `net10.0` na biblioteca, no Protocol e no Hid; a API continua `net10.0`. O único ajuste para o
  .NET 8 é o `System.Threading.Lock`, que no `net8.0` vira um apelido de `object` (`Using` com `Alias` no
  `Directory.Build.props`); os `params` de coleções são da linguagem e funcionam nos dois.
- **`BrymenMeter`** (`IAsyncDisposable`, `IDisposable`, thread-safe): todas as operações no cabo passam por um
  semáforo, então uma importação pausa a leitura contínua, que volta sozinha no fim. Uma falha de comunicação fecha o
  cabo e é registrada em `LastError` e no evento `ErrorOccurred`; a operação seguinte reabre o cabo (`Connecting`). A
  leitura contínua segue o original: as 2 primeiras falhas seguidas (`W7012`/`W8011`) só reabrem o cabo depois de 10 ms,
  e a 3ª vira `E9016`; ela, ou o `E9015`, termina a enumeração, ou, com `AutoReconnect`, espera `ReconnectDelay` e
  continua. Os eventos são disparados depois de liberar os locks, cada handler à parte; a exceção de um handler vira
  `ErrorOccurred` com o código `EVENT_HANDLER`. As operações passam para o pool antes do 1º comando (`ThreadPoolHop`):
  nenhuma E/S roda na thread de quem chama, nem com o `poll()` do hidraw nem com um `IHidDevice` síncrono. Uma exceção
  fora do contrato de um `IHidDeviceProvider` ou `IHidDevice` vira `INTERNAL`, com o estado em `Faulted`.
- **`IBrymenMeter`**: as operações do `BrymenMeter` sem o ciclo de vida. O hosting entrega uma fachada dela no
  `IBrymenMeterAccessor.Meter`, para quem usa o medidor não poder descartá-lo nem parar o laço do serviço.
- **Hosting**: o serviço hospedado implementa `IHostedLifecycleService`; abre o medidor no `StartingAsync` e inicia o
  laço no `StartedAsync`, então um serviço pode esperar o medidor no próprio `StartAsync`, em qualquer ordem de
  registro. O `ReadingsAsync` distribui o laço do serviço por `Channel` limitado (`DropOldest`, 64 leituras), sem
  consultas a mais no cabo.
- **`MeterConnection`**: abre o cabo (`E9015` se ausente), executa os comandos e aplica a política do 1º `66` lento
  ([Importação do datalogger](#importação-do-datalogger), item 6), que saiu do `MeterSession`. O `MeterSession` da API
  agora compõe um `MeterConnection`: o estado do cabo (`DeviceOpen`, `Device`) vem dele, e o polling usa
  `MeterConnection.ReadRealTimeAsync`, que decodifica o quadro e classifica as falhas (`W7012`, que fecha o cabo, e
  `W8011`, que o mantém aberto). O `BrymenMeterException` da biblioteca chega à API sem ser reembrulhado e vira
  `ProblemDetails` por `ToApiError()`.
- **`LoggerMemoryReader`**: o algoritmo de leitura da memória saiu da API (`Logger/LoggerMemoryReader.cs`) para a
  biblioteca, com a conferência do modelo (`E9017`) que antes ficava no `LoggerImportService`. A API o usa sobre o seu
  acesso exclusivo ao cabo (o `MeterExclusiveAccess` implementa `ILoggerMemoryTransport`, para que a reabertura do cabo
  numa retentativa ainda gere o evento `status` do SignalR). As tentativas e as esperas são configuráveis em
  `LoggerReadOptions`, com os padrões abaixo.
- **Simulador**: o `CaptureResponder` da API foi unificado no `SimulatedBm52xMeter` do Hid, que ganhou a injeção de
  falhas (`SimulatedFault`), a janela do `8A` logo depois do `88`, os 14 quadros de `rtd-acv.txt`
  (`CapturedRealTimeFrames`) e um `TimeProvider`; o `SimulatedHidDevice` também aceita um `TimeProvider`. A API, o
  `BrymenMeter.OpenSimulatedAsync`, o `SimulatedHidDevice.CreateBm52x()` e o `new SimulatedHidDeviceProvider()` sem
  argumentos usam `SimulatedBm52xMeter.FromCaptures`; os dois últimos dão o mesmo cabo do `OpenSimulatedAsync`
  (`CreateTypicalBm52x`, com 20 ms de latência) no provider, e sem latência no `CreateBm52x`.

## Importação do datalogger

Algoritmo da §4.11 da especificação, com os ajustes do teste no hardware de 2026-10-22
(§2.7 da especificação):

1. Confere o modelo com `52 66` (até 2 tentativas): os bytes 20–23 precisam ser `52`; senão, `E9017`.
2. Envia o `88` e, depois da resposta, **espera 200 ms** antes do próximo comando. O original faz `Sleep(200)`
   depois do `88` (RE §8.1).
3. Falha no `88` (timeout, checksum ou quadro inválido): **repete com `88`**, nunca com `8A`, até 3 tentativas,
   reabrindo o cabo a cada uma. No hardware, um `8A` cerca de 15 ms depois do `88` avançou o ponteiro como um
   `89` em 9 de 16 tentativas, e o bloco seguinte sairia duplicado.
4. Envia `ceil(TotalBytes/24) − 1` comandos `89`. **O `8A` só é usado depois de um `89`** que o medidor
   certamente processou (ao menos 1 relatório recebido ou checksum errado), até 3 vezes por bloco. Com 0
   relatórios não se sabe se o ponteiro andou: a leitura recomeça do `88` (até 3 recomeços) e confere byte a
   byte os blocos já lidos.
5. Grava a imagem bruta em `<DataDirectory>/imports/{id}.bin` antes de decodificar, num temporário forçado para o
   disco e depois renomeado (uma falha ou queda de energia não deixa um `.bin` parcial). As imagens são recarregadas
   na partida do serviço. Só as `Logger:ImportRetention` (padrão 10) importações terminadas mais novas ficam
   guardadas: ao terminar uma nova, e na partida, as mais antigas saem da memória e do disco. A poda vem antes de a
   nova aparecer como `completed`, `failed` ou `canceled`, e a nova nunca é apagada por ela.
6. O primeiro `52 66` depois de comandos de memória (o do polling que volta, ou o da conferência do modelo de uma
   nova importação) espera até **3 s** pelo 1º relatório (`MeterSession.RealTimeAfterMemoryTimeout`), e não o
   `Meter:ReportTimeout`. No hardware, o `66` seguinte à leitura do último bloco levou ≈ 2,06 s e veio com os
   displays em traços (§2.7 da especificação); com 1 s, o polling acusaria `W7012` depois de toda importação. A mesma
   espera vale para o 1º `66` da partida do serviço, porque outro programa pode ter lido a memória. É uma espera longa
   só: o `66` seguinte volta ao `ReportTimeout`, com ou sem resposta.

Durante a importação o polling fica pausado (`importing: true` em `GET /api/v1/meter`) e as rotas que dependem
do tempo real ou da imagem respondem **409 `CONFLICT`**:

| Rota | Situação |
|---|---|
| `GET /api/v1/readings/latest` | Polling pausado pela importação. |
| `POST /api/v1/acquisition/samples` | Amostra manual durante a importação. |
| `POST /api/v1/logger/imports` | Já existe uma importação em andamento. |
| `GET /api/v1/logger/imports/{id}/image` | A imagem ainda não foi gravada (importação em andamento ou que falhou antes da gravação). |
| `GET /api/v1/logger/imports/{id}/sessions`, `.../sessions/{n}`, `.../sessions/{n}/samples` e `.../samples.csv` | Importação em andamento ou que não terminou com sucesso (`detail` traz o erro). |

O SSE e o evento SignalR `reading` simplesmente param de receber leituras até o fim da importação; o modo
`interval` do gravador pula os ticks desse período.

## Rotas

Todas as rotas REST ficam em `/api/v1` e são declaradas com `MapGet`/`MapPut`/`MapPost`/`MapDelete` em
`src/Brymen.Dmm.Api/Endpoints/` (o documento OpenAPI tem os esquemas). Os erros são `ProblemDetails`
([Erros](#erros)).

| Método | Rota | Sucesso | Erros |
|---|---|---|---|
| `GET` | `/api/v1/meter` | 200 `MeterStatusDto` | — |
| `PUT` | `/api/v1/meter/link` | 200 `MeterStatusDto` | 400 `VALIDATION` (sem `enabled`); 503 `E9015` |
| `GET` | `/api/v1/readings/latest` | 200 `ReadingDto` | 409 `E9002` (link desativado pelo usuário; 503 `E9015` se o Link falhou por falta do cabo); 409 `CONFLICT` (importação); 503 `E9015`, `E9016`, `W7012` ou `W8011` (a falha ainda não superada por uma leitura boa; sem ela, `W7012`) |
| `GET` | `/api/v1/readings/stream` | 200 `text/event-stream`, evento `reading` (`id` = `sequence`); a 1ª mensagem é a última leitura conhecida. Evento `heartbeat` a cada `Sse:HeartbeatInterval`; o fluxo termina no `ApplicationStopping` | — |
| `GET` | `/api/v1/acquisition` | 200 `AcquisitionStatusDto` | — |
| `GET` | `/api/v1/acquisition/settings` | 200 `AcquisitionSettingsDto` (`mode`, `intervalSeconds`) | — |
| `PUT` | `/api/v1/acquisition/settings` | 200 `AcquisitionSettingsDto`; campos ausentes mantêm o valor | 400 `VALIDATION` (modo inválido) ou `E9007` (intervalo fora de 0..86400, ou menor que 1 no modo `interval`); 409 `CONFLICT` (gravação em andamento) |
| `POST` | `/api/v1/acquisition/start` | 200 `AcquisitionStatusDto` | 409 `E9002` (sem link); 409 `CONFLICT` (buffer cheio) |
| `POST` | `/api/v1/acquisition/stop` | 200 `AcquisitionStatusDto` | — |
| `POST` | `/api/v1/acquisition/samples` | 201 `AcquisitionSampleDto` | 409 `CONFLICT` (fora do modo `manual`, gravação parada, buffer cheio ou importação); 503 `W7012` |
| `GET` | `/api/v1/acquisition/samples` | 200 `PageDto<AcquisitionSampleDto>` (`offset` ≥ 0, `limit` 1..10000, padrão 100) | 400 `VALIDATION` |
| `GET` | `/api/v1/acquisition/samples.csv` | 200 `text/csv` (`?format=original` ou `extended`) | 400 `VALIDATION` |
| `DELETE` | `/api/v1/acquisition/samples` | 204 | — |
| `GET` | `/api/v1/comparator` | 200 `ComparatorSettingsDto` | — |
| `PUT` | `/api/v1/comparator` | 200 `ComparatorSettingsDto`; campos ausentes mantêm o valor | 400 `W7010` (Hi ≤ Lo) ou `E9007` (valor não finito); 500 `INTERNAL` (falha ao gravar `settings.json`) |
| `POST` | `/api/v1/logger/imports` | 202 `ImportJobDto` + `Location` | 409 `CONFLICT` (outra importação); 503 `E9015` |
| `GET` | `/api/v1/logger/imports` | 200 lista de `ImportJobDto`, da mais nova para a mais antiga (no máximo `Logger:ImportRetention` terminadas, mais a em andamento) | — |
| `GET` | `/api/v1/logger/imports/{id}` | 200 `ImportJobDto` (`running`, `completed`, `failed` ou `canceled`, progresso, erro e avisos) | 404 `NOT_FOUND` |
| `DELETE` | `/api/v1/logger/imports/{id}` | 204: em andamento, cancela; terminada, apaga o job e `imports/{id}.bin` | 404 `NOT_FOUND` |
| `GET` | `/api/v1/logger/imports/{id}/image` | 200 `application/octet-stream` (`{id}.bin`, blocos de 24 bytes a partir do ponteiro 0) | 404 `NOT_FOUND`; 409 `CONFLICT` |
| `GET` | `/api/v1/logger/imports/{id}/sessions` | 200 lista de `LoggerSessionDto` | 404 `NOT_FOUND`; 409 `CONFLICT` |
| `GET` | `/api/v1/logger/imports/{id}/sessions/{n}` | 200 `LoggerSessionDto` (`n` inteiro, a partir de 1) | 404 `NOT_FOUND` (importação ou sessão inexistente); 409 `CONFLICT` |
| `GET` | `/api/v1/logger/imports/{id}/sessions/{n}/samples` | 200 `PageDto<LoggerSampleDto>` | 400 `VALIDATION`; 404 `NOT_FOUND`; 409 `CONFLICT` |
| `GET` | `/api/v1/logger/imports/{id}/sessions/{n}/samples.csv` | 200 `text/csv`; `Time` = índice × intervalo | 400 `VALIDATION`; 404 `NOT_FOUND`; 409 `CONFLICT` |
| `GET` | `/health/live`, `/health/ready` | 200 `Healthy` / 503 `Unhealthy` | — |
| `GET` | `/openapi/v1.json` | 200 documento OpenAPI | — |
| — | `/hubs/meter` | Hub SignalR ([Tempo real via SignalR](#tempo-real-via-signalr)) | — |

O `{id}` de uma importação tem 32 dígitos hexadecimais; outro formato responde 404 sem tocar no disco.

Toda rota com `POST`, `PUT` ou `DELETE`, e o upgrade para WebSocket em `/hubs/meter`, responde **403 `FORBIDDEN`**
quando vem de uma página de outra origem (`CrossSiteRequestGuard`, [Segurança](#requisitos-não-funcionais)).

## Erros

Toda falha volta como `ProblemDetails` (RFC 9457) com `title` em português e o código em `extensions.code`.
Os códigos W/E são os do software original (RE §12); os outros são da API. O `CustomizeProblemDetails`
também acrescenta `code` às respostas geradas pelo próprio framework (rota inexistente, método errado, corpo
malformado, exceção não tratada).

| Código | Significado | HTTP |
|---|---|---|
| `E9015` | Cabo USB não encontrado | 503 |
| `E9016` | Tempo esgotado na transmissão (3ª falha seguida do polling ou importação) | 503 |
| `E9017` | Medidor desligado, desconectado ou sem datalogger | 503 |
| `W7012` | Nenhum dado recebido (tempo real) | 503 |
| `W8011` | Quadro inválido recebido do medidor | 503 |
| `E9002` | Link desativado (gravação ou leitura sem link) | 409 |
| `E9003` | Falha ao criar ou gravar o arquivo de backup (em `backupError` do estado da aquisição) | — |
| `E9007`, `E9008`, `W7010` | Valor inválido, limite inaceitável, Hi ≤ Lo | 400 |
| `W8000` | Bateria do multímetro fraca (anunciador `LOW_BATTERY` da leitura) | — |
| `FORBIDDEN` | Requisição que altera o estado (ou WebSocket do hub) vinda de uma página de outra origem que não está em `Cors:AllowedOrigins` | 403 |
| `NOT_FOUND` | Rota, importação ou sessão inexistente | 404 |
| `METHOD_NOT_ALLOWED` | A rota existe, mas não com esse método HTTP | 405 |
| `VALIDATION` | Corpo ou parâmetro inválido (JSON malformado, `offset`/`limit`/`format` inválidos, `enabled` ausente) e demais 4xx do framework | 400 (ou o 4xx original) |
| `CONFLICT` | Operação não permitida no estado atual (importação em andamento, gravação em andamento, buffer cheio) | 409 |
| `INTERNAL` | Exceção não tratada; a resposta não traz a exceção nem o stack trace | 500 |

## Tempo real via SignalR

Pedido do usuário: “envia cada novo dado obtido pelo multímetro”. O hub entrega as mesmas leituras do SSE,
sem polling extra do cabo.

### Hub

- `MeterHub` em **`/hubs/meter`**, mapeado com `MapHub<MeterHub>`.
- Hub **não tipado** (`MeterHub : Hub`, não `Hub<T>`): os hubs fortemente tipados geram o proxy da interface
  do cliente em tempo de execução e não são compatíveis com trimming/Native AOT. Os eventos saem com
  `SendAsync("<evento>", dto)`.
- Protocolo **JSON**: `AddSignalR().AddJsonProtocol(...)`, com `PayloadSerializerOptions.TypeInfoResolverChain`
  usando o `ApiJsonContext`, o mesmo `JsonSerializerContext` das rotas REST (camelCase e enums como string).
  Todo tipo enviado ou devolvido pelo hub precisa estar no contexto; um tipo ausente só falha em tempo de
  execução, no publish trimmed.
- Sem autenticação, como o resto da API.

### Eventos servidor → cliente

| Evento | Payload | Quando |
|---|---|---|
| `reading` | `ReadingDto`, exatamente o de `GET /api/v1/readings/latest` | Para **todos** os clientes conectados (`Clients.All`), a **cada** leitura decodificada: cada quadro `52 66` válido publicado pelo `ReadingBroadcaster`. |
| `status` | `MeterStatusDto`, o de `GET /api/v1/meter` | Quando o estado do link ou do cabo muda: conectado, desconectado, erro (com `lastError.code`), cabo aberto ou fechado, início e fim da importação. O contador de leituras sozinho não gera evento. |
| `acquisitionSample` | `AcquisitionSampleDto`, o item de `GET /api/v1/acquisition/samples` | A cada amostra gravada pelo `Recorder` (`fullSpeed`, `interval` ou `manual`). |

### Métodos cliente → servidor

| Método | Retorno |
|---|---|
| `GetLatest()` | `ReadingDto` da última leitura, ou `null` quando `GET /api/v1/readings/latest` responderia erro (link desativado, importação, falha). |
| `GetStatus()` | `MeterStatusDto`. |

O hub não reenvia o que o cliente perdeu enquanto estava desconectado. Depois de `start()` e de cada
reconexão, o cliente chama `GetStatus()` e `GetLatest()`; lacunas aparecem como saltos de `sequence`, e as
amostras gravadas continuam em `GET /api/v1/acquisition/samples`.

### Encaminhamento (`SignalRForwarder`)

- `BackgroundService` que assina o `ReadingBroadcaster` (`Subscribe`) como mais um assinante, ao lado do SSE,
  e chama `IHubContext<MeterHub>.Clients.All.SendAsync("reading", dto)` para cada leitura. A fonte é a mesma
  do SSE: nada de polling extra. As assinaturas são feitas no `StartAsync`, antes de o polling começar, então
  nenhuma leitura publicada depois da partida fica de fora.
- Não bloqueia o polling: o `Publish` do broadcaster só faz `TryWrite` no canal limitado da assinatura
  (`Subscription<T>`, `DropOldest`; `SignalRForwarder.Capacity` = 256 itens por fluxo, cerca de 50 s de leituras com
  o `PollInterval` padrão; cada SSE usa `ReadingBroadcaster.SubscriberCapacity` = 64). Um cliente ou uma rede
  lenta atrasam só o laço de encaminhamento, que envia na ordem de publicação, um item depois do outro. O envio a
  `Clients.All` espera a conexão mais lenta: uma conexão morta atrasa todos até cair no `ClientTimeoutInterval`
  (30 s).
- Backpressure: quando o canal enche, o item mais antigo é descartado e contado pelo próprio canal
  (`Subscription<T>.TakeDropped`); o `DropReporter` registra o 1º descarte na hora e, enquanto eles
  continuarem, o total a cada 10 s. Nenhuma leitura é duplicada nem omitida sem esse log.
- `acquisitionSample` vem de uma assinatura igual no `Recorder` (`SubscribeSamples`); `status`, do sinal
  `MeterSession.Changed`, comparando a fotografia nova com a anterior. Nenhum deles lê o cabo.
- Uma falha num envio é registrada em log e não interrompe o encaminhamento.
- Encerramento limpo no `ApplicationStopping`: os laços param, as assinaturas saem das listas e os descartes
  pendentes vão para o log.

### Configuração e CORS

- `SignalR:EnableDetailedErrors` (padrão `false`): repassa ao cliente a mensagem das exceções dos métodos do
  hub. Só para diagnóstico.
- `SignalR:KeepAliveInterval` (padrão `00:00:15`, o do ASP.NET Core): intervalo do ping do servidor. O
  `serverTimeout` do cliente (30 s por padrão) deve ser pelo menos o dobro.
- `Cors:AllowedOrigins` (vazio por padrão): origens liberadas para clientes browser de **outra origem**
  (`http://host:porta`), com `AllowCredentials`, que o cliente JavaScript do SignalR exige. Vazio = sem CORS;
  o painel em `/` e os clientes que não são browser não precisam dele. CORS não é controle de acesso: não
  barra clientes fora do browser, e não impede que uma página de outra origem envie um `POST` simples ou abra o
  WebSocket do hub. Isso fica a cargo do `CrossSiteRequestGuard` (403 `FORBIDDEN`), que libera as mesmas origens
  de `Cors:AllowedOrigins`. O `WebSocketOptions.AllowedOrigins` não é usado: o browser manda `Origin` também no
  WebSocket da própria origem, e o endereço do painel (IP, nome) não é conhecido de antemão.

## Configuração (`appsettings.json`, variáveis de ambiente `Meter__Transport` etc.)

```jsonc
{
  "Urls": "http://0.0.0.0:5080",
  "Meter": {
    "Transport": "Auto",          // Auto | Hid | Simulated
    "DevicePath": null,           // ex.: "/dev/hidraw0"; null = primeiro VID 0820/PID 0001
    "AutoLink": true,             // abre e começa o polling ao iniciar
    "PollInterval": "00:00:00.200",
    "ReportTimeout": "00:00:01",
    "ReconnectDelay": "00:00:02",
    "SimulatedLatency": "00:00:00.020"
  },
  "Acquisition": {
    "MaxSamples": 1000000,
    "BackupDirectory": null,      // null = <DataDirectory>/backup
    "BackupEnabled": true,
    "BackupRetention": 20         // backups ~BDMDLn.csv mantidos
  },
  "Logger": {
    "ImportRetention": 10         // importações terminadas guardadas (memória e imports/)
  },
  "SignalR": {
    "EnableDetailedErrors": false,
    "KeepAliveInterval": "00:00:15"
  },
  "Sse": { "HeartbeatInterval": "00:00:15" },
  "Cors": { "AllowedOrigins": [] }, // ex.: [ "http://192.168.0.10:8080" ]
  "DataDirectory": null,          // padrão: <ContentRoot>/data
  "AllowedHosts": "*"             // ex.: "raspberrypi.local;192.168.0.10;localhost" (DNS rebinding)
}
```

Chaves novas desta versão:

| Chave | Padrão | Descrição |
|---|---|---|
| `SignalR:EnableDetailedErrors` | `false` | Mensagens de exceção do hub para o cliente. |
| `SignalR:KeepAliveInterval` | `00:00:15` | Ping do servidor para os clientes do hub (de 1 s a 10 min). |
| `Sse:HeartbeatInterval` | `00:00:15` | Intervalo do evento `heartbeat` do SSE, que mantém a conexão viva em proxies que fecham conexões ociosas. |
| `Cors:AllowedOrigins` | vazio | Origens de clientes browser liberadas (REST e SignalR), com `AllowCredentials`. Cada uma com esquema, host e porta, sem caminho e sem `*`; vazio desliga o middleware. |
| `Acquisition:BackupRetention` | `20` | Quantos backups `~BDMDLn.csv` ficam na pasta de backup (1 a 100000); ao criar um novo, os de número menor são apagados. |
| `Logger:ImportRetention` | `10` | Quantas importações terminadas ficam guardadas, decodificadas na memória e em `imports/{id}.bin` (1 a 1000); ao terminar uma nova, e na partida, as mais antigas são apagadas. |

## Transporte HID no Linux

- A enumeração lê o sysfs (`/sys/class/hidraw/hidrawN/device/uevent`, `HID_ID=0003:00000820:00000001`) e
  devolve o nó `/dev/<DEVNAME>`. Com `Meter:DevicePath`, o caminho é comparado com os da enumeração, também
  depois de resolver links (como o `/dev/brymen-bu86x` da regra udev).
- **Abertura direta do `DevicePath`**: se o caminho configurado não aparece na enumeração, o
  `Bu86xLink.FindDevice` o consulta pelo `IHidDevicePathProbe` do provedor. No Linux, o `LinuxHidrawDeviceProvider`
  abre o nó e o identifica com o ioctl **`HIDIOCGRAWINFO`** (`struct hidraw_devinfo`: `bustype`, `vendor`,
  `product`), além de `HIDIOCGRAWNAME` e `HIDIOCGRAWUNIQ`, sem o sysfs; o caminho só é aceito com VID 0820 /
  PID 0001. É o caso do Docker com o nó repassado com outro nome (`--device /dev/hidraw3:/dev/bu86x`): o sysfs
  do contêiner mostra os nomes do host. Outro dispositivo, ou um nó que não é hidraw (`ENOTTY`), conta como
  cabo não encontrado (`E9015`). Sem permissão no nó, a resposta também é `E9015`, com o erro da abertura (e a dica
  da regra udev) no `detail` e em `lastError.message` de `GET /api/v1/meter`.
- Ao abrir, os tamanhos dos relatórios vêm do report descriptor do próprio nó (`HIDIOCGRDESCSIZE` e
  `HIDIOCGRDESC`); o `report_descriptor` do sysfs e o layout padrão do BC-86X ficam como reserva.
- O `read()` entrega 8 bytes por relatório, e o `write()` leva o número de relatório 0 na frente
  (`00 00 52 cc`).

## Requisitos não funcionais

- Use `net10.0`, `Nullable` e `TreatWarningsAsErrors` (via `Directory.Build.props`). As bibliotecas são `IsAotCompatible`, e a API usa o Request Delegate Generator e `System.Text.Json` com source generation (`JsonSerializerContext`), para permitir publicação trimmed/AOT. O hub SignalR não é tipado pelo mesmo motivo.
- `InvariantGlobalization`: toda formatação numérica usa `CultureInfo.InvariantCulture`.
- Nenhuma dependência nativa de terceiros, e pacotes NuGet só da Microsoft. No Linux o projeto usa só a `libc` (`open`/`read`/`write`/`poll`/`ioctl`/`close` via `LibraryImport`) e o sysfs; no Windows, `hid.dll`, `setupapi.dll` e `kernel32.dll`.
- Linux: acesso sem root via regra udev (`deploy/linux/99-brymen-bu86x.rules`) e `Host.UseSystemd()`. A unit usa
  `DevicePolicy=closed` com `DeviceAllow=char-hidraw rw`, resolvido pelo systemd só na partida do serviço (pela lista de
  `/proc/devices`); por isso o `install.sh` carrega o módulo `hid` no boot (`/etc/modules-load.d/brymen-dmm.conf`). O
  `install.sh` confere a arquitetura pelo interpretador do ELF, e não pelo `uname -m` (kernel).
- Publicação: `linux-arm64` e `linux-x64` self-contained (single-file e trimmed), e uma imagem Docker multi-arquitetura.
  O self-contained usa o runtime 10.0.13 (runtime packs do NuGet) quando o SDK traz um mais antigo, por causa do aviso
  GHSA-334f-vvp4-7g6p no `Microsoft.AspNetCore.SignalR.Common` 10.0.12 (target `UsePatchedRuntimePacks`).
- Segurança: a API e o hub não têm autenticação nem TLS. A exposição fora de uma rede confiável passa por
  firewall ou por um proxy reverso com autenticação e TLS (que precisa repassar WebSockets em `/hubs/meter`).
  Contra páginas maliciosas abertas num browser da rede, o `CrossSiteRequestGuard` recusa `POST`/`PUT`/`DELETE` e o
  WebSocket do hub vindos de outra origem: com `Sec-Fetch-Site`, só passam `same-origin` e `none`; sem ele, passa a
  requisição sem `Origin` (cliente fora do browser) ou com a `Origin` igual ao `Host`; as origens de
  `Cors:AllowedOrigins` passam sempre (a regra do `CrossOriginProtection` do Go). Contra DNS rebinding, restrinja
  `AllowedHosts`.
