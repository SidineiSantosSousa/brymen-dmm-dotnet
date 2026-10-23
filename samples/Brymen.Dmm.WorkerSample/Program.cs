using Brymen.Dmm.Extensions.Hosting;
using Brymen.Dmm.Hid;
using Brymen.Dmm.WorkerSample;

// Worker service com o pacote Brymen.Dmm.Extensions.Hosting: o AddBrymenMeter abre o multímetro na partida, com
// reconexão automática, e mantém a leitura contínua; o ReadingLogger registra cada leitura no ILogger.
//
//   dotnet run -- --simulated                         medidor simulado (capturas reais do BM525s), sem hardware
//   dotnet run -- --Brymen:DevicePath=/dev/hidraw0    opções pela linha de comando (ou appsettings.json, Brymen__*)

bool simulated = args.Contains("--simulated");
var builder = Host.CreateApplicationBuilder([.. args.Where(a => a != "--simulated")]);

// A ordem do registro não importa: o serviço do AddBrymenMeter abre o medidor antes do StartAsync de todos os
// serviços e só inicia a leitura depois deles, então o ReadingLogger, inscrito no próprio StartAsync, recebe a 1ª leitura.
builder.Services.AddBrymenMeter(builder.Configuration.GetSection("Brymen"));
builder.Services.AddHostedService<ReadingLogger>();
if (simulated)
{
    // DeviceProvider não vem da configuração: as duas sobrecargas do AddBrymenMeter se somam. O provider sem argumentos
    // é o cabo do BrymenMeter.OpenSimulatedAsync: as capturas reais do BM525s, com a latência típica.
    builder.Services.AddBrymenMeter(options => options.DeviceProvider = new SimulatedHidDeviceProvider());
}

await builder.Build().RunAsync();
