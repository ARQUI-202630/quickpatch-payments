using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using QuickPatch.Payments.Api.Endpoints;
using QuickPatch.Payments.Api.Http;
using QuickPatch.Payments.Api.Security;
using QuickPatch.Payments.Application;
using QuickPatch.Payments.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Logs en JSON con scopes (correlationId), para Loki (SCRUM-323) y la auditoría de 403 (RNF-04). Nunca registran el token
// de la pasarela ni datos de tarjeta (RN-P1, AC6-E1).
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // El contrato declara additionalProperties: false; un campo desconocido es un error de validación (400).
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddHealthChecks();
builder.Services.AddQuickPatchAuthentication(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Probes de k3s (Documento de Infraestructura, sección 5.8).
// live: el proceso responde; ready: además, PostgreSQL responde.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(QuickPatch.Payments.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapPaymentEndpoints();
app.MapInvoiceEndpoints();

app.Run();

/// <summary>Punto de entrada; público para las pruebas con WebApplicationFactory.</summary>
public partial class Program;