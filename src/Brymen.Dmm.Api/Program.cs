using System.Text.Json.Serialization;
using Brymen.Dmm.Api;
using Brymen.Dmm.Api.Configuration;
using Brymen.Dmm.Api.Contracts;
using Brymen.Dmm.Api.Endpoints;
using Brymen.Dmm.Api.Errors;
using Brymen.Dmm.Api.Hubs;
using Brymen.Dmm.Api.Logger;
using Brymen.Dmm.Api.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Sob systemd: Type=notify, logs no journal e encerramento limpo; fora dele não faz nada.
builder.Host.UseSystemd();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
    // Números são sempre números no JSON (e na OpenAPI), sem a leitura de strings dos padrões Web.
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.AddMissingCode);
// Corpo ou parâmetro malformado é 400 em qualquer ambiente: em Development o padrão lança BadHttpRequestException,
// que o UseExceptionHandler transformaria em 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddOpenApi(OpenApiSetup.Configure);
builder.Services.AddBrymenDmm(builder.Configuration);

var app = builder.Build();

// Falha cedo se o DataDirectory não puder ser criado.
var dataDirectory = app.Services.GetRequiredService<DataDirectory>();
app.Logger.LogInformation("DataDirectory: {Path}", dataDirectory.Root);
// As importações gravadas em imports/ são lidas na partida, e não no primeiro request a /api/v1/logger.
app.Services.GetRequiredService<LoggerImportService>();

// ProblemDetails genérico, sem exceção nem stack trace; um BadHttpRequestException (ex.: corpo grande demais) mantém o seu 4xx.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
// POST/PUT/DELETE e o WebSocket do hub só da própria origem, de Cors:AllowedOrigins ou de clientes fora do browser.
var crossSiteGuard = app.Services.GetRequiredService<CrossSiteRequestGuard>();
app.Use(crossSiteGuard.InvokeAsync);
// O "/" vira "/index.html" antes do roteamento, que por isso é explícito e vem depois: o endpoint do MapStaticAssets é o
// de "/index.html".
app.UseDefaultFiles();
app.UseRouting();
// CORS só com Cors:AllowedOrigins configurado (clientes browser de outra origem, inclusive o SignalR).
if (app.Services.GetRequiredService<IOptions<CorsSettings>>().Value.AllowedOrigins.Length > 0)
    app.UseCors();

// Painel do wwwroot pelo manifesto Brymen.Dmm.Api.staticwebassets.endpoints.json do build e do publish: serve os .gz e
// .br gerados pelo SDK conforme o Accept-Encoding, com ETag (o UseStaticFiles não os negociava).
app.MapStaticAssets();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapMeterEndpoints();
app.MapAcquisitionEndpoints();
app.MapComparatorEndpoints();
app.MapLoggerEndpoints();
app.MapHub<MeterHub>(MeterHub.Route);

app.Run();

/// <summary>Ponto de entrada; parcial e público para o <c>WebApplicationFactory</c> dos testes de integração.</summary>
public partial class Program;
