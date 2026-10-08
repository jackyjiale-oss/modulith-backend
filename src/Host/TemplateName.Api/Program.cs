using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Sample;
using TemplateName.Web.Common;
using TemplateName.Web.Common.Localization;
using TemplateName.Web.Common.Observability;
using TemplateName.Web.Common.Security;

var builder = WebApplication.CreateBuilder(args);

// Service order matters; later tasks insert at the marked slots.
builder.AddObservability();
// Before AddWebCommon, so the concurrency exception handler runs before the global one.
builder.Services.AddInfrastructureCommon(builder.Configuration);
builder.Services.AddWebCommon();
builder.Services.AddApiLocalization(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// The Bearer scheme, required by every operation that carries authorization data (RequirePermission, RequireAuthorization).
builder.Services.AddOpenApi("v1", options => options.AddBearerSecurity());
builder.Services.AddHealthChecks();
builder.Services.AddHttpSecurity(builder.Configuration);
// The fallback policy requires an authenticated user everywhere an endpoint does not say otherwise. It needs the explicit
// UseAuthentication and UseAuthorization below: without them WebApplication would add UseAuthorization ahead of UseRouting, where no
// endpoint is known and every request, health checks included, gets 401.
builder.Services.AddPermissionAuthorization();
// Module registrations
builder.Services.AddAuthModule(builder.Configuration);
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

// Seed the system roles, the declared permissions and, when configured, the first administrator (Auth:Seed:RunOnStartup, default on).
if (app.Configuration.GetValue("Auth:Seed:RunOnStartup", defaultValue: true))
{
    await app.Services.SeedAuthModuleAsync(app.Lifetime.ApplicationStopping);
}

// Pipeline order matters; later tasks insert at the marked slots.
// 1. UseForwardedHeaders
app.UseForwardedHeaders();
// 1a. UseAuthentication - before localization, so the signed-in user's saved locale claim wins over Accept-Language (decision D7).
//     It only reads the bearer token; a missing or invalid token leaves the request anonymous, and 10 decides whether that is allowed.
app.UseAuthentication();
// 1b. UseApiLocalization - must precede UseExceptionHandler, so error responses come back in the caller's language (ADR 0009)
app.UseApiLocalization();
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
// 9. UseCors - before authorization, so a CORS preflight (which carries no token) is answered without a 401
app.UseCors();
// 9a. UseRateLimiter - after UseRouting and UseAuthentication, so the endpoint's RequireRateLimiting metadata and the signed-in user
//     (the user:{sub} partition) are known; before UseAuthorization, so the 401 and 403 responses authorization short-circuits with
//     (anonymous callers, a missing permission, unknown routes) are counted too.
app.UseRateLimiter();
// 10. UseAuthorization - after UseRouting, so it sees the endpoint's metadata; the fallback policy protects every endpoint without
//     .AllowAnonymous() or a policy of its own, and an unknown route (no endpoint) too. Anonymous callers get 401 here, a signed-in
//     caller without the permission 403.
app.UseAuthorization();
// 11. UseIdempotency - after UseRouting, because it acts only on endpoints marked WithIdempotency()
app.UseIdempotency();
// 12. endpoints (health endpoints are anonymous and exempt from rate limiting)
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).DisableRateLimiting().AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") })
    .DisableRateLimiting()
    .AllowAnonymous();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

// Module endpoints map on `api`; well-known documents (the JWKS, anonymous) map at the root. Everything else is protected by the
// fallback policy unless it says .AllowAnonymous().
var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
app.MapAuthWellKnownEndpoints();
api.MapSampleEndpoints();

app.Run();

/// <summary>Makes the entry point visible to <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests.</summary>
public partial class Program;
