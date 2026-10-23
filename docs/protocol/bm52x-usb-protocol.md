# Protocolo USB do Brymen BM52x com o cabo óptico BC-86X

Especificação implementável do protocolo usado pelo software Windows *Bs82-52x Data Recording System V6.0.0.9* para falar com o multímetro **Brymen BM525s** (idêntico ao **Minipa ET-2615A**) através do cabo óptico USB **BC-86X** (kit **BU-86X**). É a fonte de verdade para o port .NET 10 (API REST, Linux arm64/x64).

Data das medições: 2026-10-22. Medidor em ACV com Hz no display secundário, pontas abertas, faixa AUTO.

---

## 0. Convenções e legenda de evidências

Cada afirmação traz a sua evidência entre colchetes:

| Marca | Significado |
|---|---|
| **[PDF-L pN]** | `10000count-professional-dual-display-mobile-logging-DMMs-protocol.pdf` (protocolo do BM52x com logging), página N |
| **[PDF-N pN]** | `10000count-professional-dual-display-DMMs-protocol.pdf` (série sem logging, BM82x), página N |
| **[PDF-M pN]** | `Memory-Read-Allocation-Decoder.pdf`, página N |
| **[CAP:nome]** | Captura real feita com o `probe.cs` (ver §7) |
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

- Byte 1 = report ID (00), byte 2 = “Command 1” (00), byte 3 = “Command 2” (0x52), byte 4 = “Command 3” [PDF-L p1].
- O 0x52 coincide com o “Model ID” devolvido nos bytes 20–23 do RTD do BM52x. Na série BM82x o comando é `00 00 82 66` e o Model ID é 0x82 [PDF-N p1]. Conclusão [INF]: “Command 2” é o código de família do medidor. O software original monta o relatório como `{00, 00, dl, cl}` com `dl = 0x52` fixo [RE 0x00426AC0..0x00426B00, 0x00426CE0].
- **Restrição desta especificação**: só os códigos 66, 88, 89 e 8A são usados. Nenhum outro código foi testado nem deve ser enviado.

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
| 20–23 | R3 | 16–19 | 0x52 0x52 0x52 0x52 (Model ID; BM82x = 0x82 [PDF-N p1]) |||||||||
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
- **OL / overload** [NC no padrão exato]: o LCD de overload dos Brymen usa os caracteres “0” e “L” (ex.: `0.L`, ` 0L `). A Table 1 não descreve OL. Regra proposta: se os 4 caracteres do display pertencem a {espaço, `0`, `L`} e há ao menos um `L`, é overload. O valor fica `null` (ou ±∞ na API) e `overload = true`. Registre o texto bruto para refinar a regra com capturas reais.
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

## 6. Questões em aberto

1. Padrão exato do LCD em OL (`0.L`, ` 0L `, posição do DP) e se o “-” do OL acende o sinal. Faltam capturas em Ω com pontas abertas e em V acima da faixa.
2. Temperatura: casas decimais na memória (o RE indica 0) e formato no RTD (3 dígitos + ‘C’/‘F’?). Faltam sessão e RTD em °C/°F.
3. Significado de `b18` (obs. 06; o original testa o bit 3 em V AC/DC), do nibble baixo de `b24` (0/1) e dos símbolos @, [H], [C], [R].
4. Por que o software original rejeita RTD com `b9.4` (@) aceso e aceita no byte 23 os valores {82, 52, FF, F7, F3, F1, F0, E0, C0, 80, 00}.
5. Significado de Model_Id = 00 × 01.
6. Mapeamento de endereço para as páginas A2/A3 (> 64 KiB de log). Validar com um log grande ou várias sessões.
7. Encadeamento real com várias sessões (PS/NS, single display de 3 bytes). Só há 1 sessão dual no dump.
8. Comportamento no Linux (hidraw: 8 bytes por read, write com report ID 0) a confirmar em arm64/x64.
9. Se o cabo responde com o medidor desligado ou fora do modo de comunicação, e com o quê. Não testado.
10. **Correção (revisão), nova questão:** o que o medidor faz com um `89` cuja resposta não chegou (0 relatórios)? O ponteiro avança ou não? Isso decide se o `8A` de recuperação é seguro (§4.11). É preciso testar com desconexão ou interferência óptica proposital.
11. **Nova questão (2026-10-22):** por que o 1º `66` depois do último bloco da memória leva ≈ 2 s e volta em traços (§2.7)? A demora cresce com memórias maiores, com várias sessões ou depois de um `89` além do fim? Os 3 s de espera da port cobrem o dump de 2272 bytes medido.

## 7. Arquivos

| Arquivo | Conteúdo |
|---|---|
| `docs/protocol/bm52x-usb-protocol.md` | este documento |
| `docs/protocol/test-vectors.json` | vetores de teste (RTD: exemplo do PDF corrigido, exemplo literal como vetor negativo, 3 capturas reais, 2 sintéticos marcados; memória: HMD, NMD, CMD, último bloco e dump completo decodificado). **Correção (revisão):** 2 vetores `8A` com captura salva foram acrescentados (§2.7), e o `cmd-current-again-after-hmd` foi marcado como sem log salvo |
| `docs/protocol/captures/memory-dump-1.txt` | dump bruto: 1 × 88 + 94 × 89 (+ linha de timeout do 96º); formato `<cmd> <hex 36 bytes>`, `#` = comentário |
| `docs/protocol/captures/memory-dump-2.txt` | dump de controle: 88 + 94 × 89 + 8A + 88 |
| `docs/protocol/captures/rtd-acv.txt` | RTD#1, RTD#2 e 12 leituras novas (ACV + Hz); formato `<cmd> <hex 27 bytes>` |
| `docs/protocol/captures/cmd-sequences-2026-10-22.txt` | `82 66` × `52 66` (quadros idênticos), sequência `88`, `8A`, `89`, `8A` e o `52 82` sem resposta (§2.7); formato `<Command2 Command3> <hex>` |
| `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts\bm52x_ref.py` e `gen_vectors.py` (área temporária) | decodificador de referência em Python e gerador dos vetores; servem de base para os testes .NET |
| `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\scripts-verify\verify_bm52x.py` (área temporária) | **Correção (revisão):** verificador independente, escrito sem reaproveitar código do autor. Decodifica todos os vetores, as 14 RTDs de `rtd-acv.txt` e os dois dumps, simulando o ponteiro do medidor para 88/89/8A. Também confere a lista RLE da §4.14 e as latências dos logs brutos. Uso: `python -I verify_bm52x.py <docs/protocol> [<scratchpad>]`. Resultado na revisão: 1956 verificações e nenhuma falha. |
