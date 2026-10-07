using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Scalar.AspNetCore;
using TemplateName.Application.Common.Messaging;
using TemplateName.Web.Common;

var builder = WebApplication.CreateBuilder(args);

// Service order matters; later tasks insert at the marked slots.
// AddObservability (Task 6)
// AddInfrastructureCommon (Task 8) - must come BEFORE AddWebCommon so its exception handler runs first
builder.Services.AddWebCommon();
// AddApiLocalization (Task 15)
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks();
// AddHttpSecurity (Task 7)
// Module registrations (Task 11)

// Kestrel binds only its endpoints from the "Kestrel" section; bind Limits (e.g. MaxRequestBodySize) lazily so test overrides apply.
builder.Services.AddOptions<KestrelServerOptions>().Configure<IConfiguration>((options, configuration) => configuration.GetSection("Kestrel").Bind(options));

// Decorators wrap every handler registered above them, so they stay last.
builder.Services.AddApplicationDecorators();

var app = builder.Build();

// Pipeline order matters; later tasks insert at the marked slots.
// 1. UseForwardedHeaders (Task 7)
// 1a. UseApiLocalization (Task 15) - must precede UseExceptionHandler
// 2. UseExceptionHandler
app.UseExceptionHandler();
// 3. UseStatusCodePages
app.UseStatusCodePages();
// 4. UseTraceIdHeader
app.UseTraceIdHeader();
// 5. UseSecurityHeaders
app.UseSecurityHeaders();
// 6. HSTS + HTTPS redirection, non-Development (Task 7)
// 7. UseRequestLogging (Task 6)
// 8. UseRouting
app.UseRouting();
// 9. UseCors (Task 7)
// 10. UseRateLimiter (Task 7)
// 11. UseIdempotency (Task 12)
// 12. endpoints (health: .DisableRateLimiting() added in Task 7)
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Module endpoints map on `api` (Task 11).
var api = app.MapGroup("/api/v1");

app.Run();

/// <summary>Makes the entry point visible to <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests.</summary>
public partial class Program;
