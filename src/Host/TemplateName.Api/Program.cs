using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TemplateName.Application.Common.Messaging;
using TemplateName.Web.Common;

var builder = WebApplication.CreateBuilder(args);

// Service order matters: later tasks insert their registrations around these.
builder.Services.AddWebCommon();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks();

// Module registrations go here; decorators wrap every handler registered above them, so they stay last.
builder.Services.AddApplicationDecorators();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseTraceIdHeader();
app.UseSecurityHeaders();
app.UseRouting();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

var api = app.MapGroup("/api/v1");

app.Run();

/// <summary>Makes the entry point visible to <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests.</summary>
public partial class Program;
