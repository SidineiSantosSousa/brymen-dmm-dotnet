using Brymen.Dmm.Extensions.Hosting;
using Brymen.Dmm.Hid;
using Brymen.Dmm.WorkerSample;

// Worker service com o pacote Brymen.Dmm.Extensions.Hosting: o AddBrymenMeter abre o multímetro na partida, com
// reconexão automática, e mantém a leitura contínua; o ReadingLogger registra cada leitura no ILogger.
//
//   dotnet run -- --simulated                         BM525s simulado (capturas reais), sem hardware
//   dotnet run -- --simulated et2517a                 ET-2517A simulado (capturas reais e pedidos descartados)
//   dotnet run -- --Brymen:DevicePath=/dev/hidraw0    opções pela linha de comando (ou appsettings.json, Brymen__*)
//   dotnet run -- --Brymen:Family=Bm82x               família fixa, sem a detecção (Auto, Bm52x ou Bm82x)

int simulatedAt = Array.IndexOf(args, "--simulated");
SimulatedBm52xMeter? simulated = null;
string[] hostArgs = args;
if (simulatedAt >= 0)
{
    // O modelo é opcional: o argumento seguinte, se não for outra opção.
    bool hasModel = simulatedAt + 1 < args.Length && !args[simulatedAt + 1].StartsWith("--", StringComparison.Ordinal);
    string model = hasModel ? args[simulatedAt + 1].ToLowerInvariant() : "bm525s";
    simulated = model switch
    {
        "bm525s" => SimulatedBm52xMeter.FromCaptures(),
        "et2517a" => SimulatedBm52xMeter.FromEt2517aCaptures(),
        _ => null,
    };
    if (simulated is null)
    {
        Console.Error.WriteLine($"--simulated aceita bm525s ou et2517a, não \"{args[simulatedAt + 1]}\".");
        return 2;
    }
    hostArgs = [.. args.Where((_, i) => i != simulatedAt && !(hasModel && i == simulatedAt + 1))];
}

var builder = Host.CreateApplicationBuilder(hostArgs);

// A ordem do registro não importa: o serviço do AddBrymenMeter abre o medidor antes do StartAsync de todos os
// serviços e só inicia a leitura depois deles, então o ReadingLogger, inscrito no próprio StartAsync, recebe a 1ª leitura.
builder.Services.AddBrymenMeter(builder.Configuration.GetSection("Brymen"));
builder.Services.AddHostedService<ReadingLogger>();
if (simulated is not null)
{
    // DeviceProvider não vem da configuração: as duas sobrecargas do AddBrymenMeter se somam. O cabo simulado tem a
    // latência típica, como o do BrymenMeter.OpenSimulatedAsync.
    builder.Services.AddBrymenMeter(options => options.DeviceProvider =
        new SimulatedHidDeviceProvider([SimulatedHidDevice.CreateTypicalBm52x(simulated)]));
}

await builder.Build().RunAsync();
return 0;
