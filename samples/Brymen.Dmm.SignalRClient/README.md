# Cliente SignalR de exemplo

Console .NET 10 que se conecta ao hub `/hubs/meter` do Brymen DMM REST e imprime cada leitura nova
do multímetro.

```bash
dotnet run --project samples/Brymen.Dmm.SignalRClient -- http://raspberrypi.local:5080
```

Sem argumento, usa `http://localhost:5080`. O endereço do próprio hub (`http://localhost:5080/hubs/meter`)
também é aceito. O cliente:

- reconecta sozinho, sem desistir (0, 2, 5 e depois 10 s entre as tentativas), e tenta a primeira
  conexão a cada 5 s enquanto o serviço não responde;
- chama `GetStatus` e `GetLatest` ao conectar e a cada reconexão;
- imprime uma linha por evento `reading` (hora local, número da leitura, display principal e secundário
  com unidade e modo, anunciadores e o resultado do comparador, quando ligado) e cada evento `status`, com o
  medidor detectado (campo `model`: BM52x ou BM82x, e se tem datalogger);
- encerra com Ctrl+C.

```text
Conectando a http://localhost:5080/hubs/meter (Ctrl+C encerra)...
18:18:14 conectado (Pz1-VjshrPMkHzPSCmwTvA).
18:18:14 [GetStatus] connected, link ativo, cabo aberto (sim://bu86x/0), transporte simulated, medidor Minipa ET-2615A (Brymen BM525s, BM52x), 45 leituras
18:18:14.314 [GetLatest] #45         0.090 V AC | 0.000 Hz        AUTO
18:18:14.521 #46         0.090 V AC | 0.000 Hz        AUTO
18:18:16 [status] unlinked, link desativado, cabo fechado (sim://bu86x/0), transporte simulated, medidor Minipa ET-2615A (Brymen BM525s, BM52x), 54 leituras
```

Com o ET-2517A (ou `Meter:SimulatedModel=Et2517a` no serviço), o medidor aparece como
`medidor Minipa ET-2517A (Brymen BM827s, BM82x) sem datalogger`. Antes da 1ª leitura, e com um serviço 0.1.0, que não manda o campo
`model`, a parte do medidor não aparece.

## Contrato do hub `/hubs/meter`

Protocolo JSON (camelCase, enums como texto). O hub não é tipado, porque hubs `Hub<T>` não são
compatíveis com trimming/Native AOT.

| Direção | Nome | Payload |
|---|---|---|
| servidor → cliente | `reading` | Cada leitura nova decodificada (cada quadro de tempo real válido: `52 66` no BM52x, `82 66` no BM82x), com o mesmo JSON de `GET /api/v1/readings/latest` |
| servidor → cliente | `status` | Mudança do link, do cabo ou da família detectada (conectado, desconectado, importação, erro com `code`, `model`), com o mesmo JSON de `GET /api/v1/meter` |
| servidor → cliente | `acquisitionSample` | Cada amostra gravada pela aquisição, com o mesmo JSON de um item de `GET /api/v1/acquisition/samples` |
| cliente → servidor | `GetLatest()` | A leitura atual, ou `null` quando `GET /api/v1/readings/latest` responderia erro (link desativado, importação, cabo em falha) |
| cliente → servidor | `GetStatus()` | O estado, como `GET /api/v1/meter` |

- Os eventos `reading` saem da mesma fonte do SSE (`/api/v1/readings/stream`), sem consulta extra ao
  medidor: cada leitura chega uma vez, na ordem do campo `sequence`.
- O polling nunca espera pelo SignalR. Se os clientes não acompanharem o ritmo, as leituras mais antigas
  da fila são descartadas e a contagem vai para o log do serviço.
- Ao conectar não há reenvio da última leitura: use `GetLatest`.

## Configuração do serviço

| Chave | Padrão | Descrição |
|---|---|---|
| `SignalR:EnableDetailedErrors` | `false` | Manda ao cliente a mensagem das exceções dos métodos do hub (só para depuração) |
| `SignalR:KeepAliveInterval` | `00:00:15` | Pings do servidor; o `ServerTimeout` dos clientes (30 s por padrão) deve ser pelo menos o dobro |
| `Cors:AllowedOrigins` | vazio | Origens de clientes browser em outro endereço, como `http://painel.local:8080` (variável de ambiente `Cors__AllowedOrigins__0`). A política aceita credenciais, exigidas pelo SignalR no browser, e por isso não aceita `*` |
| `Sse:HeartbeatInterval` | `00:00:15` | Evento `heartbeat`, sem dados, no SSE, para proxies não fecharem a conexão |

## Browser

Com `Cors:AllowedOrigins` configurado (ou servindo a página pelo próprio serviço), o cliente JavaScript
`@microsoft/signalr` funciona da mesma forma:

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://raspberrypi.local:5080/hubs/meter")
  .withAutomaticReconnect()
  .build();
connection.on("reading", r => console.log(r.timestamp, r.main?.text, r.main?.unitSymbol, r.flags));
connection.on("status", s => console.log(s.state, s.lastError?.code));
await connection.start();
console.log(await connection.invoke("GetLatest"));
```
