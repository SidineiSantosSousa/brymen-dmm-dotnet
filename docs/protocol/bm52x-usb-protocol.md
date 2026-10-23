# Protocolo USB do Brymen BM52x com o cabo óptico BC-86X

Especificação implementável do protocolo usado pelo software Windows *Bs82-52x Data Recording System V6.0.0.9* para falar com o multímetro **Brymen BM525s** (idêntico ao **Minipa ET-2615A**) através do cabo óptico USB **BC-86X** (kit **BU-86X**). É a fonte de verdade para o port .NET 10 (API REST, Linux arm64/x64).

Data das medições: 2026-10-22. Medidor em ACV com Hz no display secundário, pontas abertas, faixa AUTO.

**Acréscimo (2026-10-23):** a §8 estende a especificação à família **BM82x** (Model ID 0x82), medida com o **Minipa ET-2517A** no mesmo cabo BC-86X: comando `82 66`, ausência de memória, decodificação de quadros reais, pedidos descartados com o algoritmo de reenvio e detecção automática da família.

---

## 0. Convenções e legenda de evidências

Cada afirmação traz a sua evidência entre colchetes:

| Marca | Significado |
|---|---|
| **[PDF-L pN]** | `10000count-professional-dual-display-mobile-logging-DMMs-protocol.pdf` (protocolo do BM52x com logging), página N |
| **[PDF-N pN]** | `10000count-professional-dual-display-DMMs-protocol.pdf` (série sem logging, BM82x), página N |
| **[PDF-M pN]** | `Memory-Read-Allocation-Decoder.pdf`, página N |
| **[CAP:nome]** | Captura real feita com o `probe.cs` (ver §7) |
| **[CAP:et2517a-…]**, **[CAP:rate]**, **[CAP:rate2]** | Acréscimo (2026-10-23): capturas reais do Minipa ET-2517A, feitas com o `probe.cs` e com as ferramentas de `docs/protocol/tools/` (`sweep.cs`, `rate.cs`, `rate2.cs`); ver §8 |
| **[USR]** | Acréscimo (2026-10-23): relato do usuário sem arquivo conferível nesta especificação (ex.: o manual da Minipa) |
| **[RE 0x…]** | Engenharia reversa estática do executável original `Bs8252xV6009sA.exe` (endereço virtual, image base 0x00400000), desmontado com `dumpbin /DISASM`. O programa **não** foi executado. |
| **[INF]** | Inferência (raciocínio a partir das evidências, sem prova direta) |
| **[NC]** | Não confirmado: hipótese que precisa de captura adicional |

- **Numeração de bytes**: sempre *1-based*, como nos PDFs, sobre o quadro concatenado **incluindo o byte de report ID** que o Windows coloca no início de cada relatório de entrada. Ex.: “byte 10” = 10º byte do quadro de 27 bytes.
- `bN.k` = bit k (0 = LSB) do byte N. Ex.: `b17.5` = bit 5 do byte 17.
- Hex sem sufixo nas tabelas; `0x..` no texto.
- O executável é **Borland C++ Builder (VCL)**, não Delphi: string `Borland C++ - Copyright 1999 Inprise Corporation` no offset de arquivo 0x000C3240 e símbolos `hrdir_b.c` e `@@Loggrecord@Finalize`.
  - **Correção (revisão):** o offset correto da string é **0x000C3240** (VA 0x004C4440, seção `.data`). O byte em 0x000C323F é 0x59 (‘Y’) e pertence aos dados anteriores. Conferido por busca direta no `.exe`.
- **Revisão independente (2026-10-22):** as páginas dos PDFs foram renderizadas de novo a 220–300 dpi (`C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\protocol\png-verify\`) e relidas, e todos os vetores e dumps foram decodificados por um script escrito do zero (`C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts-verify\verify_bm52x.py`, ver §7): 1956 verificações e nenhuma divergência. Pontos de RE conferidos na desmontagem: 0x00424ED6..0x00425057, 0x00425379..0x0042542A, 0x00426CBD..0x00426D11, 0x00426D31..0x00426DE2, 0x00430462..0x0043048C, 0x0043053D..0x00430763 e 0x00430C3A..0x00430C7C. Cada ajuste está marcado com “Correção (revisão):”.

---

## 1. Resumo executivo

1. Transporte: HID “genérico”, VID **0x0820**, PID **0x0001**, usage page 0xFF00, usage 0x0001. O dispositivo não usa report IDs (o 0x00 no início de cada relatório é o placeholder do Windows). Os relatórios de entrada têm 8 bytes úteis e o de saída tem 3 bytes úteis.
2. Comando = relatório de saída `[00] 00 52 cc` (o primeiro 00 é o report ID). `cc` = `66` (tempo real), `88` (início da memória), `89` (próximo bloco), `8A` (repete o bloco atual).
3. `66` devolve 3 relatórios (27 bytes, LCD codificado em segmentos). `88`/`89`/`8A` devolvem 4 relatórios (36 bytes): 24 bytes de memória, checksum de 16 bits e 6 bytes de preenchimento.
4. **Correção principal**: no exemplo do PDF o report ID do 2º relatório aparece no byte 11 e o dígito 5 no byte 10. **Na realidade o byte 10 é o report ID II e o dígito 5 está no byte 11.** Isso é confirmado por quatro evidências independentes (§3.1).
5. No LCD, o bit 4 de cada byte de dígito é o ponto decimal **à direita** daquele dígito.
6. Na memória, os campos D3..D0 das amostras são os nibbles de um **inteiro binário de 16 bits little-endian**, e não BCD: `count = X_1 | X_2 << 8` (§4.9).
7. Dump real: 1 sessão, ACV + Hz, intervalo de 0,05 s, 375 amostras, de 0,013 V a 0,006 V, Hz sempre 0,000 (§4.14).
8. **Acréscimo (2026-10-23), família BM82x (§8):** o Minipa ET-2517A usa o mesmo cabo e o mesmo mapa de LCD, mas só responde a `00 00 82 66` (Model ID 0x82) e não tem memória. O BM525s também aceita `82 66`, então a detecção começa por ele. O ET-2517A descarta pedidos de tempo real, tanto mais quanto maior o intervalo entre eles; a recuperação é reenviar o mesmo pedido depois de ≈ 300 ms sem resposta, sem fechar o cabo, dentro de um orçamento de ≈ 2 s (§8.5).

---

## 2. Transporte HID

### 2.1 Identificação do dispositivo

| Item | Valor | Evidência |
|---|---|---|
| VID / PID | 0x0820 / 0x0001 | [CAP:probe] `\\?\hid#vid_0820&pid_0001…`; [RE 0x00426CC2] `mov dx,1 / mov ax,820h` antes da rotina de abertura 0x0042489C |
| bcdDevice (VersionNumber) | 0x8603 | [CAP:probe] |
| Manufacturer / Product | `Brymen` / `Superior DMM` | [CAP:probe] |
| Serial string | lixo não-ASCII (`£⑉`); não usar para identificar o aparelho | [CAP:probe] |
| Usage page / usage | 0xFF00 / 0x0001 (vendor-defined) | [CAP:probe] HidP_GetCaps |
| Conformidade | “USB HID1.1” | [PDF-L p1], [PDF-M p1] |

> **Correção**: os fluxogramas dizem `Vendor_ID: 0x82` / `Product_ID: 0x01` [PDF-L p2], [PDF-M p4]. O VID real é **0x0820**.
>
> **Correção (revisão):** a causa do “0x82” não é demonstrável, então “truncado” é só uma das hipóteses [INF]. O fluxograma do BM82x [PDF-N p2] também diz `Vendor_ID: 0x82`, e lá 0x82 é justamente o código da família (Command 2 e Model ID). Uma cópia desse fluxograma explica o erro tão bem quanto um truncamento. Em qualquer caso, procure o dispositivo por VID **0x0820** / PID **0x0001** [CAP:probe; RE 0x00426CC2 `mov dx,1` / `mov ax,820h`].

### 2.2 Relatórios

| Relatório | Tamanho no Windows (HidP_GetCaps) | Conteúdo útil no fio | Evidência |
|---|---|---|---|
| Input | `InputReportByteLength = 9` | 8 bytes | [CAP:probe] |
| Output | `OutputReportByteLength = 4` | 3 bytes | [CAP:probe] |
| Feature | 0 (não existe) | — | [CAP:probe] |

- No Windows, o buffer de cada relatório começa com o report ID e, como o dispositivo não declara IDs, esse byte é sempre 0x00 [INF forte: semântica do HID no Windows; as capturas mostram 00 no 1º byte de **todos** os relatórios recebidos].
- **Linux (hidraw / hidapi)** [INF, confirmar no alvo]:
  - `write()`: envie 4 bytes `00 00 52 cc`. O 1º byte é o número do relatório (0 = sem ID) e o kernel o remove, então vão 3 bytes pelo fio.
  - `read()`: devolve **8 bytes** por relatório, **sem** o 0x00 inicial.
  - **Recomendação para o port**: a camada de transporte devolve sempre 8 bytes de payload por relatório, e a camada de protocolo remonta o quadro “estilo Windows” prefixando 0x00 em cada relatório. Assim toda a numeração deste documento e os `test-vectors.json` valem sem mudança.

Mapeamento entre a numeração Windows e o payload Linux: para o byte `n` (1-based), relatório `r = (n−1) div 9`, offset `o = (n−1) mod 9`. Se `o = 0`, é report ID (não existe no Linux). Caso contrário, o índice no payload concatenado (0-based) é `r·8 + (o−1)`.

### 2.3 Comandos

| Comando | Relatório de saída (Windows, 4 bytes) | Resposta | Evidência |
|---|---|---|---|
| Cs_RTD (tempo real) | `00 00 52 66` | 3 relatórios = 27 bytes | [PDF-L p1] tabela *Commands*; [CAP:rtd] |
| Cs_HMD (início da memória) | `00 00 52 88` | 4 relatórios = 36 bytes (“27 bytes + 9 bytes”) | [PDF-L p1], [PDF-M p1]; [CAP:dump] |
| Cs_NMD (próximo bloco) | `00 00 52 89` | 4 relatórios = 36 bytes | idem |
| Cs_CMD (bloco atual de novo) | `00 00 52 8A` | 4 relatórios = 36 bytes | idem |
| Cs_RTD da família BM82x (acréscimo, 2026-10-23) | `00 00 82 66` | 3 relatórios = 27 bytes; Model ID 0x82 no BM82x e 0x52 no BM52x | [PDF-N p1]; [CAP:et2517a-commands]; [CAP:cmd-sequences]; §8.1 |

- Byte 1 = report ID (00), byte 2 = “Command 1” (00), byte 3 = “Command 2” (0x52), byte 4 = “Command 3” [PDF-L p1].
- O 0x52 coincide com o “Model ID” devolvido nos bytes 20–23 do RTD do BM52x. Na série BM82x o comando é `00 00 82 66` e o Model ID é 0x82 [PDF-N p1]. Conclusão [INF]: “Command 2” é o código de família do medidor. O software original monta o relatório como `{00, 00, dl, cl}` com `dl = 0x52` fixo [RE 0x00426AC0..0x00426B00, 0x00426CE0].
- **Restrição desta especificação**: só os códigos 66, 88, 89 e 8A são usados. Nenhum outro código foi testado nem deve ser enviado.
  - **Acréscimo (2026-10-23):** o “Command 2” pode ser 0x52 ou 0x82, mas o 0x82 só com o “Command 3” = 66 (`82 66`, §8.1). Os comandos de memória continuam `52 88`, `52 89` e `52 8A`, e só para o Model ID 0x52.

### 2.4 Leitura das respostas

1. Escreva o relatório de saída (WriteFile/`write`).
2. Leia **3** relatórios de entrada para `66` ou **4** para `88`/`89`/`8A`. Concatene na ordem de chegada (27 ou 36 bytes no estilo Windows).
3. Valide os report IDs: no estilo Windows, bytes 1, 10 e 19 (e 28 na memória) = 0x00.

O software original faz exatamente isso, com I/O *overlapped* [RE]:
- `WriteFile` de 4 bytes e `WaitForSingleObject(evento, 500 ms)`; em timeout faz `CancelIo` [RE 0x00426AC0..0x00426B71].
- Lê com `ReadFile` de 9 bytes por chamada, espera de 500 ms por relatório e `GetOverlappedResult`, acumulando até 27 (ou 27 + 9) bytes [RE 0x00426B78..0x00426C56, 0x00426C60].
- Abre e fecha o dispositivo a cada transação. Os fluxogramas mandam fechar o HID após cada leitura [PDF-L p2], [PDF-M p4]; o código faz `CloseHandle` no fim de 0x00426CA8 e de 0x00426E48 [RE].

### 2.5 Temporização observada

Medida pelo `probe.cs` com o Stopwatch iniciado logo após o `Write`, de forma síncrona, com 300 ms de pausa entre comandos [CAP:dump1, dump2, rtd]:

| Comando | Até o 1º relatório | Entre relatórios | Observação |
|---|---|---|---|
| `66` | 50–103 ms (mediana ≈ 65 ms) | 8 ms | 12 amostras |
| `88` | **386–390 ms** (4 observações) | 8 ms | sempre lento [INF: o medidor reposiciona o ponteiro e lê a EEPROM] |
| `89` | 66–75 ms (mediana ≈ 71 ms) | 7–9 ms (mediana 8) | 188 amostras |
| `8A` | 123–124 ms | 8 ms | 2 observações |

- **Correção (revisão):** nos logs salvos (`C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\rtd_raw.log`, `dump1_raw.log` e `dump2_raw.log`) há **3** observações de `88` (390, 386 e 386 ms) e **1** de `8A` (124 ms, depois do 94º `89` do dump-2). A 4ª observação de `88` e a 2ª de `8A` (123 ms) vêm de uma execução do probe sem log salvo e não puderam ser conferidas. As faixas de `66` (12 amostras, 50–103 ms; a de 103 ms é a 1ª, logo após abrir o dispositivo) e de `89` (188 amostras, 66–75 ms) e os 7–9 ms entre relatórios foram reconferidos e batem.
- Os ~8 ms entre relatórios são compatíveis com um endpoint interrupt IN de bInterval = 8 ms [INF].
- O software original espera `Sleep(200)` depois do `88` antes de ler [RE 0x00426D0C `push 0C8h; call Sleep`], mas não espera depois do `89` [RE 0x00426EB1].
- Recomendação: timeout de leitura de ≥ 1 s por relatório (o fluxograma da memória usa 1 s [PDF-M p4]; o RTD usa 4 s para os 3 relatórios [PDF-L p2]).

### 2.6 Fluxogramas propostos pela Brymen

**RTD** [PDF-L p2]: abrir o HID; alocar 3 relatórios; enviar `00 00 52 66`; zerar e iniciar o timeout; esperar os 3 relatórios (se passar de 4 s, fechar e reabrir); fechar o HID; decodificar (Tabela 1 / FIG 1); esperar até completar 0,5 s e repetir. Isso dá polling de 2 Hz.

**Memória** [PDF-M p4]:

```text
Start → abrir HID → alocar 4 relatórios (36 bytes) → enviar 00 00 52 88
      → iniciar timeout → esperar 4 relatórios
          timeout > 1 s  → (2) fechar HID e recomeçar do Start
          checksum errado → (2)
      → "allocate useful bytes as Table 2"
      → timeout > 1,6 s → (2)
      → esperar 0,001 s → (1)
(1) → alocar 4 relatórios → enviar 00 00 52 89 → iniciar timeout → esperar 4 relatórios
          timeout > 1 s ou checksum errado → realocar e enviar 00 00 52 8A (relê o bloco atual) → esperar de novo
      → "allocate useful bytes as Table 2"
      → timeout > 1,6 s → (2)
      → esperar 0,001 s → "Last data?" não → (1); sim → fechar HID → decodificar (Tabela 2)
```

Implementação real do software original [RE]:
- Cs_HMD (0x00426CA8): checksum errado → fecha, reabre e repete o `88`, sem limite de tentativas. Timeout (código 0x102 = WAIT_TIMEOUT) → reabre e repete com `Sleep(10)`, até 3 tentativas no total.
- Cs_NMD (0x00426E48): checksum errado ou timeout → fecha, reabre e reenvia como `8A` com `Sleep(10)`, até 4 tentativas no total.
- “Last data?”: o programa calcula `TotalBytes` arredondado para cima ao múltiplo de 24 e envia exatamente `ceil(TotalBytes/24) − 1` comandos `89` depois do `88` [RE 0x0042F64F..0x0042F6E0, laço 0x0042F8B9..0x0042F90C].

### 2.7 Comportamentos de borda observados

- **`89` além do fim da memória não tem resposta.** Com TotalBytes = 2272, o 95º comando (1 × 88 + 94 × 89) cobre os ponteiros 2256..2279. O 96º (`89`) não recebeu nenhum relatório em 1,5 s [CAP:dump1, linha `# 89 TIMEOUT`]. Não envie mais que `ceil(TotalBytes/24)` comandos.
  - Nota: o `probe.cs` usa handle síncrono, então a leitura pendente bloqueou o `Write` seguinte e o probe foi encerrado. Depois disso o medidor voltou a responder normalmente a `66` [CAP:rtd]. A implementação .NET deve cancelar leituras pendentes (CancelIo / fechar o fd) após timeout.
- `8A` logo após `88` devolve o mesmo bloco do `88` [CAP:probe 1ª execução]. `8A` após o 94º `89` devolve o mesmo bloco daquele `89`. Um `88` em seguida volta ao cabeçalho [CAP:dump2]. Isso confirma “Memory Pointer − 24 e lê 24 bytes” [PDF-M p1].
  - **Correção (revisão):** a observação “`8A` logo após `88`” não tem log salvo. O vetor `cmd-current-again-after-hmd` reaproveita o hex do `88`, então esse caso é relato do autor, não captura conferível. Há duas evidências salvas do `8A`:
    1. o `8A` do dump-2, depois do 94º `89`, que devolve o bloco 2256..2279 [CAP:dump2];
    2. o `8A` da sessão anterior, que devolveu `000000000D0000000000000D00000000000D0000000000000D0000003400000000000000`, idêntico aos `89` [contexto das capturas da sessão anterior]. Isso é coerente com “P − 24”: o 8A relê o último bloco de 24 bytes, e os blocos 1–5 do log são todos iguais.

    Os dois casos estão em `test-vectors.json`.
  - **Nota (2026-10-22):** “`8A` logo após `88`” só vale com uma pausa entre os dois. Com cerca de 15 ms, o `8A` avançou como um `89` em 9 de 16 tentativas (achado de hardware no fim desta seção).
- Dois dumps completos seguidos foram **byte a byte idênticos** e todos os 190 checksums estavam corretos [CAP:dump1 × dump2]. A leitura não apaga nem altera a memória.
  - **Correção (revisão):** são **192** blocos com checksum e report IDs corretos: 95 no dump-1 e 97 no dump-2 (88 + 94 × 89 + 8A + 88). As 190 respostas da sequência 88 + 94 × 89 dos dois dumps são idênticas duas a duas, e os 2272 bytes remontados são idênticos.
- Os 8 bytes depois de `TotalBytes` no último bloco vieram 0x00 [CAP:dump1].
- **`82 66` e `52 66` devolvem quadros idênticos** no BM525s: duas consultas de cada, byte a byte iguais (`00001000BFAFEFE40000BFAFAFAF00012006005252525210000D00`) [CAP:cmd-sequences, `captures/cmd-sequences-2026-10-22.txt`]. Isso responde à questão do relatório de RE (§6.3, §14 item 1) sobre o `82 66` que o software original usa. A port continua usando `52 66`.
  - Na mesma captura, o comando `52 82`, enviado por engano, não teve resposta (timeout de 1,5 s), e o cabo continuou respondendo aos comandos seguintes [CAP:cmd-sequences]. Isso não muda a restrição da §2.3.
- **Achado de hardware (2026-10-22): `8A` logo depois do `88` não é confiável.** Testes com o BM525s e o BC-86X reais, no Windows:

  | Sequência | Resultado |
  |---|---|
  | `88` e, cerca de 15 ms depois, `8A` | Em **9 de 16** tentativas o `8A` **avançou como um `89`**: devolveu o bloco dos ponteiros 24..47 em vez de reler o cabeçalho |
  | `88`, pausa de 1 s ou mais, `8A` | Releu o bloco do `88` em **6 de 6** |
  | `88` e `89` imediato | Correto em **16 de 16** (bloco 24..47) |
  | `89` e `8A` | O `8A` releu o bloco do `89` em todas as tentativas (24 de 24, com 0, 100 ou 300 ms entre os dois) |

  - A captura `cmd-sequences-2026-10-22.txt` registra um `8A` depois do `88` que releu o cabeçalho [CAP:cmd-sequences]. O intervalo entre os dois comandos não foi registrado, então ela não contradiz o achado.
  - Hipótese [INF, NC]: depois de responder ao `88`, que é lento (≈ 390 ms até o 1º relatório, §2.5), o medidor ainda leva algum tempo para assentar o ponteiro, e um `8A` nesse intervalo é tratado como “próximo bloco”. O `89` imediato não sofre com isso porque “próximo bloco” é justamente o que ele pede.
  - **Recomendação:** depois da resposta do `88`, espere **200 ms** antes do próximo comando (o original faz `Sleep(200)` depois do `88` [RE 0x00426D0C]). Recupere uma falha no `88` **sempre com outro `88`**, nunca com `8A`. Use o `8A` **só depois de um `89`** (§4.11).
- **Achado de hardware (2026-10-22, validação de integração): o 1º `66` depois do último bloco da memória demora ≈ 2 s.** BM525s e BC-86X reais no Windows, `ReportTimeout` de 4 s, só `66`, `88` e `89`; o tempo é medido da escrita do `66` até o último relatório:

  | Sequência antes do `66` | 1º `66` | `66` seguinte |
  |---|---|---|
  | `88` + 94 × `89` (o 94º cobre os ponteiros 2256..2279 e alcança TotalBytes = 2272), pausa de 0, 200, 500 ou 1000 ms | **2056–2068 ms** (6 de 6) | 64–264 ms |
  | `88` + 93 × `89` | 480 ms | 192 ms |
  | `88` + 70, 47, 20 ou 5 × `89` | 344–471 ms | 160–208 ms |
  | só o `88` (pausas de 0 a 1000 ms) | 366–439 ms | 176–208 ms |
  | `66` repetidos (tempo real normal) | 64–200 ms (1º depois de abrir: 221–308 ms) | — |

  - A demora não diminui com a pausa antes do `66`: não é um tempo contado a partir do último `89`.
  - A resposta lenta chega com os dois displays em traços (`000010005040404000005040404000012000005252525210000000`, `-.---` / `-.---`). No `66` seguinte o secundário ainda pode vir em traços, e depois a leitura volta ao normal.
  - Com `ReportTimeout` de 1 s, esse `66` dá timeout, e o polling acusaria `W7012` depois de toda importação (3 de 3 na API antes do ajuste). A port espera até **3 s** pelo 1º relatório do primeiro `66` depois de comandos de memória e na partida, quando a memória pode ter sido lida por outro programa (`MeterSession.RealTimeAfterMemoryTimeout`).
  - O tempo real atual do BM525s em ACV, nesta medição, foi de 64–200 ms por `66`, e não os 50–103 ms da §2.5: o polling com `PollInterval` de 200 ms entregou 4,9 leituras/s.

### 2.8 Notas para Linux (recomendações, não verificadas)

- Regra udev sugerida: `SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0820", ATTRS{idProduct}=="0001", MODE="0660", GROUP="plugdev"`.
- Encontre o `/dev/hidrawN` pelo VID/PID (sysfs `uevent` com `HID_ID=0003:00000820:00000001`).
- Use `poll()` com timeout por relatório e descarte relatórios residuais (leitura não bloqueante até esvaziar) antes de cada comando, porque um relatório atrasado de um comando anterior desalinharia o quadro [INF].

---

## 3. Real-Time Data (Cs_RTD), comando 66, 27 bytes

### 3.1 Estrutura e correção da numeração

O quadro são 3 relatórios de 9 bytes: R1 = bytes 1–9, R2 = bytes 10–18, R3 = bytes 19–27. O 1º byte de cada relatório é o report ID (00).

> **Correção do PDF** [PDF-L p1, Table 1 e Example]: o PDF põe “00h (Report ID = II)” no byte 11 e os segmentos do dígito 5 no byte 10. **O correto é byte 10 = report ID II e byte 11 = dígito 5** (`5b 5g 5c 4p 5a 5f 5e 5d`). Os bytes 12–27 estão corretos no PDF.

Evidências:
1. **[CAP:RTD#1/#2/#3]** O byte 10 é 0x00 em todas as capturas e o byte 11 é 0xBF (“0.”), coerente com o secundário “0.000”. Com a numeração do PDF, o dígito 5 seria 0x00 (apagado), o que é incoerente com “0.000 Hz”.
2. **[PDF-N p1]** A Table 1 do protocolo irmão (BM82x, sem logging) já traz a numeração correta (“10 = 0x00 (Report ID = II)”, “11 = 5b 5g 5c 4p…”). Mesmo assim, o **exemplo** desse PDF repete o erro (byte 10 = 6Dh, byte 11 = 00h).
3. **Tamanho dos relatórios**: com relatórios de 9 bytes [CAP:probe], os IDs só podem estar nos bytes 1, 10 e 19. O próprio PDF põe o ID III no byte 19, coerente.
4. **[RE 0x00424ED6..0x00424F73]** O software original copia o quadro descartando os bytes 1, 2, **10**, 19 e 25–27: laços `i = 2..8`, `i = 10..17` e `i = 19..23` (índices 0-based). O byte 10 é tratado como ID e o byte 11 é o 1º dígito do secundário.
   - Confirmado na revisão: os destinos são `[ebp−0x216+i]`, `[ebp−0x217+i]` e `[ebp−0x218+i]`, o que compacta os bytes 3–9, 11–18 e 20–24 num vetor contínuo. Em 0x00425379..0x0042542A, os 4 dígitos do principal saem dos bytes 5–8 e os do secundário dos bytes **11–14**, todos com `and 0EFh` antes de `call 0x00426844`.

### 3.2 Mapa byte a byte e bit a bit (numeração corrigida)

①: display principal (dígitos grandes 1–4). ②: display secundário (dígitos pequenos 5–8). ④: traço de “T1-T2”. ⑤: traço de “MAX-MIN”. ③: barra gráfica, que **não é transmitida** [PDF-L p1, FIG 1].

| Byte | Rel. | Linux idx | bit 7 | bit 6 | bit 5 | bit 4 | bit 3 | bit 2 | bit 1 | bit 0 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | R1 | — | report ID I = 00 |||||||||
| 2 | R1 | 0 | don't care (obs. 00) |||||||||
| 3 | R1 | 1 | Hi | Lo | ① DC | ① AC | MIN | ⑤ “-” (MAX-MIN) | AVG | MAX |
| 4 | R1 | 2 | ① “-” (sinal do principal) | Δ | % (de “Δ%”) | LoZ | ① T2 | LPF | ④ “-” (T1-T2) | ① T1 |
| 5 | R1 | 3 | 1b | 1g | 1c | 1p | 1a | 1f | 1e | 1d |
| 6 | R1 | 4 | 2b | 2g | 2c | 2p | 2a | 2f | 2e | 2d |
| 7 | R1 | 5 | 3b | 3g | 3c | 3p | 3a | 3f | 3e | 3d |
| 8 | R1 | 6 | 4b | 4g | 4c | **dB** | 4a | 4f | 4e | 4d |
| 9 | R1 | 7 | ② DC | ② AC | ② “-” (sinal do secundário) | @ | bateria | ② T1 | ② T2 | •))) (bipe de continuidade) |
| **10** | R2 | — | **report ID II = 00** (PDF: byte 11) |||||||||
| **11** | R2 | 8 | 5b | 5g | 5c | 4p | 5a | 5f | 5e | 5d (PDF: byte 10) |
| 12 | R2 | 9 | 6b | 6g | 6c | 5p | 6a | 6f | 6e | 6d |
| 13 | R2 | 10 | 7b | 7g | 7c | 6p | 7a | 7f | 7e | 7d |
| 14 | R2 | 11 | 8b | 8g | 8c | **%4~20mA** | 8a | 8f | 8e | 8d |
| 15 | R2 | 12 | ② m | ② u | ② A | ② V | ② D% | ② n | ② S | ② F |
| 16 | R2 | 13 | ① k | ① M | ① Ω | ① Hz | ② M | ② k | ② Ω | ② Hz |
| 17 | R2 | 14 | ① u | ① m | ① V | ① A | ① n | ① D% | ① S | ① F |
| 18 | R2 | 15 | don't care (obs. 06; ver §3.8) |||||||||
| 19 | R3 | — | report ID III = 00 |||||||||
| 20–23 | R3 | 16–19 | 0x52 0x52 0x52 0x52 (Model ID; BM82x = 0x82 [PDF-N p1]; 82 82 82 82 no ET-2517A [CAP:et2517a-sweep], §8) |||||||||
| 24 | R3 | 20 | [H] | [C] | [R] | [AUTO] | don't care | don't care | don't care | don't care |
| 25–27 | R3 | 21–23 | don't care (obs. 00) |||||||||

Fonte dos bits: [PDF-L p1, Table 1], com o deslocamento corrigido do byte 10/11. A atribuição dos bits de AC/DC, sinal, T1/T2 e unidades foi conferida contra o código de decodificação [RE 0x00425070..0x004252C6]:
- `(b3 & 0x30) >> 4` → AC/DC do principal;
- `(b9 & 0xC0) >> 6` → AC/DC do secundário;
- `b17` → V/A/S/D%/F do principal;
- `b16 & 0x20` → Ω e `b16 & 0x10` → Hz do principal;
- `b9 & 1` → bipe;
- `b8 & 0x10` → dB.

### 3.3 Posição física (FIG 1) [PDF-L p1]

- **Principal (①)**: 4 dígitos grandes, 1 (esquerda) a 4 (direita).
  - Pontos “1P”, “2P” e “3P” no canto inferior direito dos dígitos 1, 2 e 3.
  - Sinal “-” = retângulo arredondado ① à esquerda, abaixo de “LPF” (`b4.7`).
  - À esquerda: “DC/AC” (①), “T1-T2” (T1 `b4.0`, traço ④ `b4.1`, T2 `b4.3`), “LPF”, “Δ%”, “LoZ”.
  - À direita, unidades em três grupos: “MΩ” e “kHz” (`b16` bits 7–4), “D%” (`b17.2`), “nuSF” e “dBmVA” (`b17` e `b8.4`).
- **Secundário (②)**: 4 dígitos pequenos no topo, 5 (esquerda) a 8 (direita).
  - Pontos “4P”, “5P” e “6P” à direita dos dígitos 5, 6 e 7.
  - À esquerda deles: bateria, T1/T2 (②), DC/AC (②), sinal “-” (②, `b9.5`), •))) e “@”.
  - À direita, unidades em linhas: “D%nSF” e “muAV” (`b15`), “MkΩHz” (`b16` bits 3–0) e “%4~20mA” (`b14.4`).
- **Canto superior esquerdo**: [AUTO], [R], [C], [H] (`b24` bits 4–7), “MAX-MIN” (⑤ = traço, `b3.2`), “AVG”, “Hi”, “Lo”.
- **Barra gráfica (③)**: na base do LCD, não mapeada na Table 1. [INF] O valor da barra não é transmitido.
- **Correção (revisão):** no FIG 1 [PDF-L p1; PDF-N p1] o segmento central do 4º dígito está rotulado “1g” em vez de “4g”, um erro de rótulo do desenho. Vale a Table 1: 4g = `b8.6`.

### 3.4 Decodificação de 7 segmentos

Layout dos bits em cada byte de dígito: `bit7=b, bit6=g, bit5=c, bit4=p (DP), bit3=a, bit2=f, bit1=e, bit0=d` [PDF-L p1].

```text
  aaa
 f   b
  ggg
 e   c
  ddd  p
```

Tabela de caracteres, com a chave = `byte & 0xEF` (DP mascarado). Ela foi extraída da rotina de conversão do software original [RE 0x00426844, árvore de `cmp`/`sub` e jump table em 0x0042696C, emulada exaustivamente para os 256 valores]:

| Chave | Segmentos | Caractere | | Chave | Segmentos | Caractere |
|---|---|---|---|---|---|---|
| 00 | — | espaço (apagado) | | A0 | b c | 1 |
| AF | a b c d e f | 0 | | CB | a b d e g | 2 |
| E9 | a b c d g | 3 | | E4 | b c f g | 4 |
| 6D | a c d f g | 5 | | 6F | a c d e f g | 6 |
| A8 | a b c | 7 | | EF | a b c d e f g | 8 |
| ED | a b c d f g | 9 | | EE | a b c e f g | A |
| 67 | c d e f g | b | | 0F | a d e f | C |
| E3 | b c d e g | d | | 4F | a d e f g | E |
| 4E | a e f g | F | | EC | a b c f g | g |
| E6 | b c e f g | H | | 20 | c | i |
| 07 | d e f | L | | 62 | c e g | n |
| 63 | c d e g | o | | CE | a b e f g | P |
| 42 | e g | r | | 47 | d e f g | t |
| A7 | b c d e f | U | | 23 | c d e | u |
| E5 | b c d f g | y | | 40 | g | - (traço) |
| *outros* | — | `?` (inválido) | | | | |

Regras:
- **Ponto decimal**: o bit 4 do byte do dígito *k* acende o ponto **à direita** desse dígito. Valem os bits 4 dos bytes 5, 6 e 7 (principal: 1p, 2p, 3p) e 11, 12 e 13 (secundário: 4p, 5p, 6p). O bit 4 do último dígito de cada display é outro anunciador (`b8.4 = dB`, `b14.4 = %4~20mA`).
  - Evidências: [PDF-L p1, FIG 1] (1P desenhado entre os dígitos 1 e 2, 4P entre 5 e 6); [PDF-L p1, Example] (“380.1”: o bit 4 está no byte 7 = 3º dígito; “50.12”: no byte 12 = 6º dígito); [CAP:RTD#1] “0.287” com o bit 4 no byte 5; [RE 0x00425379..0x0042542A] `and 0EFh` antes da conversão de cada dígito.
- **Sinal**: o “-” não é segmento de dígito. Principal = `b4.7`, secundário = `b9.5` [PDF-L p1, FIG 1, retângulos ① e ②]. Não confundir com o caractere `-` (chave 0x40), que aparece em mensagens como “----”.
- **Temperatura**: se o 4º dígito do principal (byte 8) ou do secundário (byte 14) decodifica como `C` ou `F`, o display está em °C ou °F e o valor numérico são os 3 dígitos anteriores [RE 0x00425477..0x004254BC compara o caractere com 0x43/0x46 e liga as flags 0x20/0x40]. [NC] Não há captura de temperatura.
  - **Atualização (2026-10-23):** o ET-2517A em °F sem termopar mostra `---F` (bytes 5–8 = `40 40 40 4E`) com T1 (`b4.0`) e nenhum bit de unidade nos bytes 15–17 [CAP:et2517a-sweep, §8.4.6]. Isso confirma a posição do C/F no 4º dígito. Os 3 primeiros caracteres são traços, então o valor é `null`, sem overload. Continua sem captura uma temperatura numérica.
- **OL / overload** [NC no padrão exato]: o LCD de overload dos Brymen usa os caracteres “0” e “L” (ex.: `0.L`, ` 0L `). A Table 1 não descreve OL. Regra proposta: se os 4 caracteres do display pertencem a {espaço, `0`, `L`} e há ao menos um `L`, é overload. O valor fica `null` (ou ±∞ na API) e `overload = true`. Registre o texto bruto para refinar a regra com capturas reais.
  - **Atualização (2026-10-23):** a regra vale nos quatro padrões reais capturados, todos com os caracteres ` 0L ` e o ponto onde a faixa o põe: `0.L` (BM525s, MΩ, pontas abertas [CAP:rtd-ohm-open]); `0L.` (continuidade), `.0L` (diodo, com o ponto 1p num dígito apagado) e `0L` (V em faixa manual, sem ponto), no ET-2517A [CAP:et2517a-sweep, §8.4]. Continua sem captura um OL com o sinal aceso ou no secundário.
- **Display apagado**: os 4 dígitos com chave 0x00 → display inativo (ex.: secundário desligado).
- Qualquer `?` ou letra fora dos casos acima → leitura não numérica. Exponha o texto e deixe o valor `null`.

### 3.5 Anunciadores, unidades e prefixos por display

**Principal** (bits ①):

| Grupo | Bits |
|---|---|
| Modo | AC = `b3.4`; DC = `b3.5`; ambos = AC+DC |
| Unidade | V = `b17.5`; A = `b17.4`; Ω = `b16.5`; Hz = `b16.4`; S = `b17.1`; F = `b17.0`; D% (duty, unidade “%”) = `b17.2`; dB = `b8.4`; °C/°F = 4º dígito ‘C’/‘F’ |
| Prefixo | n = `b17.3`; u (µ) = `b17.7`; m = `b17.6`; k = `b16.7`; M = `b16.6` |
| Outros | sinal `b4.7`; T1 `b4.0`; T2 `b4.3`; traço T1-T2 `b4.1`; •))) `b9.0` [RE: o bipe entra no código de função do principal] |

- **dBm**: com `b8.4` (dB) aceso, o “m” de `b17.6` forma “dBm” e **não é prefixo** [INF: no LCD “dBmVA” o mesmo “m” serve aos dois usos; o executável tem a string `nFunc_dBm_Ohm`, offset 0x000D2420].

**Secundário** (bits ②):

| Grupo | Bits |
|---|---|
| Modo | AC = `b9.6`; DC = `b9.7` |
| Unidade | V = `b15.4`; A = `b15.5`; Ω = `b16.1`; Hz = `b16.0`; S = `b15.1`; F = `b15.0`; D% = `b15.3`; %4~20mA = `b14.4`; °C/°F = 8º dígito ‘C’/‘F’ |
| Prefixo | n = `b15.2`; u = `b15.6`; m = `b15.7`; k = `b16.2`; M = `b16.3` |
| Outros | sinal `b9.5`; T1 `b9.2`; T2 `b9.1` |

**Flags globais**, com os nomes usados em `test-vectors.json`:

| Flag | Bit | Flag | Bit |
|---|---|---|---|
| HI | `b3.7` | LO | `b3.6` |
| MIN | `b3.3` | MAX_MIN_DASH | `b3.2` |
| AVG | `b3.1` | MAX | `b3.0` |
| DELTA (Δ, relativo) | `b4.6` | DELTA_PERCENT (% de Δ%) | `b4.5` |
| LOZ | `b4.4` | LPF | `b4.2` |
| MAIN_T1 / MAIN_T2 | `b4.0` / `b4.3` | T1_T2_DASH | `b4.1` |
| AT (@) | `b9.4` | LOW_BATTERY | `b9.3` |
| SEC_T1 / SEC_T2 | `b9.2` / `b9.1` | CONTINUITY_BEEP | `b9.0` |
| HOLD_H ([H]) | `b24.7` | C_BOX ([C]) | `b24.6` |
| R_BOX ([R]) | `b24.5` | AUTO | `b24.4` |

Semântica de [H], [C], [R] e @ [NC]: o PDF só mostra os símbolos. Hipótese: H = Hold, R = gravação (MAX/MIN/AVG ou logging), C = captura de pico (crest).

### 3.6 Algoritmo RTD → valores

Entrada: 27 bytes no estilo Windows (no Linux, prefixe 00 em cada relatório de 8 bytes).

1. **Validar o quadro**:
   - comprimento 27;
   - `b1 = b10 = b19 = 0x00`;
   - `b20..b23 = 52 52 52 52`.
     - **Acréscimo (2026-10-23):** ou `82 82 82 82` na família BM82x. Os quatro bytes devem ser iguais e iguais ao código da família esperada; na detecção, aceite 0x52 e 0x82 (§8.6).
   - Opcional, como o original: rejeitar se `b15` bits 7–6 = 11 ou `b17` bits 7–6 = 11 (m e µ juntos) [RE 0x00425016..0x00425057].
   - O software original aceita no byte 23 os valores {82, 52, FF, F7, F3, F1, F0, E0, C0, 80, 00} e rejeita quadros com `b9.4` (@) aceso [RE 0x00424F75..0x00424FFD]. Motivo desconhecido [NC].
2. **Para cada display** (principal: dígitos nos bytes 5–8, sinal `b4.7`; secundário: bytes 11–14, sinal `b9.5`):
   1. `ch[k] = SEG2CHAR[byte_k & 0xEF]` (tabela da §3.4).
   2. Texto = concatenação dos `ch[k]`, inserindo “.” depois do dígito k (k = 1..3) quando o bit 4 do seu byte está aceso.
   3. Se os 4 caracteres são espaço → display inativo (`null`).
   4. Se `ch[4] ∈ {C, F}` → unidade °C/°F; use só os 3 primeiros caracteres como número.
   5. Se os caracteres ⊂ {espaço, 0, L} e contêm `L` → `overload = true`, `value = null`.
   6. Se, sem espaços e pontos, sobram só dígitos → `value = double.Parse(texto sem espaços, InvariantCulture)`; com sinal aceso, `value = −value`.
   7. Caso contrário → não numérico (`value = null`; guardar o texto).
   8. Unidade e prefixo pelas tabelas da §3.5. Se houver mais de um bit de prefixo, marque o quadro inválido.
   9. `siValue = value × 10^prefixo` (n = 10⁻⁹, u = 10⁻⁶, m = 10⁻³, k = 10³, M = 10⁶). Para %, dB/dBm e °C/°F não há conversão SI.
   10. Modo: `AC+DC` se AC e DC estão acesos, senão `AC`, `DC` ou `NONE`.
3. **Flags**: lista da §3.5.
4. **Guardar os bytes não documentados** (2, 18, nibble baixo do 24, 25–27) para diagnóstico.

**Correção (revisão):** formato exato dos campos usados em `test-vectors.json`, que antes ficava implícito:
- `digits` = os 4 caracteres crus do display, com espaço para dígito apagado (ex.: `" 0L "`).
- `text` = `"-"` quando o sinal está aceso, seguido da concatenação com os pontos e **sem espaços nas pontas** (ex.: `"-12.34"`, `"0.L"`).
- Display inteiramente apagado: o objeto do display é `null`, não um objeto com `value = null`.
- Com `dB` (`b8.4`) e `m` (`b17.6`) acesos juntos, a unidade é `dBm` e o prefixo fica vazio [INF].

O decodificador independente (§7) segue exatamente estas regras e reproduz os 7 vetores RTD.

### 3.7 Decodificações manuais

#### 3.7.1 Exemplo do PDF “AC 380.1V / 50.12Hz” [PDF-L p1, Example]

Bytes como impressos: `00 xx 10 00 E9 EF BF A0 00 6D 00 BF A0 CB 00 01 20 xx 00 52 52 52 52 1x xx xx xx`.

Com a correção (bytes 10 e 11 trocados; `xx` = 00, `1x` = 10):

```text
R1: 00 00 10 00 E9 EF BF A0 00
R2: 00 6D BF A0 CB 00 01 20 00
R3: 00 52 52 52 52 10 00 00 00
```

| Byte | Hex | Binário (b g c p a f e d) | Leitura |
|---|---|---|---|
| 3 | 10 | 0001 0000 | ① AC |
| 4 | 00 | — | sinal + |
| 5 | E9 | 1110 1001 → a b c d g | “3” |
| 6 | EF | 1110 1111 → todos, p = 0 | “8” |
| 7 | BF | 1011 1111 → a b c d e f, **p = 1** | “0.” |
| 8 | A0 | 1010 0000 → b c; bit 4 (dB) = 0 | “1” |
| 9 | 00 | — | sem anunciadores ② |
| 11 | 6D | 0110 1101 → a c d f g | “5” |
| 12 | BF | 1011 1111 → “0”, **p = 1** (5p) | “0.” |
| 13 | A0 | b c | “1” |
| 14 | CB | 1100 1011 → a b d e g; bit 4 (%4~20mA) = 0 | “2” |
| 15 | 00 | — | sem unidade ② em b15 |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 24 | 10 | bit 4 | AUTO |

Resultado: principal **380.1 V AC**; secundário **50.12 Hz**; flags {AUTO}. Bate com o título do exemplo e com os segmentos destacados no LCD do PDF.

#### 3.7.2 RTD#1 (captura real)

`00 00 10 00 BF CB EF A8 00 | 00 BF AF AF AF 00 01 20 06 | 00 52 52 52 52 11 00 00 00`

| Byte | Hex | Leitura |
|---|---|---|
| 5 | BF | “0.” |
| 6 | CB | “2” |
| 7 | EF | “8” |
| 8 | A8 = 1010 1000 → a b c | “7” |
| 3 | 10 | ① AC |
| 17 | 20 | ① V |
| 11–14 | BF AF AF AF | “0.” “0” “0” “0” (AF = a b c d e f) |
| 9 | 00 | — |
| 16 | 01 | ② Hz |
| 24 | 11 | AUTO; nibble baixo = 1 (não documentado) |
| 18 | 06 | não documentado |

Resultado: **0.287 V AC** / **0.000 Hz**, AUTO.

#### 3.7.3 RTD#2 (captura real)

`00 00 10 00 BF A0 6D 6F 00 | 00 BF AF AF AF 00 01 20 06 | 00 52 52 52 52 10 00 00 00`

Dígitos: BF “0.”, A0 “1”, 6D “5”, 6F (0110 1111 → a c d e f g) “6”. Resultado: **0.156 V AC** / **0.000 Hz**, AUTO (nibble baixo do byte 24 = 0).

#### 3.7.4 RTD#3 (capturas de 2026-10-22, 12 leituras idênticas, `captures/rtd-acv.txt`)

`00 00 10 00 BF AF ED AF 00 | 00 BF AF AF AF 00 01 20 06 | 00 52 52 52 52 10 00 00 00`

ED = 1110 1101 → a b c d f g = “9”. Resultado: **0.090 V AC** / **0.000 Hz**, AUTO.

### 3.8 Bytes não documentados

| Byte | Valores observados | Notas |
|---|---|---|
| 2 | 00 (todas as capturas) | “don't care” [PDF-L p1] |
| 18 | 06 (todas as capturas reais em ACV) | “don't care” no PDF, mas o software original testa `b18.3` quando o código de função do principal é 5 (V AC) ou 6 (V DC) e liga a flag 0x01000000 [RE 0x0042520B..0x0042523F]. Significado desconhecido [NC]. Talvez faixa ou LoZ. |
| 24 bits 3–0 | 1 (RTD#1), 0 (RTD#2 e #3) | “don't care” [PDF-L p1]. Varia entre leituras [NC]. |
| 25–27 | 00 | “don't care” |

**Acréscimo (2026-10-23):** no BM525s em Ω com as pontas abertas, `b18` = FC e o nibble baixo de `b24` = F [CAP:rtd-ohm-open]. No ET-2517A, `b18` vale 00, 06 ou F6 e o nibble baixo de `b24` vale 0 ou F; os bytes 2 e 25–27 vieram 00 nos 30 quadros distintos da varredura (§8.4.14).

---

## 4. Leitura da memória do datalogger (Cs_HMD / Cs_NMD / Cs_CMD)

### 4.1 Ponteiro de memória [PDF-M p1]

| Comando | Efeito no ponteiro (P) |
|---|---|
| `88` (Cs_HMD) | P ← 0 (endereço A0_007Ah); devolve os bytes P..P+23; P ← P + 24 |
| `89` (Cs_NMD) | devolve P..P+23; P ← P + 24 |
| `8A` (Cs_CMD) | P ← P − 24; devolve P..P+23; P ← P + 24 (repete o último bloco) |

Confirmado por [CAP:probe] (8A após 88 = bloco 0) e [CAP:dump2] (8A após o último 89 = mesmo bloco; 88 seguinte = bloco 0).

### 4.2 Layout dos 36 bytes de cada resposta

| Byte(s) | Conteúdo | Uso |
|---|---|---|
| 1 | report ID = 00 | inútil (“useless byte”) |
| 2–9 | memória P+0 … P+7 | útil |
| 10 | report ID = 00 | inútil |
| 11–18 | memória P+8 … P+15 | útil |
| 19 | report ID = 00 | inútil |
| 20–27 | memória P+16 … P+23 | útil |
| 28 | report ID = 00 | inútil |
| 29 | Checksum byte0 (LSB) | só verificação |
| 30 | Checksum byte1 (MSB) | só verificação |
| 31–36 | don't care (obs. 00) | inútil |

Fonte: [PDF-M p1–p2], tabelas *Returned bytes*. No Linux (payload de 8 bytes por relatório): dados = `r1[0..7] + r2[0..7] + r3[0..7]`; checksum = `r4[0] | r4[1] << 8`.

### 4.3 Checksum

`checksum = (Σ bytes 1..27) mod 65536`, transmitido **little-endian** em 29 (LSB) e 30 (MSB) [PDF-M p1: “=Sum of returned byte No. 1 ~ 27”]. Como os report IDs valem 0, é igual à soma dos 24 bytes de dados.

- Confirmado em **todos os 190 blocos** dos dois dumps [CAP:dump1, dump2]. Exemplos: HMD 0x0597 (§4.13) e último bloco 0x01BA.
- O software original soma os 24 bytes úteis numa `word` e compara com `b29 + (b30 << 8)` [RE 0x00426D31..0x00426DE2 e 0x00426ECC..0x00426F7D].

### 4.4 Endereços × ponteiro

- Endereços de 3 bytes no formato `A2_A1A0h` (ex.: `A0_007Ah` = A2 0xA0, A1 0x00, A0 0x7A). Nos campos PSn/NSn são transmitidos **LSB primeiro** (`Addr0`, `Addr1`, `Addr2`) [PDF-M p5, Table 2].
- **Conversão endereço → ponteiro, como no software original** [RE 0x00430462..0x0043048C]:
  `P = ((A2 − 0xA0) div 2) · 0x10000 + A1 · 0x100 + A0 − 0x7A`
  - Na página A0 equivale a `P = endereço − A0_007Ah`. Confirmado: NS1 = A0_095Ah ↔ P = 2272 = TotalBytes; o início da sessão A0_0080h ↔ P = 6 [CAP:dump1].
  - [INF] O byte alto parece ser o byte de seleção de uma EEPROM I²C de 128 KiB (A0 = bloco 0, A2 = bloco 1; A1/A3 = os mesmos com o bit de leitura). Isso explica o “A3_FFFFh last byte” [PDF-M p3, p7]: capacidade ≈ 0x20000 − 0x7A = 130 950 bytes de log. **Não validado**: o dump real cabe inteiro na página A0.
- **Erro do PDF** [PDF-M p1–p2, coluna *Address*]:
  - a partir do ponteiro 31 o endereço pula A0_0099h (o ponteiro 31 aparece como A0_009Ah);
  - os blocos de 5ª a 7ª amostras repetem A0_00A1h..A0_00A6h;
  - depois a coluna segue desalinhada (ex.: ponteiro 109 = “A0_00D6h”, quando o correto é A0_00E7h).
  - A Table 2 [PDF-M p5] também reinicia a coluna *Memory pointer* (20..25) a cada amostra.
  - **Use sempre o ponteiro sequencial**. As colunas *Memory pointer* (até 23) e *Description* da tabela *Returned bytes* estão corretas.
  - **Correção (revisão)**, conferida nas páginas renderizadas:
    - *Returned bytes* [PDF-M p1–p2]: as colunas *Memory pointer* e *Description* estão corretas **em toda a extensão** (0..109), não só até 23. Por exemplo, o ponteiro 44 é a 5ª ML_0 e o 104 é a 15ª ML_0, o que bate com `20 + 6·(k−1)`. O erro fica só na coluna *Address*: os ponteiros 31..43 aparecem deslocados em +1 (A0_009Ah..A0_00A6h). A 4ª amostra (ponteiros 38..43) já aparece como A0_00A1h..A0_00A6h, e a 5ª, a 6ª e a 7ª repetem A0_00A1h..A0_00A6h. Depois a coluna segue a partir de A0_00A7h (ponteiro 62) até A0_00D6h (ponteiro 109; o correto é A0_00E7h).
    - Table 2 [PDF-M p5–p6]: a coluna *Memory pointer* **não** reinicia a cada amostra. Ela repete 20..25 **uma vez**, na 2ª amostra, e depois continua 26, 27, …, ou seja, fica 6 abaixo do valor real da 2ª amostra em diante (15ª amostra = “98..103”; o correto é 104..109). A coluna *Address* da Table 2 tem o mesmo defeito da tabela *Returned bytes*: a 2ª amostra está correta (A0_0094h..A0_0099h), a 3ª começa em A0_009Bh e pula A0_009Ah, da 4ª à 7ª todas mostram A0_00A1h..A0_00A6h, e a 15ª termina em A0_00D6h.
    - Regra única e verificada no dump: `ponteiro(ML_0 da amostra i, 0-based, 1ª sessão) = 20 + 6·i` (dual) e `endereço = A0_007Ah + ponteiro` na página A0.

### 4.5 Memory Map (Table 2), cabeçalho global [PDF-M p5]

| Ponteiro | Endereço | Campo | Significado | Dump real |
|---|---|---|---|---|
| 0 | A0_007A | Model_Id | “DMM Model ID = 00h or 01h” | 01 |
| 1–3 | A0_007B..7D | TotalBytes_0..2 | bytes logados (LE, 24 bits) | E0 08 00 → 2272 |
| 4–5 | A0_007E..7F | TotalSession_0..1 | nº de sessões (LE, máx. 999) | 01 00 → 1 |
| 6 | A0_0080 | DLE | início da 1ª sessão (= EEh) | EE |

- **TotalBytes** conta do ponteiro 0 (Model_Id) até o ETX da última sessão, **inclusive**. No dump: ETX no ponteiro 2271 ⇒ TotalBytes = 2272 [CAP:dump1]. Então `nº de comandos = ceil(TotalBytes / 24)` (1 × 88 + o resto em 89).
- Significado de Model_Id 00 × 01 não documentado [NC]. O BM525s deste kit devolve 01.

### 4.6 Session page e encadeamento [PDF-M p2–p3, p5–p7]

Cada sessão começa num endereço *H* (a 1ª em A0_0080h; as seguintes em `NS(n−1)_Addr`):

| Offset | Campo | Valor / significado |
|---|---|---|
| H+00 | DLE | EEh (início de sessão) |
| H+01 | STX | A0h |
| H+02..04 | PSn_Addr0..2 | endereço da sessão anterior (“don't care” na 1ª) |
| H+05..07 | NSn_Addr0..2 | endereço da próxima sessão (“don't care” na última) |
| H+08 | Bfunction | Table 3 |
| H+09 | Bselect | Table 3 |
| H+0A | Bstatus | bit 7: single/dual; bit 5: A/mA; bit 4: °C/°F; bits 3–0: intervalo (Table 3-1) |
| H+0B..0D | SPn_Length0..2 | comprimento dos dados da sessão (LE, 24 bits) = SPn_DL |
| H+0E .. H+0D+DL | dados | amostras: 6 bytes (dual, Table 4) ou 3 bytes (single, Table 5) |
| H+0E+DL | DLE | **EEh** (fim de sessão) |
| H+0F+DL | ETX | **C0h** |
| H+10+DL | próxima sessão (DLE EEh / STX A0h) | deve ser igual a NSn_Addr |

- O PDF escreve os marcadores como “A0_008Dh + SP1_DL + 1/+2” (1ª sessão) e “NS1_Addr + 0Dh + SP2_DL + 1/+2” (seguintes) [PDF-M p2, p6].
- Os valores DLE/ETX de fim (EEh/C0h) estão em [PDF-M p6, coluna *Note*]. Confirmados: EE C0 nos ponteiros 2270–2271 [CAP:dump1].
- Fim da memória física: “A3_FFFFh — last byte” [PDF-M p3, p7].
- O software original **não** verifica DLE/STX/ETX. Ele percorre `TotalSession` sessões seguindo `NSn_Addr` [RE 0x004308B1..0x00430911]. A implementação .NET deve verificar os marcadores e, se divergirem, avisar e preferir `H+10+DL` ou `NSn_Addr`, conforme o que tiver marcadores válidos.
- Nº de amostras = `SPn_DL / 6` (dual) ou `SPn_DL / 3` (single) [RE 0x0043053D..0x0043057D].

### 4.7 Function Encoder (Table 3) [PDF-M p8]

| Bfunction | Bselect | Bstatus bit 7 (dual) | bit 5 (A) | bit 4 (°F) | Principal | Secundário |
|---|---|---|---|---|---|---|
| 01 | 00 | 1 | x | x | ACV | Hz |
| 01 | 01 | 1 | x | x | Hz | ACV |
| 02 | 00 | 0 | x | x | DCV | — |
| 02 | 01 | 1 | x | x | DCV | ACV |
| 02 | 02 | 1 | x | x | DCV + ACV | ACV |
| 03 | 00 | 0 | x | x | DCmV | — |
| 03 | 01 | 1 | x | x | DCmV | ACmV |
| 03 | 02 | 1 | x | x | DCmV + ACmV | ACmV |
| 03 | 03 | 0 | x | x | Hz | — |
| 03 | 04 | 0 | x | x | Duty | — |
| 04 | 00 | 1 | x | x | ACmV | Hz |
| 04 | 01 | 1 | x | x | Hz | ACmV |
| 05 | 00 | 0 | x | x | OHM | — |
| 05 | 01 | 0 | x | x | Continuidade | — |
| 05 | 02 | 0 | x | x | nS | — |
| 06 | 00 | 0 | x | 0/1 | T1 (°C/°F) | — |
| 06 | 01 | 0 | x | 0/1 | T2 (°C/°F) | — |
| 06 | 02 | 1 | x | 0/1 | T1 (°C/°F) | T2 (°C/°F) |
| 06 | 03 | 1 | x | 0/1 | T1−T2 (°C/°F) | T2 (°C/°F) |
| 07 | 00 | 0 | x | x | Capacitância | — |
| 07 | 01 | 0 | x | x | Diodo | — |
| 08 | 00 | 0 | 1 / 0 | x | DCA / DCmA | — |
| 08 | 01 | 1 | 1 / 0 | x | DCA / DCmA | ACA / ACmA |
| 08 | 02 | 1 | 1 / 0 | x | DCA+ACA / DCmA+ACmA | ACA / ACmA |
| 08 | 03 | 1 | 1 / 0 | x | ACA / ACmA | Hz |
| 09 | 00 | 0 | x | x | DCuA | — |
| 09 | 01 | 1 | x | x | DCuA | ACuA |
| 09 | 02 | 1 | x | x | DCuA + ACuA | ACuA |
| 09 | 03 | 1 | x | x | ACuA | Hz |

Notas [PDF-M p8]:
- \*1: o nibble baixo de Bstatus é o intervalo (Table 3-1).
- \*2: bit 7 = 0 → single, 1 → dual.
- \*3: bit 5 só para mA/A (0 = mA, 1 = A).
- \*4: bit 4 só para temperatura (0 = °C, 1 = °F).
- Combinações fora da tabela → função desconhecida. Exponha os bytes brutos.
- [RE 0x00434CC2..0x00434D1E] Para Bfunction 01, o software original dá ao principal o código 5 (V+AC) e ao secundário 0x400 (Hz), e inverte com Bselect 01. Coerente com a tabela.

### 4.8 Intervalo de logging (Table 3-1, nibble baixo de Bstatus) [PDF-M p8]

| Bits 3–0 | Intervalo (s) | | Bits 3–0 | Intervalo (s) |
|---|---|---|---|---|
| 0000 | 0,05 | | 1000 | 10 |
| 0001 | 0,1 | | 1001 | 15 |
| 0010 | 0,5 | | 1010 | 30 |
| 0011 | 1 | | 1011 | 60 (1 min) |
| 0100 | 2 | | 1100 | 120 (2 min) |
| 0101 | 3 | | 1101 | 180 (3 min) |
| 0110 | 4 | | 1110 | 300 (5 min) |
| 0111 | 5 | | 1111 | 600 (10 min) |

[INF] A memória não guarda data/hora: nenhum campo de relógio na Table 2. O tempo de cada amostra é `i × intervalo` a partir do início da sessão.

### 4.9 Formato das amostras (Tables 4 e 5) [PDF-M p9], com correção

Dual = 6 bytes `ML_0 ML_1 ML_2 SL_0 SL_1 SL_2`. Single = 3 bytes `ML_0 ML_1 ML_2`.

| Byte | bit 7 | bit 6 | bit 5 | bit 4 | bits 3–0 |
|---|---|---|---|---|---|
| ML_0 | sinal (1 = “−”) | OL | LB (bateria fraca) | x | MLRange (Table 6) |
| ML_1 | D1 (nibble alto) || | | D0 (nibble baixo) |
| ML_2 | D3 (nibble alto) || | | D2 (nibble baixo) |
| SL_0 | sinal | OL | x | x | SLRange |
| SL_1 | D1 || | | D0 |
| SL_2 | D3 || | | D2 |

> **Correção / precisão**: D3..D0 **não são dígitos BCD**. São os 4 nibbles de um inteiro **binário de 16 bits little-endian**: `count = ML_1 + 256 · ML_2` (o mesmo para SL).

Evidências:
1. **[CAP:dump1]** A sequência real decai suavemente pelos valores ML_1 = 0D, 0C, 0B, 0A, 09, 08, 07, 06, com oscilações 0A↔09 e 0A↔0B típicas de ruído ±1 contagem. 0x0A–0x0D são inválidos em BCD.
2. **[RE 0x00430C3A..0x00430C7C]** O software original calcula `count = byte[3] << 8 + byte[2]` (ML_2·256 + ML_1). Em [RE 0x00431360] formata com `sprintf("%04.4i", count)` (string no VA 0x004CF79C). **Correção (revisão final):** o ponto não é inserido conforme a faixa nesse passo: o texto intermediário é sempre `d.ddd` (1º dígito, ponto, 3 dígitos [BIN 0x431383..0x431470]), e a faixa entra como expoente (0x434974: V 0..3, mV −2/−1). O texto final da tela de importação e do CSV sai de `sprintf("%05.*f" | "%06.*f" | "%04.*f", 3 − e, d.ddd × 10^e)` [BIN 0x431877..0x4319FA]: 4 dígitos com zeros à esquerda e as casas da faixa (287 em 99.99 V → `02.87`). Temperatura: `"%03.0f"` [BIN 0x432015..0x43208B]. O overload sai como `.OL`, `O.L` ou `OL.` conforme a faixa [BIN 0x431A14..0x431AF9].

Faixa válida: 0..9999 [INF: display de 10 000 contagens]. Valores maiores devem ser marcados como suspeitos.

Valor: `value = (sinal ? −1 : +1) · count / 10^casas`, com `casas` e unidade da Table 6. Com OL = 1, o valor é `null`/overload: o software original mostra “OL”, ou “----” em temperatura [RE 0x00430C9B..0x00430D7E; strings em 0x004CF782/0x004CF77D].

### 4.10 Range Table (Table 6) com casas decimais [PDF-M p9]

| Função | 0000 | 0001 | 0010 | 0011 | 0100 | 0101 | 0110 |
|---|---|---|---|---|---|---|---|
| ACV, DCV, ACV+DCV | 9.999 V (3) | 99.99 V (2) | 999.9 V (1) | 9999 V\* (0) | N/A | N/A | N/A |
| ACmV, DCmV, ACmV+DCmV | 60.00 mV (2) | 600.0 mV (1) | N/A | N/A | N/A | N/A | N/A |
| Hz | 9.999 Hz (3) | 99.99 Hz (2) | 999.9 Hz (1) | 9.999 kHz (3) | 99.99 kHz (2) | 999.9 kHz (1) | 9.999 MHz (3) |
| Duty | 99.99 % (2) | 100.0 % (1) | N/A | N/A | N/A | N/A | N/A |
| Ω | 600.0 Ω (1) | 6.000 kΩ (3) | 60.00 kΩ (2) | 600.0 kΩ (1) | 6.000 MΩ (3) | 60.00 MΩ (2) | N/A |
| Continuidade •))) | 600.0 Ω (1) | N/A | N/A | N/A | N/A | N/A | N/A |
| nS | 99.99 nS (2) | N/A | N/A | N/A | N/A | N/A | N/A |
| CAP | 60.00 nF (2) | 600.0 nF (1) | 6.000 µF (3) | 60.00 µF (2) | 600.0 µF (1) | 6.000 mF (3) | 25.00 mF (2) |
| Diodo | 2.000 V (3) | N/A | N/A | N/A | N/A | N/A | N/A |
| ACA, DCA, ACA+DCA | 6.000 A (3) | 60.00 A (2) | N/A | N/A | N/A | N/A | N/A |
| ACmA, DCmA, ACmA+DCmA | 60.00 mA (2) | 600.0 mA (1) | N/A | N/A | N/A | N/A | N/A |
| ACuA, DCuA, ACuA+DCuA | 600.0 µA (1) | 6000 µA (0) | N/A | N/A | N/A | N/A | N/A |
| Temperatura (T1, T2, T1−T2) | *fora da Table 6*: 0 casas [RE] [NC] | | | | | | |

- Entre parênteses: casas decimais de `count`. \*9999 V é “not actual hardware range, software range only” [PDF-M p9]. Faixas 0111–1111 não estão definidas.
- **Confirmação por RE** [RE 0x00434974]: a função devolve, por função e faixa, um expoente *e* tal que `valor = count · 10^(e−3)` na unidade base da função. As unidades base são V, Hz, %, Ω, nS, µF, A e grau. Os expoentes extraídos dão exatamente as casas da tabela acima (`casas = 3 − e`, contando o prefixo):
  - V: 0..3;
  - mV: −2, −1;
  - Hz: 0..6;
  - Duty: 1, 2;
  - Ω: 2..7;
  - nS: 1;
  - CAP: −2..4 (base µF);
  - diodo: 0;
  - A: 0, 1;
  - mA: −2, −1;
  - µA: −4, −3.
- **Temperatura**: Bfunction 06 recebe o índice 0x17 [RE 0x00434F7A], ao qual a função 0x00434974 devolve *e* = 3 [RE 0x00434C61]. Ou seja, o software original mostra a temperatura como inteiro, sem ponto decimal. **Não há captura de sessão de temperatura para validar** [NC].

### 4.11 Algoritmo de leitura recomendado (robusto)

```text
open(VID=0x0820, PID=0x0001)
blk0 = transact(0x88, expect=4 relatórios, timeout≥1 s)   # ~390 ms de latência
valida(blk0)                                             # IDs 1/10/19/28 == 0, checksum
mem = blk0.data24
total = mem[1] | mem[2]<<8 | mem[3]<<16
n = ceil(total / 24)
for k in 1 .. n-1:
    blk = transact(0x89)
    tentativas = 0
    while (timeout ou checksum inválido) and tentativas < 4:
        descarta relatórios residuais
        blk = transact(0x8A)         # relê o mesmo bloco
        tentativas += 1
    se falhar: aborta e recomeça do 0x88 (o ponteiro do medidor ficou indefinido)
    mem += blk.data24
mem = mem[0 : total]
close()
```

- Não envie `89` depois de cobrir `total` (§2.7).
- **Correção (revisão): ambiguidade da recuperação com `8A`.** Pela semântica “P − 24 e lê” [PDF-M p1], o `8A` só relê o bloco certo se o medidor **processou** o `89` que falhou, isto é, se o ponteiro já avançou.
  - Checksum inválido: algum relatório chegou, então o `89` foi processado e o `8A` é seguro.
  - Timeout com **0 relatórios** recebidos: não dá para saber se o `89` chegou ao medidor. Se não chegou, o `8A` devolve o bloco **anterior**, e esse bloco duplicado desalinharia todo o resto. Comparar com o bloco anterior não resolve, porque blocos consecutivos podem ser legitimamente idênticos (blocos 1–5 do dump real).
  - Recomendação para o port [INF]: use `8A` só quando ao menos 1 relatório da resposta ao `89` tiver chegado. Com 0 relatórios, descarte os residuais e recomece do `88`. Reenvie os `89` até o bloco k, conferindo que os blocos já lidos coincidem byte a byte; o custo é ≈ 390 ms + k × 75 ms. O fluxograma da Brymen [PDF-M p4] e o original [RE 0x00426E48] usam `8A` nos dois casos, e isso não foi testado com falhas reais.
- Pausa entre comandos: o original usa 0,001 s [PDF-M p4] e não espera depois do `89` [RE]. O probe usou 300 ms sem problemas. Recomendação: 0–10 ms.
- **Ajuste pelo achado de hardware de 2026-10-22 (§2.7):** espere **200 ms** depois da resposta do `88` antes do 1º `89`. Uma falha no `88` (timeout, checksum ou quadro inválido) se recupera **reenviando o `88`**, nunca com `8A`: logo depois do `88`, o `8A` avançou o ponteiro como um `89` em 9 de 16 tentativas. O laço acima já só usa `8A` depois de um `89`, que é o caso seguro (o `8A` releu o bloco do `89` em todas as tentativas).

### 4.12 Algoritmo de decodificação da memória

1. Model_Id = `mem[0]`; TotalBytes = `mem[1..3]` LE; TotalSession = `mem[4..5]` LE.
2. `p = 6` (A0_0080h). Para s = 1..TotalSession:
   1. Verificar `mem[p] == 0xEE` e `mem[p+1] == 0xA0`.
   2. Ler PS = `mem[p+2..p+4]` e NS = `mem[p+5..p+7]` (LE → `A2_A1A0`); Bfunction = `mem[p+8]`, Bselect = `mem[p+9]`, Bstatus = `mem[p+10]`; DL = `mem[p+11..p+13]` (LE).
   3. dual = `Bstatus.7`; tam = dual ? 6 : 3; intervalo = Table 3-1[`Bstatus & 0x0F`]; (principal, secundário) = Table 3(Bfunction, Bselect, Bstatus.5, Bstatus.4).
   4. Amostras: para i = 0 .. DL/tam − 1, `x = mem[p+14+i·tam ..]`. Principal: sinal/OL/LB/faixa de `x[0]`, `count = x[1] | x[2]<<8`. Se dual, secundário de `x[3..5]`. Valor pela §4.9/§4.10; `t = i · intervalo`.
   5. Verificar `mem[p+14+DL] == 0xEE` e `mem[p+15+DL] == 0xC0`. Se `DL % tam ≠ 0`, avisar.
   6. Próxima sessão: `p = p + 16 + DL`. Conferir com `ptr(NS)` (§4.4) e, se divergirem, registrar aviso.
3. Ao final, `p` deve ser igual a TotalBytes.

### 4.13 Decodificação manual das capturas

#### 4.13.1 Cs_HMD (comando 88)

`00 01 E0 08 00 01 00 EE A0 | 00 18 04 A0 5A 09 A0 01 00 | 00 80 CA 08 00 00 0D 00 00 | 00 97 05 00 00 00 00 00 00`

| Byte | Hex | Ponteiro / endereço | Campo | Interpretação |
|---|---|---|---|---|
| 1 | 00 | — | report ID | — |
| 2 | 01 | 0 / A0_007A | Model_Id | 01 |
| 3–5 | E0 08 00 | 1–3 / A0_007B..7D | TotalBytes | 0x0008E0 = **2272** |
| 6–7 | 01 00 | 4–5 / A0_007E..7F | TotalSession | **1** |
| 8 | EE | 6 / A0_0080 | DLE | início da sessão 1 ✔ |
| 9 | A0 | 7 / A0_0081 | STX | ✔ |
| 10 | 00 | — | report ID | — |
| 11–13 | 18 04 A0 | 8–10 / A0_0082..84 | PS1_Addr0..2 | A0_0418 (don't care na 1ª sessão) |
| 14–16 | 5A 09 A0 | 11–13 / A0_0085..87 | NS1_Addr0..2 | **A0_095A** → ponteiro 2272 |
| 17 | 01 | 14 / A0_0088 | Bfunction | 01 |
| 18 | 00 | 15 / A0_0089 | Bselect | 00 → **ACV (principal) + Hz (secundário)** |
| 19 | 00 | — | report ID | — |
| 20 | 80 | 16 / A0_008A | Bstatus | 1000 0000: **dual**; intervalo 0000 = **0,05 s** |
| 21–23 | CA 08 00 | 17–19 / A0_008B..8D | SP1_Length | 0x0008CA = **2250 bytes** = 375 amostras duplas |
| 24 | 00 | 20 / A0_008E | 1ª ML_0 | +, sem OL, sem LB, faixa 0 (9.999 V) |
| 25–26 | 0D 00 | 21–22 / A0_008F..90 | 1ª ML_1, ML_2 | count = 0x000D = 13 → **0.013 V** |
| 27 | 00 | 23 / A0_0091 | 1ª SL_0 | +, faixa 0 (9.999 Hz) |
| 28 | 00 | — | report ID | — |
| 29–30 | 97 05 | — | Checksum | 0x0597 = 1431 |
| 31–36 | 00… | — | don't care | — |

Conferência do checksum (soma dos bytes 1–27):
- R1: 00+01+E0+08+00+01+00+EE+A0 = 0x278 (632)
- R2: 00+18+04+A0+5A+09+A0+01+00 = 0x1C0 (448)
- R3: 00+80+CA+08+00+00+0D+00+00 = 0x15F (351)
- Total: 632 + 448 + 351 = **1431 = 0x0597** ✔

Coerência: a sessão começa em A0_0080, os dados em A0_008E e o fim em A0_008E + 2250 = A0_0958 (DLE) / A0_0959 (ETX). A próxima sessão iria para A0_095A = NS1 ✔. TotalBytes = 0x959 − 0x7A + 1 = 2272 ✔.

#### 4.13.2 Cs_NMD (comando 89), 1º bloco (repetido 3× na sessão anterior e idêntico no dump)

`00 00 00 00 0D 00 00 00 00 | 00 00 0D 00 00 00 00 00 0D | 00 00 00 00 00 0D 00 00 00 | 00 34 00 00 00 00 00 00 00`

| Byte | Hex | Ponteiro | Endereço | Campo |
|---|---|---|---|---|
| 2 | 00 | 24 | A0_0092 | 1ª SL_1 |
| 3 | 00 | 25 | A0_0093 | 1ª SL_2 → secundário count 0 → 0.000 Hz |
| 4 | 00 | 26 | A0_0094 | 2ª ML_0 |
| 5 | 0D | 27 | A0_0095 | 2ª ML_1 |
| 6 | 00 | 28 | A0_0096 | 2ª ML_2 → 13 → 0.013 V |
| 7–9 | 00 00 00 | 29–31 | A0_0097..99 | 2ª SL_0..2 → 0.000 Hz |
| 11–13 | 00 0D 00 | 32–34 | A0_009A..9C | 3ª ML → 0.013 V |
| 14–16 | 00 00 00 | 35–37 | A0_009D..9F | 3ª SL → 0.000 Hz |
| 17–18 | 00 0D | 38–39 | A0_00A0..A1 | 4ª ML_0, ML_1 |
| 20 | 00 | 40 | A0_00A2 | 4ª ML_2 → 0.013 V |
| 21–23 | 00 00 00 | 41–43 | A0_00A3..A5 | 4ª SL → 0.000 Hz |
| 24–26 | 00 0D 00 | 44–46 | A0_00A6..A8 | 5ª ML → 0.013 V |
| 27 | 00 | 47 | A0_00A9 | 5ª SL_0 |
| 29–30 | 34 00 | — | — | checksum 0x0034 = 4 × 0x0D ✔ |

- **Por que o `89` repetido devolve sempre o mesmo**: 24 bytes = exatamente 4 amostras duplas. Enquanto as amostras são idênticas (as 22 primeiras têm count 13), blocos consecutivos são iguais. Não é falha do avanço do ponteiro: o dump completo mostra a mudança para 0x0C a partir do bloco que contém a amostra 23 [CAP:dump1].
  - **Correção (revisão):** “amostra 23” é a 23ª amostra, de **índice 22** na numeração 0-based da §4.14. Seu ML_1 está no ponteiro 153, no **6º** `89` (bloco 144..167), que é o 1º bloco diferente: os blocos dos `89` 1º a 5º são idênticos [CAP:dump1].
- A coluna *Address* do PDF para este bloco erra a partir do byte 9 (A0_009Ah em vez de A0_0099h) [PDF-M p1].

#### 4.13.3 Último bloco do dump (94º `89`, ponteiros 2256..2279)

`00 00 00 00 06 00 00 00 00 | 00 00 06 00 00 00 00 EE C0 | 00 00 00 00 00 00 00 00 00 | 00 BA 01 00 …`

Contém o fim da última amostra (count 6 → 0.006 V) e o marcador **DLE EE / ETX C0** nos ponteiros 2270/2271 (A0_0958/A0_0959). Os 8 bytes seguintes são 00 (além de TotalBytes). Checksum 0x01BA = 6 + 6 + 0xEE + 0xC0 = 442 ✔.

### 4.14 Resultado do dump completo (`captures/memory-dump-1.txt`)

| Item | Valor |
|---|---|
| Comandos | 1 × `88` + 94 × `89` = 95 = ceil(2272/24); o 96º (`89`) ficou sem resposta |
| Checksums | 95/95 corretos; report IDs 95/95 corretos |
| Reprodutibilidade | `memory-dump-2.txt` (88 + 94 × 89 + 8A + 88) idêntico byte a byte |
| Model_Id | 01 |
| TotalBytes / TotalSession | 2272 / 1 |
| Sessão 1 | início A0_0080 (ponteiro 6); PS A0_0418 (don't care); NS A0_095A (ponteiro 2272) |
| Função | Bfunction 01 / Bselect 00 / Bstatus 80 → **ACV (principal, modo AC) + Hz (secundário)**, dual |
| Intervalo | 0,05 s |
| Dados | 2250 bytes = **375 amostras** (t = 0 a 18,70 s); fim EE C0 ✔ |
| Flags | todas as amostras com ML_0 = 00 e SL_0 = 00: positivas, sem OL, sem bateria fraca, faixa 0 (9.999 V / 9.999 Hz) |
| Principal | count 13 → 6, ou seja, **0.013 V → 0.006 V** (decaimento com ruído ±1 contagem); valores distintos 6..13 |
| Secundário | sempre 0 → **0.000 Hz** |
| Primeiras leituras | #0 0.013 V / 0.000 Hz; #1 0.013 V; … #21 0.013 V; #22 0.012 V |
| Últimas leituras | #372 0.006 V; #373 0.006 V; #374 0.006 V (todas com 0.000 Hz) |

Sequências (amostra inicial–final: count):

```text
0–21:13  22–23:12  24–25:13  26–31:12  32:11  33–37:12  38:11  39–44:12  45–77:11  78:10  79–80:11  81:10
82–83:11  84–98:10  99:11  100–116:10  117:9  118–127:10  128–133:9  134–137:10  138:9  139:10  140–141:9
142:10  143–145:9  146–149:10  150–162:9  163:8  164–168:9  169–228:8  229–230:7  231–236:8  237:7  238–239:8
240:7  241–246:8  247–248:7  249:8  250–253:7  254–255:8  256–331:7  332:6  333–341:7  342–344:6  345–349:7
350–354:6  355:7  356–358:6  359–361:7  362–364:6  365–366:7  367–374:6
```

A lista completa (count por amostra, primeiras e últimas 3 decodificadas) está em `test-vectors.json` → `memory-dump-1-decoded`.

---

## 5. Correções e divergências em relação aos PDFs

1. **RTD, bytes 10/11** [PDF-L p1, Table 1 e Example; PDF-N p1, Example]: o report ID II está no **byte 10** e o dígito 5 (`5b…5d`, com `4p` no bit 4) no **byte 11**. Confirmado por capturas, pela Table 1 do PDF-N e por RE.
2. **VID** [PDF-L p2, PDF-M p4]: o fluxograma diz 0x82; o real é **0x0820**.
3. **Ponto decimal**: o bit 4 de cada byte de dígito acende o DP **à direita** daquele dígito (o PDF só nomeia 1p..6p). Confirmado por FIG 1, exemplo, capturas e RE.
4. **D3..D0 das amostras** [PDF-M p9, Tables 4/5]: não são BCD. São os nibbles de `count = X_1 + 256·X_2` (binário LE). Confirmado pelo dump (valores 0x0A–0x0D) e por RE.
5. **Coluna *Address*** [PDF-M p1–p2] e **coluna *Memory pointer* da Table 2** [PDF-M p5] têm erros de cópia a partir do ponteiro 31 / 2ª amostra. Use `ponteiro = n` sequencial e `endereço = A0_007Ah + ponteiro` (na página A0).
   - **Correção (revisão):** a coluna *Address* da **Table 2** [PDF-M p5–p6] tem os mesmos erros. A coluna *Memory pointer* da Table 2 repete 20..25 só na 2ª amostra e depois fica deslocada em −6; não reinicia a cada amostra. A coluna *Memory pointer* da tabela *Returned bytes* está correta do início ao fim (§4.4).
6. **Marcadores de fim de sessão**: DLE = EEh, ETX = **C0h** (o início usa DLE EEh / STX A0h) [PDF-M p6]. Confirmado no dump.
7. **Table 2** [PDF-M p7] diz “Function details … (see Table 2)” e “2'nd Session Page Data Length” para as sessões 3..n: erros de texto. O correto é Table 3 e “n-ésima sessão”.
8. **“Model ID” do RTD**: os bytes 20–23 (0x52) são o código da família e coincidem com o “Command 2” do pedido. No BM82x ambos são 0x82 [PDF-N p1].
9. **Software original**: é Borland C++ Builder (VCL), não Delphi (string no offset 0x000C3240; **Correção (revisão):** antes 0x000C323F, deslocado em 1 byte).
10. **Report IDs**: os bytes “00h (Report ID)” existem só na API do Windows. No fio (e no hidraw do Linux) cada relatório de entrada tem 8 bytes [INF forte].
11. **Acréscimo (2026-10-23), PDF-N × PDF-L:** o PDF-N tem a Table 1 com os bytes 10/11 certos e chama os bytes 20–23 de “Model ID0..3 = 0x82”, mas o exemplo dele repete o erro dos bytes 10/11 e o fluxograma repete o `Vendor_ID: 0x82`. Fora o código da família e a lista de comandos, as duas especificações são idênticas: a FIG 1 é a mesma imagem e o resto da Table 1 é igual bit a bit (§8.2).

## 6. Questões em aberto

1. Padrão exato do LCD em OL (`0.L`, ` 0L `, posição do DP) e se o “-” do OL acende o sinal. Faltam capturas em Ω com pontas abertas e em V acima da faixa.
   - **Atualização (2026-10-23):** respondida em parte. Há quatro padrões reais (`0.L`, `0L.`, `.0L` e `0L`) e a regra da §3.4 vale em todos. Faltam um OL com o sinal aceso e um OL no secundário.
2. Temperatura: casas decimais na memória (o RE indica 0) e formato no RTD (3 dígitos + ‘C’/‘F’?). Faltam sessão e RTD em °C/°F.
   - **Atualização (2026-10-23):** o RTD do ET-2517A sem termopar confirma “3 caracteres + F” (`---F`, §8.4.6). Faltam uma temperatura numérica (o ponto decimal, se houver) e uma sessão de temperatura na memória do BM52x.
3. Significado de `b18` (obs. 06; o original testa o bit 3 em V AC/DC), do nibble baixo de `b24` (0/1) e dos símbolos @, [H], [C], [R].
   - **Atualização (2026-10-23):** no ET-2517A, o [R] (`b24.5`) acende com o REC, junto de MIN, MAX e AVG (§8.4.10), o que apoia “R = gravação de MAX/MIN/AVG”. Valores novos: `b18` = 00, 06, F6 (ET-2517A) e FC (BM525s em Ω); nibble baixo de `b24` = F.
4. Por que o software original rejeita RTD com `b9.4` (@) aceso e aceita no byte 23 os valores {82, 52, FF, F7, F3, F1, F0, E0, C0, 80, 00}.
   - **Atualização (2026-10-23):** o ET-2517A devolve 82 nos quatro bytes. Nenhum dos outros valores foi visto; o `HidReadModel` do original os converte em 0x82 [RE 0x424B5A..0x424BE4], o que sugere outros firmwares BM82x [INF, NC].
5. Significado de Model_Id = 00 × 01.
6. Mapeamento de endereço para as páginas A2/A3 (> 64 KiB de log). Validar com um log grande ou várias sessões.
7. Encadeamento real com várias sessões (PS/NS, single display de 3 bytes). Só há 1 sessão dual no dump.
8. Comportamento no Linux (hidraw: 8 bytes por read, write com report ID 0) a confirmar em arm64/x64.
9. Se o cabo responde com o medidor desligado ou fora do modo de comunicação, e com o quê. Não testado.
10. **Correção (revisão), nova questão:** o que o medidor faz com um `89` cuja resposta não chegou (0 relatórios)? O ponteiro avança ou não? Isso decide se o `8A` de recuperação é seguro (§4.11). É preciso testar com desconexão ou interferência óptica proposital.
11. **Nova questão (2026-10-22):** por que o 1º `66` depois do último bloco da memória leva ≈ 2 s e volta em traços (§2.7)? A demora cresce com memórias maiores, com várias sessões ou depois de um `89` além do fim? Os 3 s de espera da port cobrem o dump de 2272 bytes medido.
12. **Nova questão (2026-10-23):** por que o ET-2517A descarta pedidos de tempo real, e por que a taxa cresce com o intervalo entre consultas (§8.5)? Hipótese [INF, NC]: o receptor óptico do medidor só fica atento logo depois de uma troca e passa a amostrar o pedido de tempos em tempos quando ocioso.
13. **Nova questão (2026-10-23):** com o orçamento de ≈ 2 s da §8.5, ainda sobra algum pedido sem resposta? Não foi medido. Com 4 tentativas (≈ 1,2 s), 1 a 5 de 40 consultas falharam [CAP:rate2].
14. **Nova questão (2026-10-23):** o que o BM525s faz com um `66` reenviado enquanto ainda prepara a resposta lenta do 1º `66` depois da memória (≈ 2 s, §2.7): ignora, enfileira ou responde duas vezes? Não testado. Por isso a §8.5 não reenvia nesse caso quando a família já é BM52x, e faz o drain de recuperação depois de qualquer reenvio.
15. **Nova questão (2026-10-23):** o secundário do ET-2517A em faixa manual de ACV vem com só o ponto 5p e o ② Hz acesos e os 4 dígitos apagados (§8.4.9). O que o LCD mostra? A regra da §3.6 o trata como apagado (`null`).
16. **Nova questão (2026-10-23):** algum BM52x ignora o `82 66`? Só o BM525s foi testado; o fallback para `52 66` da §8.6 cobre esse caso. E outros modelos BM82x: algum devolve no byte 23 um dos valores que o original converte em 0x82 (questão 4)?
17. **Nova questão (2026-10-23):** o ET-2517A responde a `82 66` com o medidor em APO, em modo de configuração ou com o dial em OFF, e com o quê? Não testado (é o caso da questão 9 para o BM82x).

## 7. Arquivos

| Arquivo | Conteúdo |
|---|---|
| `docs/protocol/bm52x-usb-protocol.md` | este documento |
| `docs/protocol/test-vectors.json` | vetores de teste (RTD: exemplo do PDF corrigido, exemplo literal como vetor negativo, 3 capturas reais, 2 sintéticos marcados; memória: HMD, NMD, CMD, último bloco e dump completo decodificado). **Correção (revisão):** 2 vetores `8A` com captura salva foram acrescentados (§2.7), e o `cmd-current-again-after-hmd` foi marcado como sem log salvo. **Acréscimo (2026-10-23):** 13 vetores reais do ET-2517A e o exemplo do PDF-N corrigido, com `command` `"8266"`, `family` `"BM82x"` e `modelBytes` `"82828282"` (§8.4) |
| `docs/protocol/captures/memory-dump-1.txt` | dump bruto: 1 × 88 + 94 × 89 (+ linha de timeout do 96º); formato `<cmd> <hex 36 bytes>`, `#` = comentário |
| `docs/protocol/captures/memory-dump-2.txt` | dump de controle: 88 + 94 × 89 + 8A + 88 |
| `docs/protocol/captures/rtd-acv.txt` | RTD#1, RTD#2 e 12 leituras novas (ACV + Hz); formato `<cmd> <hex 27 bytes>` |
| `docs/protocol/captures/cmd-sequences-2026-10-22.txt` | `82 66` × `52 66` (quadros idênticos), sequência `88`, `8A`, `89`, `8A` e o `52 82` sem resposta (§2.7); formato `<Command2 Command3> <hex>` |
| `docs/protocol/captures/rtd-ohm-open.txt` | Acréscimo (2026-10-23): BM525s em Ω com as pontas abertas (`0.L` MΩ, §3.4) |
| `docs/protocol/captures/et2517a-commands-2026-10-23.txt` | Acréscimo (2026-10-23): ET-2517A com o `probe.cs`: `52 66` sem resposta, `82 66` com Model ID 82, `52 88` e `82 88` sem resposta, e as medições de pedidos descartados do `rate.cs` e do `rate2.cs` (§8.1, §8.5); formato `<Command2 Command3> <hex>` |
| `docs/protocol/captures/et2517a-sweep-2026-10-23.txt` | Acréscimo (2026-10-23): varredura do ET-2517A com o `sweep.cs` (`82 66` a cada ≈ 120 ms, uma linha por quadro diferente do anterior, `TIMEOUT` nos pedidos perdidos) com a decodificação feita trocando os bytes de modelo para 52; 30 quadros distintos (§8.4); formato `<tempo> <hex 27 bytes> <decodificação>` |
| `docs/protocol/tools/sweep.cs`, `rate.cs`, `rate2.cs` | Acréscimo (2026-10-23): apps .NET 10 de arquivo único que geraram as capturas do ET-2517A (`dotnet run sweep.cs -- <s> <arquivo>`, `dotnet run rate.cs -- [n] [intervalos] [família]`, idem `rate2.cs`); usam o cabo real |
| `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts-n\bm82x_ref.py` (área temporária) | Acréscimo (2026-10-23): decodificador de referência das duas famílias, escrito do zero a partir da §3.2 e da §3.4. `sweep` confere os 30 quadros distintos da varredura com a coluna da captura (0 divergências), `gen` gera os 14 vetores novos e `verify` confere os 21 vetores RTD do `test-vectors.json` e que os 14 vetores anteriores não mudaram (0 falhas). Uso: `python -I bm82x_ref.py verify <docs/protocol> <test-vectors do HEAD>` |
| `…\scratchpad\scripts-n\render_n.py`, `crop_n.py`, `compare_images.py`, `diff_flowchart.py` e `…\scratchpad\protocol\png-n\` (área temporária) | Acréscimo (2026-10-23): renderização do PDF-N e do PDF-L com PyMuPDF (200 dpi e recortes a 300 dpi), comparação das imagens embutidas por SHA-256 e diff pixel a pixel dos fluxogramas (§8.2) |
| `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts\bm52x_ref.py` e `gen_vectors.py` (área temporária) | decodificador de referência em Python e gerador dos vetores; servem de base para os testes .NET |
| `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts-verify\verify_bm52x.py` (área temporária) | **Correção (revisão):** verificador independente, escrito sem reaproveitar código do autor. Decodifica todos os vetores, as 14 RTDs de `rtd-acv.txt` e os dois dumps, simulando o ponteiro do medidor para 88/89/8A. Também confere a lista RLE da §4.14 e as latências dos logs brutos. Uso: `python -I verify_bm52x.py <docs/protocol> [<scratchpad>]`. Resultado na revisão: 1956 verificações e nenhuma falha. |

---

## 8. Família BM82x (Model ID 0x82) e o Minipa ET-2517A

Medições de 2026-10-23, no Windows, com o **Minipa ET-2517A** no mesmo cabo BC-86X do BM525s. O ET-2517A pertence à família **BM82x** do PDF-N: responde ao comando do PDF-N e devolve o Model ID 0x82 [PDF-N p1; CAP:et2517a-commands]. O modelo Brymen equivalente não foi identificado [NC].

### 8.1 Identificação, comando `82 66` e ausência de memória

| Item | ET-2517A (BM82x) | BM525s (BM52x) | Evidência |
|---|---|---|---|
| Cabo | BC-86X: VID 0x0820, PID 0x0001, bcdDevice 0x8603, `Brymen` / `Superior DMM`, usage page 0xFF00, relatórios de 9 (in) e 4 (out) bytes | o mesmo | [CAP:et2517a-commands], cabeçalho; §2.1 |
| `00 00 52 66` | **sem resposta** (timeout de 1,5 s) | 27 bytes, Model ID 52 52 52 52 | [CAP:et2517a-commands]; [CAP:rtd] |
| `00 00 82 66` | 27 bytes, **Model ID 82 82 82 82** | 27 bytes **idênticos** aos do `52 66`, Model ID 52 52 52 52 | [CAP:et2517a-commands]; [CAP:cmd-sequences] |
| `00 00 52 88` / `00 00 82 88` | sem resposta / sem resposta | 36 bytes (cabeçalho da memória, §4.13.1) / não testado | [CAP:et2517a-commands]; [CAP:dump1] |
| Datalogger | não tem | tem | [USR] manual da Minipa; [PDF-N p1] lista só o `82 66` |
| Latência até o 1º relatório | 36–237 ms | 50–103 ms (§2.5) | [CAP:rate]; [CAP:rtd] |
| Pedidos de tempo real perdidos | sim, tanto mais quanto maior o intervalo (§8.5) | nunca observado: 4,9 leituras/s com `PollInterval` de 200 ms, sem lacunas por minutos | [CAP:rate]; [CAP:rate2]; §2.7; [USR] |

- O “Command 2” `0x82` é o código da família BM82x, como o `0x52` é o da BM52x (§2.3): o mesmo valor volta nos bytes 20–23 [PDF-N p1, Table 1, “Model ID0..3: 0x82”].
- **O BM525s também aceita o `82 66`** e devolve o seu Model ID 0x52 [CAP:cmd-sequences]. O ET-2517A **não** aceita o `52 66`. Conclusão [INF]: o `82 66` é o pedido de tempo real comum às duas famílias.
- **O software original usa `82 66` em todo o tempo real** (`HidReadModel`, `HidReadRtd` e a thread de aquisição) e `52 88`/`52 89`/`52 8A` na memória [RE 0x00424955, 0x00424C40, 0x004355E9; 0x00426CE0, 0x00426E84]. Antes de importar, ele consulta o modelo com `82 66` e mostra E9017 se o Model ID não for 0x52 [RE 0x0042EC99..0x0042ECBA]. Ou seja, o original lê o tempo real de um BM82x, mas recusa a importação.
- **Sem memória.** `52 88` e `82 88` ficaram sem resposta, e o manual da Minipa diz que o ET-2517A não tem data logging [USR]. A port deve se comportar como o original: com Model ID 0x82, a importação falha com **E9017** sem enviar nenhum comando de memória.
- **Restrição desta especificação** (completa a §2.3): para o BM82x, envie só `00 00 82 66`. Não envie `82 88`, `82 89` nem `82 8A` (o `82 88` foi enviado uma vez neste teste, sem resposta) nem `52 66` (só gera timeout).

### 8.2 PDF-N × PDF-L: comparação célula a célula

Método: as duas páginas de cada PDF foram renderizadas com PyMuPDF 1.27.2 a 200 dpi e recortadas a 300 dpi (Table 1, FIG 1, exemplo e fluxograma), em `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\protocol\png-n\`. Além da leitura visual, foram comparados o texto extraído, as imagens embutidas (SHA-256) e os fluxogramas pixel a pixel (§7).

| # | Elemento | PDF-L (BM52x, com logging) | PDF-N (BM82x, sem logging) | Consequência |
|---|---|---|---|---|
| 1 | Título | “Protocol for 10000-count professional dual display mobile logging DMM series” | “Protocol for 10000-count professional dual display DMM series” | — |
| 2 | Tabela de comandos | 4 comandos (`00h 00h 52h 66h/88h/89h/8Ah`), com as colunas “Returned bytes” (27; 27 + 9) e “Remark” (remete ao *Memory-Read-Allocation-Decoder*) | 1 comando, `0x00 0x00 0x82 0x66`, sob “Commands to get read-time reading data” (sic) | o BM82x não tem comandos de memória |
| 3 | Frase “Real-time download” | termina com “- 00h, 00h, 52h, 66h” | sem os bytes do comando | — |
| 4 | Table 1, bytes 10 e 11 | byte 10 = `5b 5g 5c 4p 5a 5f 5e 5d`, byte 11 = report ID II (**errado**) | byte 10 = “0x00 (Report ID= II)”, byte 11 = `5b 5g 5c 4p 5a 5f 5e 5d` (**certo**) | é a evidência 2 da §3.1 |
| 5 | Table 1, bytes 20–23 | “0x52”, sem rótulo | “Model ID0: 0x82” … “Model ID3: 0x82”, em vermelho | origem do nome “Model ID” |
| 6 | Table 1, report IDs | “00h (Report ID= I/II/III)” | “0x00 (Report ID= I/II/III)” | só notação |
| 7 | Table 1, demais células (bytes 2–9, 12–18 e 24–27) | — | **idênticas bit a bit**: Hi, Lo, ①DC, ①AC, MIN, ⑤, AVG, MAX; ①sinal, Δ, %, LoZ, ①T2, LPF, ④, ①T1; dígitos 1–8 com 1p–6p, dB e %4~20mA; ②DC, ②AC, ②sinal, @, bateria, ②T1, ②T2, •))); unidades dos bytes 15–17; [H], [C], [R], [AUTO] | mesmo mapa (§3.2) |
| 8 | FIG 1 e o LCD do exemplo | 16 imagens embutidas na p1 (os dois LCDs de 1860 × 1271 e os ícones das tabelas) | **as mesmas 16 imagens, byte a byte** (mesmos SHA-256), só em outra posição e escala | mesmo LCD, inclusive o rótulo “1g” no 4º dígito (§3.3) |
| 9 | Exemplo, bytes 20–23 | `52h` (célula “0x52”) | `82h` (célula “0x82h”, sic) | — |
| 10 | Exemplo, bytes 10/11 | `6Dh` / `00h (Report ID= II)` (erro) | `6Dh` / `00h (Report ID= II)`: **o mesmo erro**, que contradiz a Table 1 do próprio PDF-N | a correção da §3.1 vale para os dois exemplos |
| 11 | Exemplo, demais bytes e realces | — | idênticos: `00 xx 10 00 E9 EF BF A0 00 6D 00 BF A0 CB 00 01 20 xx 00 … 1x xx xx xx`, com as mesmas células realçadas | — |
| 12 | Fluxograma (p2) | “Send out **0x00, 0x00, 0x52, 0x66** commands” | “Send out **0x00, 0x00, 0x82, 0x66** commands” | resto idêntico (abaixo) |
| 13 | Paginação | as linhas 26–27 do exemplo e a base do LCD do exemplo passam para a p2 | tudo na p1 | só leiaute |
| 14 | Metadados | CreationDate 2008-12-23 17:48:49 +08:00 | 2008-12-23 17:04:42 +08:00 | ambos do Acrobat Distiller 5.0.5 |

- **Fluxogramas**: são imagens de 2171 × 3195 pixels. O diff pixel a pixel (limiar de 48 níveis) acusou 7810 pixels em duas regiões: a caixa do comando (5971 pixels, a troca de `0x52` por `0x82`) e o texto “> 4 Seconds” (1839 pixels, o mesmo texto rasterizado de novo). O resto é idêntico nos dois: `Vendor_ID: 0x82` / `Product_ID: 0x01` (o mesmo erro, §2.1), 3 relatórios de entrada (27 bytes), timeout de 4 s com fechar e reabrir, fechar o HID depois de cada leitura e ciclo de 0,5 s.
- **Conclusão**: fora o código da família (comando e Model ID) e a lista de comandos, o PDF-N e o PDF-L descrevem o mesmo quadro. A única diferença de conteúdo do mapa é a numeração dos bytes 10/11, em que o PDF-N está certo. Nenhuma diferença bit a bit foi encontrada na Table 1 nem na FIG 1.

### 8.3 Mapa e validação do quadro BM82x

- O ET-2517A usa o mapa da §3.2, a tabela de segmentos da §3.4 e o algoritmo da §3.6 sem mudança. Nos 30 quadros distintos da varredura, fora os bits “don't care” (byte 18 e nibble baixo do 24), todo bit aceso é um segmento ou anunciador da Table 1, e os bytes 2 e 25–27 vieram 00 (§8.4.14).
- **Validação** (substitui o passo 1 da §3.6 para as duas famílias):
  1. comprimento 27 e `b1 = b10 = b19 = 0x00`;
  2. `b20 = b21 = b22 = b23`, com valor 0x52 (BM52x) ou 0x82 (BM82x);
  3. depois da detecção (§8.6), o Model ID deve ser o da família da conexão. Um quadro válido com o Model ID da outra família indica troca de medidor no mesmo cabo (§8.6).
- **Nota sobre as capturas**: o `IHidDevice` entrega cada relatório sem o report ID, e o `sweep.cs` remonta o quadro no estilo Windows pondo 00 nos bytes 1, 10 e 19, como o `Bu86xLink`. Na varredura do ET-2517A, portanto, esses 00 não são observação do fio.

### 8.4 Decodificação comentada de quadros reais do ET-2517A

Fonte: `captures/et2517a-sweep-2026-10-23.txt` [CAP:et2517a-sweep]. Cada quadro foi decodificado à mão pelo mapa da §3.2, sem usar a coluna de texto da captura, e depois conferido pelo decodificador de referência (§7). Em todos os quadros abaixo: bytes 1, 10 e 19 = 00 (report IDs), byte 2 = 00, bytes 20–23 = `82 82 82 82` (Model ID BM82x) e bytes 25–27 = 00. As tabelas mostram os outros bytes; bits de dígito no formato `b g c p a f e d`.

#### 8.4.1 ACV + Hz em AUTO (0,2 s)

`00 00 10 00 BF AF A0 E9 00 | 00 BF AF AF AF 00 01 20 06 | 00 82 82 82 82 10 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 10 | bit 4 | ① AC |
| 4 | 00 | — | sinal +; sem Δ, LoZ, LPF, T1/T2 |
| 5 | BF | 1011 1111 → a b c d e f, p = 1 | “0.” |
| 6 | AF | 1010 1111 → a b c d e f | “0” |
| 7 | A0 | 1010 0000 → b c | “1” |
| 8 | E9 | 1110 1001 → a b c d g; dB = 0 | “3” |
| 9 | 00 | — | sem anunciadores ② |
| 11 | BF | 4p = 1 | “0.” |
| 12–14 | AF AF AF | 5p = 6p = 0; %4~20mA = 0 | “000” |
| 15 | 00 | — | sem unidade ② no byte 15 |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 18 | 06 | — | não documentado (06, como no BM525s em ACV) |
| 24 | 10 | bit 4 | AUTO; nibble baixo 0 |

Resultado: **0.013 V AC / 0.000 Hz**, AUTO. É o mesmo quadro das consultas de `et2517a-commands-2026-10-23.txt`. Vetor `et2517a-acv-0.013V-hz-0.000Hz-auto`.

#### 8.4.2 DCV + ACV (110,4 s)

`00 00 20 00 BF AF AF AF 40 | 00 BF E9 E9 A8 10 00 20 00 | 00 82 82 82 82 10 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 20 | bit 5 | ① DC |
| 5–8 | BF AF AF AF | “0.” “0” “0” “0” | 0.000 |
| 9 | 40 | bit 6 | ② AC |
| 11–14 | BF E9 E9 A8 | “0.” “3” “3” “7” (A8 = 1010 1000 → a b c) | 0.337 |
| 15 | 10 | bit 4 | ② V |
| 16 | 00 | — | — |
| 17 | 20 | bit 5 | ① V |
| 18 | 00 | — | não documentado |
| 24 | 10 | bit 4 | AUTO |

Resultado: **0.000 V DC / 0.337 V AC**, AUTO: DCV no principal e ACV no secundário (o Bfunction 02 / Bselect 01 da Table 3 do BM52x). Vetor `et2517a-dcv-0.000V-acv-0.337V`.

#### 8.4.3 Hz + mV AC, 60 Hz da rede (94,7 s)

`00 00 00 00 6F BF AF A0 40 | 00 6F BF AF 6F 90 10 00 00 | 00 82 82 82 82 10 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 00 | — | principal sem AC/DC |
| 5 | 6F | 0110 1111 → a c d e f g | “6” |
| 6 | BF | p = 1 | “0.” |
| 7, 8 | AF A0 | — | “0” “1” |
| 9 | 40 | bit 6 | ② AC |
| 11 | 6F | 4p = 0 | “6” |
| 12 | BF | 5p = 1 | “0.” |
| 13, 14 | AF 6F | %4~20mA = 0 | “0” “6” |
| 15 | 90 | 1001 0000 → bits 7 e 4 | ② m + ② V |
| 16 | 10 | bit 4 | ① Hz |
| 17 | 00 | — | sem unidade ① no byte 17 |
| 18 | 00 | — | não documentado |
| 24 | 10 | bit 4 | AUTO |

Resultado: **60.01 Hz / 60.06 mV AC** (siValue 0.06006 V), AUTO. As pontas abertas captam os 60 Hz da rede. Vetor `et2517a-hz-60.01Hz-acmv-60.06mV`.

#### 8.4.4 Hz + mV AC, ponto num “8” (95,1 s)

`00 00 00 00 6F BF AF A0 40 | 00 6D FF ED E9 90 10 00 00 | 00 82 82 82 82 10 00 00 00`

Igual ao anterior, exceto os bytes 11–14: `6D` = 0110 1101 → a c d f g = “5”; `FF` = 1111 1111 → chave `EF` = “8” com 5p = 1 → “8.”; `ED` = 1110 1101 → a b c d f g = “9”; `E9` = “3”. Resultado: **60.01 Hz / 58.93 mV AC** (siValue 0.05893 V). O caso mostra que o ponto se separa do caractere pela máscara `& 0xEF`. Vetor `et2517a-hz-60.01Hz-acmv-58.93mV`.

#### 8.4.5 Continuidade com as pontas abertas (96,2 s)

`00 00 00 00 00 AF 17 00 01 | 00 00 00 00 00 00 20 00 00 | 00 82 82 82 82 00 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3, 4 | 00 00 | — | sem AC/DC, sinal + |
| 5 | 00 | — | “ ” (apagado) |
| 6 | AF | p = 0 | “0” |
| 7 | 17 | 0001 0111 → d e f, p = 1 | “L.” |
| 8 | 00 | dB = 0 | “ ” |
| 9 | 01 | bit 0 | •))) (bipe de continuidade) |
| 11–14 | 00 00 00 00 | — | secundário apagado |
| 15, 17 | 00 00 | — | — |
| 16 | 20 | bit 5 | ① Ω |
| 18 | 00 | — | não documentado |
| 24 | 00 | — | sem AUTO; nibble baixo 0 |

Resultado: dígitos `" 0L "` com o ponto 3p → texto **`0L.`**, **overload**, Ω sem prefixo; flag CONTINUITY_BEEP; secundário `null`. O ponto fica onde ficaria em “600.0”, a faixa de continuidade da Table 6 [INF]. Vetor `et2517a-continuity-open-0L.-ohm`.

#### 8.4.6 Temperatura sem termopar (99,0 s)

`00 00 00 01 40 40 40 4E 00 | 00 00 00 00 00 00 00 00 00 | 00 82 82 82 82 00 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 00 | — | — |
| 4 | 01 | bit 0 | ① T1 |
| 5–7 | 40 40 40 | 0100 0000 → g | “-” “-” “-” |
| 8 | 4E | 0100 1110 → a e f g; dB = 0 | “F” |
| 9 | 00 | — | — |
| 11–14 | 00 00 00 00 | — | secundário apagado |
| 15–17 | 00 00 00 | — | nenhuma unidade nos bytes de unidade |
| 24 | 00 | — | sem AUTO |

Resultado: **`---F`**: o “F” do 4º dígito dá a unidade °F, e os 3 primeiros caracteres (“---”) não são número, então `value = null`, sem overload; flag MAIN_T1. É o aviso de sensor aberto, que não deve ser confundido com OL [INF]. Confirma a regra de temperatura da §3.4. Vetor `et2517a-temperature-no-probe-dashes-F`.

#### 8.4.7 Diodo com as pontas abertas (100,8 s)

`00 00 00 00 10 AF 07 00 00 | 00 E3 20 63 E3 00 00 20 00 | 00 82 82 82 82 00 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3, 4 | 00 00 | — | sem AC/DC, sinal + |
| 5 | 10 | 0001 0000 → só p (1p) | “ .” |
| 6 | AF | — | “0” |
| 7 | 07 | 0000 0111 → d e f, p = 0 | “L” |
| 8 | 00 | — | “ ” |
| 9 | 00 | — | — |
| 11 | E3 | 1110 0011 → b c d e g | “d” |
| 12 | 20 | 0010 0000 → c | “i” |
| 13 | 63 | 0110 0011 → c d e g | “o” |
| 14 | E3 | %4~20mA = 0 | “d” |
| 15, 16 | 00 00 | — | sem unidade ② |
| 17 | 20 | bit 5 | ① V |
| 24 | 00 | — | sem AUTO |

Resultado: principal **`.0L` V**, overload (o ponto de “2.000”, a faixa de diodo, num 1º dígito apagado [INF]); secundário com o texto **`diod`**, sem unidade, `value = null`. Vetor `et2517a-diode-open-.0L-V-diod`.

#### 8.4.8 ACV em faixa manual, secundário em traços (111,6 s)

`00 00 10 00 BF A0 A0 6D 00 | 00 50 40 40 40 00 01 20 06 | 00 82 82 82 82 00 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 10 | bit 4 | ① AC |
| 5–8 | BF A0 A0 6D | “0.” “1” “1” “5” | 0.115 |
| 11 | 50 | 0101 0000 → g, 4p = 1 | “-.” |
| 12–14 | 40 40 40 | g | “---” |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 18 | 06 | — | não documentado |
| 24 | 00 | — | **sem AUTO** |

Resultado: **0.115 V AC / `-.---` Hz** (secundário não numérico, `value = null`). O AUTO apagou: é a 1ª faixa manual depois do RANGE, 9.999 V [INF]. Vetor `et2517a-acv-manual-0.115V-hz-dashes`.

#### 8.4.9 ACV em faixa manual 99.99 V, secundário só com o ponto (114,6 s)

`00 00 10 00 AF BF E4 6F 00 | 00 00 10 00 00 00 01 20 00 | 00 82 82 82 82 00 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 10 | bit 4 | ① AC |
| 5 | AF | p = 0 | “0” (zero à esquerda aceso) |
| 6 | BF | p = 1 | “0.” |
| 7 | E4 | 1110 0100 → b c f g | “4” |
| 8 | 6F | — | “6” |
| 11 | 00 | — | “ ” |
| 12 | 10 | só p (5p) | “ .” |
| 13, 14 | 00 00 | — | “  ” |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 18 | 00 | — | não documentado (06 no quadro de 125,1 s, com o mesmo 00.46 no principal) |
| 24 | 00 | — | sem AUTO |

Resultado: **00.46 V AC** (`digits` = `"0046"`, `value` = 0.46). No secundário, os 4 caracteres são espaços, então o display é `null` pela regra da §3.6, **embora o ponto 5p e o ② Hz estejam acesos** (§8.4.14, item 3). Vetor `et2517a-acv-manual-00.46V-secondary-point-only`.

#### 8.4.10 REC com MIN, MAX e AVG (115,3 s)

`00 00 1B 00 AF BF 6F A8 00 | 00 00 10 00 00 00 01 20 06 | 00 82 82 82 82 20 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 1B | 0001 1011 → bits 4, 3, 1 e 0 | ① AC, MIN, AVG, MAX (o traço ⑤ de “MAX-MIN”, bit 2, apagado) |
| 5–8 | AF BF 6F A8 | “0” “0.” “6” “7” | 00.67 |
| 12 | 10 | só 5p | secundário sem dígitos (como em §8.4.9) |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 18 | 06 | — | não documentado |
| 24 | 20 | 0010 0000 → bit 5 | **[R]**; sem AUTO |

Resultado: **00.67 V AC**, flags MIN, MAX, AVG e R_BOX. Com o REC, o ET-2517A acende MIN, MAX e AVG juntos e mostra a leitura atual; os quadros seguintes (115,5 e 115,8 s) mostram 00.63 e 00.58 com as mesmas flags. Vetor `et2517a-rec-min-max-avg-00.67V`.

#### 8.4.11 REC mostrando o MAX (116,0 s)

`00 00 11 00 AF BF 6F A8 00 | 00 00 10 00 00 00 01 20 06 | 00 82 82 82 82 20 00 00 00`

Igual ao anterior, exceto o byte 3 = `11` (0001 0001 → ① AC e MAX). Resultado: **00.67 V AC**, flags MAX e R_BOX. O 00.67 é o maior dos valores vistos durante o REC (00.67, 00.63, 00.58), o que confere com o MAX. Vetor `et2517a-rec-max-00.67V`.

#### 8.4.12 Overload em faixa manual (121,3 s)

`00 00 10 00 00 AF 07 00 00 | 00 50 40 40 40 00 01 20 06 | 00 82 82 82 82 00 00 00 00`

Bytes 5–8 = `00 AF 07 00` → “ ” “0” “L” “ ”, sem nenhum ponto → texto **`0L`**, overload, V AC (b3 = 10, b17 = 20). Secundário `-.---` Hz como em §8.4.8; sem AUTO. Parece um quadro de transição ao sair do REC ou trocar de faixa, porque o quadro seguinte já mostra 001.9 V [INF]. Vetor `et2517a-acv-manual-0L-V-hz-dashes`.

#### 8.4.13 ACV na faixa 999.9 V, bytes 18 = F6 e 24 = 0F (121,5 s)

`00 00 10 00 AF AF B0 ED 00 | 00 50 40 40 40 00 01 20 F6 | 00 82 82 82 82 0F 00 00 00`

| Byte | Hex | Bits | Leitura |
|---|---|---|---|
| 3 | 10 | bit 4 | ① AC |
| 5, 6 | AF AF | — | “0” “0” |
| 7 | B0 | 1011 0000 → b c, p = 1 | “1.” |
| 8 | ED | 1110 1101 → a b c d f g | “9” |
| 11–14 | 50 40 40 40 | — | “-.---” |
| 16 | 01 | bit 0 | ② Hz |
| 17 | 20 | bit 5 | ① V |
| 18 | F6 | 1111 0110 | não documentado (valor novo) |
| 24 | 0F | bits 3–0 | sem [H], [C], [R] nem AUTO; **nibble baixo F** |

Resultado: **001.9 V AC / `-.---` Hz** (`value` = 1.9). É o único quadro da varredura com `b18` = F6 e nibble baixo de `b24` = F. Vetor `et2517a-acv-manual-001.9V-byte24-0F`.

#### 8.4.14 Divergências e observações

1. **Nenhuma divergência de valor.** A captura tem 43 linhas com quadro e 30 quadros distintos. A decodificação manual (e a do decodificador de referência) dá, nos 30, exatamente o texto, a unidade, o modo, o overload e as flags da coluna da captura.
2. A coluna da captura foi gerada trocando os bytes 20–23 para 52. O quadro real é **rejeitado pelo decodificador atual** (`bytes 20..23 = 82828282, esperado 52525252`): é a mudança de validação da §8.3.
3. **Semântica do secundário só com ponto**: nos quadros de 114,6 s a 116,0 s, o secundário tem o ponto 5p (`b12.4`) e o ② Hz (`b16.0`) acesos e os 4 dígitos apagados. A regra da §3.6 dá `null` (display apagado) e ignora o ponto e a unidade. É uma simplificação, não um erro de bits; o LCD deve mostrar só “.” e “Hz” [INF, NC; §6, questão 15].
4. Os caracteres `d`, `i` e `o` do texto `diod` (chaves E3, 20 e 63) estão na tabela da §3.4, que veio do original. Nenhum quadro teve caractere inválido (`?`).
5. **Bytes não documentados**: `b18` = 06 em ACV, exceto em dois quadros de ACV manual (00 aos 114,6 s e F6 aos 121,5 s), e 00 em DCV, Hz/mV, Ω, temperatura e diodo; nibble baixo de `b24` = 0, exceto F no quadro de 121,5 s. Os bytes 2 e 25–27 vieram 00 em todos.
6. **Zeros à esquerda**: em faixa manual o ET-2517A acende os zeros à esquerda da faixa (`00.46` na de 99.99 V, `001.9` na de 999.9 V). O `text` os mantém, porque segue o LCD, e o `value` não muda.

#### 8.4.15 Exemplo do PDF-N corrigido

Bytes como impressos [PDF-N p1, Example]: `00 xx 10 00 E9 EF BF A0 00 6D 00 BF A0 CB 00 01 20 xx 00 82 82 82 82 1x xx xx xx`. Com os bytes 10/11 trocados, como manda a Table 1 do próprio PDF-N (`xx` = 00, `1x` = 10):

`00 00 10 00 E9 EF BF A0 00 | 00 6D BF A0 CB 00 01 20 00 | 00 82 82 82 82 10 00 00 00`

A decodificação é a da §3.7.1 byte a byte, com o Model ID 0x82: **380.1 V AC / 50.12 Hz**, AUTO. Vetor `pdfn-example-ac380.1V-50.12Hz-corrected`.

#### 8.4.16 Vetores acrescentados ao `test-vectors.json`

Todos com `kind` `"rtd"`, `command` `"8266"`, `family` `"BM82x"`, `modelBytes` `"82828282"` e `synthetic` `false`, e com o `expected` no formato dos vetores anteriores. Os 14 vetores anteriores não mudaram.

| Vetor | Quadro | Principal | Secundário | Flags |
|---|---|---|---|---|
| `et2517a-acv-0.013V-hz-0.000Hz-auto` | §8.4.1 (0,2 s) | 0.013 V AC | 0.000 Hz | AUTO |
| `et2517a-dcv-0.000V-acv-0.337V` | §8.4.2 (110,4 s) | 0.000 V DC | 0.337 V AC | AUTO |
| `et2517a-hz-60.01Hz-acmv-60.06mV` | §8.4.3 (94,7 s) | 60.01 Hz | 60.06 mV AC | AUTO |
| `et2517a-hz-60.01Hz-acmv-58.93mV` | §8.4.4 (95,1 s) | 60.01 Hz | 58.93 mV AC | AUTO |
| `et2517a-continuity-open-0L.-ohm` | §8.4.5 (96,2 s) | `0L.` Ω, overload | `null` | CONTINUITY_BEEP |
| `et2517a-temperature-no-probe-dashes-F` | §8.4.6 (99,0 s) | `---F` °F, `value` `null` | `null` | MAIN_T1 |
| `et2517a-diode-open-.0L-V-diod` | §8.4.7 (100,8 s) | `.0L` V, overload | `diod` | — |
| `et2517a-acv-manual-0.115V-hz-dashes` | §8.4.8 (111,6 s) | 0.115 V AC | `-.---` Hz | — |
| `et2517a-acv-manual-00.46V-secondary-point-only` | §8.4.9 (114,6 s) | 00.46 V AC | `null` | — |
| `et2517a-rec-min-max-avg-00.67V` | §8.4.10 (115,3 s) | 00.67 V AC | `null` | MIN, MAX, AVG, R_BOX |
| `et2517a-rec-max-00.67V` | §8.4.11 (116,0 s) | 00.67 V AC | `null` | MAX, R_BOX |
| `et2517a-acv-manual-0L-V-hz-dashes` | §8.4.12 (121,3 s) | `0L` V AC, overload | `-.---` Hz | — |
| `et2517a-acv-manual-001.9V-byte24-0F` | §8.4.13 (121,5 s) | 001.9 V AC | `-.---` Hz | — |
| `pdfn-example-ac380.1V-50.12Hz-corrected` | §8.4.15 | 380.1 V AC | 50.12 Hz | AUTO |

### 8.5 Pedidos descartados

O ET-2517A **descarta** uma parte dos pedidos `82 66`: nenhum relatório chega, nem esperando mais. O BM525s nunca mostrou isso (§2.7).

#### 8.5.1 Medições

Todas com o ET-2517A sozinho no cabo, 40 pedidos por intervalo [CAP:et2517a-commands]. O “intervalo” é a pausa depois do fim de cada consulta; antes de cada pedido há um descarte de 5 ms.

**Taxa de pedidos sem resposta** (`rate.cs`: espera de 1 s pelo 1º relatório; na falta de resposta, um único reenvio, logo depois do timeout):

| Intervalo entre consultas | Sem resposta | Recuperados pelo reenvio |
|---|---|---|
| 0 ms | **0/40** | — |
| 100 ms | 2/40 (5 %) | 2 de 2 |
| 200 ms | 11/40 (27,5 %) | 9 de 11 |
| 300 ms | 11/40 (27,5 %) | 5 de 11 |
| 500 ms | 19/40 (47,5 %) | 9 de 19 |

Latência normal até o 1º relatório: **36 a 237 ms**.

**Reenvio rápido** (`rate2.cs`: 1º relatório em até 300 ms; senão, o mesmo pedido de novo, até 3 reenvios = 4 tentativas):

| Intervalo | Na 1ª tentativa | Depois de 1 / 2 / 3 reenvios | Falhas depois de 3 reenvios | Tempo até o quadro (mediana / máximo) |
|---|---|---|---|---|
| 100 ms | 28/40 (70 %) | 1 / 1 / 7 | 3/40 | 91 / 1090 ms |
| 200 ms | 32/40 (80 %) | 0 / 0 / 7 | 1/40 | 187 / 1174 ms |
| 500 ms | 24/40 (60 %) | 1 / 9 / 2 | 4/40 | 95 / 1252 ms |
| 1000 ms | 16/40 (40 %) | 6 / 10 / 3 | 5/40 | 552 / 1168 ms |

**Varredura** (`sweep.cs`: espera de 2,5 s por relatório e, depois de um timeout, novo pedido logo em seguida) [CAP:et2517a-sweep]:
- de 0,2 s a 24,2 s, nenhum pedido se perdeu (da ordem de 100 consultas, pelo ritmo de ≈ 120 ms mais a latência [INF]);
- a partir daí houve 23 eventos de `TIMEOUT` em 124 s. Em 9, o pedido seguinte, enviado depois de ≈ 2,5 s sem tráfego, respondeu. Em 12, ele também se perdeu (2 perdas seguidas, ≈ 5 s sem leitura). Em 2 houve 3 perdas ou mais (5,2 e 6,2 s sem leitura), durante as trocas de função [INF];
- esperar 2,5 s não recuperou nenhum pedido: o pedido se perde, e só um novo envio resolve.

#### 8.5.2 Interpretação

- O pedido perdido não recebe resposta atrasada: durante as esperas de 1 s (`rate.cs`) e de 2,5 s (`sweep.cs`) nenhum relatório chegou. Esperar mais não adianta, e a espera longa ainda **aumenta** o tempo ocioso antes do pedido seguinte, que fica mais sujeito à perda (tabela do `rate.cs` e a sequência da varredura) [INF].
- As perdas se agrupam: com o reenvio a cada 300 ms, a 200 ms de intervalo 7 dos 8 pedidos perdidos só responderam no 3º reenvio e o 8º não respondeu; a 100 ms, 7 de 12 responderam no 3º reenvio e 3 não responderam [CAP:rate2]. O 3º reenvio sai ≈ 0,9 s depois do 1º envio: há janelas de ≈ 1 s em que o medidor não atende [INF].
- Hipótese [INF, NC; §6, questão 12]: o receptor óptico do ET-2517A fica atento logo depois de uma troca e, ocioso, só amostra o pedido de tempos em tempos. Com consultas sem pausa (0 ms), 0 de 40 se perderam.
- O original consulta sem pausa nenhuma (`T8252xThread`, “não há Sleep: consulta contínua”, §6.5 de `docs/re/bs8252x-reverse-engineering.md`) e espera 4 s pelo 1º relatório [RE 0x004355A0..0x00436088]. Isso explica por que ele funciona com o ET-2517A apesar da espera longa [INF].
- **Comportamento atual da port** (até esta especificação): `52 66` fixo, que o ET-2517A ignora. Com o `82 66`, a política atual (espera de 1 s, `W7012` a cada perda, fechar e reabrir o cabo, `PollInterval` de 200 ms) transformaria ≈ 27 % das consultas em `W7012`, e as perdas agrupadas chegariam à 3ª falha seguida (`E9016`) [INF, pela tabela do `rate.cs`]. No teste com o ET-2517A, isso apareceu como uma enxurrada de erros [USR].

#### 8.5.3 Algoritmo recomendado

```text
JANELA       = 300 ms   # espera pelo 1º relatório antes de reenviar
ORÇAMENTO    = 2 s      # tempo total de uma consulta, com os reenvios
ESPERA_LONGA = 3 s      # 1º 66 depois de abrir o cabo ou de comandos de memória (RealTimeAfterMemoryTimeout, §2.7)
SILÊNCIO     = 100 ms   # drain de recuperação (RecoveryDrainTimeout)

consulta_rtd(cmd2, família, primeira):          # cmd2 = 0x82 ou 0x52; família = BM52x, BM82x ou desconhecida
    prazo   = agora + (primeira ? max(ORÇAMENTO, ESPERA_LONGA) : ORÇAMENTO)
    reenvia = not (primeira and família == BM52x)          # exceção da §8.5.4
    descartar residuais (DrainTimeout; SILÊNCIO se houver recuperação pendente)
    enviar {00, 00, cmd2, 66}; tentativas = 1
    laço:
        espera = reenvia ? min(JANELA, prazo − agora) : prazo − agora
        r1 = ler o 1º relatório (até espera)
        se r1 chegou:
            r2, r3 = ler 2 relatórios (ReportTimeout cada)
            se os 3 chegaram e o quadro é válido (§8.3: report IDs 00; Model ID da família, ou 52/82 na detecção):
                se tentativas > 1: recuperação pendente     # a resposta de outro envio pode chegar atrasada
                devolver o quadro
            recuperação pendente                            # relatório parcial ou quadro alheio
            descartar residuais (SILÊNCIO)
        se agora ≥ prazo: recuperação pendente; falhar com W7012
        se reenvia: enviar {00, 00, cmd2, 66}; tentativas += 1   # o mesmo pedido, sem fechar nem reabrir o cabo
```

Regras:

1. **Janela de ≈ 300 ms pelo 1º relatório.** Fica acima das latências normais das duas famílias: 36–237 ms no ET-2517A e 50–200 ms no BM525s em tempo real contínuo (§2.5, §2.7). No BM525s em tempo real contínuo a janela não dispara reenvios [INF]; os casos lentos são os da §8.5.4.
2. **Reenviar o mesmo pedido sem fechar o cabo.** Fechar e reabrir não recupera nada e acrescenta tempo ocioso, o que piora a perda (§8.5.2).
3. **Orçamento de ≈ 2 s por consulta**, com até ≈ 6 tentativas. `W7012` só sai quando o orçamento acaba, e não a cada pedido perdido; os reenvios não são erro (convém contá-los num diagnóstico). Com 4 tentativas (≈ 1,2 s) ainda falharam 1 a 5 de 40 consultas; o efeito do orçamento de 2 s não foi medido [NC; §6, questão 13].
4. **Aceitar o 1º quadro válido de qualquer um dos envios.** Os pedidos são iguais, então qualquer resposta é uma leitura com menos de ≈ 2 s.
5. **Drain de recuperação depois de qualquer reenvio.** Antes do próximo comando, descarte relatórios até ≈ 100 ms de silêncio, para que a resposta atrasada de um envio anterior não seja tomada como a resposta do comando seguinte. É o mesmo mecanismo que a port já usa depois de abrir o cabo e de uma troca interrompida (`RecoveryDrainTimeout`). Limite conhecido [INF]: uma resposta duplicada que chegue depois dos 100 ms de silêncio seria lida pelo comando seguinte. Se ele for outro `82 66`, ela é um quadro válido e só um pouco mais antigo; se for o `88`, o bloco falha na contagem de relatórios ou no checksum e o `88` é reenviado (§4.11).
6. **Validar report IDs e Model ID** de todo quadro recebido (§8.3). Isso recusa um resto de outra troca (por exemplo, os 3 primeiros relatórios de um bloco de memória) e detecta a troca de medidor.
7. Os comandos de memória (`88`, `89`, `8A`) não mudam: vale a §4.11.

#### 8.5.4 Exceção: o 1º `66` depois da abertura ou da memória

No BM525s, o 1º `66` depois do último bloco da memória responde em **≈ 2 s, sem perda** (2056–2068 ms até o último relatório, §2.7), e o 1º depois de abrir o cabo leva 221–308 ms até o último relatório. O reenvio **não pode encurtar essa espera longa**:

- **Prazo**: no 1º `66` depois de abrir o cabo ou de qualquer comando de memória, o prazo da consulta é `max(ORÇAMENTO, ESPERA_LONGA)` = 3 s. Com ou sem reenvios, a resposta lenta do BM525s ao 1º envio continua aceita.
- **Família BM52x já conhecida** (comandos de memória só existem no BM52x, ou o Model ID 0x52 já foi visto nesta conexão): **não reenvie** durante a espera longa. O BM525s nunca perdeu pedidos, e um reenvio no meio dos ≈ 2 s de preparo poderia gerar uma resposta duplicada [NC; §6, questão 14].
- **Família desconhecida** (o 1º `66` depois de abrir o cabo, que faz a detecção da §8.6) **ou BM82x**: reenvie a cada 300 ms até o prazo de 3 s. O ET-2517A pode descartar justamente esse pedido. Aceite o 1º quadro válido e faça o drain de recuperação em seguida.

#### 8.5.5 Consequências para a leitura contínua

- Com a janela de 300 ms e reenvios, 70–80 % das consultas saem na 1ª tentativa a 100–200 ms de intervalo, e quase todas em até ≈ 1,25 s (§8.5.1).
- **Intervalo entre consultas**: quanto menor, menos perdas (0 ms: 0 de 40). Para o BM82x, um `PollInterval` curto (≤ 100 ms) reduz os reenvios, mas não os elimina [INF]. O reenvio continua obrigatório.
- **Política de falhas**: as “3 falhas seguidas” que levam ao `E9016` (§6.5 de `docs/re/bs8252x-reverse-engineering.md`; `docs/library.md`, “Erros e códigos”) devem contar consultas com o orçamento esgotado, não pedidos perdidos. Com o orçamento de 2 s, o `E9016` sai depois de ≈ 6 s sem leitura.

### 8.6 Detecção automática de família

```text
detectar_família():                                    # logo depois de abrir o cabo
    q = consulta_rtd(cmd2 = 0x82, família = desconhecida, primeira = sim)    # funciona nas duas famílias
    se q:
        devolver {0x52: BM52x, 0x82: BM82x}[Model ID de q], q               # q já é uma leitura válida
    q = consulta_rtd(cmd2 = 0x52, família = desconhecida, primeira = não)   # fallback
    se q e Model ID de q = 0x52:
        devolver BM52x, q
    falhar (W7012; na leitura contínua, conta como falha seguida)
```

- **`82 66` primeiro**: funciona nas duas famílias [CAP:et2517a-commands; CAP:cmd-sequences], e o quadro da detecção já é uma leitura, sem consulta extra.
- **Model ID**: os bytes 20–23, os quatro iguais. 0x52 → BM52x; 0x82 → BM82x. Qualquer outro valor é `W8011`, com os bytes no diagnóstico. Não adote o mapa do original (FF, F7, F3, F1, F0, E0, C0, 80 e 00 → 0x82 [RE 0x00424B5A..0x00424BE4]), que nenhum medidor deste kit confirmou (§6, questão 4).
- **Fallback para `52 66`**: cobre um BM52x que ignore o `82 66`. Isso não foi observado; só o BM525s foi testado (§6, questão 16).
- **Comando de tempo real depois da detecção**: use como “Command 2” o código da família detectada, `52 66` no BM52x (o comando validado no Windows e no Orange Pi 5) e `82 66` no BM82x. O `82 66` também funciona no BM525s, com quadros idênticos, e é o que o original usa nas duas famílias. Adotá-lo para as duas é uma opção válida, com menos horas de validação no BM52x [INF].
- **Memória**: só com a família BM52x, sempre com `52 88`/`52 89`/`52 8A`. Com BM82x, a importação falha com `E9017` sem enviar comandos de memória, como no original [RE 0x0042EC99..0x0042ECBA]. **Na port (0.2.0)**, o código é o `E9014` (`BrymenErrorCodes.DataLoggerNotSupported`, 409 na API), próprio da biblioteca: o medidor respondeu e a família é conhecida, só não tem datalogger, o que não é o “desligado, desconectado ou sem datalogger” do `E9017` (503). O motivo está em `docs/architecture.md`, “Divergências deliberadas em relação ao original”. A recusa não envia comandos de memória: é imediata com a família fixa ou confirmada por um quadro de tempo real há menos de 2 s, e vem depois de uma consulta de tempo real com a família conhecida há mais tempo.
- **Quando detectar de novo**:
  - a cada abertura do cabo, porque o medidor é óptico e pode ser trocado sem que o USB perceba. **Na port (0.2.0)**, a família continua depois de fechar e reabrir o cabo, e a troca de medidor é detectada pelos dois itens seguintes; a leitura da memória confirma a família conhecida há mais de 2 s antes de recusar um BM82x;
  - quando chega um quadro válido com o Model ID da outra família: adote a nova família;
  - depois de um `E9016`, porque o medidor pode ter sido trocado enquanto estava sem resposta. Por exemplo, um BM525s trocado pelo ET-2517A numa conexão BM52x não responde mais ao `52 66`.
- A família é uma propriedade da conexão e convém expô-la no diagnóstico (`"BM52x"` ou `"BM82x"`).

### 8.7 Resumo para a implementação

O estado de cada item na versão 0.2.0 da port está entre colchetes; as diferenças estão também no `CHANGELOG.md`, “Diferenças da especificação”.

1. **Decodificador**: aceitar o Model ID 0x52 ou 0x82, com os quatro bytes iguais (§8.3), e expor a família. A mensagem de erro do Model ID deixa de ser só “esperado 52525252”. [Feito: `MeterFamily`, `DmmReading.Family`.]
2. **Transporte**: o “Command 2” passa a ser escolhido por consulta (0x52 ou 0x82). Até a 0.1.0, o `Bu86xLink` enviava sempre `00 52 cc`. [Feito: sobrecargas de `Bu86xLink.ExecuteAsync` com a família; as sem família continuam com `52`.]
3. **Tempo real**: o algoritmo da §8.5.3, com a exceção da §8.5.4 e a política de falhas da §8.5.5. [Feito, com uma diferença: o BM52x conhecido não usa a janela de 300 ms, e cada consulta é um envio com o `ReportTimeout`.]
4. **Detecção**: a da §8.6, a cada abertura do cabo. [Alterado: a 1ª consulta da conexão detecta, e a família continua depois de fechar e reabrir o cabo; ela troca com o Model ID da outra família, volta a ser desconhecida depois de 3 consultas seguidas sem resposta, e a memória a confirma quando o último quadro tem mais de 2 s.]
5. **Memória**: só para o BM52x; `E9017` para o BM82x. [Alterado: `E9014` para o BM82x, 409 na API; veja a §8.6.]
6. **Vetores**: o `test-vectors.json` passou de 14 para 28 vetores. Os 14 novos (BM82x) falham no decodificador atual até o item 1, e o teste que confere a quantidade de vetores precisa ser atualizado. [Feito.]
7. **Simulação** (sugestão): um medidor BM82x simulado que descarte pedidos com probabilidade crescente com o tempo ocioso, para testar a §8.5 sem o hardware. [Feito: `SimulatedBm52xMeter.FromEt2517aCaptures` e `CreateEt2517aRequestDropper`.]
