# Relatório de validação da integração

Validação de 2026-10-22 da port .NET 10 do *Bs82-52x Data Recording System V6.0.0.9*: build e testes da solução
inteira, publicação self-contained/trimmed para Linux e Windows, execução do binário trimmed e teste de ponta a
ponta com o BM525s e o cabo BC-86X reais, no Windows.

## Resumo

| Item | Resultado |
|---|---|
| `dotnet build BrymenDmm.slnx -c Release` | 0 avisos, 0 erros (também num clone sob um caminho com `src`, [§6.2](#62-build-testes-e-publicação)) |
| `dotnet test BrymenDmm.slnx` | 644 aprovados, 0 falhas, 12 ignorados (só Linux ou opcionais), depois da [revisão final](#6-revisão-final) |
| Publish linux-arm64, linux-x64 e win-x64 (self-contained, single-file, trimmed) | 0 avisos de trimming/AOT/single-file; runtime 10.0.13 embutido |
| Binário trimmed win-x64, transporte Simulated | REST, SSE, SignalR, OpenAPI, CSV e importação funcionam |
| E2E com o hardware real (binário trimmed, transporte Hid) | Tudo conferido; importação idêntica byte a byte à captura `memory-dump-1.txt` |
| Achado novo no hardware | O 1º `52 66` depois do último bloco da memória leva ≈ 2,06 s; corrigido na API (W7012 depois de toda importação) |
| Revisão final | 15 achados dos revisores independentes: 15 procedem e foram corrigidos ([§6](#6-revisão-final)) |
| Não validado | Linux real (hidraw, ioctl, udev, systemd, `modules-load.d`, Docker), execução em arm64, ligação do Native AOT, browser real contra o `CrossSiteRequestGuard` |
| Biblioteca NuGet (2026-10-23) | Solução com 788 aprovados e 0 falhas; pacotes `Brymen.Dmm` (sem dependências) e `Brymen.Dmm.Extensions.Hosting`; consumidores `net8.0`, `net10.0` e trimmed; hardware e regressão da API com o cabo real sem diferenças ([§8](#8-biblioteca-nuget)) |
| Revisão da biblioteca (2026-10-23) | 17 achados: 17 procedem e foram corrigidos (o 16 repete o 7); solução com 816 aprovados e 0 falhas; consumidores `net8.0`/`net10.0` e dois do hosting com os `Microsoft.Extensions.*` 8.0.0/10.0.0 recompilados contra os pacotes novos; hardware 4/4 nos dois alvos; publish Linux com 0 avisos ([§9](#9-revisão-da-biblioteca)) |
| Linux arm64 real, Orange Pi 5 (2026-10-23) | Build no Pi com 0 avisos; testes `net10.0`: 727 aprovados, 0 falhas; hardware pelo hidraw 7/7 + 4/4; uhid como root 5/5 (3 execuções); `install.sh`, udev, `modules-load.d` e unit `Type=notify` funcionando; API pela LAN com importação idêntica byte a byte ao dump; reconexão por unbind/bind do `usbhid`; pacote NuGet em `net10.0` e `net8.0` com o cabo real. Docker interrompido por decisão do usuário, depois de uma execução do contêiner com o cabo ([§10](#10-linux-arm64-real-orange-pi-5)). Na reverificação independente, um teste da API se mostrou instável no Pi (corrida na retenção das importações, [§10.12](#1012-reverificação-independente)). **Correções** ([§10.13](#1013-correções)): `Dockerfile` compila no builder legado, `install.sh`/`uninstall.sh` disparam o udev só no hidraw do cabo, painel servido com `br`/`gzip` pelo `MapStaticAssets` e retenção aplicada antes do estado final; PC com 820 aprovados e 0 falhas; no Pi, `Brymen.Dmm.Api.Tests` 178/0/0 em 3 rodadas e a retenção 20/20; serviço reinstalado e lendo, sem evento no `hidraw0` |

## Ambiente

| Item | Valor |
|---|---|
| Máquina | Windows 11 Pro 10.0.26200 (x64) |
| .NET SDK | 10.0.401 (o `global.json` aceita 10.0.100 ou mais novo da linha 10.0), runtime e ASP.NET Core 10.0.12 instalados |
| Runtime embutido nos publish self-contained | 10.0.13 (runtime packs do NuGet; ver [Publicação](#2-publicação)) |
| Ferramentas | PowerShell 7.6.6, Git Bash com curl 7.82.0, `dotnet run` de arquivo C# para os scripts de conferência |
| Cabo | BC-86X, `\\?\hid#vid_0820&pid_0001#9&2e0db096&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}`, VID 0x0820, PID 0x0001, bcdDevice 0x8603, `Brymen` / `Superior DMM`, serial `£⑉` (lixo, como na §2.1 da especificação) |
| Medidor | BM525s em ACV, pontas abertas (0,121–0,123 V AC, 0,000 Hz, `AUTO`); memória com a sessão do `memory-dump-1.txt` |
| Software original | Fechado durante todos os testes |
| Comandos enviados ao medidor | Só `52 66`, `52 88`, `52 89` e `52 8A` |

## O que mudou nesta etapa

| Área | Mudança |
|---|---|
| Hid | `Bu86xLink.LastCommandReportsReceived`: relatórios recebidos depois da escrita do último comando, em qualquer desfecho (sucesso, timeout, relatório curto, E/S, cabo removido, descarte). Cobre também os casos sem contagem na exceção (`HidException` de desconexão, `ObjectDisposedException`). |
| Hid | Sobrecarga `Bu86xLink.ExecuteAsync(command, reportCount, firstReportTimeout, ct)`: espera própria pelo 1º relatório. |
| Hid | Removida a constante sem uso `WindowsNative.ERROR_INSUFFICIENT_BUFFER`. |
| API | Removido o decorator `ReportCountingHidDevice`; o `MeterSession` usa `LastCommandReportsReceived`. |
| API | O 1º `52 66` depois de comandos de memória, e o da partida, espera até 3 s pelo 1º relatório (`MeterSession.RealTimeAfterMemoryTimeout`), por causa do achado da [§3.3](#33-achado-o-1º-66-depois-do-último-bloco-da-memória). |
| API | Uma falha da procura do cabo (por exemplo, `EACCES` no `Meter:DevicePath` aberto pelo `HIDIOCGRAWINFO`) vai no `detail` do E9015 e em `lastError.message`, com a dica da regra udev, em vez de só no log. |
| API | Removidos o `ReadingBroadcaster.SubscribeAsync` (só os testes o usavam) e o `CaptureResponder.CommandCount` (sem uso). |
| API | `Brymen.Dmm.Api.csproj`: target `UsePatchedRuntimePacks`, que no publish self-contained usa o runtime 10.0.13 enquanto o SDK trouxer um mais antigo (aviso GHSA-334f-vvp4-7g6p no `Microsoft.AspNetCore.SignalR.Common` 10.0.12). |
| Exemplo | `samples/Brymen.Dmm.SignalRClient` aceita o endereço da API ou o do hub (`.../hubs/meter`); antes, o comando do README montava `.../hubs/meter/hubs/meter`. |
| Testes | Hid: 3 casos novos de `LastCommandReportsReceived` e 3 da espera do 1º relatório. API: E9015 com o erro da consulta direta do caminho (2), o `66` lento depois da partida e da importação (2, conferidos falhando sem a correção) e testes do broadcaster portados para `Subscribe`. |
| Docs e deploy | `docs/architecture.md`, `README.md`, `docs/protocol/bm52x-usb-protocol.md` (§2.7 e questão 11), `docker-compose.yml` (comentário do nó renomeado) e `deploy/linux/brymen-dmm.env` (chaves novas e dica do `MaxSamples`). |

## 1. Build e testes

```powershell
dotnet build BrymenDmm.slnx -c Release
dotnet test BrymenDmm.slnx
$env:BRYMEN_HW_TESTS = '1'; dotnet test tests/Brymen.Dmm.Hid.Tests --filter "FullyQualifiedName~HardwareTests"
```

| Projeto de teste | Aprovados | Falhas | Ignorados | Total | Antes desta etapa |
|---|---|---|---|---|---|
| `Brymen.Dmm.Protocol.Tests` | 336 | 0 | 0 | 336 | 336 |
| `Brymen.Dmm.Hid.Tests` | 120 | 0 | 12 | 132 | 114 / 126 |
| `Brymen.Dmm.Api.Tests` | 151 | 0 | 0 | 151 | 146 |
| `HardwareTests` com `BRYMEN_HW_TESTS=1` (cabo real, Windows) | 6 | 0 | 1 | 7 | — |

- Os 12 ignorados da suíte normal são os `HardwareTests` (sem `BRYMEN_HW_TESTS`) e os `LinuxUhidTests` (só Linux,
  como root). No Windows, com o cabo, o único ignorado é `CableIsFoundAndOpenedByPathWithoutSysfs`, que é só Linux.
- A suíte completa rodou 3 vezes seguidas sem falha com o código final.
- Código morto: build com `EnforceCodeStyleInBuild` e IDE0051, IDE0052, IDE0060, IDE0005, IDE0059, CA1812 e CA1823
  como aviso: nenhum caso. Membros públicos e internos sem referência foram procurados por script; os sem uso foram
  removidos (tabela acima). `SimulatedHidDevice.ClearWrites` e `SimulatedBm52xMeter.SetMemoryImage` são API pública
  do simulador e ficaram.

## 2. Publicação

```powershell
$env:TrimmerSingleWarn = 'false'      # cada aviso de trimming em separado
./deploy/publish.ps1                  # linux-arm64 e linux-x64
dotnet publish src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj -c Release -r win-x64 -o artifacts/publish/win-x64 `
  --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true -p:DebugType=none `
  -p:PublishDocumentationFiles=false -p:IsTransformWebConfigDisabled=true -p:TrimmerSingleWarn=false
```

Os três publish terminaram com **0 avisos** (IL2xxx, IL3xxx, single-file ou outros) e 0 erros; com
`TreatWarningsAsErrors`, qualquer aviso do ILLink quebraria o publish.

| Artefato | Executável | Pasta do publish | `tar.gz` |
|---|---|---|---|
| `linux-arm64` (ELF aarch64) | 23 665 834 B (22,6 MiB) | 23 787 527 B | 9 583 508 B (9,1 MiB) |
| `linux-x64` (ELF x86-64) | 24 114 562 B (23,0 MiB) | 24 236 255 B | 9 850 349 B (9,4 MiB) |
| `win-x64` | 22 022 369 B (21,0 MiB) + `aspnetcorev2_inprocess.dll` 392 488 B | 22 468 478 B | — |

- O pacote tem 25 entradas: o executável (modo 0755), `appsettings*.json`, `wwwroot/` (com `.br` e `.gz`),
  `README.md`, `deploy/linux/` (LF, `install.sh` e `uninstall.sh` com 0755) e o
  `Brymen.Dmm.Api.staticwebassets.endpoints.json`, que o `UseStaticFiles` não usa (desde a [§10.13](#1013-correções),
  o `MapStaticAssets` usa o manifesto e serve os `.br` e `.gz`).
- SHA-256 dos pacotes desta validação: `207e387b7a7fce98d346035193d98b0576733ea6304cf5668725df1f0776db6a` (arm64)
  e `b08f57c79408182d18530f79a66b357113a45df96268f695fd6a12260fdcb3f5` (x64). O pacote leva o `README.md`, então
  qualquer mudança nele muda o `tar.gz`, mas não o executável.
- Runtime embutido: o `deps.json` dos três executáveis aponta para `Microsoft.NETCore.App.Runtime.<rid>/10.0.13` e
  `Microsoft.AspNetCore.App.Runtime.<rid>/10.0.13`; o `Microsoft.AspNetCore.SignalR.Common.dll` tem a versão de
  arquivo 10.0.1326. Antes do target `UsePatchedRuntimePacks`, os mesmos binários levavam o 10.0.12. O ILLink
  continua o do SDK (10.0.12): pela propriedade `RuntimeFrameworkVersion`, o SDK também trocaria o ILLink pelo
  10.0.13, que roda no runtime da máquina do build e falhou por falta do runtime 10.0.13 instalado.

### 2.1 Binário trimmed win-x64 com o transporte Simulated

```powershell
artifacts/publish/win-x64/Brymen.Dmm.Api.exe --Urls=http://127.0.0.1:5791 --Meter:Transport=Simulated --DataDirectory=<scratch>
```

Executado duas vezes: com o runtime 10.0.12 e com o build final (10.0.13). Nenhum aviso ou erro no log.

| Verificação | Resultado |
|---|---|
| `/health/live`, `/health/ready`, `/` (painel) | 200 |
| `GET /openapi/v1.json` | 200; 18 rotas `/api/v1`, 74 `$ref`, esquemas `ReadingDto`, `MeterStatusDto`, `ImportJobDto`, `AcquisitionSampleDto`, `LoggerSampleDto` e `ProblemDetails` |
| `GET /api/v1/meter`, `GET /api/v1/readings/latest` | 200, JSON por source generation (camelCase, enums como texto) |
| SSE `/api/v1/readings/stream` | 20 eventos `reading` em 4 s, com `id` = `sequence` |
| `PUT /api/v1/meter/link` `false` / `true` | 200 `unlinked` → `readings/latest` 409 `E9002` → 200 `connected` |
| Comparador | `Hi 0,2 / Lo 0,1` → `"comparator":"Low"` com 0,090 V; `Hi ≤ Lo` → 400 `W7010` |
| Aquisição `fullSpeed` (3 s) e CSV | 16 amostras; cabeçalho exato do original; `?format=extended` com `Timestamp,MainSiValue,SubSiValue,Flags,Comparator` |
| Erros do framework | 404 `NOT_FOUND`, 405 `METHOD_NOT_ALLOWED`, 400 `VALIDATION` (JSON malformado, `enabled` ausente, `limit=0`) |
| Importação | `completed` em 4,9 s; 1 sessão, 375 amostras; tudo igual à captura (mesmo script da [§3.2](#32-resultados)) |
| SignalR com `samples/Brymen.Dmm.SignalRClient` | `GetStatus`/`GetLatest` na conexão, eventos `reading` ao vivo, `status` no unlink e no link e reconexão automática depois de reiniciar o servidor |
| Memória | Working set de ≈ 73 MB |

## 3. Teste de ponta a ponta com o hardware real

### 3.1 Configuração

```powershell
artifacts/publish/win-x64/Brymen.Dmm.Api.exe --Urls=http://127.0.0.1:5792 --Meter:Transport=Hid `
  --DataDirectory=C:/Users/pc-eng/AppData/Local/Temp/claude/C--dev-Brymen-BU-86X-USB-kit/290f1ca3-cacf-4f39-8e71-e9d122aff040/scratchpad/e2e-data
```

- Binário trimmed self-contained, executado da pasta do publish (raiz de conteúdo com `appsettings.json` e `wwwroot/`).
- Três rodadas: antes da correção do `66` lento, depois dela (runtime 10.0.12) e com o build final (runtime 10.0.13).
  Os números abaixo são do build final, salvo indicação.
- Scripts usados (área temporária): `scripts/smoke.sh` (curl em todas as rotas principais),
  `scripts/importcheck/ImportCheck.cs` (importação e comparação com `memory-dump-1.txt` e `test-vectors.json`) e o
  cliente `samples/Brymen.Dmm.SignalRClient`, com as linhas contadas por PowerShell.
- No fim, o processo foi encerrado e o cabo ficou livre (nenhum processo `Brymen*` em execução).

### 3.2 Resultados

| Verificação | Resultado |
|---|---|
| `GET /api/v1/meter` | `linkEnabled: true`, `state: connected`, `transport: hid`, `deviceOpen: true`; dispositivo com o caminho, VID/PID, versão, fabricante e produto do [Ambiente](#ambiente); `lastError: null` |
| `GET /health/ready` | 200 `Healthy` (cabo aberto) |
| `GET /api/v1/readings/latest` | 200: `0.122` V, modo `AC`, `value` 0,122; secundário `0.000` Hz, modo `NONE`; anunciador `AUTO`; `rawHex` `00001000BFA0CBCB0000BFAFAFAF00012006005252525210000D00`. Coerente com ACV de pontas abertas e Hz sem sinal |
| SSE | 21 eventos `reading` em 4 s (≈ 5/s), sequenciais |
| SignalR, evento `reading` | Rodada final: **4,89 leituras/s** (150 eventos em 30,46 s, `sequence` 214..363, nenhuma lacuna; intervalo entre eventos de 112 a 392 ms, média de 204 ms). Rodadas anteriores: 4,93/s (156 em 31,4 s) e 4,90/s, também sem lacuna |
| SignalR, evento `status` | Chegou no unlink (`unlinked, link desativado, cabo fechado`) e no link (`connected, link ativo, cabo aberto`) |
| Unlink/link | `PUT {enabled:false}` → 200 `unlinked`, `deviceOpen: false`; `readings/latest` → 409 `E9002`; `PUT {enabled:true}` → 200 `connected`, leitura nova em ≈ 1 s |
| Comparador | `PUT {enabled:true, high:0.2, low:0.1, alarm:true}` → leitura com `"comparator":"Pass"` (0,122 V); `Hi ≤ Lo` → 400 `W7010`; gravado em `settings.json` |
| Aquisição `fullSpeed`, ≈ 10 s | **50 amostras** em 10,2 s (≈ 5/s, igual ao `PollInterval` de 200 ms), 0,122–0,123 V AC / 0,000 Hz; backup `~BDMDL1.csv` criado |
| Export CSV | Cabeçalho exato `#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc`, linhas CRLF, `1,  0:00:00.0,0.122,V,AC,0.000,Hz,`; o estendido acrescenta `Timestamp,MainSiValue,SubSiValue,Flags,Comparator` |
| Importação do datalogger | `202` + `Location`; `completed` em **10,9 s** (95/95 blocos, 0 retentativas, 0 recomeços); durante ela, `readings/latest` → 409 `CONFLICT` |
| Sessões e amostras | 1 sessão, `A0_0080` → `A0_095A`, `Bfunction 01`/`Bselect 00`/`Bstatus 80`, dual, **ACV + Hz**, **0,05 s**, 2250 bytes, **375 amostras**; 1ª `0.013 V AC / 0.000 Hz`, última (`0:00:18.7`) `0.006 V` |
| Comparação com `memory-dump-1.txt` | Imagem de 2280 bytes **idêntica** à captura; `count` do principal e do secundário iguais aos 375 de `test-vectors.json`; `elapsedSeconds` = i × 0,05; CSV da sessão com 376 linhas |
| Depois da importação | 1ª leitura 2056 ms depois do `completedAt` (displays em traços, ver §3.3); `lastError: null` |
| Repetibilidade | 8 importações no total, todas `completed` com 0 retentativas e 0 recomeços |
| Memória | Working set de ≈ 70 MB |

### 3.3 Achado: o 1º `66` depois do último bloco da memória

Na 1ª rodada, depois de **3 de 3** importações, o primeiro `52 66` do polling ficou sem resposta dentro do
`Meter:ReportTimeout` (1 s), e a API registrou `W7012` antes de se recuperar sozinha. A partida também teve um
`W7012` no 1º `66`. Medição dedicada com o `Bu86xLink` (só `66`, `88` e `89`, `ReportTimeout` de 4 s, tempo da
escrita até o último relatório):

| Antes do `66` | 1º `66` | `66` seguinte |
|---|---|---|
| `88` + 94 × `89` (o último bloco alcança TotalBytes = 2272), pausa de 0 ms | 2056, 2056, 2064 ms | 80, 136, 264 ms |
| `88` + 94 × `89`, pausa de 200 / 500 / 1000 ms | 2068 / 2056 / 2065 ms | 64 / 168 / 184 ms |
| `88` + 93 × `89` | 480 ms | 192 ms |
| `88` + 70 / 47 / 20 × `89` | 424 / 376 / 344 ms | 200 / 208 / 176 ms |
| `88` + 5 × `89`, pausas de 0 a 1000 ms | 344–471 ms | 160–192 ms |
| só o `88`, pausas de 0 a 1000 ms | 366–439 ms | 176–208 ms |
| `66` repetidos | 71–200 ms (1º depois de abrir: 221–308 ms) | — |

- A demora de ≈ 2,06 s só aparece depois do bloco que alcança o fim da memória e não diminui com a pausa.
- A resposta lenta vem com os dois displays em traços (`-.---`, `value: null`,
  `000010005040404000005040404000012000005252525210000000`); na leitura seguinte o secundário ainda pode vir em
  traços. A API publica essas leituras como vieram.
- Correção: o 1º `66` depois de comandos de memória, e o da partida, espera até 3 s pelo 1º relatório. Conferido no
  hardware: depois de 3 importações (com `lastError` conferido) e numa partida logo após uma importação feita com o
  link desativado, nenhum `W7012`.
  Os testes `SlowRealTimeAfterMemoryTests` simulam o atraso e falham sem a correção.
- Registrado na §2.7 e na questão 11 da especificação.

### 3.4 Observações

- O tempo real do BM525s em ACV ficou em 64–200 ms por `66`, acima dos 50–103 ms da §2.5 da especificação; mesmo
  assim o polling manteve ≈ 5 leituras/s com o `PollInterval` de 200 ms.
- No Windows, com a saída redirecionada para arquivo, o log do console sai na página de código OEM (`conex�o`). No
  terminal e no journal do systemd (UTF-8) isso não acontece.
- `lastError` guarda o último erro mesmo depois da recuperação (por projeto); o `status` do SignalR mostra a mudança.
- Nos ≈ 2 s entre o fim da importação e a 1ª leitura, `GET /api/v1/readings/latest` responde 503 `W7012` (ainda
  não há leitura atual), sem erro registrado em `lastError`.

## 4. Limitações: o que não foi validado

- **Linux real**: nada rodou num Linux nesta máquina, nem em x64 nem em arm64. Ficaram sem execução:
  - o transporte hidraw (`open`/`poll`/`read`/`write` da libc) com o cabo real e os `LinuxUhidTests` (cabo virtual
    pelo `/dev/uhid`);
  - os ioctls `HIDIOCGRAWINFO`, `HIDIOCGRAWNAME`, `HIDIOCGRAWUNIQ`, `HIDIOCGRDESCSIZE` e `HIDIOCGRDESC` e a abertura
    direta do `Meter:DevicePath` (`CableIsFoundAndOpenedByPathWithoutSysfs`);
  - a regra udev (`99-brymen-bu86x.rules`), o `install.sh`/`uninstall.sh` e a unit systemd (`Type=notify`,
    endurecimento, `DeviceAllow=char-hidraw`);
  - o Dockerfile multi-arquitetura e o `docker-compose.yml`, inclusive o nó renomeado no contêiner.
- Os executáveis linux-arm64 e linux-x64 só foram conferidos pelo cabeçalho ELF e pelo conteúdo do pacote.
- O runtime 10.0.13 dos pacotes Linux não foi executado; no Windows, o binário com 10.0.13 passou em toda a
  validação das §2.1 e §3.
- Ligação do Native AOT (falta o MSVC/vswhere nesta máquina) e o cliente JavaScript e o CORS num browser real.
- Gravações longas, consumo de memória num Raspberry Pi, medidor desligado ou em APO e o comportamento com
  memórias maiores ou várias sessões.

## 5. Questões em aberto

1. **Runtime 10.0.12 fora do publish self-contained**: o `dotnet run`, os testes e o modo framework-dependent
   usam o runtime instalado (aqui, 10.0.12, com o aviso GHSA-334f-vvp4-7g6p). Atualize o SDK/runtime da máquina e
   do alvo; com um SDK que traga o 10.0.13 ou mais novo, o target `UsePatchedRuntimePacks` deixa de agir e pode ser
   removido.
2. **`66` lento depois da memória**: a causa não é conhecida, e os 3 s cobrem só o dump de 2272 bytes medido. O
   simulador da API (`CaptureResponder`) não reproduz o atraso; os testes usam um decorador do dispositivo.
3. **SignalR**: o envio a `Clients.All` espera a conexão mais lenta; um cliente morto atrasa todos até o
   `ClientTimeoutInterval` (30 s), com a fila de 256 itens e os descartes registrados em log.
4. **Simulador do `8A` logo depois do `88`**: modelo determinístico e conservador (qualquer `8A` até 200 ms depois do
   `88` avança); o limite real entre 15 e 200 ms não é conhecido.
5. **Evento `status` com o cabo ausente**: sai um a cada `Meter:ReconnectDelay` (2 s), com o `lastError` atualizado.
6. **Dois simuladores**: `SimulatedBm52xMeter` (biblioteca Hid, API pública) e `CaptureResponder` (API, com injeção
   de falhas e o modelo do `8A`) reproduzem o mesmo medidor; unificá-los não fez parte desta etapa.
7. **Logs brutos**: os resultados 9/16, 6/6 e 16/16 do `8A` (§2.7) não têm log bruto em `docs/protocol/captures/`;
   as medições do `66` lento estão neste relatório.
8. Questões do protocolo e do software original: §6 da especificação (itens 1 a 11) e §14 do relatório de RE.

## 6. Revisão final

Correções finais de 2026-10-22, a partir dos 15 achados dos revisores independentes (lentes correção, linux-arm e
api-segurança). Cada achado foi conferido no código antes da correção; o achado 4 também foi conferido na desmontagem
do original.

### 6.1 Achados e vereditos

| # | Lente | Achado | Veredito | Motivo e correção |
|---|---|---|---|---|
| 1 | correção | `GET /readings/latest` responde com um `lastError` antigo (E9015 depois de um unlink deliberado, E9016 depois da importação) | Procede, corrigido | O `_lastError` é histórico e só era zerado num Link bem-sucedido. O `MeterSnapshot` ganhou `LinkFailure` (só o Link que falhou por falta do cabo; zerado no unlink e no Link) e `CurrentFault` (a falha ainda não superada por uma leitura boa; zerada no `ReportSuccess`, no Link e no unlink). O `GetLatest` usa os dois, e o `lastError` de `GET /api/v1/meter` continua como histórico. Testes: unlink depois de um E9015 já recuperado → 409 `E9002`; unlink sem o cabo → 409 `E9002`, também depois de um `POST /logger/imports` sem cabo; Link sem o cabo → 503 `E9015` no Link e na leitura; depois da importação, sem leitura atual, `W7012`/`E9016` e não o `W8011` antigo. |
| 2 | correção | Bloco com os 3 × `8A` esgotados conta uma retentativa a mais e faz um reset e uma espera inúteis | Procede, corrigido | A 4ª tentativa também fazia `_retries++`, `ResetAsync` e a espera, e o `ReadAsync` repetia os dois no recomeço. O `ReadNextAsync` agora sai antes disso na última tentativa, como o `ReadHeadAsync`. Teste: 89 e três `8A` com checksum errado → 3 `8A`, `Retries` = 3, `Restarts` = 1 e 4 resets (um por `8A` e um no recomeço). |
| 3 | correção | Max/Min da aquisição mistura unidades quando a função do medidor muda | Procede, corrigido | O `Extend` guardava só a unidade da 1ª amostra. A faixa (`ValueRange`) agora vale para uma unidade e um acoplamento e recomeça quando um dos dois muda; overload não mexe nela. O `ValueRangeDto` ganhou `mode`, e o painel o mostra. Teste: ACV → Ω → DCV. |
| 4 | correção | Texto das amostras do datalogger sem os zeros à esquerda do original (`%04.4i`) | Procede, corrigido no CSV da sessão | Conferido na desmontagem: em 0x431360 o original monta sempre `d.ddd` (o ponto **não** depende da faixa), e o texto final da tela e do CSV sai de `sprintf("%05.*f" \| "%06.*f" \| "%04.*f", 3 − e, d.ddd × 10^e)` [BIN 0x431877..0x4319FA], o que dá 4 dígitos com zeros à esquerda (`02.87`, `001.3`, `0006`, `-02.87`); temperatura usa `"%03.0f"` (`025`) [BIN 0x432015..0x43208B]. O CSV da sessão (`LoggerCsv.Data`) agora segue esse formato. O `text` do JSON continua como o LCD (`2.87`), coerente com o tempo real, e o overload continua `OL`/`----` (o original usa `.OL`, `O.L` ou `OL.` conforme a faixa, lido só para V/mV): os dois pontos estão na tabela de divergências da `architecture.md`. A evidência 2 da §4.9 da especificação foi corrigida. Na faixa de 3 casas do dump real as saídas já coincidiam. |
| 5 | linux-arm | A condição `'/src/'` do `Directory.Build.props` casa com o caminho do clone | Procede, corrigido | A condição testava o caminho absoluto. Agora ela usa o caminho relativo à raiz do repositório (`$([MSBuild]::MakeRelative(...))`, propriedade `BrymenRepoRelativeDirectory`) e confere se começa com `src\` ou `src/`. Conferido com uma cópia das fontes em `.../scratchpad/src/brymen-check`: build da solução com 0 avisos, `IsAotCompatible` vazio em `tests\Brymen.Dmm.Api.Tests` e `true` em `src\Brymen.Dmm.Hid`. |
| 6 | linux-arm | `DeviceAllow=char-hidraw` só é resolvido na partida da unit; com o HID como módulo, o hidraw pode ficar negado até reiniciar | Procede, corrigido | O `install.sh` instala `/etc/modules-load.d/brymen-dmm.conf` (linha `hid`), roda `modprobe hid` e avisa se o hidraw não estiver em `/proc/devices`; o `uninstall.sh` remove o arquivo. O `EPERM` na abertura (controlador de dispositivos do cgroup) ganhou mensagem própria, diferente do `EACCES` (permissão do nó), com a dica do `/proc/devices`, do `modules-load.d` e do `systemctl restart`. README, unit e `architecture.md` explicam quando o `DeviceAllow` é avaliado. Funções conferidas com um harness no Git Bash; não rodou num Linux real. |
| 7 | linux-arm | A checagem de arquitetura usa `uname -m` (kernel) e aprova o Raspberry Pi OS de 32 bits com kernel arm64 | Procede, corrigido | O `check_architecture` agora decide pelo interpretador do ELF (`/lib/ld-linux-aarch64.so.1` ou `/lib64/ld-linux-x86-64.so.2`, conferidos no `PT_INTERP` dos executáveis publicados) e aborta com `die` quando ele falta; com `getconf LONG_BIT` = 32, a mensagem pede o sistema de 64 bits. Harness com `uname`, `dpkg` e `getconf` falsos: armhf com kernel aarch64, armv7l, Alpine (musl), x64 no arm64 e arm64 no x64 abortam; arm64 e x64 com o interpretador passam. README: o sistema de 64 bits se confere com `dpkg --print-architecture`, e `status=203/EXEC` com `No such file or directory` é o sintoma do userland de 32 bits. |
| 8 | linux-arm | `global.json` exige o SDK 10.0.401: o SDK do apt do Ubuntu (10.0.1xx) não compila | Procede, corrigido | `version` 10.0.100 com `rollForward: latestFeature`, que aceita qualquer SDK 10.0.x a partir dele (aqui continua o 10.0.401). Nada no repositório depende da banda 4xx; o runtime 10.0.13 do publish self-contained vem do target `UsePatchedRuntimePacks`, que independe do SDK. README atualizado. |
| 9 | api-segurança | `GET /readings/latest` decide E9015 × E9002 por um `lastError` velho | Procede, corrigido | Mesmo defeito do achado 1, com a mesma correção. O caso do `POST /logger/imports` sem cabo com o link desativado (o `ReportFault` do `AcquireExclusiveAsync`) agora responde 409 `E9002`, com teste. |
| 10 | api-segurança | Páginas de qualquer origem disparam POSTs sem corpo e abrem o hub por WebSocket; o CORS não as barra | Procede, corrigido | Novo `CrossSiteRequestGuard` (regra do `CrossOriginProtection` do Go): `POST`, `PUT`, `DELETE` e o upgrade para WebSocket com `Sec-Fetch-Site` diferente de `same-origin`/`none`, ou, sem ele, com `Origin` diferente do `Host`, recebem 403 `FORBIDDEN` (código novo), a não ser que a origem esteja em `Cors:AllowedOrigins`. Clientes sem `Origin` (curl, C#, cliente SignalR .NET) e o painel passam; o `Sec-Fetch-Site` funciona atrás de um proxy que reescreve o `Host`. O `WebSocketOptions.AllowedOrigins` não foi usado: o browser manda `Origin` também no WebSocket da própria origem, cujo endereço (IP ou nome do Pi) não é conhecido de antemão. `AllowedHosts` continua `*` por padrão (os nomes do alvo não são conhecidos), mas está documentado como a proteção contra DNS rebinding no README, na `architecture.md` e no `brymen-dmm.env`. A frase do README sobre o CORS foi corrigida. Testes: 16 casos (POST simples, `same-site`, `Origin: null`, PUT JSON, DELETE, GET liberado, WebSocket recusado e aceito, origens liberadas). |
| 11 | api-segurança | Export CSV ignora a desconexão do cliente | Procede, corrigido | Os dois exports passam o `RequestAborted` ao `CsvWriter.WriteAsync`, que confere o token a cada linha e o repassa às escritas. Teste: com um `MemoryStream` (que, como o Kestrel depois do abort, aceita tudo), o cancelamento na 11ª linha para a formatação ali. A cópia do buffer (`Snapshot`, ≈ 8 MB de referências com 1 milhão de amostras) foi mantida: custa milissegundos e dá um export consistente. |
| 12 | api-segurança | `PUT` parcial do comparador e das configurações da aquisição perde atualizações concorrentes | Procede, corrigido | O comparador agora resolve os campos ausentes dentro do delegate do `SettingsStore.UpdateAsync`, sob o gate da gravação (o delegate pode devolver null para não gravar um valor inválido), e devolve os limites gravados. O `Recorder.UpdateSettings` aceita `AcquisitionMode?` e resolve `mode ?? _mode` dentro do lock. Teste: 10 pares de `PUT` concorrentes em cada rota, sem perder campo. |
| 13 | api-segurança | Falha ou cancelamento ao gravar `imports/{id}.bin` deixa um arquivo parcial que reaparece na partida | Procede, corrigido | A imagem vai para `{id}.bin.tmp`, com `Flush(flushToDisk: true)`, e é renomeada com `File.Move(..., overwrite: true)`; numa falha o temporário é apagado. Na partida, os `*.bin.tmp` que sobraram de uma queda de energia são apagados. Teste da limpeza na partida. |
| 14 | api-segurança | Unlink cancelado enquanto espera o `_io` deixa `linkEnabled=false` com o estado antigo e sem evento `status` | Procede, corrigido | O unlink espera o `_io` sem o token da requisição: o link já foi desativado antes da espera, que é curta (a consulta em curso é cancelada; cada comando de memória dura poucos segundos). Teste com o 88 da importação preso no simulador e o cliente desistindo: o unlink termina, o estado vai a `unlinked` e o sinal de mudança dispara. |
| 15 | api-segurança | Importações ficam para sempre na RAM e no disco, sem retenção, e são decodificadas no primeiro acesso | Procede, corrigido | Nova chave `Logger:ImportRetention` (padrão 10, de 1 a 1000): ao terminar uma importação, e na partida, as terminadas mais antigas saem da memória e do disco; na partida só as mais novas são lidas e decodificadas. O `LoggerImportService` é criado na partida (`Program`), e não no primeiro request. A paginação de `GET /logger/imports` não foi feita: a lista fica limitada pela retenção, e o contrato (lista simples) não muda. Teste com retenção 2: partida com 3 imagens e um temporário, depois uma importação nova. |

### 6.2 Build, testes e publicação

| Item | Resultado |
|---|---|
| `dotnet build BrymenDmm.slnx -c Release` | 0 avisos, 0 erros; também numa cópia em `.../scratchpad/src/brymen-check` (achado 5) |
| `dotnet test BrymenDmm.slnx` | Protocol 336/336; Hid 121 aprovados e 12 ignorados de 133; Api 187/187. Total: 644 aprovados, 0 falhas |
| `deploy/publish.ps1` (linux-arm64 e linux-x64, self-contained, single-file, trimmed, `TrimmerSingleWarn=false`) | 0 avisos de trimming/AOT/single-file, 0 erros; pacotes com 25 entradas |
| Executáveis | linux-arm64 23 702 698 B (`PT_INTERP` `/lib/ld-linux-aarch64.so.1`); linux-x64 24 153 474 B (`/lib64/ld-linux-x86-64.so.2`) |
| Pacotes | `brymen-dmm-0.1.0-linux-arm64.tar.gz` 9 591 818 B, SHA-256 `c296649b3d8276579370221295589f0286ef97c7d067c856842455fb885a81c8`; `brymen-dmm-0.1.0-linux-x64.tar.gz` 9 857 564 B, SHA-256 `7e983ba8636f56f998ef2cf38e7bb1ad2d7ceee8f7bc7ede0691a1cff1916702` |

Testes novos: Api +36 (leitor da memória, CSV da sessão, `MeterSession`, export cancelado, Max/Min, link × falhas,
leitura depois da importação, `PUT` concorrentes, `CrossSiteRequestGuard`, retenção das importações) e Hid +1 (`EPERM`).

### 6.3 Binário trimmed win-x64 e hardware real

Um publish win-x64 trimmed do código final rodou com o transporte Simulated e depois com o cabo real (BM525s em ACV,
pontas abertas; software original fechado; só `52 66`, `88` e `89` enviados):

| Verificação | Resultado |
|---|---|
| `POST /api/v1/acquisition/stop` com `Origin: http://intruso.test` | 403 `FORBIDDEN` (ProblemDetails do `ApiJsonContext`); com `Sec-Fetch-Site: same-origin` e com curl sem `Origin`, 200 |
| Aquisição | `mainRange` `{min 0.09, max 0.287, unit V, mode AC}` |
| `Logger:ImportRetention=1` pela linha de comando | Duas importações; só a última fica na lista e em `imports/` |
| Hardware: importação | `completed`, 95/95 blocos, 0 retentativas, 0 recomeços, 375 amostras; imagem de 2280 bytes; durante a leitura `readings/latest` → 409 `CONFLICT`; CSV da sessão `1,  0:00:00.0,0.013,V,AC,0.000,Hz,` … `375,  0:00:18.7,0.006,V,AC,0.000,Hz,` |
| Hardware: logo depois da importação | `readings/latest` → 503 `W7012` (sem leitura atual); 3 s depois, 200 com `0.123` V |
| Hardware: unlink | `readings/latest` → 409 `E9002` |
| Hardware: partidas | A 1ª partida, com o medidor ocioso havia ≈ 2 h, teve um `W7012` isolado no polling inicial, com recuperação na consulta seguinte; duas partidas seguidas depois disso, sem nenhum. O caminho do polling não mudou nesta etapa. |

### 6.4 Não validado nesta etapa

- `install.sh`/`uninstall.sh` num Linux real: `modules-load.d`, `modprobe hid`, checagem do interpretador e o
  `DeviceAllow` com o hidraw registrado depois da partida (só harness no Git Bash).
- Build num Linux (o caminho relativo com `/`) e com o SDK 10.0.1xx do Ubuntu.
- O `CrossSiteRequestGuard` contra um browser real (Fetch Metadata, `Origin` no WebSocket) e atrás de um proxy reverso.
- O formato do overload por faixa no CSV da sessão do original fora de V/mV e o uso de `"%03.0f"` em todas as
  temperaturas.


## 7. Biblioteca Brymen.Dmm

Etapa de 2026-10-22, pedido do usuário: “faça o pacote/biblioteca/classe standalone para a integração em outros projetos
.NET”. O desenho está em [architecture.md](architecture.md#biblioteca-brymendmm).

### 7.1 O que mudou

| Área | Mudança |
|---|---|
| `src/Brymen.Dmm` (novo) | `BrymenMeter`, `BrymenMeterOptions`/`LoggerReadOptions`, `MeterState`, `BrymenErrorCodes`/`BrymenMeterError`/`BrymenMeterException`, `MeterConnection`, `LoggerMemoryReader`, `LoggerMemoryImage`, `LoggerReadProgress`, `ILoggerMemoryTransport`/`MeterCommandResult`. Pacote NuGet único `Brymen.Dmm` 0.1.0. |
| Protocol e Hid | Alvos `net8.0` e `net10.0`. No `net8.0`, `Lock` é um apelido de `object` (`Directory.Build.props`); nenhum outro ajuste foi preciso. Documentação XML completa (CS1591/CS1573 ativos nas três bibliotecas empacotadas). |
| Hid | `SimulatedBm52xMeter` absorveu o `CaptureResponder` da API: `SimulatedFault`, `FaultInjector`, `CommandCount`, `CurrentAfterHeadWindow`, `CapturedRealTimeFrames` (os 14 quadros de `rtd-acv.txt`, embutidos), `FromCaptures(TimeProvider)` e `MemoryBlockCount`. `SimulatedHidDevice` aceita um `TimeProvider` (latência e timeouts num relógio falso). |
| API | Referencia `src/Brymen.Dmm`. O `LoggerMemoryReader` e o `CaptureResponder` saíram da API; o `MeterSession` compõe um `MeterConnection` (abertura, E9015 e política do 1º `66` lento); o `LoggerImportService` usa o leitor da biblioteca, que faz também a conferência do modelo (E9017). As rotas e os DTOs não mudaram. |
| Testes | `tests/Brymen.Dmm.Tests` (novo, `net10.0` e `net8.0`): 60 testes e 4 de hardware. Os 13 testes do leitor da memória saíram da API para lá, e os 4 do `CaptureResponder`, para o Hid (`SimulatedBm52xMeterFaultTests`, com mais 1 do `TimeProvider`). |
| Docker | O `Dockerfile` copia também o `Brymen.Dmm.csproj` na camada de restore. |

### 7.2 Build, testes e pacote

| Item | Resultado |
|---|---|
| `dotnet build BrymenDmm.slnx -c Release` | 0 avisos, 0 erros |
| `dotnet test BrymenDmm.slnx` | Protocol 336; Hid 126 (+12 ignorados); Brymen.Dmm 60 (+4 de hardware ignorados) em `net10.0` e 60 em `net8.0`; Api 170 |
| Publish linux-arm64 e linux-x64 (self-contained, single-file, trimmed) | 0 avisos |
| `dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages` | `Brymen.Dmm.0.1.0.nupkg` e `.snupkg`, 0 avisos (sem licença nem `RepositoryUrl`, que ainda não foram definidos) |
| Conteúdo do `.nupkg` | `lib/net8.0` e `lib/net10.0` com `Brymen.Dmm`, `Brymen.Dmm.Protocol` e `Brymen.Dmm.Hid` (DLL e XML); `README.md`; nuspec só com grupos de dependência vazios; os PDBs no `.snupkg` |
| Consumo do pacote | Um console `net8.0`/`net10.0` com `PackageReference` ao `.nupkg` local abriu o simulado, leu, usou eventos, importou a memória (375 amostras) e recebeu E9015 sem cabo, nos runtimes 8.0.32 e 10.0.12 |

### 7.3 Hardware real (`BRYMEN_HW_TESTS=1`)

BM525s em ACV, pontas abertas, e BC-86X no Windows; só `52 66`, `88`, `89` e `8A`. Os 4 testes de
`tests/Brymen.Dmm.Tests/HardwareTests.cs` passaram em `net10.0` e em `net8.0`:

| Verificação | Resultado |
|---|---|
| Abertura e leitura | `\?\hid#vid_0820&pid_0001#...`, `Brymen` / `Superior DMM`, bcdDevice 8603; 1ª leitura em 213–276 ms, 2ª em 198–220 ms (0,122 V AC, Model ID `52525252`) |
| Leitura contínua por 3 s (`PollInterval` 200 ms) | 14–15 leituras (4,7–5,0/s), sem falhas |
| Modo por eventos por 2 s | 9 leituras, sem falhas |
| Importação | 95 blocos em ≈ 10,9 s, 0 retentativas, 0 recomeços, 1 sessão de 375 amostras; imagem **idêntica byte a byte** à de `memory-dump-1.txt` |
| 1º `66` depois da importação | 2054–2071 ms, displays em traços (`-.---`), dentro dos 3 s da política, sem W7012; o seguinte, 92–235 ms |
| 1ª execução | O 1º `66`, com o medidor ocioso havia muito tempo, ficou sem resposta em 3 s (W7012); os comandos seguintes responderam normalmente, e as execuções seguintes não repetiram. É o mesmo `W7012` isolado da 1ª partida da [§6.3](#63-binário-trimmed-win-x64-e-hardware-real). O `ReadAsync` não tenta de novo (está documentado); a leitura contínua tolera 2 falhas seguidas. O teste de abertura agora tenta uma 2ª vez e registra o caso. |

## 8. Biblioteca NuGet

Validação de integração de 2026-10-23 dos pacotes `Brymen.Dmm` e `Brymen.Dmm.Extensions.Hosting` (desenho em
[architecture.md](architecture.md#biblioteca-brymendmm) e guia em [library.md](library.md)) e da API, que agora usa a
biblioteca. Mesma máquina (Windows 11 Pro 10.0.26300), SDK 10.0.401, runtimes 8.0.32 e 10.0.12, e o mesmo cabo do
[Ambiente](#ambiente); BM525s em ACV, pontas abertas, com a memória do `memory-dump-1.txt`; software original fechado;
só `52 66`, `88`, `89` e `8A` enviados.

Nenhuma quebra de integração apareceu, e o código não precisou de correção. Esta etapa só completou a documentação: o
pacote de hosting e os exemplos na tabela de projetos da [architecture.md](architecture.md#projetos), o uso de
`MeterConnection.ReadRealTimeAsync` e do `MeterExclusiveAccess` pela API, o projeto de testes do hosting na seção
Testes do README e, na [library.md](library.md), um aviso: `new SimulatedHidDeviceProvider()` sem argumentos já traz um
cabo simulado, e o cabo ausente pede `new SimulatedHidDeviceProvider([])` (o 1º rascunho dos consumidores abaixo caiu
nisso).

### 8.1 Build e testes

```powershell
dotnet build BrymenDmm.slnx -c Release            # também --no-incremental e em Debug
dotnet test BrymenDmm.slnx                        # 3 execuções: Debug, Release e Debug
$env:BRYMEN_HW_TESTS = '1'; dotnet test tests/Brymen.Dmm.Tests --filter "FullyQualifiedName~HardwareTests"
```

| Projeto de teste | Aprovados | Falhas | Ignorados |
|---|---|---|---|
| `Brymen.Dmm.Protocol.Tests` | 336 | 0 | 0 |
| `Brymen.Dmm.Hid.Tests` | 126 | 0 | 12 |
| `Brymen.Dmm.Tests` (`net10.0` / `net8.0`) | 60 / 60 | 0 | 4 / 4 (hardware) |
| `Brymen.Dmm.Extensions.Hosting.Tests` (`net10.0` / `net8.0`) | 16 / 16 | 0 | 0 |
| `Brymen.Dmm.Api.Tests` | 174 | 0 | 0 |
| **Total** | **788** | **0** | **20** |

- Build Release: 0 avisos e 0 erros, também com `--no-incremental`; o build Debug também deu 0 avisos.
- As três execuções da suíte deram o mesmo resultado. A API tem 174 testes: os 170 da [§7.2](#72-build-testes-e-pacote)
  e mais 4 da adoção da biblioteca.
- `HardwareTests` de `tests/Brymen.Dmm.Tests` com o cabo real: 4/4 em `net10.0` e 4/4 em `net8.0`.

### 8.2 Pacotes

```powershell
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages
dotnet pack src/Brymen.Dmm.Extensions.Hosting -c Release -o artifacts/packages
```

Os dois packs terminaram com 0 avisos: nenhum `NU5xxx` e nenhum aviso de licença, então nada foi suprimido.

| Pacote | Tamanho | Conteúdo | Dependências no nuspec |
|---|---|---|---|
| `Brymen.Dmm.0.1.0.nupkg` | 265 632 B | `lib/net8.0/` e `lib/net10.0/` com `Brymen.Dmm`, `Brymen.Dmm.Protocol` e `Brymen.Dmm.Hid` (DLL e XML de cada um), mais o `README.md` (o `src/Brymen.Dmm/README.md`, 7 952 B) | Nenhuma: só os grupos vazios `net10.0` e `net8.0` |
| `Brymen.Dmm.0.1.0.snupkg` | 90 348 B | Os 6 PDBs (3 por TFM) | — |
| `Brymen.Dmm.Extensions.Hosting.0.1.0.nupkg` | 45 915 B | `lib/net8.0/` e `lib/net10.0/` com DLL e XML, mais o `README.md` (4 328 B) | `net10.0`: `Brymen.Dmm` 0.1.0 e `Microsoft.Extensions.Configuration.Binder`, `.Hosting.Abstractions`, `.Logging.Abstractions` e `.Options` 10.0.13. `net8.0`: `Brymen.Dmm` 0.1.0, Binder 8.0.2, Hosting.Abstractions 8.0.1, Logging.Abstractions 8.0.2 e Options 8.0.2. O Protocol e o Hid não aparecem |
| `Brymen.Dmm.Extensions.Hosting.0.1.0.snupkg` | 31 643 B | Os 2 PDBs | — |

- Metadados: `id`, `version` 0.1.0 (do `Directory.Build.props`), `title`, `description`, `tags` e `readme` nos dois
  pacotes, mais `<repository type="git" />` sem URL (posto pelo SourceLink do SDK). Não há licença nem `RepositoryUrl`.
  O `authors` é o padrão do SDK, igual ao `id`.
- As DLLs do pacote têm a versão 0.1.0.0, com `InformationalVersion` 0.1.0 e o `TargetFramework` da pasta.
- O `.nupkg` muda de hash a cada pack (o zip é refeito), mas os tamanhos não mudaram. SHA-256 dos pacotes que ficaram
  em `artifacts/packages`, os mesmos usados pelos consumidores abaixo: `Brymen.Dmm.0.1.0.nupkg`
  `1d9f94f2df0d32059a1131e0614130ed39e5801fe2c1d48910f866cb49e97507` e `Brymen.Dmm.Extensions.Hosting.0.1.0.nupkg`
  `467929059d0b1631623bb32ab968d8417bae3214a2d962b41428a1b24ea4175e`.

### 8.3 Consumidores isolados

Os consumidores ficam fora da solução, em `.../scratchpad/consumers/`, com um `Directory.Build.props` e um
`Directory.Build.targets` vazios (nada herdado de pastas acima) e um `nuget.config` em cada projeto: `<clear />`, o feed
local `C:\dev\Brymen BU-86X USB kit\artifacts\packages` e o nuget.org, com `packageSourceMapping` (`Brymen.Dmm*` só no
feed local). `NUGET_PACKAGES` apontou para `.../scratchpad/nuget-cache`, com as pastas `brymen.*` apagadas antes de cada
restore; o SHA-512 do pacote no cache conferiu com o de `artifacts/packages`. O cache global do usuário
(`%USERPROFILE%\.nuget\packages`) não tem nenhum pacote `brymen.*`.

Os consumidores (a) e (b) compilam o mesmo código, com `TreatWarningsAsErrors` e os analisadores de trimming (e, no
`net10.0`, os de AOT), e fazem 13 conferências com o medidor simulado:

- `OpenSimulatedAsync` e 3 `ReadAsync`: os 3 primeiros quadros de `rtd-acv.txt` (0,287, 0,156 e 0,090 V AC, 0,000 Hz);
- `ReadContinuouslyAsync` a 100 ms: 5 leituras em ≈ 480 ms;
- `Start` com eventos e, com o laço ativo, `ReadLoggerMemoryAsync`: 1 sessão ACV + Hz de 375 amostras a 0,05 s, 95/95
  blocos; 8 leituras antes da importação e 16 depois (pausa e retomada); `StopAsync` deixa `IsRunning` falso;
- `ReadLoggerImageAsync`: imagem de 2280 bytes idêntica byte a byte ao `memory-dump-1.txt`, lido pelo próprio consumidor
  (bytes 1 a 8 dos 3 primeiros relatórios de cada quadro, com o checksum conferido), sem o parser da biblioteca;
- `FaultInjector` com checksum errado a cada 7º `89`: 15 retentativas, 0 recomeços, imagem ainda idêntica;
- `OpenAsync` com um `SimulatedHidDeviceProvider([])`: `BrymenMeterException` `E9015`;
- cabo simulado removido na 3ª leitura de `ReadContinuouslyAsync` e religado 700 ms depois (`ReconnectDelay` de 200 ms):
  a enumeração continua até a 8ª leitura, com `W7012` e 4 × `E9015` em `ErrorOccurred` e o estado final `Connected`.

| Consumidor | Runtime | Build/publish | Resultado |
|---|---|---|---|
| (a) console `net8.0` | .NET 8.0.32 | 0 avisos | 13/13 |
| (b) console `net10.0` | .NET 10.0.12 | 0 avisos | 13/13 |
| (c) o (b) publicado self-contained win-x64 com `PublishTrimmed=true` | 10.0.12 embutido | 0 avisos, também com `TrimmerSingleWarn=false` e sem `TreatWarningsAsErrors`: nenhum IL2xxx/IL3xxx do pacote | 13/13. Depois do trimming, `Brymen.Dmm.dll` tem 63 488 B, `Brymen.Dmm.Hid.dll` 63 488 B e `Brymen.Dmm.Protocol.dll` 53 248 B (73 728, 98 304 e 60 928 B no pacote); pasta de 20,6 MB com 35 arquivos. As capturas embutidas no Hid continuam lá depois do trimming |
| Extra: o (a) publicado da mesma forma | 8.0.31 embutido | 0 avisos | 13/13 |

(d) Exemplos, com o mesmo `NUGET_PACKAGES` limpo e o `samples/nuget.config`:

| Exemplo | Resultado |
|---|---|
| Build Release `--no-incremental` dos dois | 0 avisos; `brymen.dmm` e `brymen.dmm.extensions.hosting` 0.1.0 restaurados do feed local |
| `ConsoleSample --simulated --count 5` | 5 leituras (0,287 → 0,090 V AC), código 0 |
| `ConsoleSample --simulated --import sim.csv` | 1 sessão e 375 amostras (95 blocos, 0 retentativas, 0 recomeços); CSV de 376 linhas |
| `ConsoleSample --help`, `--bogus`, `--count 0`, `--list` | Códigos 0, 2, 2 e 0; o `--list` mostra o cabo real |
| `WorkerSample --simulated --Brymen:PollInterval=00:00:00.2`, 4 s | Log 1 (“Multímetro aberto no cabo sim://bu86x/0”), `Connecting -> Connected` e 16 leituras |

### 8.4 Hardware real com os pacotes

| Verificação | Resultado |
|---|---|
| `ConsoleSample --count 25` | Cabo `\\?\hid#vid_0820&pid_0001#9&2e0db096&0&0000#{...}`; 25 leituras em 4,9 s (0,124–0,125 V AC, 0,000 Hz, `AUTO`), a cada ≈ 200 ms, sem erros; 5,4 s com a abertura |
| `ConsoleSample --import hw.csv --count 5` | 95 blocos, 0 retentativas, 0 recomeços, 1 sessão e 375 amostras em ≈ 10,6 s. O `hw.csv` é **idêntico byte a byte** ao `sim.csv` do simulado, cuja memória é o `memory-dump-1.txt` embutido. A 1ª leitura veio ≈ 2,09 s depois da importação, em traços (`-.---`), sem `W7012`; o secundário continuou em traços nas 4 leituras seguintes |
| Consumidor (c), trimmed, `--hardware` | 1ª `ReadAsync` em 261 ms; importação em 10,48 s (95 blocos, `TotalBytes` 2272, 0 retentativas); imagem de 2280 bytes **idêntica byte a byte** ao dump; 1º `66` depois dela em 2052 ms (traços) e o seguinte em 223 ms; `LastError` nulo |
| Consumidor (a), `net8.0`, `--hardware` | 1ª leitura em 189 ms; importação em 10,63 s; imagem idêntica; 1º `66` depois dela em 2055 ms e o seguinte em 255 ms; `LastError` nulo |
| `WorkerSample` com o cabo, 5 s | 22 leituras, nenhum aviso no log |

Nenhum `W7012` em nenhuma execução desta etapa, inclusive no 1º `66` de cada processo.

### 8.5 Regressão da API com o cabo real

A API do código atual foi publicada trimmed para win-x64 (`Brymen.Dmm.Api.exe` de 22 055 518 B, 0 avisos, com os
parâmetros da [§2](#2-publicação)) em `.../scratchpad/api-win-x64` e executada com
`--Urls=http://127.0.0.1:5793 --Meter:Transport=Hid --DataDirectory=.../scratchpad/e2e-lib`. Os testes usaram os
scripts da [§3.1](#31-configuração) (`smoke.sh` e `ImportCheck.cs`) e o `samples/Brymen.Dmm.SignalRClient`, conectado do
começo ao fim.

| Verificação | Resultado |
|---|---|
| `GET /api/v1/meter` | `connected`, `hid`, `deviceOpen: true`, dispositivo com o caminho, `0x0820`/`0x0001`, `0x8603`, `Brymen` / `Superior DMM`; `lastError: null` |
| `/health/live`, `/health/ready`, `/`, `/openapi/v1.json` | 200; 18 rotas `/api/v1` |
| `GET /api/v1/readings/latest` | 200: `0.122` V `AC`, secundário `0.000` Hz `NONE`, `AUTO`, `rawHex` `00001000BFA0CBCB0000BFAFAFAF00012006005252525210000000` |
| SSE, 4 s | 21 eventos `reading` |
| Comparador | `"comparator":"Pass"` com 0,121 V; `Hi ≤ Lo` → 400 `W7010` |
| Aquisição `fullSpeed`, 10 s | 50 amostras; CSV de 51 linhas com o cabeçalho exato; o estendido com `Timestamp,MainSiValue,SubSiValue,Flags,Comparator` |
| Erros do framework | 404 `NOT_FOUND`, 405 `METHOD_NOT_ALLOWED` e 400 `VALIDATION` (JSON malformado, `enabled` ausente, `limit=0`) |
| Importação via REST | 202 + `Location`; `completed` em 10,84 s, 95/95 blocos, 0 retentativas, 0 recomeços; 409 `CONFLICT` em `readings/latest` durante a leitura; 1 sessão `A0_0080` → `A0_095A`, ACV + Hz, 0,05 s, dual, 2250 bytes, 375 amostras com os `count` do `test-vectors.json`; imagem de 2280 bytes idêntica à captura; CSV da sessão com 376 linhas; 1ª leitura 2063 ms depois do `completedAt` (traços); `lastError: null` |
| Unlink/link (2 vezes) | `PUT {enabled:false}` → 200 `unlinked`, `deviceOpen: false`; `readings/latest` → 409 `E9002`; `/health/ready` → 503; `PUT {enabled:true}` → 200 `connected`, `deviceOpen: true`, 1ª leitura ≈ 0,6 s depois; `lastError: null` |
| SignalR, evento `reading` | 336 eventos (`sequence` 53..388) em 84,7 s, **nenhuma lacuna**; 4,87 leituras/s antes da importação e 4,92/s depois (3,97/s na média, que inclui a importação e os unlinks) |
| SignalR, evento `status` | No unlink, no link, no início da importação (`importando o datalogger`) e no fim dela |
| Log do servidor | Nenhum `warn`; um único `fail`, o do health check com o cabo fechado durante o unlink, que é esperado |
| Encerramento | API e cliente encerrados; nenhum processo `Brymen*` em execução; logo depois, o `ConsoleSample` abriu o cabo e leu normalmente |

Os resultados coincidem com os da [§3.2](#32-resultados) e da [§6.3](#63-binário-trimmed-win-x64-e-hardware-real): a
troca do leitor da memória, da política do 1º `66` e do polling pela biblioteca não mudou o comportamento da API.

### 8.6 Publicação para Linux

`$env:TrimmerSingleWarn = 'false'; ./deploy/publish.ps1` (linux-arm64 e linux-x64, self-contained, single-file,
trimmed): **0 avisos** e 0 erros. Rodou depois da última mudança do `README.md`, que vai no pacote.

| Artefato | Executável | Pasta do publish | `tar.gz` | SHA-256 do `tar.gz` |
|---|---|---|---|---|
| `linux-arm64` (`PT_INTERP` `/lib/ld-linux-aarch64.so.1`) | 23 698 983 B (22,6 MiB) | 23 831 504 B | 9 603 533 B (9,2 MiB), 25 entradas | `6e851e07255ead32567a7569f0c44c52a22a8113ed79e8256b8d890723f3e78b` |
| `linux-x64` (`PT_INTERP` `/lib64/ld-linux-x86-64.so.2`) | 24 149 759 B (23,0 MiB) | 24 282 280 B | 9 871 745 B (9,4 MiB), 25 entradas | `37cd90c8dbe9eb482acfb9909434033c52a234d71f01e7c0c8b1518e1526aa09` |

- Em relação à [§6.2](#62-build-testes-e-publicação), os dois executáveis ficaram 3 715 B menores. O runtime embutido
  continua o 10.0.13.
- O win-x64 trimmed da §8.5 tem 22 055 518 B (22 022 369 B na [§2](#2-publicação)).

### 8.7 Não validado nesta etapa

- Linux real para os pacotes e os exemplos (hidraw, permissões, udev): só Windows com o cabo.
- Ligação do Native AOT de um consumidor (falta o MSVC nesta máquina): só os analisadores e o publish trimmed.
- Parada graciosa (Ctrl+C) do `WorkerSample` e da API com o cabo real: os processos foram encerrados à força. A parada
  do serviço hospedado é coberta pelos testes do hosting.
- Publicação num feed (nuget.org ou privado): os pacotes ainda não têm licença, `RepositoryUrl` nem `Authors` próprio.

## 9. Revisão da biblioteca

Correções de 2026-10-23, a partir dos 17 achados dos revisores independentes das lentes design-usabilidade (10),
correção-concorrência (3) e empacotamento-compat (4), sobre os pacotes `Brymen.Dmm` e `Brymen.Dmm.Extensions.Hosting`
da [§8](#8-biblioteca-nuget). Cada achado foi conferido no código antes da correção. Os que mudam comportamento ganharam
testes, e os testes da thread de quem chama e da contagem do `Bu86xLink` foram conferidos falhando sem a correção.
Mesma máquina, SDK 10.0.401, runtimes 8.0.32 e 10.0.12, e o mesmo cabo do [Ambiente](#ambiente); só `52 66`, `88`, `89`
e `8A` enviados.

### 9.1 Achados e vereditos

| # | Lente | Achado | Veredito | Motivo e correção |
|---|---|---|---|---|
| 1 | design-usabilidade | `IBrymenMeterAccessor.Meter` entrega o `BrymenMeter` do serviço; um `await using` ou um `StopAsync` do consumidor desliga o serviço sem aviso | Procede, corrigido | Nova interface `IBrymenMeter` no `Brymen.Dmm` (`Device`, `State`, `LastError`, `ReadAsync`, `ReadLoggerMemoryAsync`, `ReadLoggerImageAsync`), implementada pelo `BrymenMeter`. `Meter` e `WaitForMeterAsync` passam a devolver uma fachada privada (`MeterHandle`) que só repassa essas operações: sem `Dispose`, `DisposeAsync`, `Start` nem `StopAsync`, e sem ser o `BrymenMeter` (não há cast). O `await using` do cenário não compila mais. Depois da parada, as operações dela lançam `ObjectDisposedException`, e o `State` dela vai a `Disposed`. Testes: a fachada não é `IDisposable`/`IAsyncDisposable` nem `BrymenMeter`, e uma leitura avulsa por ela não interrompe o laço. |
| 2 | design-usabilidade | O exemplo "Leitura contínua" do README do pacote termina com `TaskCanceledException` não tratada; o "Início rápido" da `library.md` usa um token não declarado | Procede, corrigido nos documentos | O comportamento (o cancelamento do token de quem chama termina a enumeração com `OperationCanceledException`) é o documentado e o do `ConsoleSample`, e foi mantido. O README do pacote envolve o `await foreach` em `try`/`catch (OperationCanceledException) when (cts.IsCancellationRequested)` e explica o caso; o "Início rápido" agora para por contagem (`break`), sem token, e avisa que, com um token, o cancelamento lança. O XML do `ReadContinuouslyAsync` diz o mesmo no resumo. |
| 3 | design-usabilidade | Não há `IAsyncEnumerable` compartilhado: o caminho óbvio cria um 2º laço de polling e dobra o tráfego no cabo | Procede, corrigido | `IBrymenMeterAccessor.ReadingsAsync(CancellationToken)`: cada enumeração recebe as leituras do laço do serviço por um `Channel` limitado a 64 (`DropOldest`), sem consultas próprias; termina na parada do serviço e no descarte do contêiner, e com a falha da abertura se o serviço desistir. O `ReadContinuouslyAsync` ficou fora do `IBrymenMeter`, e o XML e a `library.md` dizem que cada enumeração dele faz as suas consultas. No `BrymenMeter` não entrou um equivalente: o `Start` com o `ReadingReceived` já distribui um laço só (documentado). A evolução das interfaces fica com métodos de implementação padrão (escrito no XML e na `library.md`). Testes: duas enumerações recebem as mesmas instâncias, na ordem, com os `52 66` no cabo entre as leituras do laço e elas + 2; o consumidor lento perde as mais antigas; a enumeração termina na parada, e uma nova termina na hora. |
| 4 | design-usabilidade | `WaitForMeterAsync` nunca termina quando o serviço desiste de abrir (`AutoReconnect=false`), nem depois do `StopAsync` | Procede, corrigido | A espera termina com falha quando o medidor não vai mais aparecer: a `BrymenMeterException` da abertura (`E9015`) quando o serviço desiste, `InvalidOperationException` na parada, a exceção da partida que falhou (por exemplo, o `ArgumentException` de uma opção inválida) e `ObjectDisposedException` no descarte do contêiner. As chamadas seguintes falham na hora, até uma nova partida; antes da 1ª partida, a espera continua. As tarefas com falha são marcadas como observadas (sem `UnobservedTaskException` quando ninguém espera). Testes: sem cabo e sem `AutoReconnect`, a espera feita antes da partida e a enumeração terminam com `E9015`; depois da parada, `InvalidOperationException`; opção inválida, `ArgumentException`; contêiner descartado durante a espera, `ObjectDisposedException`. |
| 5 | design-usabilidade | Um `IHidDeviceProvider` que lança fora de `IOException` deixa o medidor preso em `Connecting`, sem `LastError` nem `ErrorOccurred` | Procede, corrigido | `ReadAsync` e `ReadLoggerImageAsync` passaram a tratar como o laço já tratava: uma exceção fora do contrato (nem `BrymenMeterException`, nem cancelamento, nem descarte) fecha o cabo e vira `BrymenMeterException` `INTERNAL`, com a original em `InnerException`, registrada em `LastError` e `ErrorOccurred`, e o estado vai a `Faulted`. Vale também para a exceção de um `IProgress` da importação. A tabela de erros (README e `library.md`) e o XML documentam o `INTERNAL` nesses métodos. Testes: provider que lança `InvalidOperationException` na reabertura (`INTERNAL`, `Faulted`, evento, e recuperação quando ele volta) e progresso que lança. |
| 6 | design-usabilidade | O XML de `MeterState.Closed` diz que é o estado inicial do `BrymenMeter`, mas um `BrymenMeter` nunca fica em `Closed` | Procede, corrigido | O XML agora diz que `Closed` só aparece no `IBrymenMeterAccessor`, antes da 1ª abertura e com o serviço parado, e que o `OpenAsync` devolve o medidor em `Connected`. Com o achado 1, um consumidor não descarta mais o medidor; o `Disposed` → `Closed` do acessor só aparece na corrida com a parada, que é o "serviço parado" do XML. |
| 7 | design-usabilidade | As DLLs Hid e Protocol do pacote `Brymen.Dmm` saem com `Product` "Brymen DMM REST" e `Company` igual ao nome do assembly | Procede, corrigido | O `Directory.Build.props` usa `Product` = `Brymen.Dmm` por padrão (a API define o dela, e o hosting, o dele) e `GenerateAssemblyCompanyAttribute=false`. Não foi inventado um autor: `Authors`, `Company` e `Copyright` continuam como pendência do autor (`library.md`). Conferido no `FileVersionInfo` das 8 DLLs dos pacotes novos: `Product` `Brymen.Dmm` (hosting: `Brymen.Dmm.Extensions.Hosting`) e `CompanyName` vazio. |
| 8 | design-usabilidade | Superfície pública vazada no `Brymen.Dmm.Hid`: detalhes de implementação e ganchos de teste viram contrato no 0.1.0 | Procede, corrigido | `HidReportDescriptor` e `HidReportLayout` são internos, e o `LinuxHidrawDeviceProvider` tem só o construtor público sem parâmetros: o de `sysfsRoot`/`devRoot`, as propriedades `SysfsRoot`/`DevRoot` e `ReadReportLayout` (que expunha o `HidReportLayout`) ficaram internos, para os testes. O `EnumerateAll` continua público: é a enumeração de diagnóstico de todos os hidraw e não expõe tipo interno. A tabela de namespaces da `library.md` lista os tipos públicos do Hid, inclusive os transportes de cada plataforma. |
| 9 | design-usabilidade | O hosting comporta um só medidor por contêiner: não há registro nomeado nem com chave para dois cabos | Procede, corrigido | `AddBrymenMeter(string name, Action<BrymenMeterOptions>?)` e `AddBrymenMeter(string name, IConfiguration)`: opções nomeadas (`IOptionsMonitor.Get(name)`), `IBrymenMeterAccessor` com chave (`GetRequiredKeyedService`, `[FromKeyedServices]`) e um serviço hospedado por nome. Chamar de novo com o mesmo nome só acrescenta configuração, e o registro sem nome é independente. O log vai para a categoria do tipo seguida de `.{nome}`. Teste e consumidor com dois cabos simulados: acessores distintos, 2 serviços, leituras e log em cada um. |
| 10 | design-usabilidade | Os simuladores padrão não reproduzem o mesmo medidor que o `OpenSimulatedAsync` | Procede, corrigido | `SimulatedHidDevice.CreateBm52x()` sem medidor usa `SimulatedBm52xMeter.FromCaptures()` (os 14 quadros e o `8A` que avança como `89` logo depois do `88`) e continua sem latência, como o XML agora diz. Novos `SimulatedHidDevice.TypicalResponseLatency` (20 ms) e `CreateTypicalBm52x()`; o `new SimulatedHidDeviceProvider()` sem argumentos e o `OpenSimulatedAsync` usam esse cabo, e o `BrymenMeter.SimulatedLatency` é o mesmo valor. Testes: o provider padrão devolve os quadros capturados com a latência típica; o `CreateBm52x()` reproduz o `8A` logo depois do `88`; o `OpenSimulatedAsync` e o provider padrão dão as mesmas leituras e o mesmo cabo. O `WorkerSample` passou a usar o provider sem argumentos. |
| 11 | correção-concorrência | No Linux, os métodos "Async" do `BrymenMeter` bloqueiam a thread de quem chama durante toda a troca HID | Procede, corrigido | Em dois níveis. (a) `LinuxHidrawDevice.ReadReportAsync` faz um `poll(0)` e devolve na hora um relatório que já está na fila; senão, o laço de `poll()` em fatias roda num `Task.Run`, e a tarefa volta em andamento. (b) O `BrymenMeter` passa para o pool (`ThreadPoolHop`, um awaiter que sempre continua no pool, sem o contexto de quem chama) antes da 1ª E/S de `ReadAsync`, `ReadLoggerImageAsync` e de cada consulta da leitura contínua, o que cobre também um `IHidDevice` próprio síncrono, o simulado sem latência e o `ReadContinuouslyAsync(TimeSpan.Zero)`. O XML da classe e a seção "Threads" da `library.md` descrevem o comportamento por plataforma. Teste com um `IHidDevice` que bloqueia 200 ms de forma síncrona: `ReadAsync`, `ReadLoggerImageAsync` e o `MoveNextAsync` com intervalo zero voltam não concluídos antes do bloqueio; com o `ThreadPoolHop` desligado, o teste falha. O teste do uhid (`TimeoutCancellationAndLateReports`) ganhou a mesma conferência para o hidraw, mas só roda num Linux com `BRYMEN_UHID_TESTS=1`. |
| 12 | correção-concorrência | `WaitForMeterAsync` no `StartAsync` de um serviço registrado antes do `AddBrymenMeter` (a ordem que a documentação recomendava) trava a partida do host | Procede, corrigido | O serviço implementa `IHostedLifecycleService`: a 1ª abertura (e o `Attach` no acessor) acontece no `StartingAsync`, que o host chama em todos os serviços antes de qualquer `StartAsync`, e o laço de leitura (ou as novas tentativas de abertura, ou a desistência) começa no `StartedAsync`, depois de todos. Sem o ciclo de vida (uma chamada direta ao `StartAsync`), as duas fases ficam no `StartAsync`. A ordem do registro deixou de importar para não perder leituras; a `library.md`, o README do hosting e o `WorkerSample` não pedem mais o registro antes. Efeito documentado: as mudanças de estado e o log da 1ª abertura acontecem no `StartingAsync`, e quem assina `StateChanged` no `StartAsync` lê o `State` atual (o `ReadingLogger` do exemplo registra o estado na partida). Teste: serviços registrados antes e depois do `AddBrymenMeter` esperam o medidor no próprio `StartAsync`; a partida termina, e os dois recebem a 1ª leitura do laço (`0.287`). |
| 13 | correção-concorrência | `ReportsReceived` antigo depois de um `Close` concorrente: o leitor pode tomar por processado um `89` que nem foi enviado e usar o `8A` | Procede, corrigido | O `Bu86xLink.ExecuteAsync` zera `LastCommandReportsReceived` na entrada, antes do `ObjectDisposedException.ThrowIf` e da espera pela vez, e de novo dentro dela. Uma chamada que falha antes da escrita deixa 0, o lado seguro: o leitor recomeça do `88`. O XML diz que, com chamadas concorrentes, uma chamada que entra pode zerar o valor (nunca o contrário). Teste: depois de um `89` com 4 relatórios, o link é descartado; o `ExecuteAsync` seguinte lança `ObjectDisposedException` e a contagem é 0 (antes da correção, 4). |
| 14 | empacotamento-compat | O pacote de hosting exige no mínimo o patch mais recente dos `Microsoft.Extensions.*` (10.0.13 e 8.0.2); um consumidor com uma versão anterior falha no restore com `NU1605` | Procede, corrigido | As 4 dependências estão agora na menor versão de cada linha: 10.0.0 no `net10.0` e 8.0.0 no `net8.0`, inclusive o `Configuration.Binder` (os geradores 8.0.0 e 10.0.0 compilam o binding, e os testes de configuração passam nos dois alvos e no publish trimmed). O `IHostedLifecycleService` do achado 12 existe desde o `Hosting.Abstractions` 8.0.0. Nuspec conferido (`>= 8.0.0` e `>= 10.0.0`). Os consumidores de hosting da [§9.5](#95-consumidores-isolados) referenciam o `Microsoft.Extensions.Hosting` e o `Microsoft.Extensions.Options` 8.0.0 e 10.0.0 direto: o restore passa sem `NU1605` e o `project.assets.json` resolve as duas versões. |
| 15 | empacotamento-compat | As DLLs publicadas trazem caminhos absolutos da máquina local; a do hosting `net8.0` traz o caminho do fonte, e o `ContinuousIntegrationBuild` não normaliza nada sem git | Procede, corrigido | No Release, o `Directory.Build.props` define `PathMap` = raiz do repositório → `/_/` para os projetos de `src/`, sem depender do git nem da variável `CI`; o Debug mantém os caminhos reais, para o depurador. Nas 8 DLLs dos pacotes novos não há mais "Brymen BU-86X USB kit" nem o nome do usuário (procurado em ASCII e UTF-16): os PDBs aparecem como `/_/src/<projeto>/obj/Release/<tfm>/<projeto>.pdb`, e o `[InterceptsLocation]` do `net8.0` do hosting como `/_/src/Brymen.Dmm.Extensions.Hosting/BrymenMeterSettings.cs`. O binding interceptado continua funcionando (testes `net8.0`, consumidor `net8.0`). A `library.md` e os comentários dos `.csproj` foram corrigidos. |
| 16 | empacotamento-compat | As DLLs do Protocol e do Hid herdam o `Product` da API, e o `Company` das quatro DLLs é o nome do assembly | Procede (o mesmo do achado 7), corrigido | Mesma correção do achado 7. |
| 17 | empacotamento-compat | Protocol e Hid continuam empacotáveis: um `dotnet pack` da solução gera pacotes que duplicam as DLLs embutidas no `Brymen.Dmm` | Procede, corrigido | `IsPackable=false` por padrão no `Directory.Build.props` (o `Brymen.Dmm` e o hosting já definem `true`), e `WarnOnPackingNonPackableProject=false`, porque o Web SDK passaria a avisar ao pular a API. `dotnet pack BrymenDmm.slnx -c Release` gera só `Brymen.Dmm` e `Brymen.Dmm.Extensions.Hosting` (`.nupkg` e `.snupkg`), sem avisos. |

### 9.2 Mudanças na API pública

Os pacotes ainda não foram publicados (0.1.0), então as mudanças vão na mesma versão:

| Pacote | Mudança |
|---|---|
| `Brymen.Dmm` | Nova `interface IBrymenMeter`; `BrymenMeter : IBrymenMeter`. `ReadAsync` e `ReadLoggerImageAsync` podem lançar `INTERNAL`. A E/S nunca roda na thread de quem chama. |
| `Brymen.Dmm.Hid` | Internos: `HidReportDescriptor`, `HidReportLayout`, os construtores `LinuxHidrawDeviceProvider(string, string)`, `SysfsRoot`, `DevRoot` e `ReadReportLayout`. Novos: `SimulatedHidDevice.TypicalResponseLatency` e `CreateTypicalBm52x(SimulatedBm52xMeter?, TimeProvider?)`. O medidor padrão de `CreateBm52x()` passou a ser o de `FromCaptures()`, e o `new SimulatedHidDeviceProvider()` sem argumentos ganhou a latência típica. |
| `Brymen.Dmm.Extensions.Hosting` | `IBrymenMeterAccessor.Meter` é `IBrymenMeter?`, e `WaitForMeterAsync` devolve `Task<IBrymenMeter>` e termina com falha nos casos do achado 4. Novos: `IBrymenMeterAccessor.ReadingsAsync(CancellationToken)` e as sobrecargas `AddBrymenMeter(string name, ...)`. O serviço é um `IHostedLifecycleService`, e a mensagem do evento 1 do log ficou "Multímetro aberto no cabo {DevicePath} ({Product})." (o laço começa depois). Dependências a partir da 8.0.0 e da 10.0.0. |

### 9.3 Build e testes

```powershell
dotnet build BrymenDmm.slnx -c Release --no-incremental      # e também em Debug
dotnet test BrymenDmm.slnx -c Release
$env:BRYMEN_HW_TESTS = '1'; dotnet test tests/Brymen.Dmm.Tests -c Release --filter "FullyQualifiedName~HardwareTests"
```

| Projeto de teste | Aprovados | Falhas | Ignorados | Antes ([§8.1](#81-build-e-testes)) |
|---|---|---|---|---|
| `Brymen.Dmm.Protocol.Tests` | 336 | 0 | 0 | 336 |
| `Brymen.Dmm.Hid.Tests` | 128 | 0 | 12 | 126 |
| `Brymen.Dmm.Tests` (`net10.0` / `net8.0`) | 65 / 65 | 0 | 4 / 4 (hardware) | 60 / 60 |
| `Brymen.Dmm.Extensions.Hosting.Tests` (`net10.0` / `net8.0`) | 24 / 24 | 0 | 0 | 16 / 16 |
| `Brymen.Dmm.Api.Tests` | 174 | 0 | 0 | 174 |
| **Total** | **816** | **0** | **20** | 788 |

- Build Release com `--no-incremental` e build Debug: 0 avisos e 0 erros.
- Testes novos: Hid +2 (contagem zerada num link descartado; simuladores padrão iguais ao `OpenSimulatedAsync`),
  `Brymen.Dmm.Tests` +5 (operações que voltam antes da E/S bloqueante, provider e progresso que lançam viram
  `INTERNAL`, simulado do `OpenSimulatedAsync` igual ao do provider padrão, operações pelo `IBrymenMeter`) e hosting +8
  (fachada, serviço registrado antes que espera no `StartAsync`, `ReadingsAsync` compartilhado, fim na parada, descarte
  das mais antigas, dois medidores com nome, desistência sem `AutoReconnect`, descarte do contêiner durante a espera);
  4 testes do hosting foram ajustados ao contrato novo.
- A API não mudou: os 174 testes passam sem alteração.

### 9.4 Pacotes

`dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages` e o mesmo para `src/Brymen.Dmm.Extensions.Hosting`:
0 avisos.

| Pacote | Tamanho | SHA-256 |
|---|---|---|
| `Brymen.Dmm.0.1.0.nupkg` | 270 960 B (3 DLLs e 3 XML por alvo e o README de 8 946 B) | `17c8206bc4210c1da9d29066283bda8624d86be3e300a4966c4c6d3ebab11569` |
| `Brymen.Dmm.0.1.0.snupkg` | 91 000 B (6 PDBs) | — |
| `Brymen.Dmm.Extensions.Hosting.0.1.0.nupkg` | 61 971 B (DLL e XML por alvo e o README de 6 122 B) | `08c80fe5998cee55c42fff688a51e722ebdcc587cd1c64e9691345aa82a99fea` |
| `Brymen.Dmm.Extensions.Hosting.0.1.0.snupkg` | 34 066 B (2 PDBs) | — |

- Dependências do hosting no nuspec: `net10.0`: `Brymen.Dmm` 0.1.0 e os 4 `Microsoft.Extensions.*` 10.0.0; `net8.0`:
  `Brymen.Dmm` 0.1.0 e os 4 em 8.0.0. O `Brymen.Dmm` continua sem dependências.
- `FileVersionInfo`: `Product` `Brymen.Dmm` nas 6 DLLs do `Brymen.Dmm` e `Brymen.Dmm.Extensions.Hosting` nas 2 do
  hosting; `CompanyName` vazio; versão 0.1.0.0.
- Caminhos: nenhuma das 8 DLLs tem o caminho do repositório ou o nome do usuário; só os caminhos mapeados `/_/...`.
- O hosting ficou maior (45 915 → 61 971 B) por causa do `ReadingsAsync`, do ciclo de vida, do registro nomeado e da
  documentação XML deles.
- `dotnet pack BrymenDmm.slnx -c Release -o .../scratchpad/slnpack`: só os 4 arquivos dos dois pacotes, sem avisos.

### 9.5 Consumidores isolados

Os consumidores da [§8.3](#83-consumidores-isolados), em `.../scratchpad/consumers/`, foram recompilados do zero
(`bin` e `obj` apagados) contra os pacotes novos, com `NUGET_PACKAGES` em `.../scratchpad/nuget-cache` e as pastas
`brymen.*` apagadas antes do restore; o SHA-512 dos dois pacotes no cache conferiu com o de `artifacts/packages`. O
cache global do usuário continua sem pacotes `brymen.*`. Dois consumidores novos do hosting (`hosting-net8` e
`hosting-net10`, com o código em `shared-hosting/HostingChecks.cs`) referenciam o `Microsoft.Extensions.Hosting` e o
`Microsoft.Extensions.Options` no patch mais antigo da linha (8.0.0 e 10.0.0), direto.

| Consumidor | Runtime | Build | Resultado |
|---|---|---|---|
| (a) console `net8.0`, `Brymen.Dmm` | .NET 8.0.32 | 0 avisos | 13/13, as mesmas conferências da §8.3 (imagem idêntica ao `memory-dump-1.txt`, `FaultInjector`, `E9015`, reconexão) |
| (b) console `net10.0`, `Brymen.Dmm` | .NET 10.0.12 | 0 avisos | 13/13 |
| (c) `hosting-net8`, Options e Hosting 8.0.0 | .NET 8.0.32 | 0 avisos, sem `NU1605` | 8/8 |
| (d) `hosting-net10`, Options e Hosting 10.0.0 | .NET 10.0.12 | 0 avisos, sem `NU1605` | 8/8 |
| (e) o (d) publicado self-contained win-x64 com `PublishTrimmed=true` e `TrimmerSingleWarn=false` | 10.0.12 embutido | 0 avisos de trimming | 8/8 |

As 8 conferências do hosting: a partida com um serviço registrado antes do `AddBrymenMeter` que espera o medidor e lê
pela fachada no próprio `StartAsync` (≈ 200 ms, sem travar); `Meter` como fachada sem descarte; duas enumerações de
`ReadingsAsync` com as mesmas 5 leituras; a importação pela fachada (375 amostras); `InvalidOperationException` depois
da parada, com a fachada em `Disposed`; dois medidores com nome; e, sem cabo e sem `AutoReconnect`, a espera que termina
com `E9015`. O `PollInterval` vem da configuração em memória, pelo binding do pacote.

Exemplos, com o mesmo cache e o `samples/nuget.config`: o build Release dos dois deu 0 avisos;
`ConsoleSample --simulated --count 5` leu 5 leituras (0,287 → 0,090 V AC) e terminou com código 0; o
`WorkerSample --simulated --Brymen:PollInterval=00:00:00.2` rodou 4 s com 16 leituras e nenhum aviso, e o
`ReadingLogger`, agora registrado depois do `AddBrymenMeter`, recebeu a 1ª leitura (`0.287`).

### 9.6 Hardware real

BM525s em ACV, pontas abertas (0,002–0,010 V AC nesta sessão), e o BC-86X no Windows; software original fechado.

| Verificação | Resultado |
|---|---|
| `HardwareTests` de `tests/Brymen.Dmm.Tests` | 4/4 em `net10.0` e 4/4 em `net8.0`, em duas execuções |
| Abertura e leitura | 1ª leitura em 174–305 ms, a 2ª em 180–197 ms |
| Leitura contínua por 3 s / eventos por 2 s | 14–15 leituras (4,7–5,0/s) / 9 leituras |
| Importação | 95 blocos em 10,28–10,52 s, 0 retentativas, 0 recomeços; imagem idêntica à do `memory-dump-1.txt` |
| 1º `66` depois da memória | 2046 ms (traços), sem `W7012`; o seguinte, 194–217 ms |
| `WorkerSample` com o cabo, `PollInterval` 200 ms, 5 s | Abertura no `StartingAsync`, `ReadingLogger` com o estado `Connected` na partida, 20 leituras e nenhum aviso (22 na [§8.4](#84-hardware-real-com-os-pacotes), com a partida contada nos 5 s) |

Nenhum `W7012` nesta etapa. A troca de thread do `ThreadPoolHop` não mudou os tempos medidos com o cabo.

### 9.7 Publicação para Linux

`$env:TrimmerSingleWarn = 'false'; ./deploy/publish.ps1` (linux-arm64 e linux-x64, self-contained, single-file,
trimmed): **0 avisos** e 0 erros.

| Artefato | Executável | Pasta do publish | `tar.gz` | SHA-256 do `tar.gz` |
|---|---|---|---|---|
| `linux-arm64` (`PT_INTERP` `/lib/ld-linux-aarch64.so.1`) | 23 698 983 B | 23 831 504 B | 9 603 685 B, 25 entradas | `71f97a6c333e71103d168add2ffc9f8205005c7072c0e4fba178e7c0dcf240b5` |
| `linux-x64` (`PT_INTERP` `/lib64/ld-linux-x86-64.so.2`) | 24 150 271 B | 24 282 792 B | 9 871 812 B, 25 entradas | `f2f6b65e6ae7f21bc0ae21732835f556f8036547d018af396112f12b12d2db7a` |

Observação fora dos achados: nos dois executáveis da API, o único caminho local que sobrou é o do PDB do
`Microsoft.OpenApi.dll`, que o ILLink regrava ao ligar o assembly (`.../obj/Release/net10.0/<rid>/linked/Microsoft.OpenApi.pdb`).
O `PathMap` do compilador não chega nele, e ele não está nos pacotes da biblioteca; os assemblies `Brymen.*` do
executável já saem com `/_/`.

### 9.8 Não validado nesta etapa

- O `LinuxHidrawDevice` novo num Linux real: o teste do uhid foi atualizado, mas só roda no Linux com
  `BRYMEN_UHID_TESTS=1`. No Windows, a troca de thread do `BrymenMeter` foi conferida com um `IHidDevice` síncrono.
- O registro nomeado com um contêiner de terceiros sem suporte a serviços com chave.
- Parada graciosa (Ctrl+C) do `WorkerSample` com o cabo: o processo foi encerrado à força, como na §8.7.
- Publicação num feed: licença, `RepositoryUrl`, `Authors`, `Company` e `Copyright` continuam a definir pelo autor.

## 10. Linux arm64 real (Orange Pi 5)

Validação de 2026-10-23, a primeira num Linux real: build, testes, pacote, serviço systemd, API pela LAN, reconexão do
cabo e a biblioteca NuGet num Orange Pi 5 com o BC-86X e o BM525s. Só `52 66`, `88`, `89` e `8A` foram enviados, e o
cabo nunca teve dois usuários ao mesmo tempo nas etapas desta validação: o serviço ficou parado durante os testes de
hardware fora dele (conferido com `fuser /dev/hidraw1`).

### 10.1 Ambiente

| Item | Valor |
|---|---|
| Placa | Orange Pi 5 (RK3588, 8 núcleos aarch64, 3,7 GB de RAM), `orangepi5b`, cartão de 14 GB (7,1 GB livres) |
| Sistema | Ubuntu 22.04.5 LTS, kernel 5.10.110-rockchip-rk3588, glibc 2.35, systemd 249 |
| HID no kernel | `CONFIG_HID`, `CONFIG_HIDRAW`, `CONFIG_UHID` e `CONFIG_USB_HID` = `y` (`hid.ko` e `usbhid.ko` em `modules.builtin`); `236 hidraw` em `/proc/devices` desde o boot |
| .NET no Pi | SDK 10.0.302 e 9.0.316; runtimes 10.0.10 e 9.0.18; nenhum runtime 8 |
| Docker | 29.6.2 (containerd snapshotter), sem os plugins `buildx` e `compose`; `docker.service` desabilitado e ativado pelo `docker.socket` |
| Cabo | Barramento 7, porta `7-1`, interface `7-1:1.0` (`usbhid`), HID `0003:0820:0001.0002` → `/dev/hidraw1`; `Brymen` / `Superior DMM`, bcdDevice 0x8603, serial `£⑉` (os mesmos bytes `A3004924` do Windows) |
| Outro HID | `/dev/hidraw0`: QDtech MPI7003 (`0484:5750`, porta `4-1`), a tela do usuário; nada foi escrito nele |
| Medidor | BM525s em **DCV**, pontas abertas (`-0.000 V DC`, `AUTO`, sem display secundário); memória com a sessão do `memory-dump-1.txt` |
| Máquina de desenvolvimento | Windows 11 Pro 10.0.26300, SDK 10.0.401, na mesma LAN (192.168.100.69 → 192.168.100.116) |
| Acesso | `ssh`/`scp` com chave e `BatchMode=yes`; trabalho em `/root/brymen-validation/`, removido no fim ([§10.9](#109-limpeza-e-linha-de-base)) |

O Pi não tem RTC: ele subiu às 10:06:41 com o relógio 60 586 s atrasado, e o chrony o acertou às 10:07:00. Por isso os
nós e os serviços criados no boot aparecem com a data de 22/10, 17:17.

### 10.2 Build e testes no Pi

```bash
# no Git Bash do PC: 422 kB, 224 entradas
tar -czf brymen-src.tar.gz --exclude=./artifacts --exclude=bin --exclude=obj --exclude=.vs \
    --exclude='./docs/protocol/png*' --exclude=./.claude --exclude=./Bs82x-52xV6009sA.exe .
# no Pi, em /root/brymen-validation/src
dotnet build BrymenDmm.slnx -c Release
dotnet test tests/<projeto> -c Release --no-build -f net10.0                                   # (a), um por projeto
BRYMEN_HW_TESTS=1 dotnet test tests/Brymen.Dmm.Hid.Tests -c Release --no-build -f net10.0 \
    --filter "FullyQualifiedName~HardwareTests"                                                # (b)
BRYMEN_HW_TESTS=1 dotnet test tests/Brymen.Dmm.Tests -c Release --no-build -f net10.0 \
    --filter "FullyQualifiedName~HardwareTests"                                                # (b)
BRYMEN_UHID_TESTS=1 dotnet test tests/Brymen.Dmm.Hid.Tests -c Release --no-build -f net10.0 \
    --filter "FullyQualifiedName~LinuxUhidTests"                                               # (c), como root
```

O `NUGET_PACKAGES` e os caches HTTP do NuGet ficaram em `/root/brymen-validation/`, sem tocar no cache do usuário.

- **Build**: 0 avisos e 0 erros, em 65 s (com o restore do zero). O `global.json` (10.0.100 com `latestFeature`) aceitou
  o SDK 10.0.302.
- A condição `src/` do `Directory.Build.props` funciona com `/`: o `Brymen.Dmm.Hid.xml` foi gerado, e o dos testes não.
  O `PathMap` também: o `Brymen.Dmm.Hid.dll` tem `/_/` e não tem `brymen-validation`.

| Execução | Projeto | Aprovados | Falhas | Ignorados | Tempo |
|---|---|---|---|---|---|
| (a) normal | `Brymen.Dmm.Protocol.Tests` | 336 | 0 | 0 | 3,8 s |
| (a) normal | `Brymen.Dmm.Hid.Tests` | 128 | 0 | 12 (7 de hardware, 5 de uhid) | 9,2 s |
| (a) normal | `Brymen.Dmm.Tests` (`net10.0`) | 65 | 0 | 4 (hardware) | 8,9 s |
| (a) normal | `Brymen.Dmm.Extensions.Hosting.Tests` (`net10.0`) | 24 | 0 | 0 | 5,2 s |
| (a) normal | `Brymen.Dmm.Api.Tests` | 174 | 0 | 0 | 22,0 s |
| (a) | **Total** | **727** | **0** | **16** | 49 s |
| (b) `BRYMEN_HW_TESTS=1` | `Brymen.Dmm.Hid.Tests`, `HardwareTests` | 7 | 0 | 0 | 7,0 s |
| (b) `BRYMEN_HW_TESTS=1` | `Brymen.Dmm.Tests`, `HardwareTests` (`net10.0`) | 4 | 0 | 0 | 21,1 s |
| (c) `BRYMEN_UHID_TESTS=1`, root | `Brymen.Dmm.Hid.Tests`, `LinuxUhidTests` | 5 | 0 | 0 | 5,0 s (3 execuções, todas 5/5) |

Os tempos são do processo `dotnet test` inteiro. Os alvos `net8.0` compilam, mas não rodam no Pi (sem runtime 8). O
`net8.0` foi validado com um consumidor self-contained ([§10.7](#107-biblioteca-nuget-no-arm)).

Na reverificação independente ([§10.12](#1012-reverificação-independente)), a mesma rodada deu os mesmos números, com
uma exceção: o `Brymen.Dmm.Api.Tests` falhou uma vez em `LoggerRetentionTests.Only_the_newest_imports_are_kept_on_disk_and_in_memory`
(173/1/0). O teste é instável no Pi por causa de uma corrida no código ([§10.10](#1010-defeitos-e-observações), defeito 5),
e os 174/0/0 acima não garantem que ele passe sempre.

Detalhes dos testes de hardware pelo hidraw, os primeiros num Linux:

| Teste | Resultado |
|---|---|
| `EnumeratesTheCable` | `/dev/hidraw1`, 0x8603, `Brymen` / `Superior DMM`; descriptor com `InputReportLength` 8, `OutputReportLength` 3 e sem report IDs |
| `CableIsFoundAndOpenedByPathWithoutSysfs` (só Linux; nunca tinha rodado) | Com um sysfs vazio, o nó foi identificado e aberto pelos ioctls (`HIDIOCGRAWINFO`, `NAME`, `UNIQ`): `Brymen Superior DMM` |
| `RealTimeAndMemoryFramesThroughBu86xLink` | `66` em 227 ms, `88` em 418 ms, `89` em 102 ms, `8A` em 159 ms (igual ao `89`), `66` em 495 ms |
| `DisposeDuringAPendingReadFailsPromptlyAndTheCableReopens` | A leitura pendente falhou 4 ms depois do `Dispose` |
| `ReadTimeout...`, `ZeroTimeout...`, `LateReports...` | Aprovados; `66` depois do cancelamento em 128 ms |
| `Opens_the_real_cable_and_reads_the_meter` | 1ª leitura em 329 ms, 2ª em 184 ms (`-0.000 V DC`) |
| `Imports_the_memory_identical_to_the_capture...` | 95 blocos em 10 711 ms, 0 retentativas, 0 recomeços, 375 amostras; imagem idêntica; 1º `66` depois da memória em 2043 ms (traços), o seguinte em 143 ms |
| `Reads_continuously_for_3_seconds` / `Event_loop...` | 14 leituras em 3 s (4,7/s) / 10 leituras em 2 s |
| `LinuxUhidTests` | O `/dev/hidraw2` virtual foi criado e removido em cada teste. Na remoção a quente, a leitura pendente falhou em 3 ms e a escrita com `ENODEV`; o nó fora da enumeração foi aberto pelo ioctl. O `hidraw0` e o `hidraw1` não mudaram |

### 10.3 Pacote e instalação

```powershell
./deploy/publish.ps1 -Rid linux-arm64                       # no PC
```
```bash
scp ... artifacts/brymen-dmm-0.1.0-linux-arm64.tar.gz root@192.168.100.116:/root/brymen-validation/
bash pkg/brymen-dmm-0.1.0-linux-arm64/deploy/linux/install.sh /root/brymen-validation/brymen-dmm-0.1.0-linux-arm64.tar.gz
```

- Publish: 0 avisos, em 35,6 s; executável de 23 698 983 B; `tar.gz` de 9 603 710 B, SHA-256
  `aaf7c28a67fdbac1e649339fe161863d0172d6ff84f43987bc202a216ebcb78d`, 25 entradas, dono 0/0, executável e scripts com
  0755. O SHA-256 conferiu no Pi.
- `install.sh`: código 0, em 2,4 s. Nenhum script de `deploy/` precisou de correção.

| Verificação | Resultado |
|---|---|
| Usuário e grupo | `brymen-dmm`, uid 999, gid 998, home `/var/lib/brymen-dmm`, shell `/usr/sbin/nologin` |
| Arquivos | `/opt/brymen-dmm` (root, executável 0755), `/var/lib/brymen-dmm` 0750 `brymen-dmm`, `/etc/brymen-dmm/brymen-dmm.env` 0640, unit e regra 0644 |
| `udevadm test` no `hidraw1` | `99-brymen-bu86x.rules:34 GROUP 998`, `MODE 0660`, `LINK 'brymen-bu86x'`; `:39 RUN 'uaccess'`; `BRYMEN_BU86X=1`, `TAGS=:seat:uaccess:` |
| `udevadm info` | `S: brymen-bu86x`, `DEVLINKS=/dev/brymen-bu86x`; o `--attribute-walk` mostra `idVendor 0820` e `idProduct 0001` |
| Nó e symlink | `/dev/brymen-bu86x -> hidraw1`; `crw-rw----+ root brymen-dmm 236,1`; a ACL do `uaccess` dá `rw` ao usuário `orangepi` da sessão local |
| `hidraw0` | `crw------- root root 236,0` antes e depois, sem ACL, sem `BRYMEN_BU86X` e sem symlink. Só a data mudou (17:17 → 10:20): o `udevadm trigger --subsystem-match=hidraw --action=change` do `install.sh` manda um `change` a todos os hidraw ([§10.10](#1010-defeitos-e-observações); corrigido na [§10.13](#1013-correções)) |
| `/etc/modules-load.d/brymen-dmm.conf` | O comentário e a linha `hid`. Com o HID embutido, o `modprobe hid` não fez nada, e não houve aviso |
| `systemd-analyze verify` | Código 0, nenhuma mensagem da `brymen-dmm.service` (só avisos de uma unit do usuário, `phaserapi.service`, que não foi tocada); `systemd-analyze security`: exposição 3,2, OK |
| `Type=notify` | `Starting` às 10:20:48,914, `Now listening` às 10:20:50,110, `Application started` às 10:20:50,115 e `Started` do systemd às 10:20:50,122: o `active (running)` só veio depois do `READY=1`, 1,21 s depois do início. `NotifyAccess=main`, `NRestarts=0` |
| Journal | Na partida: `DataDirectory`, `Cabo BC-86X aberto: /dev/hidraw1` e `Now listening on: http://0.0.0.0:5080`; `journalctl -p warning` sem entradas |
| `DeviceAllow=char-hidraw` | O hidraw abriu na partida e depois da reconexão ([§10.6](#106-reconexão-do-cabo)), sem `EPERM`. Com o HID embutido, o caso do módulo carregado depois da unit não acontece neste kernel |

### 10.4 API pela LAN

Todas as chamadas saíram do PC Windows para `http://192.168.100.116:5080`, o serviço instalado.

| Verificação | Resultado |
|---|---|
| `GET /api/v1/meter` | 200: `linkEnabled: true`, `state: connected`, `transport: hid`, `deviceOpen: true`; dispositivo `/dev/hidraw1`, `0x0820`/`0x0001`, `0x8603`, `Brymen` / `Superior DMM`; `lastError: null`, `pollIntervalMs: 200` |
| `GET /api/v1/readings/latest` | 200: `text "-0.000"`, `negative: true`, `value 0`, `unit V`, `mode DC`, `secondary: null`, `flags ["AUTO"]`, `rawHex 00002080BFAFAFAF00000000000000002007005252525210000000`. É o que o medidor mostrava (DCV, pontas abertas) |
| `/health/live` e `/health/ready` | 200 e 200 `Healthy` |
| SSE, 4 s (`curl -N`) | 21 eventos `reading`, `id` 426 a 446, sem lacunas |
| SignalR: `samples/Brymen.Dmm.SignalRClient` no PC, 15 s | `GetStatus` (`connected`, `/dev/hidraw1`, `hid`) e `GetLatest` na conexão; **72 eventos `reading`** (`sequence` 583 a 654) em 14,16 s, **5,01 leituras/s**, **nenhuma lacuna**; intervalo de 184 a 376 ms, média de 199 ms |
| Aquisição `fullSpeed`, 10 s | 51 amostras (10,2 s); `mainRange {min 0, max 0, unit V, mode DC}`; backup `~BDMDL1.csv` com 52 linhas, dono `brymen-dmm`, 0640 |
| Export CSV | 52 linhas, cabeçalho exato, CRLF; de `1,  0:00:00.0,-0.000,V,DC,,,` a `51,  0:00:10.0,-0.000,V,DC,,,`. O `?format=extended` acrescenta `Timestamp,MainSiValue,SubSiValue,Flags,Comparator` |
| Comparador | `Hi 0.2 / Lo 0.1` → `Low`; `Hi 0.1 / Lo -0.1` → `Pass`; `Hi -0.05 / Lo -0.1` → `High`; `Hi ≤ Lo` → 400 `W7010`; `enabled: false` → `comparator: null`. Gravado em `settings.json` (dono `brymen-dmm`); no fim, os valores padrão foram restaurados |
| Importação | `202` + `Location`; durante a leitura, `readings/latest` e a imagem → 409 `CONFLICT`; `completed` 10,67 s depois do `createdAt` (10,93 s medidos no PC); 95/95 blocos, `totalBytes` 2272, 0 retentativas, 0 recomeços, `modelId 1`, `warnings []` |
| Imagem (`GET .../image`) | 200, 2280 B, `application/octet-stream`, **idêntica byte a byte** ao `memory-dump-1.txt` (bytes 1 a 8 dos 3 primeiros relatórios dos 95 quadros); SHA-256 `dbce1fe399ae2740dc66eb261457542e27a709cf347718b267a03c90c5207e2f`. A memória não mudou desde a captura |
| Sessões | 1 sessão, `A0_0080` → `A0_095A`, `function 1`, `select 0`, `status 128`, dual, ACV + Hz, 0,05 s, 2250 bytes, 375 amostras; CSV da sessão com 376 linhas, de `1,  0:00:00.0,0.013,V,AC,0.000,Hz,` a `375,  0:00:18.7,0.006,V,AC,0.000,Hz,` |
| Depois da importação | `readings/latest` → 503 por ≈ 2 s (sem leitura atual, como na [§3.4](#34-observações)), depois 200; nenhum `W7012` no journal |
| Unlink e link | `PUT {enabled:false}` → 200 `unlinked`, `deviceOpen: false`; `readings/latest` → 409 `E9002`; `/health/ready` → 503; o `fuser` não mostra ninguém no `/dev/hidraw1`. `PUT {enabled:true}` → 200 `connected`; 1ª leitura 611 ms depois; `/health/ready` → 200 |
| Painel `/` | 200, `text/html`, 3804 B, `<title>Brymen DMM</title>`; `css/app.css` (4612 B) e `js/app.js` (11 908 B) com 200 |
| `GET /openapi/v1.json` | 200, 49 896 B, 18 rotas `/api/v1`, 74 `$ref`; esquemas `ReadingDto`, `MeterStatusDto`, `ImportJobDto`, `AcquisitionSampleDto`, `LoggerSampleDto` e `ProblemDetails` |
| Erros | 404 numa rota inexistente, 405 em `DELETE /api/v1/meter`, 403 `FORBIDDEN` num `POST` com `Origin: http://intruso.test` |

Consumo do processo, amostrado no Pi a cada 1 s (`/proc/<pid>/status`, `/proc/<pid>/stat` e
`systemctl show -p MemoryCurrent`; CPU em % de um núcleo):

| Situação | RSS | CPU | `MemoryCurrent` | Threads |
|---|---|---|---|---|
| Polling a 200 ms, logo depois da partida (15 s) | 53,6 MiB (`ps`: 54 872 KB; `top`: RES 54 872, SHR 40 392) | média 1,0 %, máx. 1,9 % | 34,8 MiB | 27 |
| Durante a importação (18 s) | 70,3 a 72,6 MiB | média 7,2 %, máx. 22,3 % | média 49,7 MiB, máx. 50,3 MiB | 28 |
| Polling depois da importação e da reconexão (10 s) | 67,2 MiB | média 3,4 %, máx. 12,6 % | 44,8 MiB | — |

### 10.5 Parada e reinício

| Operação | Resultado |
|---|---|
| `systemctl restart` | 1465 ms até voltar (parada e partida, com o `READY=1`) |
| `systemctl stop` sem clientes | 140 ms (`Stopping` → `Deactivated` em 130 ms); `Result=success`, código 0 |
| `systemctl start` | De 1310 a 1489 ms, em 4 partidas |
| `systemctl stop` com um SSE e o cliente SignalR conectados | 187 ms (`Stopping` → `Deactivated` em 171 ms), `Result=success`. O `curl` do SSE terminou com código 0; o cliente SignalR registrou a conexão perdida, reconectou sozinho depois do `start` e voltou a receber leituras |

Nenhuma parada chegou perto do `TimeoutStopSec` de 45 s, e não houve `SIGKILL`.

### 10.6 Reconexão do cabo

O cabo foi "removido" e "religado" com o unbind e o bind da interface no driver `usbhid`, com o serviço rodando. Antes,
a interface `7-1:1.0` foi conferida: dispositivo `7-1` com `idVendor 0820`, `idProduct 0001`, `product Superior DMM` e
`bInterfaceClass 03`, HID `0003:0820:0001.0002` e `hidraw1`. O `hidraw0` fica na `4-1:1.0`, em outro barramento.

```bash
echo -n 7-1:1.0 > /sys/bus/usb/drivers/usbhid/unbind      # 10:26:10,139
echo -n 7-1:1.0 > /sys/bus/usb/drivers/usbhid/bind        # 10:26:18,413
```

| Momento | Resultado |
|---|---|
| Unbind | O `/dev/hidraw1` e o `/dev/brymen-bu86x` somem. Journal, os dois com prioridade warning: 10:26:10,145 `[W7012] Nenhum dado recebido ... (Dispositivo /dev/hidraw1 desconectado.)` e 10:26:10,161 `[E9015] Cabo adaptador USB não encontrado. Nenhum BC-86X (VID 0820, PID 0001) em /dev/brymen-bu86x.` |
| Cabo ausente (8 s) | `GET /api/v1/meter`: `reconnecting`, `deviceOpen: false`, `lastError` `E9015` renovado a cada 2 s (`ReconnectDelay`); `readings/latest` → 503 |
| Bind | O udev recria o `hidraw1` (236,1, `root:brymen-dmm`, 0660, ACL) e o `/dev/brymen-bu86x -> hidraw1`. Journal: 10:26:20,218 `Cabo BC-86X aberto: /dev/hidraw1`, 1,8 s depois do bind, na tentativa seguinte |
| Depois | 1ª leitura 2301 ms depois do bind, sem intervenção; `connected`, `deviceOpen: true`. O `lastError` guarda o `E9015` (histórico, por projeto). O `hidraw0` ficou igual (`ls -l --full-time` antes e depois) |

### 10.7 Biblioteca NuGet no ARM

```powershell
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages                       # no PC
dotnet pack src/Brymen.Dmm.Extensions.Hosting -c Release -o artifacts/packages
```

Os dois packs deram 0 avisos. Pacotes, copiados para um feed local no Pi (`/root/brymen-validation/feed`):

- `Brymen.Dmm.0.1.0.nupkg`: 270 960 B, SHA-256 `138997bf594bb084e8468f02748c2eff438e87b178a0ba8db966c6bd7486fb32`;
- `Brymen.Dmm.Extensions.Hosting.0.1.0.nupkg`: 61 973 B, SHA-256
  `5e298ade18524b69fcd5bb4331ad0ab3a4181ae19d30d2bdbaf07aad0841d1c4`.

O consumidor é um console fora da solução, com `Directory.Build.props` e `Directory.Build.targets` vazios,
`TreatWarningsAsErrors`, o analisador de trimming e um `nuget.config` com `<clear />`, o feed local e o nuget.org (com
`packageSourceMapping`). O `NUGET_PACKAGES` ficou isolado em `/root/brymen-validation/nuget-cache`, e o SHA-512 do
pacote no cache conferiu com o do feed. Antes de cada execução, o serviço foi parado (`systemctl stop brymen-dmm`). O
consumidor faz 6 conferências com o cabo real:

1. um cabo enumerado;
2. `OpenAsync` com `DevicePath=/dev/brymen-bu86x`, em `Connected`;
3. pelo menos 20 leituras de `ReadContinuouslyAsync` em 5 s;
4. a imagem de `ReadLoggerImageAsync` idêntica ao `memory-dump-1.txt`, lido pelo próprio consumidor (bytes 1 a 8 dos 3
   primeiros relatórios, com o checksum conferido), sem o parser da biblioteca;
5. o 1º `66` depois da memória;
6. nenhum erro.

| Consumidor | Build | Resultado com o cabo |
|---|---|---|
| `net10.0`, compilado no Pi (SDK 10.0.302), .NET 10.0.10 Arm64 | 0 avisos | 6/6. `FindDevices` → `/dev/hidraw1`; `OpenAsync` em 68 ms; 1ª leitura em 364 ms; **25 leituras em 5,02 s** (5,0/s); importação de 95 blocos em 10 677 ms, `TotalBytes` 2272, 0 retentativas, 0 recomeços, 0 avisos, 1 sessão e 375 amostras; **imagem idêntica** (0 bytes diferentes, SHA-256 `DBCE1F…7E2F`); 1º `66` depois dela em 2051 ms (traços), o seguinte em 64 ms; `LastError` nulo |
| `net8.0`, publicado no PC self-contained para `linux-arm64` (runtime 8.0.31 embutido, 190 arquivos, 78 MB, ELF aarch64) | 0 avisos | 6/6. .NET 8.0.31 Arm64; `OpenAsync` em 66 ms; 1ª leitura em 354 ms; 24 leituras em 5,02 s (4,8/s); importação em 10 637 ms, 0 retentativas; imagem idêntica; 1º `66` depois dela em 2048 ms, o seguinte em 256 ms; nenhum erro |
| `samples/Brymen.Dmm.WorkerSample`, compilado no Pi contra os pacotes do feed (`samples/nuget.config`) | 0 avisos | Com o `appsettings.json` (`PollInterval` de 1 s): 5 leituras em 5 s. Com `--Brymen:PollInterval=00:00:00.2`: abertura no `StartingAsync` (“Multímetro aberto no cabo /dev/hidraw1 (Superior DMM).”), `ReadingLogger` com `Connected` na partida, **22 leituras em 5 s**, nenhum aviso. O `SIGINT` no fim fez a **parada graciosa** (“Application is shutting down...”, “Multímetro fechado.”, código 0), que estava pendente na [§8.7](#87-não-validado-nesta-etapa) e na [§9.8](#98-não-validado-nesta-etapa) |

Depois, o serviço foi religado (`systemctl start brymen-dmm`) e voltou a ler: `connected`, `lastError: null`, leituras
novas.

### 10.8 Docker: interrompido por decisão do usuário

O usuário mandou **abortar o teste com Docker** durante o build, por não ser necessário: o serviço já roda direto no Pi.
O aviso de aborto não chegou ao agente de execução em andamento. Ele abriu uma segunda cópia desse agente, que fez a
limpeza em paralelo; veja o incidente abaixo. Por isso o contêiner chegou a rodar uma vez com o cabo, mas o nó renomeado
e o perfil `Simulated` (porta 5092) ficaram sem teste. A sequência foi esta:

- O Docker do Pi não tem o plugin `buildx`. O comando `DOCKER_BUILDKIT=0 docker build --build-arg TARGETARCH=arm64
  --build-arg BUILDPLATFORM=linux/arm64 .` falhou no passo 2/26 (`FROM --platform=$BUILDPLATFORM ...`) com
  `failed to parse platform : "" is an invalid OS component`. O builder legado só substitui num `FROM` os `ARG`
  declarados antes do primeiro `FROM`, e o `Dockerfile` declara ali só o `DOTNET_VERSION`. Ele depende do `BUILDPLATFORM`
  automático do BuildKit, e o `--build-arg` não cria a declaração ([§10.10](#1010-defeitos-e-observações)).
- Uma cópia temporária do `Dockerfile`, fora do repositório, com `ARG BUILDPLATFORM=linux/arm64` no topo, terminou com
  código 0 em 424 s: restore em 25,9 s e publish trimmed `linux-arm64` sem avisos dentro da `sdk:10.0`. A imagem
  `brymen-dmm:validation` é arm64, com o usuário 1654, 236 MB em disco e 55,3 MB de conteúdo. O `TARGETARCH` passado por
  `--build-arg` funciona, porque ele é declarado no estágio de build.
- **Contêiner com o cabo** (agente de execução original, que não recebeu o aborto): o build terminou às 10:38:26. O
  `brymen-dmm` foi parado e o `fuser` confirmou o cabo livre. O contêiner `brymen-val-hid` subiu às 10:38:42 na porta
  5091, com `--device /dev/hidraw1` e `--group-add 998`. Resultados:
  - `/health/live` em 2,2 s;
  - `meter` com `connected`, `hid` e `deviceOpen`;
  - `readings/latest` com 200 e `-0.000 V DC`;
  - `/health/ready` com 200 e o `HEALTHCHECK` como `healthy`;
  - uid 1654 com o grupo 998, e `/data` de `app:app`;
  - no `docker stats`, 3,7 % de CPU e 31,4 MiB.
- **Incidente: dois agentes no Pi ao mesmo tempo, sem uso simultâneo do cabo.** A cópia do agente aberta pelo aviso de
  aborto fez a limpeza (`docker rm -f`) às 10:39:06, durante o `docker stop` do original. Em seguida, o `docker run` do
  `brymen-val-ren` (nó renomeado, `--device /dev/hidraw1:/dev/bu86x`) falhou porque a imagem já tinha sido apagada. O
  laço `until curl .../5091/health/live` que ficou no Pi foi encerrado pelo próprio agente original, com `pkill`, por
  volta das 10:42; não terminou sozinho. O cabo nunca teve dois processos: o serviço ficou parado durante o contêiner e
  foi religado às 10:40:29, depois que o contêiner saiu e o `fuser` mostrou o cabo livre. Nenhuma sessão estranha a esta
  validação acessou o Pi.
- **Limpeza**: foram removidos o contêiner, a imagem e as 23 camadas intermediárias, as bases
  `mcr.microsoft.com/dotnet/sdk:10.0` e `runtime-deps:10.0` (baixadas só para isso), o volume anônimo do `VOLUME /data`
  (criado às 10:38:42), o `Dockerfile` temporário e o script. O `docker builder prune -f` não tinha o que apagar: o
  builder legado não usa o cache do BuildKit. No fim, o `docker system df` mostra 0 imagens, 0 contêineres, 0 volumes e
  0 B de cache, como antes.

### 10.9 Limpeza e linha de base

| Verificação | Resultado |
|---|---|
| `ss -ltnp` | As portas da linha de base (22 sshd, 80 nginx, 111 rpcbind, 53 systemd-resolved, 5037/5555 adbd, 5432 postgres, 6001 PhaserAPI) e só a **5080** (`Brymen.Dmm.Api`) a mais; nada de 5090 a 5099 |
| Serviços | `nginx`, `postgresql` (`postgresql@14-main`), `phaserapi`, `ssh`, `rpcbind`, `systemd-resolved` e `adbd` ativos, com `NRestarts=0` e a mesma hora de ativação do boot; nenhum foi parado, reiniciado ou reconfigurado |
| Docker | Nenhum contêiner nem imagem. O `docker.service` estava inativo (só o `docker.socket`) e foi ativado às 10:12:27, 1 s depois de um login SSH às 10:12:26: muito provavelmente pelo `docker ps -a` do 1º diagnóstico desta validação. Ele ficou rodando, porque a regra proíbe parar o daemon. Para voltar ao estado anterior, use `systemctl stop docker.service` (o `docker.socket` continua ativo) |
| `/dev/hidraw0` | `crw------- root root`, sem ACL, durante toda a validação |
| Removido | `/root/brymen-validation` (580 MB: fontes, builds, caches do NuGet, consumidores, pacotes e logs, que estão guardados no PC); os temporários dos testes em `/tmp` (`brymen-*-tests`, `NuGetScratchroot`, `.dotnet` e as pastas de trabalho do `dotnet`); `/root/.nuget`, criado pelo 1º restore; e os arquivos novos do CLI em `/root/.dotnet` (`sdk-advertising`, a sentinela e o cache de CRLs gravado pelos restores, que se recria sozinho). Espaço livre de volta a 7,1 GB. Duas pastas vazias tinham ficado para trás: `/root/.dotnet/corefx/cryptography/crls` (criada pelo 1º restore às 10:16:12) e `/root/.config/procps` (criada pelo `top` da medição de consumo às 10:22:08). A reverificação as removeu às 11:05 ([§10.12](#1012-reverificação-independente)) |
| Mantido (o usuário decide) | O serviço instalado e habilitado: `/opt/brymen-dmm`, `/etc/systemd/system/brymen-dmm.service`, `/etc/udev/rules.d/99-brymen-bu86x.rules`, `/etc/modules-load.d/brymen-dmm.conf`, `/etc/brymen-dmm/brymen-dmm.env`, o usuário e o grupo `brymen-dmm`, e os dados em `/var/lib/brymen-dmm` (`settings.json` com o comparador padrão, `backup/~BDMDL1.csv` da aquisição de 10 s e `imports/` com a importação desta validação). Para remover: `sudo /opt/brymen-dmm/uninstall.sh` |

### 10.10 Defeitos e observações

| # | Arquivo | Achado | Evidência |
|---|---|---|---|
| 1 | `Dockerfile` (linhas 14 a 17) | O build falha sem o BuildKit (Docker sem o plugin `buildx`, ou com `DOCKER_BUILDKIT=0`), mesmo com `--build-arg BUILDPLATFORM=...`, porque o `BUILDPLATFORM` do `FROM --platform=$BUILDPLATFORM` não está declarado no escopo global | Passo 2/26: `failed to parse platform : "" is an invalid OS component`. Com `ARG BUILDPLATFORM=linux/arm64` antes do 1º `FROM`, numa cópia temporária, o build passou. O README só documenta o `buildx` |
| 2 | `deploy/linux/install.sh` (linha 198) | O `udevadm trigger --subsystem-match=hidraw --action=change` manda um evento `change` a todos os hidraw da máquina, e não só ao do BC-86X; as regras dos outros dispositivos rodam de novo | O `/dev/hidraw0` (a tela do usuário) recebeu o evento: a data do nó mudou de 17:17 para 10:20, mas o modo, o dono e o grupo ficaram iguais. Gravidade baixa |
| 3 | — (observação) | O `GET /js/app.js` com `Accept-Encoding: br` volta sem compressão (11 908 B): os `.br` e `.gz` do `wwwroot` vão no pacote, mas o `UseStaticFiles` não os serve | Coerente com a [§2](#2-publicação), em que o `staticwebassets.endpoints.json` não é usado. Não é defeito |
| 4 | — (observação) | O unlink deliberado registra no journal o health check `Unhealthy` (“Cabo BC-86X fechado.”) com a prioridade `err` | O mesmo `fail` esperado da [§8.5](#85-regressão-da-api-com-o-cabo-real) |
| 5 | `src/Brymen.Dmm.Api/Logger/LoggerImportService.cs` (linhas 137 e 164); `tests/Brymen.Dmm.Api.Tests/LoggerRetentionTests.cs` (linha 46) | Corrida: o `job.Complete(...)` publica o estado `completed` antes do `ApplyRetention()`, que só roda no `finally`, depois da liberação do acesso exclusivo (`await using`). Um cliente que vê `completed` e lista as importações logo em seguida ainda encontra uma a mais que o `Logger:ImportRetention`. O teste da retenção faz exatamente isso e falha de vez em quando no Pi | Reverificação ([§10.12](#1012-reverificação-independente)): 1ª rodada do `Brymen.Dmm.Api.Tests` com 173/1/0; esperado `[id, cccc…]`, obtido `[id, dddd…, cccc…]`. Só o teste, 6 vezes: 1 falha. A 2ª rodada inteira deu 174/0/0. Gravidade baixa: o efeito no serviço é só uma lista com um item a mais por instantes |
| 6 | — (observação) | Um `W7012` isolado fora dos testes: às 10:42:20,830, com o serviço religado desde 10:40:29, `[W7012] ... (Sem resposta do medidor ao comando 52 66.)`; o cabo foi reaberto 71 ms depois e as leituras seguiram. O `lastError` ficou com esse `W7012` até a partida das 11:00:52 da reverificação | Coincide com a limpeza do Pi: um login SSH do PC às 10:42:19,8, e o `/root/.dotnet` e o `/root` com data de 10:42:20 (remoção do `/root/brymen-validation`). A causa não foi estabelecida. Não houve evento USB no kernel. O erro não se repetiu em ≈ 25 min de leitura na reverificação, nem durante a remoção de 393 MB às 11:04:31 |

O defeito 5 é o único em `src/`/`tests/`. Ele não apareceu na rodada original e só foi visto na reverificação. No repositório, só este relatório mudou; o `artifacts/` (publish `linux-arm64` e os dois pacotes) foi gerado de novo no PC.

Os defeitos 1, 2 e 5 e a observação 3 foram corrigidos depois, no PC e no Pi ([§10.13](#1013-correções)). As
observações 4 e 6 não pedem mudança.

### 10.11 Pendências

- Docker no Linux real, interrompido por decisão do usuário: o contêiner rodou uma vez com o cabo e o `HEALTHCHECK`
  ([§10.8](#108-docker-interrompido-por-decisão-do-usuário)), mas ficaram sem teste o nó renomeado no contêiner, o
  perfil `Simulated` e o `docker-compose.yml` (o Pi também não tem o plugin `compose`). O defeito 1 foi corrigido e
  conferido no Docker do PC ([§10.13](#1013-correções)); o `Dockerfile` corrigido não foi compilado no Pi.
- O `DeviceAllow=char-hidraw` com o HID como módulo carregado depois da unit (Debian e Ubuntu genéricos): neste kernel o
  HID é embutido.
- Os alvos `net8.0` dos testes no Linux. O consumidor `net8.0` self-contained cobriu a biblioteca.
- A remoção física do cabo: o unbind e o bind do `usbhid` simulam a remoção do HID, mas não a do dispositivo USB.
- Fora desta etapa: Linux x64, Raspberry Pi OS (inclusive o de 32 bits, que o `install.sh` recusa), Native AOT, um
  browser real contra o `CrossSiteRequestGuard` e gravações longas.
- Decisões do usuário: manter ou remover o serviço instalado, e parar ou não o `docker.service` ativado pelo socket
  ([§10.9](#109-limpeza-e-linha-de-base)).
- Os defeitos 1, 2 e 5 foram corrigidos na [§10.13](#1013-correções). Ficam sem teste no Pi o `uninstall.sh` (só a
  função do `udevadm trigger` rodou, em `--dry-run`, porque o serviço fica instalado) e o build cruzado com o builder
  legado (`--platform` de outra arquitetura), que no Docker Desktop do PC falhou por limitação do builder legado.

### 10.12 Reverificação independente

Feita das 10:50 às 11:05, por um verificador separado do agente de execução, no mesmo Pi e com o mesmo cabo. As
chamadas à API saíram do PC (192.168.100.69). Os testes rodaram numa cópia nova das fontes em `/root/brymen-validation`
(432 kB, 224 entradas; o relatório tinha crescido), com `HOME`, `TMPDIR` e os caches do NuGet dentro dela. Os testes de
hardware rodaram com o serviço parado e o `fuser /dev/hidraw1` vazio, antes e depois. Comandos ao medidor: só `52 66`,
`88`, `89` e `8A`.

| Verificação | Resultado |
|---|---|
| Serviço | `active (running)`, `enabled`, `Type=notify`, `NotifyAccess=main`, `NRestarts=0`, `TimeoutStopSec=45s`. No journal desde a instalação (10:20:48), todas as paradas terminaram com `Deactivated successfully` em 130 a 330 ms, sem `SIGKILL` nem `Restart=` disparado, e nenhuma exceção. Com prioridade warning, só o `Unhealthy` do unlink, o `W7012`/`E9015` da reconexão e o `W7012` das 10:42:20 (observação 6 da [§10.10](#1010-defeitos-e-observações)). Uma parada e uma partida da reverificação: 258 ms e 1,34 s (`Started` 7 ms depois do `Application started`) |
| Binário | O `/opt/brymen-dmm/Brymen.Dmm.Api` (23 698 983 B) é idêntico ao do `artifacts/brymen-dmm-0.1.0-linux-arm64.tar.gz` (SHA-256 `71bf9818…235e`). O `tar.gz` e os dois `.nupkg` do PC têm os SHA-256 da [§10.3](#103-pacote-e-instalação) e da [§10.7](#107-biblioteca-nuget-no-arm) |
| udev | `/dev/brymen-bu86x -> hidraw1`; `crw-rw----+ root brymen-dmm`, com ACL; no banco do udev, `BRYMEN_BU86X=1`, `DEVLINKS=/dev/brymen-bu86x` e `TAGS=:uaccess:` (o `:seat:` da [§10.3](#103-pacote-e-instalação) só aparece no `udevadm test`). `hidraw0`: `crw------- root root`, sem ACL, sem `BRYMEN_BU86X`, com a data de 10:20:48 do `install.sh`, sem mudança durante a reverificação |
| Usuário e arquivos | `brymen-dmm` uid 999, gid 998, `/usr/sbin/nologin`; os modos e donos da [§10.3](#103-pacote-e-instalação) conferem; `settings.json` com o comparador padrão (`enabled false`, 200/100) |
| `meter` e `latest` pela LAN | `connected`, `hid`, `/dev/hidraw1`, 0x8603; `sequence` crescendo a ≈ 5/s. Com o medidor parado em `-0.000 V DC`, o `rawHex` ficou constante por 20 s (60 amostras iguais), como o display. Logo depois de uma importação, o SSE mostrou o medidor de verdade: `-.---` (`…5040404000…`), `-0.024` (`…BFAFCBE4…`) e de volta a `-0.000` |
| SSE | 21 eventos `reading` em 4 s, `id` 3570 a 3590, sem lacunas |
| SignalR | `samples/Brymen.Dmm.SignalRClient` no PC por 12 s: `GetStatus`/`GetLatest` na conexão; 58 eventos (`#3635` a `#3692`) em 11,4 s, 5,0/s, sem lacunas |
| Importação nova (2 vezes) | `202` + `Location`; durante a leitura, `latest`, a 2ª `POST` e a imagem → 409; `completed` em 10,56 s e 10,61 s, 95/95, `totalBytes` 2272, 0 retentativas, 0 recomeços, `warnings []`; imagem de 2280 B com SHA-256 `dbce1fe3…7e2f`, **idêntica byte a byte** à remontada no PC do `memory-dump-1.txt` (bytes 1 a 8 dos 3 primeiros relatórios do `88` e dos 94 `89`); 1 sessão `A0_0080` → `A0_095A`, ACV + Hz, 375 amostras; CSV com 376 linhas. A 1ª leitura veio ≈ 2,07 s depois do fim, sem `W7012`. As duas importações foram apagadas depois (`DELETE` → 204); ficou só a `c104f594…` da validação |
| Painel e OpenAPI | `/` 200 `text/html` 3804 B; `openapi/v1.json` 49 896 B com 18 rotas `/api/v1`; `POST` com `Origin: http://intruso.test` → 403 |
| Build no Pi | `dotnet build BrymenDmm.slnx -c Release`: 0 avisos, 0 erros, 76,9 s com o restore do zero |
| Testes (a) `net10.0` | Protocol 336/0/0; Hid 128/0/12; Dmm 65/0/4; Hosting 24/0/0; Api **173/1/0** na 1ª rodada (defeito 5) e 174/0/0 na 2ª |
| Testes (b) `BRYMEN_HW_TESTS=1` | Hid `HardwareTests` 7/7 (7,3 s); Dmm `HardwareTests` 4/4 (21,1 s): importação de 95 blocos em 10 672 ms, 0 retentativas; 1º `66` depois da memória em 2051 ms, o seguinte em 167 ms; 14 leituras em 3 s |
| Linha de base | `ss -ltnp`: 22, 80, 111, 53 (127.0.0.53), 5037/5555, 5432 e 6001 como antes, mais só a 5080; nada de 5090 a 5099. `nginx`, `postgresql@14-main`, `phaserapi`, `ssh`, `rpcbind` e `systemd-resolved` ativos desde o boot (22/10, 17:17), com `NRestarts=0`; o `adbd` escuta na 5037/5555. Docker com 0 contêineres, 0 imagens, 0 volumes e 0 B de cache; `docker.service` saiu de inativo às 10:12:27, ainda ativo (desabilitado, ativado pelo socket). Nenhum processo `dotnet`, `curl` ou de contêiner sobrando; só o serviço |
| Sessões SSH desde as 10:00 | Todas do PC (192.168.100.69): 1 por senha (a do usuário, que instalou a chave às 10:11) e as demais com a chave `claude-brymen-validation` |
| Limpeza da reverificação | `/root/brymen-validation` (393 MB) e o `/tmp/.dotnet` vazio criado pelo build foram removidos. Também foram removidas as duas pastas vazias da validação ([§10.9](#109-limpeza-e-linha-de-base)). O `/root/.nuget` não foi recriado. Espaço livre: 7,1 GB |

### 10.13 Correções

Feitas das 11:10 às 11:50 pelo agente de correções, no PC e no mesmo Pi, para os defeitos da
[§10.10](#1010-defeitos-e-observações) e da reverificação. A lista recebida tinha 7 itens: o do `Dockerfile` e o do
`install.sh` vieram duas vezes (da execução e da verificação). No Pi, o cabo só foi usado pelo serviço
(`fuser /dev/hidraw1`), e os comandos ao medidor foram só `52 66`, `88`, `89` e `8A` (o polling e uma importação pelo
serviço). Os testes da API usam o transporte Simulated e não abrem o cabo.

| # | Defeito | Veredito | Correção |
|---|---|---|---|
| 1 | `Dockerfile` não compila sem BuildKit/buildx | **Procede.** Só o `DOTNET_VERSION` vinha antes do 1º `FROM`, e o builder legado só substitui num `FROM` os `ARG` do escopo global | `ARG BUILDPLATFORM` **sem valor** antes do 1º `FROM`, com comentário. No cabeçalho do `Dockerfile` e no README (pré-requisitos e seção Docker), o comando do builder legado: `DOCKER_BUILDKIT=0 docker build --build-arg BUILDPLATFORM=linux/arm64 --build-arg TARGETARCH=arm64 -t brymen-dmm:0.1.0 .`. O `TARGETARCH` já era declarado no estágio de build e não precisou mudar |
| 2 | `install.sh` dispara `change` em todos os hidraw | **Procede**, e o `uninstall.sh` (linha 75) tinha o mesmo `udevadm trigger --subsystem-match=hidraw` | Função `trigger_cable_nodes` nos dois scripts: percorre `/sys/class/hidraw/hidraw*`, confere `HID_ID=0003:00000820:00000001` no `device/uevent` e roda `udevadm trigger --action=change --subsystem-match=hidraw --sysname-match=hidrawN` só para esses nós. O `--sysname-match` funciona também em udev antigos. Sem o cabo, o `udevadm` não é chamado. O README, o `docs/library.md` e o comentário da regra udev trocaram o comando manual pelo mesmo laço |
| 3 | `wwwroot/*.br` e `*.gz` vão no pacote, mas nunca são servidos | **Procede** (era a observação 3). O `UseStaticFiles` não negocia os arquivos pré-comprimidos | `Program.cs`: `UseDefaultFiles` → `UseRouting` explícito → `UseCors` → `MapStaticAssets()`, no lugar do `UseStaticFiles`. O roteamento vem depois do `UseDefaultFiles`, para o `/` já chegar como `/index.html`, que é o endpoint do manifesto. Teste novo `Panel_files_are_served_compressed_when_the_client_accepts_it` (`/`, `/js/app.js` e `/css/app.css` com `Accept-Encoding: gzip`: `Content-Encoding: gzip`, `Vary`, `ETag` e o conteúdo descomprimido igual ao sem compressão) |
| 4 | Corrida na retenção das importações | **Procede.** O `completed` era publicado antes do `ApplyRetention()`, que rodava no `finally` depois da liberação do cabo | `LoggerImportService.Finish(job, publish)`: a retenção roda antes do `Complete`, do `Fail` e do `MarkCanceled`, com `try`/`finally` para o estado final sair mesmo se a retenção falhar. O `finally` antigo saiu. A importação que termina conta como terminada e nunca é apagada: ficam ela e as `ImportRetention − 1` outras mais novas, mesmo com o relógio atrasado (o Pi não tem RTC). Teste novo `The_retention_runs_before_the_import_becomes_completed`, que fotografa a lista a cada log "Importação ..." do serviço: no "apagada", a nova ainda está `running`; no "concluída", a lista já tem só duas. O teste original não mudou e agora é determinístico |
| 5 | Relatório omitia o `W7012` das 10:42:20, as sobras da limpeza e o teste instável | **Procede, já corrigido pelo verificador.** O Resumo, a [§10.2](#102-build-e-testes-no-pi), a [§10.9](#109-limpeza-e-linha-de-base), a [§10.10](#1010-defeitos-e-observações) (defeito 5 e observação 6), a [§10.11](#1011-pendências) e a [§10.12](#1012-reverificação-independente) foram conferidos | Nada a mudar além desta seção |

#### Verificação dos defeitos 1 e 3 no Docker do PC

O Docker do Pi não foi usado, pela decisão do usuário da [§10.8](#108-docker-interrompido-por-decisão-do-usuário). O
Docker Desktop do PC (27.5.1, buildx 0.20.1, image store do containerd) estava parado: foi ligado para isto e desligado
no fim.

| Verificação | Resultado |
|---|---|
| BuildKit, `Dockerfile` mínimo (busybox) com `ARG BUILDPLATFORM` global sem valor, `--platform linux/arm64` | `build=linux/amd64 target=arm64 uname=x86_64`, igual ao sem a declaração: o valor automático continua valendo |
| BuildKit, com `ARG BUILDPLATFORM=linux/arm64` (valor padrão) | `build=linux/arm64 uname=aarch64`: o padrão toma o lugar do valor automático, e o estágio de build roda emulado. Por isso a declaração não tem valor |
| Builder legado (`DOCKER_BUILDKIT=0`), sem a declaração, com `--build-arg BUILDPLATFORM=linux/amd64` | `failed to parse platform : "" is an invalid OS component`, o mesmo erro do Pi |
| Builder legado, com a declaração | Passa; sem os `--build-arg`, o mesmo erro (por isso o README e o `Dockerfile` mostram os dois argumentos) |
| `docker buildx build --check .` no `Dockerfile` real | `Check complete, no warnings found.` |
| Builder legado, `Dockerfile` real, nativo (`--build-arg BUILDPLATFORM=linux/amd64 --build-arg TARGETARCH=amd64`) | 27 passos, código 0 em 260 s, publish trimmed sem avisos. O contêiner (`Meter__Transport=Simulated`, `127.0.0.1:5093`) respondeu, e o painel saiu com `br` (988, 3599 e 1322 B), `gzip` (1255, 4114 e 1572 B) e sem compressão (3804, 11 908 e 4612 B) para `/`, `app.js` e `app.css`, com `ETag` e `Vary: Accept-Encoding` |
| Builder legado, cruzado (`--platform linux/arm64` num PC x64) | Passou do `FROM --platform=$BUILDPLATFORM` (passo 3/27) e falhou no `COPY` (passo 8) com `No such image` de uma imagem intermediária: limitação do builder legado com o image store do containerd, e não do `Dockerfile`. O README indica o builder legado só para a arquitetura da própria máquina |

**Incidente no PC.** Para limpar as imagens dos testes, foi usado `docker image prune -f`, que apaga **todas** as
imagens soltas (sem tag) do Docker Desktop, e não só as dos testes. O Docker não registrou eventos de remoção, então não
dá para saber se havia imagens soltas do usuário antes. As 18 imagens com tag e os 90 volumes não foram afetados. Depois
disso, a limpeza foi feita só por nome e por ID: `busybox:1.36` e `mcr.microsoft.com/dotnet/runtime-deps:10.0` (baixadas
pelos testes, com a tag de hoje), `brymen-dmm:legacy-check` e as intermediárias dos dois builds legados. A lista de
imagens voltou a ser igual à de antes dos builds. O `mcr.microsoft.com/dotnet/sdk:10.0`, que já existia, ficou. O cache
do BuildKit não foi limpo, para não apagar o do usuário: ficaram só as poucas entradas dos testes com o busybox.

#### Build, testes e publicação

| Onde | Resultado |
|---|---|
| PC, `dotnet build BrymenDmm.slnx -c Release` | 0 avisos, 0 erros (22 s) |
| PC, `dotnet test BrymenDmm.slnx` | **820 aprovados, 0 falhas**, 20 ignorados (hardware, uhid e só Linux): Protocol 336; Hid 128 + 12 ignorados; Dmm 65 + 4 em `net10.0` e em `net8.0`; Hosting 24 em `net10.0` e em `net8.0`; Api 178 (174 + o teste novo da retenção + os 3 casos da compressão) |
| PC, teste novo da retenção contra o código antigo | Falha com a retenção no `finally` (a ordem antiga) e com a ordem invertida dentro do `Finish` (esperado `[(id, Running), (cccc…, Failed)]`, obtido `[(id, Completed), …]`); passa com a correção |
| PC, `./deploy/publish.ps1 -Rid linux-arm64` | 0 avisos, 44,3 s; executável de 23 781 124 B (82 141 B a mais, do `Microsoft.AspNetCore.StaticAssets`); `tar.gz` de 9 639 412 B, 25 entradas, SHA-256 `71b3e051c99da85c38bbf9d786b5c261607e89df3b7f9c5ffdf062d898fb9e5c`, conferido no Pi |
| Pi, `dotnet build BrymenDmm.slnx -c Release` | 0 avisos, 0 erros, 56,6 s com o restore do zero (fontes: 439 kB, 224 entradas; `HOME`, `TMPDIR` e caches do NuGet em `/root/brymen-validation`) |
| Pi, testes `net10.0` | Protocol 336/0/0; Hid 128/0/12; Dmm 65/0/4; Hosting 24/0/0; Api **178/0/0 em 3 rodadas** (23,7 s, 22,4 s e 22,3 s) |
| Pi, `LoggerRetentionTests` isolado | **20 execuções, 20 aprovadas** (40 testes, 120 s). Antes da correção: 1 falha em 6 |
| Pi, testes de hardware e uhid | Não repetidos: as correções não tocam o `Brymen.Dmm.Hid` nem o `Brymen.Dmm`, e o cabo foi conferido pelo serviço |

#### Reinstalação e serviço

O `install.sh` do pacote novo rodou com um `udevadm monitor --kernel --udev --property` gravando todos os eventos.

| Verificação | Resultado |
|---|---|
| `install.sh` | Código 0, em 2,6 s, com a linha nova `reaplicando as regras udev a /dev/hidraw1`. O `/opt/brymen-dmm/Brymen.Dmm.Api` ficou com o SHA-256 `2cc8c7fe0e8e97d6952bde61014d61671213a168117e70888bac1a241aad4f3f`, o mesmo do pacote |
| Eventos do udev | **Um** `change` do kernel e um do udev (SEQNUM 5470), só do `hidraw1` (`0003:0820:0001.0015`), com `BRYMEN_BU86X=1`, `DEVLINKS=/dev/brymen-bu86x` e `TAGS=:seat:uaccess:`. Nenhum evento de outro dispositivo |
| `hidraw0` | `crw------- root root`, sem ACL, sem `BRYMEN_BU86X` e sem symlink, **com a data de 10:20:48,36** (a da 1ª instalação) antes e depois. O `hidraw1` passou a 11:41:32,86 |
| Funções `trigger_cable_nodes` em `--dry-run` | As do `install.sh` e do `uninstall.sh` instalado escolhem só o `hidraw1`; com um `HID_ID` inexistente, nenhuma chama o `udevadm`. `bash -n` sem erros nos dois scripts |
| Unit | `Stopping` às 11:41:33,456 e `Deactivated successfully` 145 ms depois; `Starting` às 11:41:33,605, `Cabo BC-86X aberto: /dev/hidraw1` às 11:41:34,845, `Started` às 11:41:34,997 (`Type=notify`, `NotifyAccess=main`, `NRestarts=0`) |
| `GET /api/v1/meter` e leituras, pela LAN | `connected`, `hid`, `/dev/hidraw1`, 0x8603, `lastError: null`. O medidor estava em **ACV** (0,201 a 0,203 V AC, 0,000 Hz); SSE com 20 eventos em 4 s; `/health/live` e `/health/ready` com 200 |
| Painel com compressão, pela LAN | `Accept-Encoding: br` → `Content-Encoding: br` (988, 3599 e 1322 B para `/`, `app.js` e `app.css`); `gzip` → 1255, 4114 e 1572 B; sem compressão, 3804, 11 908 e 4612 B, sempre com `Vary: Accept-Encoding`. As três versões descomprimem para os mesmos bytes (SHA-256). `ETag` com `Cache-Control: no-cache`, e `If-None-Match` → 304 |
| Outras rotas | `openapi/v1.json` 200 com 49 896 B; rota inexistente → 404; `POST /js/app.js` → 405 (o endpoint do arquivo só aceita `GET` e `HEAD`) |
| Importação pelo serviço | `completed` em 10,64 s (journal: 11:42:35,229 → 11:42:45,872), 95/95 blocos, `totalBytes` 2272, 0 retentativas, 0 recomeços, `warnings []`; imagem com o SHA-256 `dbce1fe3…7e2f` do dump; a lista lida logo depois do `completed` tinha a nova e a `c104f594…` (com a retenção padrão de 10, nada a podar). A importação nova foi apagada (`DELETE` → 204) |
| Journal desde a reinstalação | Nenhuma entrada com prioridade warning |
| Raiz de conteúdo errada (instância temporária `Simulated` em `127.0.0.1:5094`, executada de outra pasta, sem `ASPNETCORE_CONTENTROOT`) | `/` → 404, mas `/index.html` e `/js/app.js` → 200; README atualizado. A instância foi encerrada |

#### Limpeza

- Removidos `/root/brymen-validation` (425 MB), o `VBCSCompiler` que o build deixou rodando (PID 62369, iniciado às
  11:36:32) e o `/tmp/.dotnet` com o socket dele. Ao encerrar a instância da porta 5094, o `pkill -f` casou também com o
  próprio shell remoto, que tinha o mesmo padrão na linha de comando; nenhum outro processo tinha esse padrão.
- `/root` e `/tmp` iguais à linha de base tirada antes das correções, exceto a data do `/root` e o `PrivateTmp` da nova
  instância do serviço. Portas iguais: as da linha de base e a 5080 do serviço; nada de 5090 a 5099. `nginx`,
  `postgresql@14-main`, `phaserapi`, `ssh`, `rpcbind` e `systemd-resolved` com a mesma ativação do boot e
  `NRestarts=0`; no `ss`, só mudaram os números dos descritores do systemd no socket do `rpcbind`, pelo `daemon-reload`
  do `install.sh`. Docker do Pi com 0 contêineres e 0 imagens. Espaço livre: 7,1 GB.
- Mantido (o usuário decide): o serviço reinstalado, agora com o binário corrigido, e os dados em `/var/lib/brymen-dmm`
  (só a importação `c104f594…` da validação em `imports/`).

### 10.14 Desinstalação a pedido do usuário

O usuário pediu, no fim da validação, para parar e remover o serviço. O `uninstall.sh --purge` removeu tudo, mas revelou
um defeito:

| # | Arquivo | Achado | Correção e evidência |
|---|---|---|---|
| 1 | `deploy/linux/uninstall.sh` | Depois de apagar a regra udev, o `udevadm trigger --action=change` só nos hidraw do cabo tirava o `/dev/brymen-bu86x`, mas o nó continuava `crw-rw----+ root 998`: gid órfão do grupo apagado, 0660 e a ACL do `uaccess`. Nenhuma regra restante redefine dono, modo ou ACL; isso só acontece ao reconectar o cabo. | Nova `reset_cable_nodes`: depois do `change`, o nó do BC-86X volta a `root:root 0600` sem ACL, com `setfacl -b` ou, sem o pacote `acl` (caso do Orange Pi 5), `os.removexattr` do `python3`; os outros hidraw não são tocados. Ciclo `install.sh` → `uninstall.sh --purge` no Pi: `hidraw1` voltou a `crw------- root root` com `listxattr` vazio, e o `hidraw0` manteve até a data (10:20). |

O nó do primeiro `--purge`, feito com o script antigo, foi restaurado à mão para o mesmo estado. Os pacotes
`linux-arm64` e `linux-x64` foram republicados com o script corrigido.

Estado final do Pi, igual ao de antes da instalação:
- sem a unit, a regra udev, o `modules-load.d`, `/opt`, `/var/lib` e `/etc/brymen-dmm`, e sem o usuário e o grupo
  `brymen-dmm`;
- `/dev/hidraw0` e `/dev/hidraw1` em `root:root 0600`, sem ACL;
- só as portas da linha de base, com a 5080 livre;
- nginx, postgres, PhaserAPI, ssh e adbd (pelo `usbdevice.service`) ativos.

O `docker.service`, ativado pelo socket no 1º diagnóstico ([§10.9](#109-limpeza-e-linha-de-base)), foi parado a pedido do
usuário (`systemctl stop docker.service`). Voltou ao estado de antes: `inactive` e `disabled`, com o `docker.socket`
ativo.
