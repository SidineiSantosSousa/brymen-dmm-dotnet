# Engenharia reversa do Bs82-52x Data Recording System V6.0.0.9s (Brymen / B.T.C.)

Documento técnico de engenharia reversa estática do software Windows original, que serve de fonte para a port .NET 10 (API REST, Linux arm64/x64). O par de hardware é o multímetro Brymen BM525s (= Minipa ET-2615A) com o cabo óptico USB BC-86X (kit BU-86X).

Nada foi executado: nem o `.exe` original nem o cabo USB. A análise usou o REA (Reverse Engineer Anything) 6.7.0, o `llvm-objdump` do NDK, o `dumpbin` do Visual Studio e scripts Python próprios.

## 0. Convenções

- **[BIN 0x…]**: endereço virtual (VA) no `Bs8252xV6009sA.exe` (image base 0x00400000). Quando é recurso, indico o nome do recurso e o offset de arquivo.
- **[STR n]**: entrada `n` da string table (RT_STRING) do executável.
- **[DFM Form.Componente]**: formulário Delphi/VCL decodificado do recurso RCDATA.
- **[PDF-L pN]**: `10000count-professional-dual-display-mobile-logging-DMMs-protocol.pdf`, página N. **[PDF-M pN]**: `Memory-Read-Allocation-Decoder.pdf`.
- **[CAP …]**: capturas reais do hardware (RTD#1, RTD#2, HMD, NMD, CMD) fornecidas no enunciado.
- **[INF]** marca inferência minha, sem prova direta. **[NC]** marca algo não confirmado ou não analisado.
- Índices de byte: `raw[i]`, com i de 0 a 26, é a concatenação dos 3 relatórios HID de 9 bytes **incluindo** os report IDs (`raw[0]`, `raw[9]` e `raw[18]` são report IDs). "PDF Byte N" segue a numeração do PDF, que começa em 1.
- O protocolo no nível do PDF está em `docs/protocol/bm52x-usb-protocol.md`, de outro agente. Este documento trata do que o **binário** faz, e só remete àquele quando é preciso.

---

## 1. Resumo executivo

1. **Compilador.** O executável é **C++Builder 5** com VCL 5, não Delphi. Evidências: a string "Borland C++ - Copyright 1999 Inprise Corporation" [BIN 0x4C443D], o linker 5.00, as seções `.text/.data/.tls/.rdata/.idata/.edata/.rsrc/.reloc`, o entry point em 0x401000 e 600 exports com nomes mangled Borland, como `@@Hid_ctrl_001@Initialize`.
2. **Estrutura.** Os exports `@@<Unit>@Initialize` revelam os 27 módulos-fonte e delimitam o código de cada um (§4.1). Os VMTs VCL dão **147 handlers publicados** com endereço exato (§5).
3. **Comunicação USB.**
   - Enumeração: SetupDi + `HidD_GetHidGuid`, `CreateFileA` com `FILE_FLAG_OVERLAPPED`, filtro VID 0x0820 / PID 0x0001 via `HidD_GetAttributes`.
   - E/S: `WriteFile` / `ReadFile` sobrepostos. Nunca usa `HidD_SetOutputReport` / `HidD_GetInputReport`: os únicos imports de hid.dll são os ordinais 3, 6 e 15 (`HidD_GetAttributes`, `HidD_GetHidGuid`, `HidD_GetSerialNumberString`).
   - Timeouts: escrita 2000 ms, 1º relatório 4000 ms, relatórios seguintes 100 ms (tempo real). Na memória, 500 ms em cada etapa.
4. **Correção importante.** Para tempo real o software original envia **`00 00 82 66`**, não `00 00 52 66` como diz o PDF [BIN 0x424955, 0x424C40, 0x4355E9]. Para a memória ele envia `00 00 52 88/89/8A` [BIN 0x426CE0, 0x426E84]. O `52 66` funciona no hardware (confirmado no probe); se `82 66` também funciona no BM525s está **[NC]**.
5. **Hipótese preliminar confirmada pelo binário.**
   - O binário descarta `raw[0]`, `raw[9]` e `raw[18]` como report IDs [BIN 0x424EE0..0x424F73], então o 2º report ID está no byte real 10 (`raw[9]`) e o dígito 5 no byte 11 (`raw[10]`).
   - O bit 4 de cada byte de dígito é o ponto decimal à direita daquele dígito: o binário calcula o expoente a partir dos bits 4 de `raw[4..6]` e de `raw[10..12]` [BIN 0x426355, 0x426670].
   - A reimplementação decodifica RTD#1 como "0.287" V AC + "0.000" Hz e RTD#2 como "0.156" V AC + "0.000" Hz (§7.8).
6. **Formatos de arquivo** (§10): `.g53` (gravador) e `.TMP` (backup em tempo real) usam registros binários de 169 bytes. O CSV tem o cabeçalho `#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc`. O `BTCDL.ini` fica no diretório **corrente**, sem seção, com `iPort` (porta COM 1..8), `fHiLimit` e `fLowLimit` (limites do Comparator).
7. **Aquisição.** Uma thread (`T8252xThread`) consulta o medidor em laço contínuo, sem sleep. O gravador amostra por um `TTimer`: "Full Speed" = **500 ms**, "Customization…" = N s inteiros, ou "Manual".
8. **Correção (revisão adversarial independente, §16).** A thread **não** valida o quadro nem a escrita. Ela produz o texto do display com o ponto real e o expoente só do prefixo. O flag de sinal do secundário vem de `raw[11].4` (bug). O NMD faz 4 tentativas no total. O índice da memória usa `((hi−0xA0)/2)·64 KiB`. O formato de arquivo é escolhido pela extensão.

---

## 2. Artefatos analisados

| Arquivo (cópia em `scratchpad\target\`) | Tamanho | SHA-256 | Observações |
|---|---|---|---|
| `Bs8252xV6009sA.exe` | 1 419 264 | `37e01bb0…77441ef1` | PE32 x86 GUI, timestamp 2012-07-10, C++Builder 5 |
| `PEGRAP32.DLL` | 940 032 | `e30f6320…5cf5a2` | ProEssentials (Gigasoft), 1997, linker 2.55; importado por ordinal |
| `hid.dll` | 20 992 | `c0834c73…fa6` | Cópia Microsoft (XP, 2008-04-14). Os ordinais 3/6/15 coincidem com o `SysWOW64\hid.dll` atual |
| `BTCDL.ini` | 38 | `6a9124e6…caf` | `iPort=2`, `fHiLimit=200`, `fLowLimit=100`, CRLF, **sem seção** |

Hashes completos estão em `scratchpad\re\rea-inspect-*.json` (campo `digest.sha256`).

- **Identidade** [STR 1..5]: "Data Logging System Express", "Ver 6.0.0.9s Alpha", "JUL 10, 2012", "Copyright(c) B.T.C. 2012", "ALL RIGHTS RESERVED".
- **Modelos citados na string table** [STR 10..62]: Dmm8x, Bs81-51x, DMSC-8L, BS20x, DMSC-2, K1, BS85x, BS13x, Bs15x, Bs25x, **Bs82-52x**, DMSC-8LU.
- **Versão PE** (VERSIONINFO): idioma 1028 (chinês tradicional). O ícone principal é `MAINICON` [REA inspect-pe-resources, recurso 107].

---

## 3. Como o REA foi usado

Os comandos foram executados no Git Bash com `REA=…\scratchpad\tools\rea\node_modules\.bin\rea` e `T=…\scratchpad\target\Bs8252xV6009sA.exe`, sempre com `--format json`. As saídas brutas estão em `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\re\`.

| # | Comando exato | Resultado | Arquivo |
|---|---|---|---|
| 1 | `$REA --help`, `$REA doctor --format json` | `healthy:false`: nenhum motor de análise profunda (Hopper, Ghidra, IDA, JADX, Kuna) | `rea-doctor.json` |
| 2 | `$REA capabilities --format json`, `$REA providers --format json` | Disponíveis: `rea-artifact-graph`, `rea-dotnet-static`, `rea-pe-resources`. Ghidra: `not_configured` (falta `GHIDRA_INSTALL_DIR`; só há JRE 13) | `rea-capabilities.json`, `rea-providers.json` |
| 3 | `$REA inspect-artifact "$T" --format json` | OK: PE sem contêineres filhos; sugere `inspect_managed_*` | `rea-inspect-artifact.json` |
| 4 | `$REA inspect-managed-artifact "$T" --format json` | OK: não há metadados CLI (binário nativo) | `rea-inspect-managed-artifact.json` |
| 5 | `$REA inspect-pe-resources "$T" --format json` | **OK, completo**: 116 recursos, 240 entradas (cursores, bitmaps, 17 RCDATA de formulários + DVCLAL, 45 blocos de string table, ícones, VERSION) | `rea-inspect-pe-resources.json` |
| 6 | `$REA inspect-pe-resources …\PEGRAP32.DLL --format json` | OK | `rea-inspect-pe-resources-pegrap32.json` |
| 7 | `$REA inspect-binary-layout "$T"` | **Falhou**: `capability_unavailable` ("pwntools artifact profile supports Linux x64 only") | `rea-inspect-binary-layout.json` |
| 8 | `$REA inspect-signature "$T"` | **Falhou**: provider `native-macos` (exige macOS) | `rea-inspect-signature.json` |
| 9 | `$REA analyze "$T"` | **Falhou**: `provider_unavailable` (nenhum provider de análise profunda) | `rea-analyze.json` |
| 10 | `$REA execute-rizin-command "$T" "iI"` | **Falhou**: Rizin não instalado (`REA_RIZIN_COMMAND` não definido) | `rea-execute-rizin-command.json` |
| 11 | `REA_OBJDUMP_COMMAND='C:\Android\Sdk\ndk\28.2.13676358\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-objdump.exe' $REA inspect-with-objdump "$T" file_headers --format json` | **OK**: o REA aceitou o llvm-objdump como "GNU objdump" (`coff-i386`) | `rea-objdump-file_headers.json` |
| 12 | Idem, `section_headers` | OK (8 seções) | `rea-objdump-section_headers.json` |
| 13 | Idem, `disassemble` | **Parcial**: o REA trunca o stdout em 16 MiB (`output_truncated:true`, `completion_status:unknown`) | `rea-objdump-disassemble.json` (35 MB) |

**Descobertas sobre o REA.**

- O provider de objdump é escolhido pela variável **`REA_OBJDUMP_COMMAND`** (código em `rea-agents/dist/composition/reverseEngineering.js`). O Rizin, por `REA_RIZIN_COMMAND`. A pasta `docs/` citada no README não está no pacote npm instalado.
- `inspect-with-objdump` só repassa `-f/-h/-t/-r/-W/-d/-D`. Não há start/stop address, então não dá para desmontar uma função isolada.
- Para PE, os comandos `function`, `decompile`, `xrefs`, `search` e `trace` exigem Ghidra, Hopper ou IDA. Nenhum está disponível.

**Ferramentas usadas fora do REA, e por quê.** O REA truncou a desmontagem e não tinha provider de funções.

- `llvm-objdump -d --no-show-raw-insn -M intel` direto: `objdump-text-intel.txt`, 274 mil linhas, cerca de 7 s.
- `dumpbin /headers /imports /exports` do exe, de `hid.dll` (para resolver os ordinais) e de `PEGRAP32.DLL`.
- Scripts próprios em `scratchpad\scripts\`, todos executados com `python -I`:

| Script | Função |
|---|---|
| `dfm_extract.py` | Extrai RCDATA e decodifica TPF0 (formulários binários) para `.dfm.txt` e `forms-summary.json` |
| `pe_stringtable.py` | String table, id → texto (`stringtable.tsv`, 409 entradas) |
| `pe_strings.py` | Strings ASCII/UTF-16 com offset, VA e seção (`strings-exe.tsv`) |
| `vcl_vmt.py` | Varre VMTs VCL 5 (self-pointer em vmt−76), com nome da classe, pai, métodos e campos publicados (`vmt.json`, `vmt-published.txt`) |
| `resync_asm.py` | Redesmonta trechos que a varredura linear desalinhou (dados embutidos no `.text` do BCB), a partir de entradas com prólogo `55 8B EC` (`objdump-text-intel-resync.txt`) |
| `annotate_asm.py` | Anota a desmontagem com nomes de imports (thunks `jmp [IAT]`), métodos publicados, VMTs e literais de string (`asm-annotated.txt`) |
| `clean_asm.sh` | Remove o ruído do BCB (destrutores AnsiString, contadores de contexto de exceção) para leitura |
| `switch_resolver.py` | Segue árvores de `cmp/sub/jcc/jmp [tabela]` para resolver switches (tabela de 7 segmentos, unidades) |
| `verify_decode.py` | Reimplementa a decodificação do binário e a aplica às capturas reais (`verify_decode.out.txt`) |
| `render_pdf.py`, `crop_png.py` | Renderizam e recortam os PDFs (PyMuPDF) |

---

## 4. Arquitetura do programa

### 4.1 Módulos (units) e faixas de código

As faixas vêm dos exports `@@<Unit>@Initialize`, que o BCB emite no fim do código de cada unit [`dumpbin-exports.txt`]. Exemplo: `@@Hid_ctrl_001@Initialize` = 0x426FEC, logo após a última função HID.

| Unit | Faixa aproximada (VA) | Conteúdo |
|---|---|---|
| Main4 | 0x401059–0x409594 | `TFormMain` (janela principal, link, TimerGetSd), `TWaitOffLineThread`, INI |
| Rscomm | 0x4095A4–0x409B84 | Abertura de porta COM (`CreateFileA` "COMn", `SetCommState`) |
| Globalvar | 0x409B94–0x409C18 | Globais |
| Getunitfuncstr | 0x409C30–0x40A3DC | Textos de função/unidade [INF] |
| Dlgrsport | 0x40A3EC–0x40A844 | `TDlgRsPortForm` (COM1..COM8) |
| Dlgabout | 0x40A854–0x40B4B0 | `TDlgAboutForm` (SO, firmware e SN do cabo) |
| Automesgform | 0x40B4C0–0x40B718 | `TAutoMsgForm` (aviso com auto-retentativa) |
| Digitalmeter | 0x40B728–0x40CE68 | `TDigitalMeterForm`, `TDigitalMeterThread` |
| Messageshow, Tarecord | 0x40CE78–0x40D0E4 | Caixas de erro (0x40CE88) e construtor de registro (0x40D0A4) |
| Comparator | 0x40D0F4–0x40EEC4 | `TComparatorForm`, `TComparatorThread` |
| Dlghilow | 0x40EED4–0x40F7BC | `TDlgHiLowForm` |
| Analogmeter | 0x40F7CC–0x411558 | `TAnalogMeterForm`, `TAnalogMeterThread` |
| Graphicalrecord | 0x411568–0x41E314 | `TGraphicalForm` (gravador, .g53, .TMP, CSV) |
| Dlgoption | 0x41E324–0x41EDE8 | `TDialogOption` (intervalo) |
| Viewgraphunit | 0x41EDF8–0x420794 | `TFormViewGraphic` |
| Dlgfitexportunit, Dlgexportunit | 0x4207A4–0x4244DC | Diálogos de exportação |
| Mesgform | 0x4224FC–0x42275C | `TMsgForm` (progresso) |
| **Hid_ctrl_001** | 0x4244EC–0x426FEC | **Camada HID**: enumeração, RTD, decodificação, HMD/NMD |
| Datastringgrid | 0x426FFC–0x428540 | `TDataStringGridForm` (Data List) |
| **Unitimportmain** | 0x428550–0x4347E8 | `TFormImport` (importação da memória, .slr) |
| **Unit_decodefunc** | 0x4347F8–0x435358 | Decodificação da memória (função, faixa, intervalo) |
| Globalthreadvaribales | 0x435368–0x4354F0 | Variáveis globais da thread |
| **Thread_bm8252x** | 0x435508–0x43874C | `T8252xThread` (laço de aquisição) |
| Func_unit_string | 0x43875C–0x439028 | Palavra de função → string de unidade |
| (VCL, RTL, Pegvcl) | 0x439028– | Biblioteca |

### 4.2 Classes com VMT (published) e threads

Os VMTs estão em `.data`; o resultado completo está em `vmt.json`. `Execute` é o slot +4 do VMT (TThread VCL 5).

| Classe | VMT | Tamanho | `Execute` |
|---|---|---|---|
| `T8252xThread` | 0x4D35A4 | 56 | **0x4355A0** |
| `TDigitalMeterThread` | 0x4C75F4 | 184 | 0x40B858 |
| `TComparatorThread` | 0x4C7E54 | 64 | 0x40D184 |
| `TAnalogMeterThread` | 0x4C8B40 | 72 | 0x40F890 |
| `TWaitOffLineThread` | 0x4C5FD0 | 48 | 0x4016E8 |

### 4.3 Globais compartilhadas pela thread de aquisição

Os endereços são de `.data` e foram inferidos pelo uso em [BIN 0x4358E8..0x436083] e [BIN 0x41BAB6..0x41BBC6].

| VA | Tipo | Significado |
|---|---|---|
| 0x4D962C | CRITICAL_SECTION | Protege o bloco de leitura abaixo |
| 0x4D9644 | byte[27] | Último RTD bruto (com report IDs) |
| 0x4D965F | byte[…] | RTD sem report IDs (P[0..6]=raw[2..8], P[7..14]=raw[10..17], P[15..]=raw[19..]) |
| 0x4D9673 / 0x4D9680 | byte | Flag "negativo" principal / secundário. **Correção (revisão):** o principal vem de `raw[3].7` [BIN 0x43595A], mas o secundário vem de `P[8] & 0x10` = **`raw[11].4`** (ponto decimal 5p) [BIN 0x43596A], e não de `raw[8].5`. É bug do original; a port deve usar `raw[8].5` |
| 0x4D9674 / 0x4D9684 | AnsiString | Texto do display com sinal. **Correção (revisão):** na thread, o ponto decimal fica na posição real ("12.34", "-0.287", "OL", "O.L"); a mantissa **não** é normalizada para "d.ddd" [BIN 0x436210..0x436406 e "-" inserido em 0x4369FA / 0x4373BB]. A normalização "d.ddd" só existe em `HidReadRtd` (§7.4) |
| 0x4D9678 / 0x4D9688 | AnsiString | Expoente **só do prefixo** (k=3, M=6, m=−3, µ=−6, n=−9), em texto. **Correção (revisão):** na thread não entram o ponto decimal nem os ajustes +6/+9/+3 [BIN 0x43741C..0x437522] |
| 0x4D967C / 0x4D968C | uint32 | Palavra de função principal / secundária (bit 31 = bateria fraca) |
| 0x4D9690 / 0x4D9694 | AnsiString | Prefixo ("k", "M", "u", "m", "n", "") |
| 0x4D9698 / 0x4D969C | AnsiString | Unidade ("V", "A", "W"=Ω, "Hz", "%", "F", "S", "dB") |
| 0x4D96A0 / 0x4D96A4 | uint32 | Função & 0x7FFFFFFF |
| 0x4D96A8 / 0x4D96CC | AnsiString | Rótulo AC/DC/T ("DC+AC", "DC", "AC", "T1", "T2", "T1-T2") |
| 0x4D96AC, 0x4D96BC, 0x4D96C4 | char[16], char[8], char[8] | "Valor formatado", prefixo e unidade principais (para o gravador). **Correção (revisão):** 0x4D96AC é um `strcpy` do texto 0x4D9674 [BIN 0x435AA6..0x435AB6], sem `sprintf`. Para a palavra 0x2000 (dB), o campo de prefixo recebe a unidade ("dB") e o de unidade recebe o prefixo [BIN 0x435AD6..0x435B03]. Para 0x2080, o prefixo fica "" [BIN 0x435B08] |
| 0x4D96D0, 0x4D96E0, 0x4D96E8 | char[16], char[8], char[8] | Idem, secundários |
| 0x4D96F0 | byte | Bateria fraca |
| 0x4D9710 + 0x4D9728 | CS + byte | "Dado válido" (1 = última leitura OK) |
| 0x4D96F4 + 0x4D970C | CS + byte | "Linkado" |
| 0x4D9618 | Event | Leitura nova disponível (SetEvent a cada RTD OK) |
| 0x4D961C | Event | A thread parou por erro |
| 0x4D9628 | Event | A thread terminou |
| 0x4D9624 | Event | Sinal para o Comparator [INF] |
| 0x4D956C | TList* | Buffer de registros do gravador |
| 0x4D85AC | AnsiString | Diretório corrente (`GetCurrentDirectoryA`) [BIN 0x4033F1] |
| 0x4D85BC / 0x4D85C4 | double | fHiLimit / fLowLimit |
| 0x4C60E8 | int | iPort (COM 1..8) |
| 0x4CE8A8 | HANDLE | Handle HID usado por `TimerGetSdTimer` |

---

## 5. Inventário de funcionalidades e UI

### 5.1 Formulários (RCDATA TPF0 decodificados)

Os 17 formulários foram consumidos integralmente pelo decodificador (bytes lidos = tamanho do recurso). Os arquivos estão em `scratchpad\re\dfm\*.dfm.txt` e o resumo em `forms-summary.json`. Os captions aparecem como `'FormMain'`, `'Panel1'` etc. porque **o programa troca os textos em tempo de execução**, a partir da string table (§5.2).

| Recurso | Classe / papel | Componentes relevantes (DFM) |
|---|---|---|
| TFORMMAIN | Janela principal (barra de ferramentas) | `sbtnLink` (2 glifos, liga/desliga), `sbtnDigitMeter`, `sbtnAnalogMeter`, `sbtnDataList`, `sbtnGraphical`, `sbtnComparator`, `sbtnWinTile`, `sbtnPort`, `sbtnAbout`, `Panel1..3` (status), `MainMenu` vazio (montado em código), `TimerGetSd` (Enabled=False) |
| TDIGITALMETERFORM | Digital Meter | `PanelLcd` → `pnlMainLcd` {`FuncPanel`, `UnitPanel`, `DataPanel`, `pnlAcDc`}, `pnlSubLcd` {`SubDataPanel`, `pnlSubAcDc`, `pnlLB` (bateria)} |
| TANALOGMETERFORM | Analog Meter | `Panel1` {`FuncPanel`, `UnitPanel`, `MultPanel` (multiplicador da escala)}; ponteiro desenhado em `FormPaint` |
| TCOMPARATORFORM | Comparator | `Panel1` {Hi: `HiFuncPanel`, `HiUnitPanel`, `HiLimitPanel`}, `Panel2` {Low: …}, `bbtnAlart` (Caption "OFF"; alterna "ON"/"OFF" [BIN 0x40DD57/0x40DD8F]), `bbtnHiLowLimit` |
| TDLGHILOWFORM | "Comparator Hi-Low limit setting" | `mkeHiLimit`, `mkeLowLimit` (TMaskEdit), "Upper-Limit : ", "Lower-Limit : ", `stxtHi/LowFunc`, `stxtHi/LowUnit` |
| TGRAPHICALFORM | Graphical Recorder | `ToolPanel`: `sbtnRecordStart`, `sbtnRecordStop`, `sbtnManualLogging`, `sbtnOpenFile`, `sbtnSaveFile`, `sbtnPrintGraphic`, `sbtnClear`, `sbtnOption`, `sbtnPageUp/Down`, `sbtnFirstPage/LastPage`, `sbtnFitView`, `sbtnUndoZoom`, `sbtnExport`, `sbtnImport`. Painéis Reading/Max/Min (Func/Unit/Data), `TimePanel`, `StatusBar`, `ProgressBar`, `PEGraph`, `TimerLogger`, `TimerTime`, `OpenDialog`, `SaveDialog`, `PrintDialog`, `PrinterSetupDialog`, `ColorDialog1` |
| TFORMVIEWGRAPHIC | Graphical viewer | `PEGraphView`, painéis Max/Min com tempo, `MaxPFPanel`/`MinPFPanel` (principal/secundário), `sbtnPrint`, `sbtnUndoZoom`, `sbtnExport` |
| TDIALOGOPTION | "Acquisition Interval option" [STR 106] | `rgIntervalMode` ("Acquisition Mode" [STR 200]), `rbtnManual` [STR 201], `rbtnAutomatic` [STR 202], `cbxInterval` |
| TDLGEXPORTFORM / TDLGFITEXPORTFORM | Exportação | Destino: `rbtnClipBoard`, `rbtnFile` (+`btnBrowse`), `rbtnPrinter`; escopo: `rbtnPageAll`, `rbtnThisPage`, `rbtnPage` (`editPageFrom/To`); tipo: `rbtnMetaFile`, `rbtnBitMap`, `rbtnText`; tamanho: `rbtnPixels/Millimeters/Inches/Points`, `editWidth/High` |
| TFORMIMPORT | "Import data from DMM" | `sbtnImport`, `sbtnOpen`, `sbtnSave`, `sbtnClear`, `sbtnExport`, `PageControl` (abas por sessão), `PEGraph`, grupos Main/Sub com Max/Min e tempo, `rbtnMainData`/`rbtnSubData` |
| TDATASTRINGGRIDFORM | Data List | `PageControl`; as abas e grids ("Sheet", "DataList", `StrGrid`) são criados em código |
| TDLGRSPORTFORM | "Serial Port selection" | `rbtnCom1..8` |
| TAUTOMSGFORM | Aviso não modal com retentativa (fsStayOnTop) | `lbMsg1..3`, `ProgressBar` (0..100), `bbtnAbort`, `bbtnYes` ("&Continue", ModalResult 4), `Timer1` (100 ms) |
| TMSGFORM | Progresso ("Data added:00000") | `ProgressBar`, `bbtnAbort` |
| TDLGGRAPHLINECOLOR | "Graph Line Color" | `btnLineColor1/2`, `btnDefault`: padrão principal = 0xFF0000 (azul em BGR) e secundário = 0x0000FF (vermelho) [BIN 0x42469F/0x4246B8] |
| TDLGABOUTFORM | About | `Memo1` com BRAND, APPTitle, VERSION, Copyright, Date, rights, OS, "Cable Firmware Ver.", "Cable SN" (via `HidD_GetAttributes.VersionNumber` e `HidD_GetSerialNumberString` [BIN 0x40AF42, 0x40B156]) |

### 5.2 Menus e hints (string table)

O `TMainMenu` do DFM está vazio, então o menu é montado em código. Os textos vêm de [STR 500..1055].

- **&File** [501]: &Open… / Save &As… / Print Data as &Text… / &Print Data as Graphic… / Printer Set&up… / &Import data from DMM / &Clear / E&xit.
- **&Link** [600]: &Link / Un&link [601].
- **&Edit** [610]: Clear all data [611].
- **&Window** [631]: &Tile, &Digital Meter, &Analog Meter, &Comparator, &Graphical Recorder, Data &List.
- **&Port** [701]: Port setting… / C&OM port / &USB port [702..704].
- **&Acquisition** [761]: Star&t, Sto&p, &Manual, &Interval… [762..765].
- **&Page** [801]: F&irst page, Page &Up, Page &Down, &Last page, &Print this page…, E&xport… [802..807].
- **&View** [831]: Graph Line &Color, &Fit all data, &Undo Zooming [832..834].
- **&Help** [996]: &Contents, &Index, &About… [997..999].
- Hints dos botões [1000..1055]: "Link to DMM", "Unlink DMM", "Windows tile", "Port selection…", "Acquisition start/stop", "Interval option…", "Manual acquisition", "Import data from DMM", etc.
- Títulos das janelas [100..108]: "Digital Meter", "Analog Meter", "Comparator", "Data list", "Graphical Recorder", "Graphical viewer", "Acquisition Interval option", "Exporting Data Logger", "Data Logger".

### 5.3 Handlers publicados (endereços)

A lista completa (147 métodos) está em `vmt-published.txt`. Os principais:

| Handler | VA | Papel |
|---|---|---|
| `TFormMain.FormCreate` | 0x401FA4 | Lê o INI, testa COM1..COM8, monta o menu |
| `TFormMain.sbtnLinkClick` | 0x4049A4 | Link/unlink USB (§6.4) |
| `TFormMain.TimerGetSdTimer` | 0x404DC4 | Leitura RTD por timer (caminho alternativo, §6.6) |
| `TFormMain.sbtnPortClick` | 0x40476C | Diálogo COM; abre a porta (Rscomm 0x409798) |
| `TFormMain.FormDestroy` / gravação do INI | 0x403C4C / 0x403A10.. | Salva o INI |
| `TGraphicalForm.sbtnRecordStartClick` | 0x415DB4 | Inicia a gravação e cria o `.TMP` |
| `TGraphicalForm.TimerLoggerTimer` | 0x41B7E0 | Amostra (§9) |
| `TGraphicalForm.sbtnSaveFileClick` | 0x41795C | Grava o `.g53` |
| `TGraphicalForm.sbtnOpenFileClick` | 0x4163BC | Lê `.g53` / `.TMP` |
| `TGraphicalForm.sbtnExportClick` | 0x419F4C | Exportação (CSV em 0x417DE8) |
| `TGraphicalForm.sbtnOptionClick` | 0x4162D8 | Diálogo de intervalo |
| `TGraphicalForm.sbtnImportClick` | 0x41DD10 | Importação a partir do gravador [NC] |
| `TFormImport.sbtnImportClick` | 0x42EB68 | **Importação da memória do medidor** (§8) |
| `TFormImport.sbtnSaveClick` / `sbtnOpenClick` | 0x42B09C / 0x42A914 | `.slr` [NC] |
| `TComparatorForm.bbtnAlartClick` / `bbtnHiLowLimitClick` | 0x40DD14 / 0x40DDCC | Alarme e limites |
| `TDialogOption.cbxIntervalChange` | 0x41EA18 | Intervalo "Customization…" |

---

## 6. Comunicação com o cabo (camada HID)

### 6.1 Enumeração e abertura

**`OpenHidByIndex(index, flags)` [BIN 0x4247A4]**

1. `HidD_GetHidGuid(&guid)`.
2. `SetupDiGetClassDevsA(&guid, NULL, NULL, DIGCF_PRESENT|DIGCF_DEVICEINTERFACE = 0x12)`.
3. `SetupDiEnumDeviceInterfaces(hdev, NULL, &guid, index, &ifd)`, com `cbSize = 0x1C`.
4. `SetupDiGetDeviceInterfaceDetailA` duas vezes (tamanho, depois dados, com `cbSize = 5`).
5. `CreateFileA(path, GENERIC_READ|GENERIC_WRITE = 0xC0000000, FILE_SHARE_READ|FILE_SHARE_WRITE = 3, &sa{nLength=12}, OPEN_EXISTING = 3, flags, NULL)`.
6. Em erro, devolve −1. O `HDEVINFO` nunca é liberado (vazamento do original).

**`FindHid(vid, pid, flags)` [BIN 0x42489C]**

1. Para `index` de 0 a **19** (limite fixo `cmp …, 0x14` em [BIN 0x424917]): abre, chama `HidD_GetAttributes` e compara `VendorID == 0x0820` e `ProductID == 0x0001`.
2. Se casar, guarda o índice em [0x4CE8AC] e devolve o handle. Caso contrário, fecha (o handle só não é fechado quando `HidD_GetAttributes` falha) e segue.
3. Todos os chamadores passam `vid=0x0820`, `pid=0x0001` e `flags=FILE_FLAG_OVERLAPPED (0x40000000)` [BIN 0x404BF7, 0x404DF9, 0x426CBD, 0x426E61, 0x4355B6, 0x42EB97].

**Correção (revisão):** a `T8252xThread` não chama 0x42489C. Ela usa uma **cópia** de `FindHid` na própria unit, em [BIN 0x4385E0] (com `OpenHidByIndex` em 0x4384E4), com a mesma lógica: limite `cmp …, 0x14` [BIN 0x43865E] e comparação de VID/PID [BIN 0x438634, 0x43863E]. Os índices 0..19 contam **todas** as interfaces HID presentes (teclado, mouse etc.), e as que falham no `CreateFileA` são puladas.

O binário **não** lê HidP_Caps nem usa os tamanhos de relatório: os 4 bytes de saída e os 9 de entrada estão fixos no código.

### 6.2 Primitivas de E/S

Toda a E/S é sobreposta: `CreateEventA(NULL, FALSE, FALSE, NULL)` e uma `OVERLAPPED` por direção.

**RTD com identificação de modelo, `HidReadModel(h, &model)` [BIN 0x424930]**

1. `WriteFile(h, {00,00,82,66}, 4)` e espera até **2000 ms** (`0x7D0`).
2. Laço até acumular 27 bytes:
   - `ReadFile(h, buf+total, 9)`;
   - espera **4000 ms** (`0xFA0`) se `total == 0`, senão **100 ms** (`0x64`);
   - `GetOverlappedResult(h, &ov, &n, TRUE)` e `total += n`.
3. Códigos de retorno:

   | Retorno | Situação |
   |---|---|
   | 0x80 / 0x102 / 0xFFFFFFFF | Resultado da espera na escrita (abandoned / timeout / failed) |
   | **0x182** | Timeout sem nenhum byte lido |
   | **0x183** | Timeout com leitura parcial |
   | 0 | OK |

4. Modelo: o byte `raw[22]`. Se for 0x82 ou 0x52, é devolvido como está. Se for 0xFF, 0xF7, 0xF3, 0xF1, 0xF0, 0xE0, 0xC0, 0x80 ou 0x00, devolve 0x82. Qualquer outro valor é devolvido sem mudança [BIN 0x424B5A..0x424BE4].

**RTD completo e decodificado, `HidReadRtd(h, &negMain, &mainStr, …)` [BIN 0x424BEC, `ret 0x18`]**

- Mesma E/S (`00 00 82 66`, 2000 / 4000 / 100 ms), seguida da decodificação da §7.
- Devolve 0x102 se o byte de modelo ou os anunciadores forem inválidos (§7.2).
- **Correção (revisão):** 0x102 é ambíguo. É também o `WAIT_TIMEOUT` da espera da escrita, devolvido sem mudança [BIN 0x424D1A..0x424D45]. O chamador não distingue "timeout na escrita" de "quadro inválido". A port deve usar códigos distintos.
- `HidReadRtd` só é chamada por `TimerGetSdTimer` [BIN 0x40502F]. A thread de aquisição tem decodificação própria (§6.5, §7.4).

**Memória** (abre e fecha o dispositivo a cada bloco)

| Função | VA | Comportamento |
|---|---|---|
| `HidSendCmd(h, c2, c3)` | 0x426AC0 | `WriteFile({00,00,c2,c3}, 4)`, espera **500 ms** (`0x1F4`). Devolve o resultado da espera (0 = OK, 0x102 = timeout) |
| `HidReadN(h, buf, n)` | 0x426B78 | Lê n bytes em relatórios de 9, espera **500 ms** a cada um |
| `HidRead27Plus9` | 0x426C60 | 27 bytes de dados + 9 bytes de checksum |
| `ReadHMD(out24)` | 0x426CA8 | §8.1 |
| `ReadNMD(out24)` | 0x426E48 | §8.1 |

**Bug do original, não replicar.** Todas as chamadas `CancelIo` recebem `&handle` (o endereço da variável), não o handle: por exemplo `lea edx,[ebp-0x4]; push edx; call CancelIo` [BIN 0x4249CF]. Na prática o cancelamento falha.

### 6.3 Comando de tempo real: 82 66 × 52 66

- O byte "Command 2" usado pelo software é **0x82** em todas as leituras de tempo real: [BIN 0x424955] (identificação), [BIN 0x424C40] (`HidReadRtd`) e [BIN 0x4355E9] (`T8252xThread`).
- Não existe no binário nenhuma construção de `{00,00,52,66}`. A busca por `mov byte ptr [ebp-…], 0x52` mostra 0x52 apenas como `dl` de `HidSendCmd` para os comandos 88, 89 e 8A [BIN 0x426CE2, 0x426E87].
  - Revisão: confirmado por busca nos bytes de código (`C6 45 xx 66` / `C6 85 xxxxxxxx 66`). Há exatamente 3 gravações do byte 0x66, todas logo após um 0x82: [BIN 0x424959, 0x424C47, 0x4355F0].
  - Consequência: a importação da memória chama `HidReadModel` (82 66) antes do HMD [BIN 0x42EC99]. Se o medidor não responder a 82 66, o original mostra E9017 e não importa.
- O PDF-L p1 manda `52 66`, e o probe confirmou que `52 66` responde no hardware real. O próprio software usa `raw[22]` (0x52 no BM52x) para validar o modelo, o que é coerente com a interpretação "Command 2 = código de família" do documento de protocolo.
- **[NC]**: se o BM525s responde a `82 66`. A port deve usar `52 66`, já validado, e opcionalmente tentar `82 66` como fallback.

### 6.4 Link e unlink (`sbtnLinkClick` [BIN 0x4049A4])

**Link** (`sbtnLink.Down`)

1. `h = FindHid(…)`. Se for −1, solta o botão e mostra uma MessageBox com texto [STR 9015] "USB adapter cable not found." e caption "Error[E9015]" (`wsprintf(LoadStr(9999)="Error[E%d]", 9015)`).
2. Se encontrar, `CloseHandle(h)`.
3. Cria `T8252xThread` [BIN 0x404CE6, ctor 0x435520] e zera o contador de erros (+0x3A0).
4. Liga a flag "linkado" (0x4D970C = 1, sob a CS 0x4D96F4).
5. Muda o caption do Digital Meter para "Digital Meter : USB Device".

**Unlink**

1. `Terminate` da thread e `WaitForSingleObject(evtThreadExit 0x4D9628, INFINITE)`.
2. Linkado = 0.
3. Caption "Digital Meter : No Device".
4. Fecha o handle [0x4CE8A8], se houver, e envia a mensagem 0x405 a outro form [INF: atualização de UI].

### 6.5 Laço de aquisição (`T8252xThread.Execute` [BIN 0x4355A0])

```
h = FindHid'(0x0820, 0x0001, OVERLAPPED)                     ; cópia em 0x4385E0; guardado em this+0x30, mantido aberto
retry = 0
while (!Terminated):                                          ; [BIN 0x436088]
    WriteFile(h, {00,00,82,66}); Wait 2000 ms                 ; resultado da espera IGNORADO (ver correção)
    ler 27 bytes (9 por vez; 4000 ms no 1º, 100 ms nos demais); falha -> total = 0, CancelIo(&h)
    se a espera da leitura falhou:
        valido = 0                                            ; 0x4D9728, sob a CS 0x4D9710
        h2 = FindHid'(); se h2 == -1 -> SetEvent(0x4D961C); sair   ; sem mensagem
        CloseHandle(h2)
        retry = retry + 1                                     ; [BIN 0x4357CF]
        se retry < 3: CloseHandle(h); Sleep(10); h = FindHid'(); continuar   ; [BIN 0x4357D2]
        senão: MessageBox(texto=STR 8004 "No data received.", caption="Error[E9015]")
               SetEvent(0x4D961C); sair
    senão:
        retry = 0; valido = 1
        EnterCS(0x4D962C); copia raw -> 0x4D9644; decodifica (§7.4, "Thread") para os globais; LeaveCS
        SetEvent(0x4D9618)                                    ; "nova leitura"; não há Sleep: consulta contínua
CloseHandle(h); SetEvent(0x4D9628)
```

**Correções (revisão)**

- **Escrita sem verificação.** A thread guarda o resultado do `WaitForSingleObject(…, 2000)` da escrita em `[ebp-0x84]` [BIN 0x43568A] e o sobrescreve na espera da leitura, sem testá-lo [BIN 0x4356DC/0x4356F1]. Um timeout na escrita só aparece como timeout na leitura.
- **Contagem de retentativas.** O contador é incrementado antes da comparação [BIN 0x4357CF..0x4357D6]. São **2 reaberturas silenciosas**, e o erro E9015/8004 sai na **3ª falha consecutiva**, não depois de "3 retentativas". O contador zera a cada leitura boa [BIN 0x4358C8].
- **Sem validação do quadro.** A thread **não** confere `raw[22]`, "@" nem m+µ: decodifica qualquer quadro de 27 bytes [BIN 0x4358C8..0x435930]. A validação da §7.2 existe só em `HidReadRtd`.
- O mesmo bug do `CancelIo` aparece aqui: `add eax, 0x30; push eax` passa `&this->h` [BIN 0x435703].

**[INF]** A cadência efetiva é o tempo de ida e volta do cabo. Não existe período fixo de polling, então a port deve impor um mínimo configurável.

### 6.6 Leitura por timer e avisos (`TimerGetSdTimer` [BIN 0x404DC4])

Esse caminho abre o dispositivo, chama `HidReadRtd` e fecha a cada tick. Quem o habilita no fluxo USB normal **[NC]** (o DFM o define com `Enabled=False`).

- **Falha na leitura.**
  - Na 1ª falha consecutiva, só reabilita o timer.
  - Na 2ª, mostra o `TAutoMsgForm` com caption "Warning[W7012]" e as linhas [STR 8004] "No data received.", [STR 7006] "Will re-try automatically in 3 seconds." e [STR 7012] "Check cable connection and USB\COM port selection if required."
  - Se o resultado modal for 3 (Abort), desliga o link; senão, reabilita o timer.
  - Ao mesmo tempo, `ProgressBar` 0..100 e `Timer1` de 100 ms dão o "3 segundos" [INF].
- **Bateria fraca** (bit 31 da palavra principal) [BIN 0x4053D3]: aviso "Warning[W8000]" com [STR 8000] "The battery power of the DMM is low." e [STR 7009] "(Will continue automatically in 3 seconds.)". O `TimerLoggerTimer` repete o mesmo aviso [BIN 0x41BBCB].
- **Dispositivo ausente**: E9015 e unlink.

### 6.7 "Handshaking error" e demais mensagens

- [STR 8011] "A handshaking error occurred." existe na string table, mas nenhum `LoadStr(0x1F4B)` foi encontrado no código da aplicação **[NC: possivelmente morto]**.
- A tabela completa de códigos está na §12.

---

## 7. Decodificação do RTD (27 bytes)

### 7.1 Remoção dos report IDs: confirma a hipótese

[BIN 0x424EE0..0x424F73] copia `raw[2..8]`, `raw[10..17]` e `raw[19..23]` para um buffer contíguo, **descartando `raw[0]`, `raw[9]` e `raw[18]`**. A thread faz o mesmo em 0x4360C4. Logo, o 2º report ID está em `raw[9]` (byte real 10), e o conteúdo que o PDF chama de "Byte 10" (dígito 5) está em `raw[10]`. O PDF-L p1 troca os bytes 10 e 11. Mapa usado pelo binário:

| raw | PDF Byte | Conteúdo (PDF-L Table 1, com a correção) |
|---|---|---|
| 2 | 3 | Hi, Lo, (1)DC, (1)AC, MIN, (5)□, AVG, MAX (bits 7..0) |
| 3 | 4 | (1)□ = **sinal − principal** (bit 7), △, %, LoZ, (1)T2 (bit 3), LPF, (4)□, (1)T1 (bit 0) |
| 4..7 | 5..8 | Dígitos 1..4 (principal); bit 4 = 1p, 2p, 3p, e em `raw[7]` = **dB** |
| 8 | 9 | (2)DC (b7), (2)AC (b6), (2)□ = **sinal − secundário** (b5), @ (b4), bateria (b3), (2)T1 (b2), (2)T2 (b1), bip (b0) |
| 9 | 10 | **Report ID II (0x00)** (o PDF o põe no byte 11) |
| 10..13 | 11..14 | Dígitos 5..8 (secundário); bit 4 = 4p, 5p, 6p, e em `raw[13]` = %4~20mA |
| 14 | 15 | (2)m, (2)u, (2)A, (2)V, (2)D%, (2)n, (2)S, (2)F |
| 15 | 16 | (1)k, (1)M, (1)Ω, (1)Hz, (2)M, (2)k, (2)Ω, (2)Hz |
| 16 | 17 | (1)u, (1)m, (1)V, (1)A, (1)n, (1)D%, (1)S, (1)F |
| 17 | 18 | "don't care" no PDF. **O binário usa o bit 3** (§7.3) |
| 18 | 19 | Report ID III |
| 19..22 | 20..23 | 0x52 0x52 0x52 0x52; o binário valida `raw[22]` |
| 23 | 24 | **Correção (revisão):** a linha faltava. PDF-L Table 1, Byte 24: H (b7), C (b6), R (b5), AUTO (b4); bits 3..0 "don't care". Nas capturas vale 0x11 / 0x10, ou seja, AUTO ligado. O binário copia `raw[23]` (`P[19]`) mas não foi encontrado nenhum uso [NC] |
| 24..26 | 25..27 | "don't care" (PDF-L); descartados pelo binário |

Os bits 4 de `raw[4..6]` e `raw[10..12]` são o ponto decimal **à direita** daquele dígito: o expoente da §7.4 só é coerente com essa leitura. A FIG 1 do PDF-L mostra 4P, 5P e 6P entre os dígitos 5/6, 6/7 e 7/8.

### 7.2 Validação do quadro (`HidReadRtd`)

A leitura é rejeitada com retorno 0x102 se qualquer condição abaixo valer:

1. `raw[22]` ∉ {0x82, 0x52, 0xFF, 0xF7, 0xF3, 0xF1, 0xF0, 0xE0, 0xC0, 0x80, 0x00} [BIN 0x424F75..0x424FDD];
2. `raw[8] & 0x10` (anunciador "@") [BIN 0x424FEF];
3. `(raw[14] & 0xC0) == 0xC0` (m e µ juntos no secundário);
4. `(raw[16] & 0xC0) == 0xC0` (idem no principal).

Revisão: as quatro condições foram conferidas em [BIN 0x424F75..0x42506B]. **Correção (revisão):** essa validação só existe em `HidReadRtd` (caminho `TimerGetSdTimer`). A `T8252xThread`, que alimenta o Digital Meter, o gravador e o Comparator, não valida nada (§6.5). Validar na port é uma decisão de projeto, não paridade com o original.

### 7.3 Palavra de função (32 bits)

Ela é montada a partir dos anunciadores [BIN 0x425070..0x42533E; thread 0x437528].

**Principal**

- bit 31 (0x80000000) ← `raw[8].3`: **bateria fraca**.
- byte 1 (bits 15..8):
  - 0x80 ← `raw[3].0` (T1); 0x40 ← `raw[3].3` (T2);
  - 0x20 ← `raw[7].4` (dB); 0x10 ← `raw[16].1` (S);
  - 0x08 ← `raw[16].2` (D%); 0x04 ← `raw[15].4` (Hz);
  - 0x02 ← `raw[16].4` (A); 0x01 ← `raw[8].0` (bip/continuidade).
- byte 0 (bits 7..0):
  - 0x80 ← `raw[15].5` (Ω); 0x08 ← `raw[16].0` (F); 0x04 ← `raw[16].5` (V);
  - 0x02 ← `raw[2].5` (DC); 0x01 ← `raw[2].4` (AC).
- Se o resultado for 5 ou 6 (V AC / V DC) e `raw[17] & 0x08`, soma 0x01000000 [BIN 0x42520B]. A correspondência com nFunc_AcV6/DcV6 é **[INF]**.
- **Correção (revisão):** em `HidReadRtd`, a comparação com 5/6 usa a palavra inteira, com o bit 31 [BIN 0x42520E, 0x425228]. Com bateria fraca (0x80000005), o 0x01000000 não é somado. O mesmo vale para os ajustes F/S/dB da §7.4 (`cmp [word], 0x8` etc. [BIN 0x42651A..0x426625]). A thread mascara com 0x7FFFFFFF antes de comparar [BIN 0x437666, 0x437687]. A port deve sempre comparar a palavra mascarada.

**Secundário**

- byte 1:
  - 0x80 ← `raw[8].2` (T1); 0x40 ← `raw[8].1` (T2);
  - 0x10 ← `raw[14].1` (S); 0x04 ← `raw[15].0` (Hz); 0x02 ← `raw[14].5` (A).
- byte 0:
  - 0x80 ← `raw[15].1` (Ω); 0x08 ← `raw[14].0` (F); 0x04 ← `raw[14].4` (V);
  - 0x02 ← `raw[8].7` (DC); 0x01 ← `raw[8].6` (AC).

**Ajustes pós-dígitos**

- Se o 4º caractere do display for 'C', soma 0x20 (°C); se for 'F', soma 0x40 (°F). Vale para os dois displays.
- Se a string principal for "Auto", a palavra principal vira 0x800000 [BIN 0x425C47].

**Unidades por palavra** (resolvidas com `switch_resolver.py` em `Func→unidade` [BIN 0x437C24]; resultado em `unitstr-0x437c24.txt`)

| Palavra(s) | Unidade (como o programa exibe) |
|---|---|
| 0x04, 0x05 (AC), 0x06 (DC), 0x07 (AC+DC), 0x14, 0x1000005, 0x1000006 | "V" |
| 0x08 | "F" (capacitância) |
| 0x80, 0x180 (Ω+bip) | "W" (Ω na fonte **Symbol**) |
| 0x201 (AC), 0x202 (DC), 0x203 (AC+DC) | "A" |
| 0x400 | "Hz" |
| 0x800 | "%" (duty) |
| 0x1000 | "S" |
| 0x2000 | "dB" |
| 0x2080 | "W" (dBm com referência em Ω) |
| 0x200000 | "%" (4–20 mA) |
| 0x4000, 0x8000, 0xC000 | "" (T2, T1, T1−T2) |
| 0x4020, 0x8020, 0xC020, 0x4040, 0x8040, 0xC040 | "" (°C e °F aparecem como último dígito) |
| 0x00 | "" (sem função, por exemplo o secundário vazio). Revisão: confirmado por simulação do switch (`rev_unitsim.py`) |
| outras (inclusive 0x800000 "Auto" e 0x1000007) | "?" |

Revisão: a tabela inteira foi conferida simulando o switch de [BIN 0x437C4F] (`scripts-verify\rev_unitsim.py`).

**Rótulos da thread** [BIN 0x435B64..0x436046]: "DC+AC" (0x4D299D) para AC+DC, "DC", "AC", "T1", "T2", "T1-T2". A string table interna tem a enumeração de funções nFunc_AcV, AcV6, DcV, DcV6, AcDcV, Dx, V, dB, dBm_Ohm, DcA, AcA, AcDcA, %4-20mA, Hz, Duty, Cx, T1C, T1F, T2C, T2F, T1T2C, T1T2F, T1, T2, T1T2, Ohm, Conti, S, N/A [BIN 0x4D35D0..]. Nenhuma referência direta a essa tabela no código [NC: possivelmente vestigial], mas ela é um bom nome de enum para a port.

**Fontes.** "Airal" (sic) e "Symbol" alternam nos painéis de unidade, porque Ω é desenhado como 'W' e µ como 'u'. Na port, emitir Unicode ("Ω", "µ", "°C").

### 7.4 Números: mantissa, expoente e OL

**Dígitos.** Cada byte `& 0xEF` (bit 4 limpo) passa pela tabela de 7 segmentos [BIN 0x426844, duplicada em 0x437844]. A tabela foi conferida para os 256 valores (`sevenseg-0x426844-full.txt`). O bit 7 é b, o 6 é g, o 5 é c, o 3 é a, o 2 é f, o 1 é e e o 0 é d.

| byte | char | byte | char | byte | char | byte | char |
|---|---|---|---|---|---|---|---|
| 0x00 | ' ' | 0x4F | 'E' | 0xA0 | '1' | 0xE5 | 'y' |
| 0x07 | 'L' | 0x62 | 'n' | 0xA7 | 'U' | 0xE6 | 'H' |
| 0x0F | 'C' | 0x63 | 'o' | 0xA8 | '7' | 0xE9 | '3' |
| 0x20 | 'i' | 0x67 | 'b' | 0xAF | '0' | 0xEC | 'g' |
| 0x23 | 'u' | 0x6D | '5' | 0xCB | '2' | 0xED | '9' |
| 0x40 | '-' | 0x6F | '6' | 0xCE | 'P' | 0xEE | 'A' |
| 0x42 | 'r' | 0x47 | 't' | 0xE3 | 'd' | 0xEF | '8' |
| 0x4E | 'F' | 0xE4 | '4' | | | demais | '?' |

**Mantissa.** É sempre normalizada como `d1 "." d2 d3 d4` e testada com `AnsiString::ToDouble` (try/catch) [BIN 0x4254BF..0x425666]. **Correção (revisão):** isso vale só para `HidReadRtd`. A thread de aquisição mantém o ponto na posição real (ver "Thread" abaixo). Sinal: com `raw[3].7` o resultado vira "-"+mantissa e `negMain = 1`; com `raw[8].5` o mesmo vale para o secundário [BIN 0x425CAA, 0x4262D5].

**Exceção (texto não numérico)** [BIN 0x425674..]

- Se `d2 == '0'` e `d3 == 'L'` (o 'O' do display decodifica como '0'), é "OL". O ponto decimal é preservado pelo código `c = (raw[4].4<<2) | (raw[5].4<<1) | raw[6].4`:

  | c | Texto |
  |---|---|
  | 0 | "OL" |
  | 1 | "OL." |
  | 2 | "O.L" |
  | 4 | ".OL" |
  | outro | "OL" |

  O secundário usa `raw[10..12]` [BIN 0x425F28].
- Se a palavra for 0x2080 (dBm-Ω), os brancos viram '0' e a mantissa é "d1.d2d3d4".
- Fora isso, a string é `Trim(d1 d2 d3 d4)` (ex.: "Auto", "----").
- " .   " vira "    ".

**Expoente do ponto decimal** [BIN 0x426355]: c = 4 → 0; 2 → 1; 1 → 2; 0 → 3; outro → 0. Exemplo: "12.34" tem mantissa "1.234" e expoente 1.

**Expoente do prefixo**

- Principal: `(raw[15] & 0xC0) | ((raw[16] & 0xC8) >> 3)` → k = 3, M = 6, µ (0x10) = −6, m (0x08) = −3, n (0x01) = −9, senão 0 [BIN 0x4263C8].
- Secundário: `(raw[14] & 0xC4) | ((raw[15] & 0x0C) >> 2)` → m (0x80) = −3, µ (0x40) = −6, n (0x04) = −9, M (0x02) = 6, k (0x01) = 3 [BIN 0x4266E3].

**String de expoente** que `HidReadRtd` devolve: prefixo + ponto decimal, com os ajustes abaixo. Temperaturas (0x8020, 0x8040, 0x4020, 0x4040, 0xC020, 0xC040) usam a string "0" [BIN 0x426490, 0x4267A4].

| Palavra principal | Ajuste | Unidade interna resultante [INF] |
|---|---|---|
| 0x08 (F) | +6 | µF |
| 0x1000 (S) | +9 | nS |
| 0x2000 e 0x2080 | +3 | (sem leitura física clara) |

O secundário não recebe esses ajustes. Os rótulos de eixo "(uF)", "(Ohm)", "(dBm)", "(nS)" [BIN 0x4C8D8F..] confirmam a escolha de unidades internas.

**Thread.** O `T8252xThread` separa o expoente do prefixo [BIN 0x43741C], que vira o texto do prefixo ("n", "u", "m", "", "k", "M") via [BIN 0x437A80].

**Correção (revisão):** a thread decodifica de outro jeito, e é esse caminho que alimenta o gravador, o CSV e o `.g53`.

- **Mantissa.** É o texto do display com o ponto decimal na posição real (c = 4 → "d.ddd", 2 → "dd.dd", 1 → "ddd.d", 0 → "dddd"), e o "-" é inserido por `raw[3].7` / `raw[8].5` [BIN 0x436148..0x437419].
- **Expoente.** Só o do prefixo, sem ponto decimal e sem +6/+9/+3 [BIN 0x43741C].
- **Sem sprintf.** O `sprintf("%05.*f" | "%06.*f" | "%04.*f", …)` [BIN 0x405A34..0x405B3F] fica dentro de `TFormMain.TimerGetSdTimer` (0x404DC4..), não na thread. Na thread, o "valor formatado" 0x4D96AC é cópia literal do texto [BIN 0x435AB6].
- **Unidades internas.** As de µF e nS são aplicadas depois, no `TimerLoggerTimer` (§7.5).

### 7.5 Valor numérico para gráficos

O `TimerLoggerTimer` calcula `valor = ToDouble(mantissa) × 10^expoente` e troca o sinal se `neg` estiver ativo [BIN 0x41D4A0..0x41D5D0]. O resultado é guardado como float32 no registro. Substituições para temperatura sem sensor ("---" / "----"), conforme a palavra 0x4020 (°C) / 0x4040 (°F):

| Texto | °C | °F |
|---|---|---|
| "---" | +1375.0 | +2510.0 |
| "----" | −1375.0 | −2510.0 |

Nos demais casos, o valor é 0 [BIN 0x41D647..0x41D738].

**Correção (revisão):** o cálculo real do `TimerLoggerTimer` [BIN 0x41B828..0x41D746] tem mais etapas, todas sobre os textos da thread.

1. **Valor normal.** `valor = ToDouble(mantissa + "E" + expPrefixo)` (string "E" em 0x4C8DE2 / 0x4C8DFD).
   - No principal, depois: ×10^6 se a palavra for 0x8 (F → µF) e ×10^9 se for 0x1000 (S → nS) [BIN 0x41C4F3..0x41C52A].
   - O "---" / "----" (±1375 / ±2510) é aplicado por palavra exata.
     - Principal: 0x20/0x4020/0x8020/0xC020 → ±1375 e 0x40/0x4040/0x8040/0xC040 → ±2510 [BIN 0x41C974..0x41C9C9, 0x41CAF3..0x41CB48].
     - Secundário: só 0x4020 / 0x4040; as outras palavras dão 0 [BIN 0x41D647..0x41D738].
     - A tabela acima (só 0x4020/0x4040) vale, portanto, apenas para o secundário.
2. **OL não vale 0.** Se a mantissa contém "L", o valor é substituído por `k × 10^n × 10^expPrefixo`, com `n = Pos(".") − 2`, ou `Length − 1` sem ponto [BIN 0x41C4D0, 0x41D31C]. A função 0x4AFA78 é tratada como pow10 [INF].
   - **Principal** [BIN 0x41C58B..0x41C82A]:

     | k | Palavras |
     |---|---|
     | 10,9989 | V AC/DC/AC+DC (5..7), Hz (0x400) e S (0x1000, ×10^9) |
     | 6,6 | V (0x4), F (0x8, ×10^6), Ω (0x80/0x180), dBm-Ω (0x2080), A (0x201..0x203) e 0x1000005/6 |
     | 110,0 fixo | Duty (0x800) |
     | 1100,0 fixo | dB (0x2000) |

   - **Secundário** [BIN 0x41D3BA..0x41D4C2]: 6,6 para 0x5 e 0x201; 10,9989 para 0x400 e 0x200000.
   - Constantes 10,9989 e 6,6 em 0x41DD00 / 0x41DD08.
   - **OL em palavra fora da tabela descarta a amostra:** o handler retorna sem gravar [BIN 0x41C82F..0x41C902, 0x41D4C7..0x41D5B0].
3. **Sinal.** No principal, com a flag 0x4D9673 o "-" é retirado, o texto é normalizado e o valor é negado com `fchs` [BIN 0x41BFD8..0x41C0B5, 0x41C907..0x41C91C]. No secundário, a flag 0x4D9680 (= `raw[11].4`, bug; §4.3) remove o 1º caractere e nega [BIN 0x41D25E..0x41D2C0, 0x41D5B5..0x41D5CA]. Um secundário positivo com ponto em 5p sai, portanto, negativo e truncado no gráfico do original. **Não replicar.**
4. **Temperatura.** Para 0x4020/0x4040/0x8020/0x8040/0xC020/0xC040, o último caractere ('C'/'F') é retirado da mantissa [BIN 0x41C0F6..0x41C20C].
5. **Mantissa ".".** Se a mantissa for "." (display vazio), a amostra é descartada [BIN 0x41C215..0x41C308].

A lógica completa de todos os ramos (por exemplo dB/dBm em 0x41C30D) **não foi lida linha a linha [NC]**.

### 7.6 Formato de tempo das amostras

`GetLocalTime` seguido de `sprintf("%3d:%02d:%02d.%1d", hora, min, seg, ms/100)` [BIN 0x41B8A8..0x41B8E1]. Revisão: confirmado. `SYSTEMTIME.wHour/wMinute/wSecond/wMilliseconds` são lidos em ebp−0x1B0/−0x1AE/−0x1AC/−0x1AA. A hora sai com largura 3, preenchida com espaços (ex.: " 14:05:09.3"), sem data. O handler só age se o flag "dado válido" 0x4D9728 estiver ligado [BIN 0x41B7EC..0x41B818]. A primeira amostra recebe na frente o texto guardado em `FormMain+0x430`, depois zerado [INF: data ou marcador de sessão].

### 7.7 Diferenças em relação ao PDF

- Bytes 10 e 11 trocados no PDF (§7.1).
- O byte 18 é usado (bit 3).
- "@" (byte 9, bit 4) invalida a leitura.
- O comando de tempo real do software é 82 66.
- No fluxograma, "Vendor_ID: 0x82" deve ser lido como 0x0820 (é o que o binário usa).

### 7.8 Verificação com as capturas reais

`verify_decode.py` reimplementa §7.2–§7.4 e produziu `verify_decode.out.txt`:

| Captura | Principal | Secundário | Modelo / bateria |
|---|---|---|---|
| RTD#1 | palavra 0x5 (V AC), "0.287", exp 0 → **0.287 V AC** | 0x400 (Hz), "0.000", exp 0 → **0.000 Hz** | 0x52, bateria OK |
| RTD#2 | 0x5, "0.156" → **0.156 V AC** | 0x400, "0.000" Hz | — |

Os dois resultados conferem com o display (ACV com Hz no secundário, pontas abertas) e confirmam a hipótese preliminar.

---

## 8. Importação da memória do datalogger

### 8.1 Leitura de blocos (Hid_ctrl_001)

**`ReadHMD(out24)` [BIN 0x426CA8]**

1. `FindHid`.
2. `HidSendCmd(h, 0x52, 0x88)`.
3. `Sleep(200)`.
4. Lê 27 + 9 bytes.
5. Monta `out[0..23]` = `raw[1..8] + raw[10..17] + raw[19..26]`.
6. Calcula a soma de 16 bits desses 24 bytes e compara com `chk = raw9[1] | raw9[2]<<8`. raw9 é o 4º relatório: `[ebp-0x1B]` é o byte 1 e `[ebp-0x1A]` o byte 2.
7. Retentativas:
   - checksum errado: `CloseHandle` e **repete sem limite** (`jmp 0x426CBD`) [BIN 0x426DF1];
   - resultado 0x102: até 3 tentativas (`Sleep(10)` e reabertura); depois devolve 0x102.
8. Fecha o dispositivo.

**Correção (revisão):**

- A falha na **escrita** (`HidSendCmd` ≠ 0, por exemplo 0x102 após 500 ms) **não** é retentada: fecha e devolve o código na hora [BIN 0x426CEF..0x426D07].
- Na leitura, só 0x102 é retentado. 0x80 e 0xFFFFFFFF são devolvidos direto [BIN 0x426DF6..0x426E40].
- No checksum errado não há `Sleep` nem contador: o laço reabre e reenvia 0x88 imediatamente.
- As 3 tentativas são totais (1 + 2 retentativas): o contador é incrementado antes de `cmp …, 3` [BIN 0x426DFF..0x426E06].

**`ReadNMD(out24)` [BIN 0x426E48]**

1. Envia 0x89 (sem o Sleep de 200 ms).
2. Em checksum errado **ou** timeout, troca o comando para **0x8A** (Cs_CMD, "reler o bloco atual") e tenta de novo, até 4 vezes (`cmp …, 4`), reabrindo o dispositivo a cada vez e com `Sleep(10)`.
3. Esgotadas as tentativas, devolve 0x102.

**Correção (revisão):**

- São **4 tentativas no total**: 1 com 0x89 e até **3 reenvios de 0x8A**. O contador é incrementado antes de `cmp …, 4` [BIN 0x426FA2..0x426FA9].
- Checksum errado é convertido em resultado 0x102 [BIN 0x426F8E..0x426F92] e entra na mesma contagem.
- Uma falha na **escrita** devolve o código na hora, sem retentativa [BIN 0x426E94..0x426EAC].
- O 0x8A também é usado quando a 1ª tentativa falha por timeout. Ainda não se sabe se o medidor avança o ponteiro em 0x89 que deu timeout [NC].

**Checksum.** O PDF-M p1 diz "soma dos bytes 1..27". O binário soma só os 24 bytes úteis. As duas formas são equivalentes porque os report IDs valem 0x00. Ambas as capturas conferem: HMD 0x0597 = 0x0597; NMD 0x0034 = 0x0034.

### 8.2 Fluxo da importação (`TFormImport.sbtnImportClick` [BIN 0x42EB68])

1. `FindHid`. Se falhar, E9015.
2. `HidReadModel` (82 66) e `CloseHandle`. Se o modelo ≠ 0x52, mostra E9017 [STR 9017] "Your meter is power off, disconnected or without built-in DATA LOGGING function." [BIN 0x42ECB3].
   - **Correção (revisão):** o código de retorno de `HidReadModel` não é testado; só o valor do modelo, comparado com 0x52 [BIN 0x42EC99..0x42ECBA]. Como `HidReadModel` converte 0xFF/F7/F3/F1/F0/E0/C0/80/00 em 0x82, só `raw[22] == 0x52` passa.
3. Confirmação. **Correção (revisão):** confirmado.
   - Texto: [STR 6001] "All existing data in buffer will be destroyed." + separador (0x4CF74C) + [STR 8010] "Do you really want to proceed?".
   - Caption: `wsprintf(STR 8999, 8010)` = "Warning[W8010]".
   - Estilo: `MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2` (0x124). IDNO = 7 cancela [BIN 0x42EDAD..0x42EEDF].
4. `ReadHMD`. Tratamento de erro:

   | Resultado | Mensagem |
   |---|---|
   | −1 (0xFFFFFFFF: cabo não encontrado ou `WAIT_FAILED`) | E9015 [BIN 0x42EFA4]. **Correção (revisão):** o teste é `== −1`, não "≠ 0 genérico" |
   | 0x102 ou 0x80 | **E9016** [STR 9016] "Data transmission timeout.\nPlease check meter and cable connection as well as meter power." [BIN 0x42F0B0, 0x42F1D1] |

5. Mostra [STR 8012] "Acquiring  data from DMM … please wait …" com o prefixo "[W8012] ".
6. **TotalBytes** = `hmd[1] | hmd[2]<<8 | hmd[3]<<16` [BIN 0x42F65D]. O buffer é arredondado para cima ao múltiplo de 24. Se a alocação falhar, E9006 "Insufficient system memory !".
7. Copia os 24 bytes do HMD para `buf[0..23]`. Para `pos` de 24 até o fim, de 24 em 24, chama `ReadNMD(buf+pos)`, com barra de progresso e o mesmo tratamento E9015 / E9016 [BIN 0x42F8EE..].
8. Com a captura HMD: TotalBytes = 2272, ou seja, 95 blocos (1 HMD + 94 NMD).
   - Revisão: o laço é `pos = 24; while (pos < TotalArredondado) { ReadNMD(buf+pos); pos += 24 }` [BIN 0x42F8EE..0x42F90C, 0x42FE12].
   - Entre os blocos há `Sleep(1)` e o teste do flag de cancelamento 0x4D95DC (botão Abort) [BIN 0x42FF93..0x42FFA1].
9. [STR 8013] "Processing data … please wait …" e decodificação das sessões. O índice no buffer é `endereço − 0xA0007A`.
   - **Correção (revisão):** a fórmula exata é `idx = ((hi − 0xA0) div 2)·0x10000 + mid·0x100 + lo − 0x7A`, com divisão com sinal por `sar` [BIN 0x430462..0x43048C].
   - `endereço − 0xA0007A` só vale para `hi` = 0xA0/0xA1. Para memórias maiores, o byte alto avança de 2 em 2 por página de 64 KiB [INF sobre o significado].
   - A 1ª sessão é fixa em 0xA00080 (hi=0xA0, mid=0, lo=0x80 → idx 6) [BIN 0x43024D..0x43025F].
   - `TotalSession = buf[4] | buf[5]<<8` (16 bits LE) [BIN 0x430269..0x43028A].

**Por sessão** [BIN 0x430462..0x43090B]

1. Lê `PS` (+2..+4), `NS` (+5..+7), `Bfunction` (+8), `Bselect` (+9), `Bstatus` (+0x0A) e `SP_Length` (+0x0B..+0x0D, 24 bits LE).
   - **Correção (revisão):** a leitura de `PS` (+2..+4) **não foi encontrada** neste laço. São lidos +5..+7 (NS: lo, mid, hi) [BIN 0x4308B1..0x4308F3], +8, +9, +0x0A e +0x0B..+0x0D [BIN 0x430492..0x430537]. Uso de PS: **NÃO CONFIRMADO**.
2. Tamanho do registro: 6 bytes se `Bstatus & 0x80` (dual), senão 3.
   - Revisão: quantidade = `SP_Length / 6` ou `/ 3`, divisão inteira [BIN 0x43054C..0x430577].
3. As amostras começam em +0x0E.
   - Revisão: cada registro de 3 bytes `b0 b1 b2` vira, em memória, `[b0 >> 4, b0 & 0x0F, b1, b2]`. No modo dual, os 3 bytes seguintes vão para o vetor do secundário [BIN 0x43067B..0x430767, 0x430787..].
   - O significado de cada campo (faixa, dígitos) está no PDF-M / documento de protocolo.
4. A próxima sessão está em `NS`.

**HMD real** (verificado): Model_Id = 1, TotalSession = 1, DLE/STX = EE/A0, NS1 = 0xA0095A, Bfunction = 01, Bselect = 00, Bstatus = 0x80, SP1_Length = 2250, isto é, **375 amostras duais**.

### 8.3 Decodificação (Unit_decodefunc)

**Intervalo de logging** = `Bstatus & 0x0F` [BIN 0x434808, tabela em 0x434829]:

| Código | Intervalo | Código | Intervalo |
|---|---|---|---|
| 0 | 0,05 s | 8 | 10 s |
| 1 | 0,1 s | 9 | 15 s |
| 2 | 0,5 s | 10 | 30 s |
| 3 | 1 s | 11 | 60 s |
| 4 | 2 s | 12 | 120 s |
| 5 | 3 s | 13 | 180 s |
| 6 | 4 s | 14 | 300 s |
| 7 | 5 s | 15 | 600 s |

A captura HMD tem código 0, ou seja, 0,05 s.

Revisão: os 16 valores (doubles gravados em `[ebp-0xC]/[ebp-0x8]`) foram conferidos resolvendo a tabela de saltos de 0x434829. O byte passado é `Bstatus` da sessão [BIN 0x4309C2..0x4309D5].

**Função** `(Bfunction, Bselect, …)` → (palavra principal, tipo, palavra secundária, tipo) [BIN 0x434C78, tabela em 0x434C9A para Bfunction 0..9]. Casos lidos diretamente:

| Bfunction | Bselect | Principal (palavra, tipo) | Secundário (palavra, tipo) | Evidência |
|---|---|---|---|---|
| 1 | 0 | 0x5, 1 | 0x400 (Hz), 7 | [BIN 0x434CD1] |
| 1 | 1 | 0x400, 7 | 0x5, 1 | [BIN 0x434CFA] |
| 2 | 0 | 0x6 (V DC), 2 | 0, 0 | [BIN 0x434D36] |
| 2 | 1 | 0x6, 2 | 0x5, 1 | [BIN 0x434D5B] |
| 2 | 2 | 0x7 (V AC+DC), 3 | 0x5, 1 | [BIN 0x434D84] |

Os demais Bfunction (3..9, entradas 0x434DAD..0x435288) **não foram resolvidos neste trabalho [NC]**. A tabela do PDF-M p8 está transcrita em `docs/protocol/bm52x-usb-protocol.md` §4.7.

**Posição do ponto decimal** por índice de função e faixa [BIN 0x434974, `switch` sobre `eax` 0..0x17 com tabela em 0x434993 e subswitch sobre o byte de faixa]. Ex.: o índice 0x17 sempre devolve 3 [BIN 0x434C61]. Os detalhes estão no documento de protocolo §4.10. A resolução completa da tabela é **[NC]**.

**Montagem de sessões na UI.** O `PageControl` ganha uma aba "Session n" ("Session " [BIN 0x4CF735]). O grid tem as colunas "Session", "Reading", "Time", "Main-Data" e "Sub-Data", com Max./Min. para cada dado [BIN 0x4CF58C..0x4CF611]. Arquivo próprio `*.slr` ("session logger data files(*.slr)|*.slr", "Untitled.slr") — formato **não analisado [NC]**. Exportação CSV com o mesmo cabeçalho da §10.3 [BIN 0x42BDFF..0x42C06C].

---

## 9. Aquisição no Graphical Recorder e intervalos

### 9.1 Diálogo de intervalo (`TDialogOption`)

[BIN 0x41E3BC..0x41EDE8]

- `cbxInterval` tem apenas dois itens: [STR 203] **"Full Speed"** e [STR 204] **"Customization…"**.
- "Customization…" abre um `InputBox` (caption [STR 205] "Interval in second(s)", prompt [STR 206] "Input an integer value ? ", valor inicial = intervalo atual).
- O texto passa por `StrToInt`. Se não for inteiro ou for < 0, mostra E9007 "Not a valid floating point value !" e pergunta de novo [BIN 0x41EBA7..0x41EC3D].
  - **Correção (revisão):** só o valor **negativo** volta a perguntar (`cmp [+0x2F4], 0; jl 0x41EAEC`) [BIN 0x41EBB8..0x41EBD3, 0x41EC36].
  - Com texto não inteiro, a exceção de `StrToInt` cai em 0x41EBE0: mostra E9007, mantém o valor anterior e ajusta `cbxInterval.ItemIndex` para 0 (valor 0) ou 1. Não pergunta de novo.
- O valor fica em `+0x2F4` (segundos, 0 = Full Speed). Revisão: digitar 0 em "Customization…" equivale a Full Speed (500 ms) [BIN 0x416355..0x41636D].
- Modo: `rbtnManual` / `rbtnAutomatic`.

### 9.2 Timer de amostragem

**Intervalo**

- `TimerLogger.Interval` = `[+0x460]` ms: **Full Speed = 500 ms** (constante `0x4C63CC = 500`) e N s = N × 1000 ms [BIN 0x416355..0x416383].
- O padrão na criação é 500 ms [BIN 0x413989].
- `[+0x464] = 200` [INF: pontos por página do gráfico, NC].

**`sbtnRecordStartClick` [BIN 0x415DB4]**

1. Se não houver link, mostra E9002 "You must link to the DMM by clicking the link button before starting data logging."
2. Configura o intervalo e o `Enabled` do timer (este depende do modo).
3. Incrementa o contador de sessões `+0x458` e cria o `.TMP` (§10.2).

**`TimerLoggerTimer` [BIN 0x41B7E0]**

1. Só age se "dado válido" (0x4D9728) estiver ativo.
2. Copia o instantâneo da thread sob a CS.
3. Trata a bateria fraca.
4. Cria o registro (§10.1) e o anexa ao TList 0x4D956C.
5. **Grava imediatamente** o registro no `.TMP` com `WriteFile` [BIN 0x41DA60..0x41DAAA].
6. Atualiza o PEGraph (`PEvset`) e os painéis Max/Min.

**Manual.** `sbtnManualLoggingClick` [BIN 0x4161B0] adiciona uma amostra por clique [INF pelo nome e menu; NC no detalhe].

---

## 10. Formatos de arquivo

### 10.1 Registro de amostra (memória, `.g53` e `.TMP`)

Estrutura em memória (12 bytes): `{ uint8 hasSub; MEAS* main; MEAS* sub; }`, com `hasSub = (palavra secundária ≠ 0)` [BIN 0x41D81E..0x41D843]. Cada `MEAS` tem **84 bytes (0x54)**, é zerado pelo ctor 0x40D0A4 e preenchido com `strcpy` [BIN 0x41D848..0x41DA19]:

| Offset | Tamanho | Conteúdo | Origem |
|---|---|---|---|
| 0x00 | 8 | Mantissa com sinal ("0.287", "OL", "-12.34"), C-string. **Correção (revisão):** é o texto da thread, com o ponto na posição real (§4.3). Sofre ajustes no `TimerLoggerTimer`: `Trim`, "-" normalizado e 'C'/'F' final retirado nas temperaturas [BIN 0x41BFD8..0x41C20C] | `mainStr` |
| 0x08 | 4 | Expoente **do prefixo** em texto ("0", "-3"). **Correção (revisão):** não inclui o ponto decimal. O valor real é `ToDouble(+0x00 + "E" + +0x08)` | `mainExp` |
| 0x0C | 4 | Palavra de função (uint32 LE; pode conter o bit 31) | 0x4D967C |
| 0x10 | 16 | Hora "%3d:%02d:%02d.%1d" | §7.6 |
| 0x20 | 16 | "Valor formatado". **Correção (revisão):** é cópia literal do texto do display vindo da thread (0x4D96AC = `strcpy` de 0x4D9674 [BIN 0x435AB6]), sem `sprintf` | 0x4D96AC |
| 0x30 | 8 | Prefixo ("k", "m", "u", "n", "M", "", ou "dB" em casos especiais) | 0x4D96BC |
| 0x38 | 16 | Unidade ("V", "W", "Hz", …) | 0x4D96C4 |
| 0x48 | 4 | float32: valor numérico para o gráfico (§7.5) | calculado |
| 0x4C | 8 | Rótulo "DC" / "AC" / "DC+AC" / "T1" / … | 0x4D96A8 |

Os tamanhos dos campos de string foram deduzidos dos offsets consecutivos; o terminador NUL está incluído **[INF]**.

### 10.2 `.TMP` (backup em tempo real)

- **Nome**: `<diretório corrente>\~BDMDL<n>.TMP`, com `n` = contador de sessões de gravação (IntToStr de `+0x458`) [BIN 0x415F43..0x415FF8; STR 6 "~BDMDL"; 0x4C8BEC ".TMP"].
- **Criação**: `CreateFileA(GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL)`. Em falha, E9003 "Unable to create file for real time temporary storage in hard disk."
- **Conteúdo**: sequência de registros de 169 bytes (`hasSub`:1 + `MEAS` principal:84 + `MEAS` secundário:84), **sem cabeçalho**, um por amostra.
- **Abertura**: o `OpenDialog` aceita "Temporary files(*.TMP)|*.tmp" [BIN 0x4C8BF6]. O leitor faz `ReadFile` em 0x416A6D..0x417434 **[NC: detalhe de como distingue .TMP de .g53]**.
  - **Correção (revisão): resolvido.** O tipo é escolhido pela **extensão no nome do arquivo**, com `AnsiString::Pos`, que diferencia maiúsculas [BIN 0x4B69A8], e o número de registros sai do tamanho (`GetFileSize`) [BIN 0x41681B..0x416A72]:

    | Extensão | Registros | Observação |
    |---|---|---|
    | ".g51" | `tamanho / 0x4C` | Formato legado de 76 bytes por registro [BIN 0x41689C]. Layout **não analisado [NC]** |
    | ".g52" | `tamanho / 0x54` | Formato legado de 84 bytes, só um MEAS [BIN 0x41692C]. Layout **não analisado [NC]** |
    | ".tmp" ou ".TMP" | `tamanho / 0xA9` | 169 bytes, sem cabeçalho [BIN 0x416A3E] |
    | qualquer outra (".g53") | `int32` lido do início | [BIN 0x416A55..0x416A6D] |

  - Um ".Tmp" com caixa mista seria lido como `.g53` [INF].

### 10.3 `.g53` (Save As do Graphical Recorder)

Gravado por [BIN 0x41795C..0x417BCF]: DefaultExt "g53", FileName "Untitled.g53", Filter "Data Logger files(*.g53)|*.g53", com `ofOverwritePrompt`.

```
int32 LE  N                                  ; número de registros (TList.Count)
repetir N vezes:
    uint8   hasSub
    byte[84] MEAS principal                  ; layout da §10.1
    byte[84] MEAS secundário
```

O tamanho do arquivo é 4 + 169·N bytes. Revisão: confirmado em [BIN 0x417A62..0x417B78]. A sequência é `CreateFileA(GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL)`, depois `WriteFile(&Count, 4)` e, por registro, `WriteFile(rec, 1)` + `WriteFile(main, 0x54)` + `WriteFile(sub, 0x54)`. O MEAS secundário é gravado mesmo com `hasSub = 0`. Em erro de criação, E9005 "File save error." Ao final, as flags `+0x469` / `+0x46A` voltam a 0 ("dados não salvos") [BIN 0x417B9E]. O aviso [STR 7005] "Do you want to save the data to a file before exit ?" usa essas flags [INF].

### 10.4 CSV (exportação "Text/Data Only" para arquivo)

Função [BIN 0x417DE8] no gravador; existe uma equivalente no Import [BIN 0x42BDFF]. Filtro "CSV files(*.csv)|*.csv", nome padrão "Untitled" [BIN 0x4CC945].

- **Separador**: vírgula. As strings "," estão em 0x4C8CD3.. e se repetem entre todos os campos.
- **Cabeçalho exato**: `#item,Time,MainData,MainUnit,MainFunc,Sub Data,SubUnit,SubFunc` [BIN 0x4C8CCD..0x4C8D12].
- **Linhas**: montadas em `TStringList` (`Add`, VMT+0x34) e gravadas no fim:

| Coluna | Origem |
|---|---|
| #item | Índice da amostra. **Correção (revisão):** é `IntToStr(índice + 1)`, ou seja, começa em 1 [BIN 0x4194B1..0x4194BE] |
| Time | `main+0x10` (hora "%3d:%02d:%02d.%1d") |
| MainData | `main+0x20` (valor como exibido). **Correção (revisão):** é o texto do display copiado da thread (ex.: "0.287", "-12.34", "OL"), não uma string de `sprintf` |
| MainUnit | `main+0x30` + `main+0x38`, com 'W' → "Ohm" e as formas de temperatura "C", " C [", "]", "F", " F [", "]" (ex.: ` C [T1]`) [BIN 0x4C8D1A..] |
| MainFunc | `main+0x4C` |
| Sub Data, SubUnit, SubFunc | Idem, com `sub` |

- **Casas decimais**: o valor do CSV é a string já formatada (`%05.*f` etc., 3 − expoente casas). Não há reformatação.
  - **Correção (revisão):** no gravador, a string é o texto do display da thread, com o ponto onde o medidor o mostra. O `%05.*f` pertence a `TimerGetSdTimer` [BIN 0x405A34], que não alimenta o `.g53` / CSV (§7.4). Continua valendo que não há reformatação.
- Revisão: são 7 separadores "," [0x4C8D50..0x4C8D5C] para 8 colunas, sem vírgula final.
- Revisão: o arquivo é gravado por `TStrings.SaveToFile` (VMT+0x64) [BIN 0x419ABF]. Pelo padrão da VCL 5, isso dá linhas CRLF em ANSI [INF].
- Revisão: a exportação percorre o intervalo de páginas escolhido no diálogo (todas, esta página ou de/até) [BIN 0x418351..0x41841C]. Não percorre necessariamente o buffer inteiro [INF no detalhe].
- **Data e hora**: só a hora, no formato acima. A string `"  ampm hh:nn:ss'\"'"` [BIN 0x4C8DAF] é formato de `FormatDateTime` do eixo X [INF].

A regra exata de concatenação de unidade para todos os casos de temperatura está **[NC]** (não foi lida linha a linha).

### 10.5 `BTCDL.ini`

- **Caminho**: `GetCurrentDirectoryA()` + [STR 7] `"\\BTCDL.ini"` [BIN 0x4033CC..0x40347C]. É o diretório **corrente**, não o do executável.
- **Leitura**: `TStringList.LoadFromFile` + `Values[...]`, ou seja, pares `nome=valor` **sem seção** [BIN 0x4034D2..0x403632]:

| Chave | Conversão | Destino | Uso |
|---|---|---|---|
| `iPort` | `StrToInt` | 0x4C60E8 | Número da porta COM (1..8) do diálogo "Serial Port selection" (`rbtnCom1..8` → 1..8 [BIN 0x40A65C..0x40A764]). Usado em `sbtnPortClick` para abrir "COMn" (Rscomm 0x409798) e no caption "Digital Meter : COM n". Não afeta o USB |
| `fHiLimit` | `StrToFloat` | double 0x4D85BC | Limite superior do Comparator |
| `fLowLimit` | `StrToFloat` | double 0x4D85C4 | Limite inferior do Comparator |

- **Padrões em exceção** (arquivo ausente ou inválido) [BIN 0x40366F..0x403693]: iPort = 2, fHiLimit = 100.0, **fLowLimit = 300.0**. O par fica invertido (Hi < Lo), o que parece bug do original; não replicar.
  - Revisão: confirmado. Os doubles valem 0x4059000000000000 = 100,0 e 0x4072C00000000000 = 300,0.
  - O bloco `try` é único: uma chave inválida (por exemplo, `fHiLimit=abc`) faz os **três** valores voltarem ao padrão, inclusive um `iPort` já lido.
  - `StrToFloat` / `FloatToStr` usam o separador decimal do Windows. Com locale pt-BR, o original gravaria "1,5" [INF].
- **Gravação** ao sair [BIN 0x403A1F..0x403B22]: `Values["iPort"] = IntToStr(iPort)` e os dois limites atuais via `FloatToStr`, depois `SaveToFile`.
- **Inicialização**: o programa tenta `CreateFileA("COM1".."COM8")` para descobrir as portas existentes [BIN 0x403363..0x4033CA].

---

## 11. Comparator e Analog Meter

### 11.1 Comparator

- **Limites**: Hi = `+0x318`, Lo = `+0x320` (double), carregados de fHiLimit/fLowLimit.
- **Edição**: `TDlgHiLowForm` com TMaskEdit e o prefixo e a unidade da função atual. Erros possíveis:
  - "Hi Limit is lower than or equal to Low Limit !" (W7010);
  - E9008 "Limit setting is unacceptable.";
  - E9007 "Not a valid floating point value !".
- **Alarme** (`bbtnAlart`, alterna "ON"/"OFF"; flag `+0x308`). Com o alarme ligado, a cada leitura, para `v = +0x32C` (float) [BIN 0x40EB4A..0x40EBEF]:
  - se **v > Hi**: `Beep(1047 Hz, 100 ms)`;
  - senão, se **v < Lo**: `Beep(523 Hz, 100 ms)`;
  - dentro da faixa: silêncio.

  No Windows 9x usa uma rotina própria de som (0x40DFA0); no NT, `Beep()`.
- **Mostrador** [BIN 0x40D924..0x40DA68]: ponteiro/arco com escala de **[Lo − d, Hi + d]**, onde **d = (Hi − Lo) × 0,5**.
  - Fator angular: 20 / d graus por unidade (constantes 0,5 em 0x40DB3C e 20,0 em 0x40DB40).
  - Ângulo do ponteiro: `130 − x × fator`, com `x = v − (Lo − d)` limitado a [0, 4d]. O arco vai de 130° (v ≤ Lo − d) a 50° (v ≥ Hi + d). **Correção (revisão):** Lo cai em **110°** (x = d) e Hi em **70°** (x = 3d), e não em 120°/60° [BIN 0x40D93D..0x40D9D8; 130,0 em 0x40DB48].
  - Comparações do alarme: estritas (`v > Hi`, `v < Lo`). `v == Hi` e `v == Lo` não bipam [BIN 0x40EB5F..0x40EBE6].
  - Animação do ponteiro: aproximação em passos de 0,1 × diferença [INF: constante extended 0,1 em 0x40DB4C]; ângulo inicial 90° [BIN 0x40D6D8].
- **OL / "---"**: v recebe o ponto médio `(Hi + Lo) × 0,5` ou um extremo [BIN 0x40EA7D..0x40EADE].
- O PASS/FAIL **não** aparece como texto: o resultado é visual (ponteiro e painéis Hi/Low) e sonoro. Não há strings "PASS"/"FAIL" no binário.

### 11.2 Analog Meter

- **Escala**: ângulos em graus convertidos por π/180 (0x410320 = 0,0174532924) e ticks ×100 [BIN 0x41029D..0x4103C3].
- **Multiplicador** `+0x308` = fator × valor, com fatores **1, 6, 2, 5** por função e faixa [BIN 0x410D85..0x410FD5; floats em 0x4112B8..0x4112C4]. Ele aparece no `MultPanel`. "---" é tratado à parte [BIN 0x411162].
- A tabela exata função/faixa → fator é **[NC]**. **[INF]** É uma escala analógica de 0 a 10 com multiplicadores ×1, ×2, ×5, ×6 (6000 contagens).

---

## 12. Mensagens do programa

O formato é sempre: caption `Warning[W<id>]` [STR 8999] ou `Error[E<id>]` [STR 9999]; texto = `LoadStr(<id>)`. Também existe o prefixo "[W<id>] " [BIN 0x42F47C].

| Código | Texto | Onde aparece (evidência) |
|---|---|---|
| E9015 | USB adapter cable not found. | Link, timer, importação [BIN 0x404C39, 0x404E6F, 0x42EBC8] |
| E9015 + 8004 | No data received. | Thread na 3ª falha consecutiva (2 reaberturas silenciosas antes) [BIN 0x43581D]. Correção (revisão) |
| W7012 | 8004 + 7006 + 7012 | Falha de leitura no timer [BIN 0x4050D0..0x40515E] |
| W8000 | The battery power of the DMM is low. + 7009 | Bateria fraca [BIN 0x4053F9, 0x41BBF1] |
| E9016 | Data transmission timeout.\nPlease check meter and cable connection as well as meter power. | HMD/NMD com timeout [BIN 0x42F0DC] |
| E9017 | Your meter is power off, disconnected or without built-in DATA LOGGING function. | Modelo ≠ 0x52 [BIN 0x42ECD9] |
| E9002 | You must link to the DMM … before starting data logging. | Start sem link [BIN 0x415E37] |
| E9003 | Unable to create file for real time temporary storage in hard disk. | .TMP [BIN 0x4160BA] |
| E9005 | File save error. | .g53 [BIN 0x417AC4] |
| E9006 | Insufficient system memory ! | Importação [BIN 0x42F75B] |
| E9007 | Not a valid floating point value ! | Intervalo/limites [BIN 0x41EBC9] |
| W8015 | You must select a valid USB\COM port before linking to the DMM. | Porta COM inválida [BIN 0x404802] |
| W8010/8012/8013 | Proceed? / Acquiring… / Processing… | Importação |
| 8011 | A handshaking error occurred. | Não referenciada [NC] |
| 9001, 9004, 9008–9014, 9018, 7010, 7011, 8001–8009, 8014, 7001–7004, 7999 | Ver `stringtable.tsv` | Diversos |

---

## 13. Requisitos de paridade para a port .NET (priorizados)

### P0: obrigatórios para ler o medidor corretamente

1. **Transporte HID.**
   - Escrever relatórios de saída de 4 bytes `[00, 00, 0x52, cmd]`.
   - Ler relatórios de entrada de 9 bytes: 3 relatórios (27 bytes) no RTD e 4 (36 bytes) em HMD/NMD/CMD.
   - Identificar o dispositivo por VID 0x0820 / PID 0x0001, enumerando **todas** as interfaces (o limite de 20 do original não deve ser replicado).
   - No Linux, usar hidraw (`/dev/hidraw*`) ou libusb. Sem report ID numerado, o kernel pode entregar 8 bytes sem o 0x00 inicial; o parser deve aceitar ambos **[INF]**.
     - Revisão: pela documentação do hidraw, em dispositivo sem report IDs numerados o `read()` devolve só os 8 bytes, e o `write()` exige o 1º byte = 0x00 (número do relatório) seguido dos dados. Assim, `[00 00 52 66]` vira o relatório de 3 bytes `00 52 66`.
     - Isso vem do conhecimento geral do kernel, **não foi testado neste hardware [NC]**. É o mesmo layout do Windows (OutputReportByteLength = 4 com o ID).
2. **Comando RTD.** Usar `52 66` (validado no hardware). `82 66` fica como opção configurável **[NC]**.
3. **Timeouts equivalentes**: escrita ≤ 2 s; 1º relatório ≤ 4 s; relatórios seguintes ≤ 100 ms no RTD; 500 ms por etapa na memória.
4. **Remoção dos report IDs** em `raw[0]`, `raw[9]` e `raw[18]` (e no `raw[27]` da memória).
5. **Validação do quadro**: modelo em `raw[22]`, "@", combinações m+µ. **Correção (revisão):** isto vai além do original. A thread de aquisição do original não valida (§6.5, §7.2); só `HidReadRtd` valida. Recomenda-se manter na port, descartando o quadro com erro próprio e não com 0x102 ambíguo.
6. **Tabela de 7 segmentos** completa (§7.4, 30 símbolos + '?'), com o bit 4 mascarado.
7. **Números.**
   - Ponto decimal à direita (expoente por c = 4/2/1/0 → 0/1/2/3), prefixos k/M/m/µ/n por display, sinal −.
   - OL preservando a posição do ponto, "----", "---" e "Auto".
   - A API deve expor o valor numérico (double), o texto exibido e a unidade SI.
8. **Palavra de função e unidades** da §7.3, com os rótulos AC/DC/AC+DC/T1/T2/T1−T2, °C/°F pelo último dígito e bateria fraca (bit 31). Em vez de 'W' + fonte Symbol, usar "Ω" e "µ".
9. **Laço de aquisição** em thread de fundo com estado "válido/linkado" e evento ou fila de "nova leitura".
   - 3 retentativas silenciosas (reabrir o dispositivo, 10 ms), depois erro E9015 "No data received.". **Correção (revisão):** no original, o erro sai na **3ª falha consecutiva**, depois de 2 reaberturas silenciosas, e o timeout da escrita não é verificado (§6.5).
   - Sinal do secundário: usar `raw[8].5`, nunca o flag 0x4D9680 do original, que lê `raw[11].4` (§4.3).
   - Dispositivo ausente → evento de desconexão.
   - A port deve impor um período mínimo configurável (o original não tem sleep).
10. **Importação da memória.**
    - HMD: total de bytes; 1 + ⌈TotalBytes/24⌉ − 1 NMDs.
    - Checksum LE de 16 bits da soma dos 24 bytes úteis.
    - Em erro de checksum ou timeout no NMD, reenviar CMD (0x8A) até 4 vezes. **Limitar** também as retentativas do HMD (o original repete sem fim).
      - **Correção (revisão):** são 4 tentativas no total por bloco (1 × 0x89 + até 3 × 0x8A).
      - No original, uma falha na escrita não é retentada (§8.1).
    - Montagem por sessão (Bstatus.7 → 6 ou 3 bytes por registro; intervalo pelo nibble baixo; cadeia NS_Addr; endereço − 0xA0007A).
      - **Correção (revisão):** o índice é `((hi − 0xA0) div 2)·0x10000 + mid·0x100 + lo − 0x7A`.
      - A 1ª sessão fica em 0xA00080.
      - `TotalSession = hmd[4..5]` LE (§8.2).
    - Verificação do modelo (0x52) antes de importar (E9017).

### P1: paridade funcional da aplicação

11. **Gravador (Graphical Recorder).**
    - Modos Full Speed (500 ms), intervalo inteiro em segundos (≥ 0; inválido → E9007) e Manual.
    - Start exige link (E9002).
    - Max/Min por display, paginação, contagem "Data in buffer : %d" e "Page : %d of %d".
12. **Backup contínuo** `~BDMDL<n>.TMP`, com append por amostra de 169 bytes. Recomendado: diretório de dados configurável em vez do diretório corrente.
13. **`.g53`**: leitura e escrita byte a byte compatíveis (`int32 N` + N × 169), para abrir arquivos gerados no Windows. Strings em ANSI/CP1252, C-strings de tamanho fixo.
14. **CSV**: mesmo cabeçalho e colunas (§10.4), separador ",", unidade "Ohm" no lugar de Ω e hora "%3d:%02d:%02d.%1d". Revisão: `#item` começa em 1, e MainData/Sub Data são o texto do display.
    - Na leitura de arquivos, decidir o formato pela extensão: `.g53` com cabeçalho `int32`, `.tmp`/`.TMP` com tamanho/169, e os legados `.g51` (76 B) e `.g52` (84 B) não analisados (§10.2).
15. **Comparator**: limites Hi/Lo persistidos, validação Hi > Lo, alarme ON/OFF.
    - Na API, o alarme vira um estado `Above` / `Below` / `Within`; o original usa bipes de 1047 Hz e 523 Hz, 100 ms.
    - Não replicar o padrão invertido (100/300) do original. Padrões propostos: Hi = 200, Lo = 100, como no INI distribuído.
16. **Configuração** compatível com `BTCDL.ini` (chaves `iPort`, `fHiLimit`, `fLowLimit`, sem seção) para importação. Em Linux, guardar em JSON e oferecer import/export do INI.
17. **Mensagens e códigos**: preservar os IDs W/E da §12 nos erros da API (ex.: `{"code":"E9016", …}`), facilitando o suporte cruzado com o software original.

### P2: desejáveis

18. Analog Meter (escala 0–10 com multiplicador ×1/×2/×5/×6) e mostrador do Comparator (faixa [Lo − d, Hi + d], d = (Hi − Lo) / 2): na port REST, como dados para o front-end.
19. Data List em abas por sessão, com colunas Session/Reading/Time/Main-Data/Sub-Data e Max/Min.
20. `.slr` (session logger) da importação [NC: formato a levantar].
21. Exportação de gráfico (bitmap/metafile/impressora). O original depende do ProEssentials PEGRAP32.DLL, que não tem equivalente no Linux; substituir por PNG/SVG gerado no servidor ou pelo front-end.
22. "About" com versão do firmware (`HIDD_ATTRIBUTES.VersionNumber`, 0x8603 no cabo real) e número de série do cabo (string serial HID).
23. Porta COM (RS-232, `iPort`) apenas se houver demanda: no V6.0.0.9s o link usa somente USB; a porta COM é aberta no diálogo, mas não alimenta a thread [INF].

---

## 14. Questões em aberto

1. O BM525s/BC-86X responde a `00 00 82 66` (comando que o software original usa)? Testar no hardware com `probe.cs -- 1 66` versus um comando 82 66 (o probe hoje fixa o byte 2 em 0x52).
2. Significado do bit 3 de `raw[17]` (PDF: "don't care"), que o binário usa para marcar V AC/DC com 0x01000000 (provável "AcV6/DcV6").
3. Origem das palavras 0x14 ("V", provavelmente diodo) e 0x200000 ("%", 4–20 mA): não saem da decodificação RTD analisada; podem vir de Unit_decodefunc (memória).
4. Tabela completa Bfunction 3..9 × Bselect (0x434C78) e posição do ponto decimal (0x434974): só parcialmente resolvidas aqui; ver o documento de protocolo §4.7–§4.10.
5. Quem habilita `TimerGetSd` no fluxo normal, e se esse caminho ainda é usado na V6.0.0.9s, ou se só a `T8252xThread` alimenta a UI.
6. Por que os 3 NMD seguidos da captura devolveram blocos idênticos: o ponteiro de memória não avançou? Repetir HMD seguido de NMD em sequência, com e sem fechar o handle entre comandos, como faz o original.
7. Formato `.slr`; como o leitor de `.g53` diferencia `.TMP` (sem contador); significado do texto prefixado à 1ª amostra (`FormMain+0x430`).
   - **Correção (revisão):** a parte do `.TMP` está resolvida: a distinção é pela extensão (§10.2). Continuam em aberto o `.slr`, o `FormMain+0x430` e os layouts legados `.g51` / `.g52`.
8. Fator exato do Analog Meter por função/faixa.
9. (Revisão) O flag secundário 0x4D9680 vem de `raw[11].4` [BIN 0x43596A]. Também é copiado para `TDigitalMeterThread+0x40` [BIN 0x40B8F9..0x40B902], e o efeito no mostrador do Digital Meter original **não foi analisado [NC]**. Ainda não há captura com secundário negativo para confirmar o comportamento do medidor.
10. (Revisão) Semântica do `n` (posição do ponto) na substituição de OL do gráfico (§7.5) e da função 0x4AFA78 (provável pow10) **[INF]**.

---

## 15. Arquivos gerados nesta análise

Base: `C:\Users\pc-eng\AppData\Local\Temp\claude\C--dev-Brymen-BU-86X-USB-kit\290f1ca3-cacf-4f39-8e71-e9d122aff040\scratchpad\`

- **`re\`**:
  - REA: `rea-*.json`;
  - dumpbin: `dumpbin-*.txt`;
  - desmontagem: `objdump-text-intel.txt` (linear), `objdump-text-intel-resync.txt`, `asm-annotated.txt`;
  - strings e símbolos: `strings-exe.tsv`, `strings-data-clean.tsv`, `stringtable.tsv`, `vmt.json`, `vmt-published.txt`;
  - formulários: `dfm\*.dfm.txt`, `dfm\forms-summary.json`;
  - tabelas e verificação: `sevenseg-0x426844-full.txt`, `unitstr-0x437c24.txt`, `verify_decode.out.txt`;
  - extratos limpos: `timerlogger.txt`, `import.txt`, `unit_decodefunc.txt`, `comparator.txt`, `analog.txt`, `thread-decode-tail.txt`.
- **`scripts\`**: `dfm_extract.py`, `pe_stringtable.py`, `pe_strings.py`, `vcl_vmt.py`, `resync_asm.py`, `annotate_asm.py`, `clean_asm.sh`, `switch_resolver.py`, `verify_decode.py`, `render_pdf.py`, `crop_png.py`.
- **`target\`**: cópias dos arquivos do programa instalado (nenhum foi executado).

---

## 16. Revisão adversarial independente

Um revisor diferente do autor fez esta revisão estática. Nada foi executado: nem o `.exe`, nem o USB, nem downloads.

**Escopo e método**

- Desmontagem feita direto do binário instalado `C:\Program Files (x86)\BTC\Bs82-52x\6009s\Bs8252xV6009sA.exe`, cujo SHA-256 `37e01bb0…77441ef1` é idêntico ao da cópia em `target\`.
- Ferramenta: `llvm-objdump --start-address/--stop-address` do NDK, mais scripts próprios em `scratchpad\scripts-verify\`, todos com `python -I`:

| Script | Função |
|---|---|
| `rev_pe.py` | Parser PE: seções, IAT e mapa de thunks `jmp [IAT]` |
| `rev_dis.py` | Desmontagem anotada com nomes de imports e literais |
| `rev_exports.py` | Resolve os ordinais 3/6/15 de `hid.dll`, local e SysWOW64: `HidD_GetAttributes`, `HidD_GetHidGuid`, `HidD_GetSerialNumberString` |
| `rev_switchsim.py` | Simula os switches do compilador (7 segmentos, OL do gráfico) |
| `rev_unitsim.py` | Simula o switch palavra → unidade |
| `rev_strtab.py` | Lê a string table RT_STRING diretamente |

- Saídas intermediárias: `rev_*.asm` e `rev_7seg_raw.txt`.

**Confirmado de forma independente** (sem mudança no texto)

- Sequência HID de `HidReadModel` / `HidReadRtd`: `82 66`, esperas de 2000 / 4000 / 100 ms, códigos 0x182 / 0x183, descarte de `raw[0]`, `raw[9]` e `raw[18]`, mapa de modelos e o bug `CancelIo(&h)`.
- `FindHid` com limite de 20 interfaces e VID 0x0820 / PID 0x0001.
- Palavra de função, todos os bits, principal e secundário [BIN 0x425070..0x42533E].
- Tabela de 7 segmentos: 30 símbolos + '?', idêntica, simulada nos 256 valores.
- Expoente do ponto decimal (c = 4/2/1/0 → 0/1/2/3).
- Prefixos do principal e do secundário, e strings de OL.
- Checksums HMD 0x0597 e NMD 0x0034. TotalBytes = 2272, com 94 NMD.
- Tabela de intervalos de 0,05 a 600 s.
- Bfunction 1 e 2 × Bselect.
- Timer do gravador (500 ms ou N × 1000).
- Cabeçalho CSV exato.
- `.g53` = `int32` + N × 169.
- Caminho e chaves do `BTCDL.ini` e padrões 2 / 100 / 300.
- Bipes de 1047 / 523 Hz por 100 ms com o alarme ON.
- Textos das mensagens E9002, 9003, 9005, 9006, 9007, 9008, 9015, 9016, 9017; W7010, 7012, 8000, 8010, 8012, 8013; 6001.
- STR 8011 sem referência imediata no código.

**Corrigido** (marcado com "Correção (revisão):" no texto)

- §4.3: semântica das globais da thread.
- §6.1: cópia de `FindHid` na thread.
- §6.2: 0x102 ambíguo.
- §6.3: as 3 gravações de 0x66.
- §6.5: escrita não verificada, contagem de retentativas e ausência de validação.
- §7.1: `raw[23]` = Byte 24.
- §7.2: escopo da validação.
- §7.3: comparação sem máscara.
- §7.4: a thread não usa `sprintf`.
- §7.5: cálculo do valor do gráfico, com OL e sinal.
- §8.1: retentativas de HMD/NMD.
- §8.2: modelo, confirmação, E9015, índice de endereço, PS e TotalSession.
- §9.1: validação do intervalo.
- §10.1, §10.2, §10.4: campos MEAS, leitor por extensão e `#item` começando em 1.
- §10.5: bloco `try` único.
- §11.1: ângulos 110° / 70°.
- §12 e §13: textos e números ajustados.
