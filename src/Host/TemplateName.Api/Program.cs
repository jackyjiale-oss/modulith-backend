using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Sample;
using TemplateName.Web.Common;
using TemplateName.Web.Common.Observability;
using TemplateName.Web.Common.Security;

var builder = WebApplication.CreateBuilder(args);

// Service order matters; later tasks insert at the marked slots.
builder.AddObservability();
// Before AddWebCommon, so the concurrency exception handler runs before the global one.
builder.Services.AddInfrastructureCommon(builder.Configuration);
builder.Services.AddWebCommon();
// AddApiLocalization (Task 15)
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks();
builder.Services.AddHttpSecurity(builder.Configuration);
// Module registrations
builder.Services.AddSampleModule();

// Kestrel binds only its endpoints from the "Kestrel" section; bind Limits (e.g. MaxRequestBodySize) lazily so test overrides apply.
builder.Services.AddOptions<KestrelServerOptions>().Configure<IConfiguration>((options, configuration) => configuration.GetSection("Kestrel").Bind(options));

// Decorators wrap every handler registered above them, so they stay last.
builder.Services.AddApplicationDecorators();

var app = builder.Build();

// Migrate on start only when Database:ApplyMigrationsOnStartup is set (Development); production uses the API's migrate mode (ADR 0006).
if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup)
{
    await app.Services.MigrateModuleDatabasesAsync(app.Lifetime.ApplicationStopping);
}

// Pipeline order matters; later tasks insert at the marked slots.
// 1. UseForwardedHeaders
app.UseForwardedHeaders();
// 1a. UseApiLocalization (Task 15) - must precede UseExceptionHandler
// 2. UseExceptionHandler
app.UseExceptionHandler();
// 3. UseStatusCodePages
app.UseStatusCodePages();
// 4. UseTraceIdHeader
app.UseTraceIdHeader();
// 5. UseSecurityHeaders
app.UseSecurityHeaders();
// 6. HSTS + HTTPS redirection, non-Development
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// 7. UseRequestLogging
app.UseRequestLogging();
// 8. UseRouting
app.UseRouting();
// 9. UseCors
app.UseCors();
// UseAuthentication/UseAuthorization (Auth plan) go here, between slots 9 and 10, so the limiter's user:{sub} partition sees the signed-in user.
// 10. UseRateLimiter
app.UseRateLimiter();
// 11. UseIdempotency - after UseRouting, because it acts only on endpoints marked WithIdempotency()
app.UseIdempotency();
// 12. endpoints (health endpoints are exempt from rate limiting)
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") }).DisableRateLimiting();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Module endpoints map on `api`.
var api = app.MapGroup("/api/v1");
api.MapSampleEndpoints();

app.Run();

/// <summary>Makes the entry point visible to <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests.</summary>
public partial class Program;
