# Web.Common (`TemplateName.Web.Common`)

## Purpose

The HTTP edge shared by the host and the module endpoints: `Result` to RFC 9457 ProblemDetails mapping with `code`, `traceId` and localized `detail` (ADR 0002, ADR 0009), the global exception handler, trace-id and security headers, CORS, rate limiting and forwarded headers, request localization, the current user, permission-based authorization, and observability (Serilog, OpenTelemetry). It depends on SharedKernel, Application.Common, ASP.NET Core, Serilog and OpenTelemetry.

Code: `src/BuildingBlocks/TemplateName.Web.Common/`.

## Public types and extension methods

| Extension / type | Does |
|---|---|
| `services.AddWebCommon()` | ProblemDetails with `CustomizeProblemDetails` (below), `GlobalExceptionHandler`, `ICurrentUser` (`HttpContextCurrentUser`: `UserId` from the `sub` claim, else the name identifier, and `SessionId` from the `sid` claim, each as a `Guid` or null), `CommonErrorMessages`, and `RouteHandlerOptions.ThrowOnBadRequest`, so malformed bodies reach the exception handler as 400 `request.malformed`. |
| `services.AddPermissionAuthorization()` | Authorization with `PermissionAuthorizationHandler` (scoped) and a **fallback policy** that requires an authenticated user, so every endpoint without its own authorization metadata is protected and an anonymous endpoint must say `.AllowAnonymous()`. Needs `AddWebCommon` (`ICurrentUser`) and an `IPermissionChecker` (the Auth module registers it). Call it together with explicit `UseAuthentication` (pipeline slot 1a, before localization) and `UseAuthorization` (slot 10, after `UseRouting`, `UseCors` and `UseRateLimiter`): when authorization services exist and the pipeline does not call `UseAuthorization`, `WebApplication` adds it ahead of `UseRouting` by itself, where no endpoint is known yet and the fallback policy answers 401 to every request. The fallback policy also covers a request that matched no endpoint, so an anonymous caller gets 401 for an unknown route too (a signed-in one gets 404). The 401 is a body-less challenge (`WWW-Authenticate: Bearer`) that `UseStatusCodePages` turns into a localized `http.401` problem. |
| `endpoint.RequirePermission(code)` | Requires an authenticated user who holds the permission `code` (`module.resource.action`, compared exactly): the endpoint gets its own policy, `RequireAuthenticatedUser()` plus one `PermissionRequirement`; there is no dynamic policy provider (decision D6, [ADR 0016](../adr/0016-server-side-permissions-with-per-user-cache.md)). The handler succeeds only when `ICurrentUser` is authenticated, has a user id and `IPermissionChecker.HasPermissionAsync` says yes; otherwise it fails closed (401 for an anonymous caller, 403 for a signed-in one), and a checker exception propagates instead of granting. Works on any `IEndpointConventionBuilder` (an endpoint or a group). |
| `result.ToProblem()` | Maps a failed `Result` to a problem response: `ErrorType` → 400/404/409/401/403/500, `code` = `Error.Code`, `params` = `Error.Parameters` when there are any; a `ValidationError` becomes a validation problem with `errors`. Throws on a successful result. |
| `app.UseTraceIdHeader()` | `X-Trace-Id` on every response, including errors (written in `Response.OnStarting`). |
| `app.UseSecurityHeaders()` | The baseline security headers on every response, including errors ([list](../services/api.md#pipeline)). |
| `services.AddHttpSecurity(configuration)` | CORS default policy from `Cors:AllowedOrigins` (credentials, any header and method; `*` rejected; exposes `X-Trace-Id`, `Location`, `Retry-After`, `Idempotency-Replayed`), the global fixed-window rate limiter from `RateLimiting` (partition `user:{sub}` or `ip:{address}`; 429 `rate_limit.exceeded` with `Retry-After`), the named policy `RateLimitPolicies.AuthStrict` (below), and forwarded headers from `ForwardedHeaders` (`X-Forwarded-For`, `X-Forwarded-Proto`; trusted proxies only). All validated on start. |
| `RateLimitPolicies.AuthStrict` (`"auth-strict"`) | The rate-limit policy for anonymous credential endpoints (login, register, forgot password, resend confirmation): a fixed window partitioned by `ip:{address}` only (the address the forwarded-headers middleware leaves, so a spoofed `X-Forwarded-For` cannot pick a partition), `RateLimiting:AuthStrictPermitLimit` requests (default 10) per `RateLimiting:AuthStrictWindow` (default 1 minute). An endpoint opts in with `.RequireRateLimiting(RateLimitPolicies.AuthStrict)`; the global limiter still applies. A rejection is the same 429 `rate_limit.exceeded` problem with `Retry-After`. |
| `services.AddApiLocalization(configuration)` | Binds and validates `Localization`, configures request localization, and sets FluentValidation's process-wide language manager to `ValidationMessageTranslations` (adds `ms` and `zh-Hans`). |
| `app.UseApiLocalization()` | Sets the default thread cultures to `DefaultCulture` (for background work) and adds the request localization middleware. Register it after `UseAuthentication` (so the `locale` claim is visible) and before `UseExceptionHandler`. |
| `ApiLocalizationExtensions.CreateRequestLocalizationOptions(options)` | The `RequestLocalizationOptions` the API uses: the providers `UserLocaleClaimCultureProvider` then `Accept-Language` (no query string, no cookie), UI cultures from the settings, formatting culture fixed to the default, parent-culture fallback, `Content-Language` echoed. |
| `UserLocaleClaimCultureProvider` | Reads the `locale` claim (`LocaleClaimType`) of the authenticated request principal and offers it as the UI culture only; the formatting culture stays the default. It runs before `Accept-Language`, so a signed-in user's saved language wins over the browser's (decision D7). A missing claim, an anonymous principal, or a value that is unsupported even after parent fallback (`fr-FR`, a malformed name) leaves the choice to `Accept-Language`, and from there to the default culture. |
| `ApiLocalizationOptions` | `DefaultCulture`, `SupportedUICultures` (section `Localization`). |
| `builder.AddObservability()` | Serilog (levels from `Serilog`, `Application`/`Environment` properties, sensitive-data masking), the request-activity listener, and, when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, OTLP export of logs, traces and metrics. |
| `app.UseRequestLogging()` | Serilog request logging: one event per request, with `UserId`; healthy `/health` requests at `Verbose`, exceptions and 5xx at `Error`. |
| `SensitiveDataDestructuringPolicy` | Masks with `***` the values of properties whose name contains `password`, `token`, `secret`, `otp` or `apikey` (case-insensitive) when an object is logged with `{@Name}`, including nested objects. Dictionary keys and plain strings are not inspected: never log secrets as message arguments. |

## Error responses

Every problem response, whoever writes it, goes through `ProblemDetailsSetup.Customize`, the one place messages are localized:

- adds `traceId` (the W3C trace id, the same as `X-Trace-Id`);
- adds `code` `http.{status}` when the writer set none (unknown route, wrong method, …);
- replaces `detail` with the message for `code` from the registered `*ErrorMessages` sets in the request's UI culture, filling `{placeholders}` from `params`; without a message the English detail (or the title) stays.

`GlobalExceptionHandler` answers `BadHttpRequestException` with its status (400) and `request.malformed`, and anything else with 500 `server.unexpected_error`; only in Development does it add the exception text in `exceptionDetails`. `detail` is always the generic, localized message.

Error codes (messages in `Resources/CommonErrorMessages.resx`, `.ms.resx`, `.zh-Hans.resx`):

| Code | HTTP | Message (en) |
|---|---|---|
| `validation.failed` | 400 | One or more validation errors occurred. |
| `request.malformed` | 400 | The request is malformed. |
| `server.unexpected_error` | 500 | An unexpected error occurred. |
| `rate_limit.exceeded` | 429 | Too many requests. Try again later. |
| `http.400`, `http.401`, `http.403`, `http.404`, `http.405`, `http.415` | as named | Generic messages for body-less status responses. |
| `pagination.invalid_cursor`, `pagination.cursor_mismatch`, `pagination.invalid_sort` | 400 | Declared in Application.Common's `PaginationErrors` ([application-common](application-common.md#pagination)). |

## Observability

- Console: readable template in Development, compact JSON elsewhere.
- OTLP (when `OTEL_EXPORTER_OTLP_ENDPOINT` is set): Serilog's OpenTelemetry sink for logs; traces from ASP.NET Core (without `/health`), `HttpClient`, SqlClient and EF Core; metrics from ASP.NET Core, `HttpClient` and the runtime. `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` switches from gRPC to HTTP. The endpoint is read once at registration, so tests cannot switch export on or off with configuration overrides.
- `RequestActivityBackgroundService` keeps an `ActivityListener` on the `Microsoft.AspNetCore` source, so every request has a real W3C trace id even when OpenTelemetry is off.
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` is the prerelease `1.19.1-beta.1`, the only prerelease dependency (no stable version exists; [ADR 0013](../adr/0013-prerelease-ef-core-opentelemetry-instrumentation.md)).
- With export on, traces record request URLs, so query-string secrets would reach the trace store.

## Configuration

`Cors`, `RateLimiting`, `ForwardedHeaders`, `Localization`, `Serilog` and `OTEL_EXPORTER_OTLP_*`, with defaults, validation and known limitations, are listed in [`docs/services/api.md`](../services/api.md#configuration).

## How to use from a module

```csharp
// Endpoints/LeaveRequestEndpoints.cs
group.MapGet("/{id:guid}", GetByIdAsync)
    .Produces<LeaveRequestResponse>()
    .ProducesProblem(StatusCodes.Status404NotFound);

private static async Task<IResult> GetByIdAsync(
    Guid id,
    IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse> handler,
    CancellationToken cancellationToken)
{
    var result = await handler.HandleAsync(new GetLeaveRequestByIdQuery(id), cancellationToken);

    return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
}
```

A protected endpoint names its permission; an anonymous one says so explicitly:

```csharp
group.MapPost("/{id:guid}/approve", ApproveAsync).RequirePermission(LeavePermissions.Approve);
group.MapPost("/login", LoginAsync).AllowAnonymous();
```

Modules never write ProblemDetails, headers or translations themselves: they return an `Error` with a code (and parameters), and add the code's messages to their own `*ErrorMessages.resx` files.

## Tests

Unit: `tests/TemplateName.UnitTests/Web/` (result mapping, exception handler, error pipeline, middleware, current user including `SessionId`, permission handler and `RequirePermission`, the fallback policy, the `auth-strict` policy through the real rate limiter (`RateLimitingPolicyTests`), the locale claim provider through the real localization middleware (`UserLocaleClaimCultureProviderTests`), observability, masking) and `Localization/`. Integration: `Host/HostTests`, `Host/HttpSecurityTests`, `Host/TraceIdTests`, `Localization/LocalizationTests`, and `Auth/PipelineTests` (anonymous endpoints, the fallback policy, forged tokens, the saved locale) against the test-only `GET /test/protected` endpoint the factory adds, and `Auth/RateLimitOrderTests` (401 and 403 responses are rate limited) which also uses the factory's `GET /test/permission` endpoint, protected by `RequirePermission` for a permission nobody holds.
