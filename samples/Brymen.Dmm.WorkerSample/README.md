# Brymen.Dmm.WorkerSample

Worker service .NET 10 com os **pacotes** `Brymen.Dmm.Extensions.Hosting` e `Brymen.Dmm` (este como dependência do
primeiro). O `AddBrymenMeter` registra o serviço que abre o multímetro na partida (BM52x, como o BM525s = Minipa
ET-2615A, ou BM82x, como o Minipa ET-2517A), tenta de novo enquanto o cabo não aparece e mantém a leitura contínua com
reconexão automática; o [`ReadingLogger`](ReadingLogger.cs) registra cada leitura e cada mudança de estado no
`ILogger`, pelos eventos do `IBrymenMeterAccessor`.

Como o [ConsoleSample](../Brymen.Dmm.ConsoleSample/README.md), o projeto fica fora da solução e restaura os pacotes do
feed local `artifacts/packages` ([`samples/nuget.config`](../nuget.config)), na versão do `Directory.Build.props`.

## Fluxo: `dotnet pack` e depois `dotnet run`

Na raiz do repositório:

```bash
dotnet pack src/Brymen.Dmm -c Release -o artifacts/packages
dotnet pack src/Brymen.Dmm.Extensions.Hosting -c Release -o artifacts/packages

dotnet run --project samples/Brymen.Dmm.WorkerSample -- --simulated            # BM525s simulado
dotnet run --project samples/Brymen.Dmm.WorkerSample -- --simulated et2517a    # ET-2517A simulado
```

```text
info: Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService[1]
      Multímetro aberto no cabo sim://bu86x/0 (Superior DMM).
info: Brymen.Dmm.WorkerSample.ReadingLogger[102]
      Multímetro na partida: Connected (cabo sim://bu86x/0)
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Brymen.Dmm.Extensions.Hosting.BrymenMeterHostedService[8]
      Família do multímetro: Minipa ET-2615A (Brymen BM525s, BM52x), com datalogger.
info: Brymen.Dmm.WorkerSample.ReadingLogger[100]
      Leitura: 0.287 V AC | 0.000 Hz [AUTO]
```

O medidor abre no `StartingAsync` do serviço do `AddBrymenMeter`, antes do `StartAsync` de todos os serviços, e a
leitura contínua começa depois deles: o `ReadingLogger`, registrado depois do `AddBrymenMeter` e inscrito no próprio
`StartAsync`, já encontra o medidor aberto e recebe a 1ª leitura (`0.287`). A ordem do registro não importa.

A 1ª leitura detecta a família pelo Model ID, e o serviço a registra (evento 8). Com `--simulated et2517a`, a linha é
“Família do multímetro: Minipa ET-2517A (Brymen BM827s, BM82x), sem datalogger.”, e os pedidos que o simulado descarta, como o
ET-2517A real, são reenviados sem nenhum aviso no log.

Sem `--simulated`, o worker usa o primeiro cabo BC-86X. Sem o cabo, ele sobe assim mesmo, registra o `E9015` uma vez
como aviso e tenta de novo a cada `ReconnectDelay`, até o cabo aparecer (as tentativas seguintes e as passagens por
`Connecting` vão para o nível Debug).

## Configuração

As opções ficam na seção `Brymen` do [`appsettings.json`](appsettings.json) e podem vir também da linha de comando ou
de variáveis de ambiente (`Brymen__DevicePath`, `Brymen__PollInterval` etc.):

```bash
dotnet run --project samples/Brymen.Dmm.WorkerSample -- --Brymen:DevicePath=/dev/brymen-bu86x --Brymen:PollInterval=00:00:00.500
dotnet run --project samples/Brymen.Dmm.WorkerSample -- --Brymen:Family=Bm82x        # família fixa: Auto, Bm52x ou Bm82x
```

O `appsettings.json` usa `PollInterval` de 1 s, para o log não crescer rápido. Com o ET-2517A, um intervalo longo perde
mais pedidos (cerca de 60 % na 1ª tentativa a 1 s, no medidor real): os reenvios cobrem as perdas, e um intervalo menor,
como `--Brymen:PollInterval=00:00:00.200`, as reduz. As chaves `RealTimeResendTimeout` (300 ms) e
`RealTimeResendBudget` (2 s) ajustam os reenvios.

As chaves aceitas e o restante da integração estão em [`docs/library.md`](../../docs/library.md#integração-com-o-generic-host).
O `DeviceProvider` (o medidor simulado do `--simulated`) só pode ser ajustado por código, como no
[`Program.cs`](Program.cs).

## Publicar para o Raspberry Pi

```bash
dotnet publish samples/Brymen.Dmm.WorkerSample -c Release -r linux-arm64 --self-contained -p:PublishTrimmed=true
```

O publish trimmed não tem avisos de trimming: o binding da configuração é gerado em tempo de compilação. No Linux, o
usuário do serviço precisa de acesso ao `/dev/hidrawN` do cabo (regra udev em
[`docs/library.md`](../../docs/library.md#linux-x64-e-arm64)). Para rodar como serviço do systemd, acrescente o pacote
`Microsoft.Extensions.Hosting.Systemd` e `builder.Services.AddSystemd()`.

Depois de mudar `src/`, gere os pacotes com outra versão ou apague-os do cache do NuGet, como explicado no
[README do ConsoleSample](../Brymen.Dmm.ConsoleSample/README.md#depois-de-mudar-a-biblioteca).
