# Brymen.Dmm.ConsoleSample

Console .NET 10 que usa o **pacote** `Brymen.Dmm`, como um projeto de fora deste repositório usaria: imprime as
leituras do multímetro ao vivo e importa a memória do datalogger para um CSV.

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
14:02:31.415  0.287 V AC       | 0.000 Hz         [AUTO]
...
```

### Argumentos

| Argumento | Efeito |
|---|---|
| `--simulated` | Medidor simulado (`BrymenMeter.OpenSimulatedAsync`), sem hardware. |
| `--device <caminho>` | Cabo a abrir: `/dev/hidraw0`, `/dev/brymen-bu86x` ou o caminho `\\?\hid#vid_0820&pid_0001...` do Windows. Sem ele, o primeiro VID 0820h/PID 0001h. |
| `--count <N>` | Para depois de N leituras. Sem ele, lê até o Ctrl+C. |
| `--import [arquivo]` | Lê a memória do datalogger (`ReadLoggerMemoryAsync`, com o progresso por bloco) e salva em CSV (padrão: `datalogger.csv`). Sem `--count`, termina depois da importação; com ele, lê ao vivo em seguida. |
| `--list` | Lista os cabos BC-86X conectados (`BrymenMeter.FindDevices`). |
| `--help` | Mostra a ajuda. |

Exemplos com o cabo real:

```bash
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --list
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --device /dev/hidraw0
dotnet run --project samples/Brymen.Dmm.ConsoleSample -- --import memoria.csv --count 5
```

O CSV tem uma linha por amostra (sessão, amostra, tempo, função, texto do LCD, valor, unidade e modo do principal e
do secundário), com vírgula como separador, ponto decimal e UTF-8 com BOM. Os códigos de saída são 0 (sucesso, também
depois do Ctrl+C), 1 (falha do cabo ou do medidor, com o código `E9015`, `W7012` etc.) e 2 (argumento inválido).

No Linux, o usuário precisa de acesso ao `/dev/hidrawN` do cabo: veja a regra udev em
[`docs/library.md`](../../docs/library.md#linux-x64-e-arm64).

## Depois de mudar a biblioteca

O NuGet guarda cada versão de pacote no cache global (`~/.nuget/packages/brymen.dmm/<versão>`) e não a extrai de
novo: gerar outra vez a **mesma** versão não chega ao exemplo. Depois de mudar `src/`, faça uma destas:

```bash
# a) Uma versão de desenvolvimento só para o teste (o -p:Version vale para o pack e para o run)
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages -p:Version=0.1.0-dev.2
dotnet run --project samples/Brymen.Dmm.ConsoleSample -p:Version=0.1.0-dev.2 -- --simulated --count 5

# b) Ou apague a versão do cache e gere de novo
rm -rf ~/.nuget/packages/brymen.dmm/0.1.0
```

No PowerShell, o item b é `Remove-Item -Recurse -Force "$env:USERPROFILE\.nuget\packages\brymen.dmm\0.1.0"`.
