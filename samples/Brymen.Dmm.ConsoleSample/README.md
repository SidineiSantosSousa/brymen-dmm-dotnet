# Brymen.Dmm.ConsoleSample

Console .NET 10 que usa o **pacote** `Brymen.Dmm`, como um projeto de fora deste repositório usaria: imprime as
leituras do multímetro ao vivo, da família BM52x (BM525s = Minipa ET-2615A) ou Minipa ET-2517A (Brymen BM827s, BM82x), e importa a
memória do datalogger do BM52x para um CSV.

O projeto não está na solução (`BrymenDmm.slnx`) e não referencia o código de `src/`: ele restaura o pacote do feed
local `artifacts/packages`, configurado em [`samples/nuget.config`](../nuget.config), na versão do
`Directory.Build.props`. Por isso o pacote precisa ser gerado antes.

## Fluxo: `dotnet pack` e depois `dotnet run`

Na raiz do repositório:

```bash
# 1. Gera artifacts/packages/Brymen.Dmm.<versão>.nupkg (e o .snupkg)
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages

# 2. Restaura o pacote do feed local, compila e roda (os argumentos do exemplo vêm depois do --)
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --simulated --count 10
```

Saída (medidor simulado, com as capturas reais do BM525s em ACV):

```text
Cabo: sim://bu86x/0 (Brymen Superior DMM)
10 leituras (Ctrl+C interrompe):
Medidor: Minipa ET-2615A (Brymen BM525s, BM52x), com datalogger
14:02:31.415  0.287 V AC       | 0.000 Hz         [AUTO]
...
Tempo real: 10 consultas, 10 pedidos, 0 reenvios, 0 consultas recuperadas por reenvio, 0 sem resposta
```

A linha `Medidor:` sai na 1ª leitura, que detecta a família pelo Model ID, e de novo se o medidor for trocado no cabo.
A linha `Tempo real:` sai no fim, com os contadores de `BrymenMeter.RealTimeStatistics`. Com o ET-2517A simulado, que
descarta pedidos como o real, os reenvios aparecem nela sem nenhum erro. As 60 leituras levaram cerca de 18 s, e não
12 s: as consultas com reenvio demoram mais que os 200 ms do intervalo.

```text
$ dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --simulated et2517a --count 60
Cabo: sim://bu86x/0 (Brymen Superior DMM)
60 leituras (Ctrl+C interrompe):
Medidor: Minipa ET-2517A (Brymen BM827s, BM82x), sem datalogger
17:21:27.217  0.013 V AC       | 0.000 Hz         [AUTO]
...
17:21:44.889  0.000 V DC       | 0.000 V AC       [AUTO]
Tempo real: 60 consultas, 84 pedidos, 24 reenvios, 13 consultas recuperadas por reenvio, 0 sem resposta
```

O descarte do simulado é determinístico (a mesma semente perde os mesmos pedidos), mas depende dos tempos reais da
máquina: os números podem variar um pouco de uma execução para outra.

### Argumentos

| Argumento | Efeito |
|---|---|
| `--simulated [modelo]` | Medidor simulado (`BrymenMeter.OpenSimulatedAsync`), sem hardware: `bm525s` (padrão; `SimulatedBm52xMeter.FromCaptures`, com datalogger) ou `et2517a` (`SimulatedBm52xMeter.FromEt2517aCaptures`: a varredura real, sem datalogger e com o descarte de pedidos medido no medidor real). |
| `--device <caminho>` | Cabo a abrir: `/dev/hidraw0`, `/dev/brymen-bu86x` ou o caminho `\\?\hid#vid_0820&pid_0001...` do Windows. Sem ele, o primeiro VID 0820h/PID 0001h. |
| `--family <família>` | `BrymenMeterOptions.Family`: `auto` (padrão: detectada na 1ª leitura), `bm52x` (sempre `52 66`, como a versão 0.1.0) ou `bm82x` (sempre `82 66`, com os reenvios). Com a família fixa, um medidor da outra família dá `W8011`. |
| `--count <N>` | Para depois de N leituras. Sem ele, lê até o Ctrl+C. |
| `--import [arquivo]` | Lê a memória do datalogger (`ReadLoggerMemoryAsync`, com o progresso por bloco) e salva em CSV (padrão: `datalogger.csv`). Só o BM52x tem datalogger: no BM82x, a leitura falha na hora com `E9014`, sem nenhum comando de memória. Sem `--count`, termina depois da importação; com ele, lê ao vivo em seguida. |
| `--list` | Lista os cabos BC-86X conectados (`BrymenMeter.FindDevices`). |
| `--help` | Mostra a ajuda. |

Exemplos com o cabo real:

```bash
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --list
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --device /dev/hidraw0
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --import memoria.csv --count 5     # BM525s
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --family bm82x --count 50          # ET-2517A
```

O CSV tem uma linha por amostra (sessão, amostra, tempo, função, texto do LCD, valor, unidade e modo do principal e
do secundário), com vírgula como separador, ponto decimal e UTF-8 com BOM. Os códigos de saída são 0 (sucesso, também
depois do Ctrl+C), 1 (falha do cabo ou do medidor, com o código `E9015`, `W7012`, `E9014` etc.) e 2 (argumento
inválido).

No Linux, o usuário precisa de acesso ao `/dev/hidrawN` do cabo: veja a regra udev em
[`docs/library.md`](../../docs/library.md#linux-x64-e-arm64).

## Depois de mudar a biblioteca

O NuGet guarda cada versão de pacote no cache global (`~/.nuget/packages/brymen.dmm/<versão>`) e não a extrai de
novo: gerar outra vez a **mesma** versão não chega ao exemplo. Depois de mudar `src/`, faça uma destas:

```bash
# a) Uma versão de desenvolvimento só para o teste (o -p:Version vale para o pack e para o run)
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages -p:Version=0.2.0-dev.2
dotnet run --project samples/Brymen.Dmm.ConsoleSample -p:Version=0.2.0-dev.2 -- --simulated --count 5

# b) Ou apague a versão do cache e gere de novo
rm -rf ~/.nuget/packages/brymen.dmm/0.2.0
```

No PowerShell, o item b é `Remove-Item -Recurse -Force "$env:USERPROFILE\.nuget\packages\brymen.dmm\0.2.0"`.
