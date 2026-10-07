# Web.Common (`TemplateName.Web.Common`)

## Purpose

The HTTP edge shared by the host and the module endpoints: `Result` to RFC 9457 ProblemDetails mapping with `code`, `traceId` and localized `detail` (ADR 0002, ADR 0009), the global exception handler, trace-id and security headers, CORS, rate limiting and forwarded headers, request localization, the current user, and observability (Serilog, OpenTelemetry). It depends on SharedKernel, Application.Common, ASP.NET Core, Serilog and OpenTelemetry.

Code: `src/BuildingBlocks/TemplateName.Web.Common/`.

## Public types and extension methods

| Extension / type | Does |
|---|---|
| `services.AddWebCommon()` | ProblemDetails with `CustomizeProblemDetails` (below), `GlobalExceptionHandler`, `ICurrentUser` (`HttpContextCurrentUser`: the `sub` claim, else the name identifier, as a `Guid`), `CommonErrorMessages`, and `RouteHandlerOptions.ThrowOnBadRequest`, so malformed bodies reach the exception handler as 400 `request.malformed`. |
| `result.ToProblem()` | Maps a failed `Result` to a problem response: `ErrorType` → 400/404/409/401/403/500, `code` = `Error.Code`, `params` = `Error.Parameters` when there are any; a `ValidationError` becomes a validation problem with `errors`. Throws on a successful result. |
| `app.UseTraceIdHeader()` | `X-Trace-Id` on every response, including errors (written in `Response.OnStarting`). |
| `app.UseSecurityHeaders()` | The baseline security headers on every response, including errors ([list](../services/api.md#pipeline)). |
| `services.AddHttpSecurity(configuration)` | CORS default policy from `Cors:AllowedOrigins` (credentials, any header and method; `*` rejected), the global fixed-window rate limiter from `RateLimiting` (partition `user:{sub}` or `ip:{address}`; 429 `rate_limit.exceeded` with `Retry-After`), and forwarded headers from `ForwardedHeaders` (`X-Forwarded-For`, `X-Forwarded-Proto`; trusted proxies only). All validated on start. |
| `services.AddApiLocalization(configuration)` | Binds and validates `Localization`, configures request localization, and sets FluentValidation's process-wide language manager to `ValidationMessageTranslations` (adds `ms` and `zh-Hans`). |
| `app.UseApiLocalization()` | Sets the default thread cultures to `DefaultCulture` (for background work) and adds the request localization middleware. Register it before `UseExceptionHandler`. |
| `ApiLocalizationExtensions.CreateRequestLocalizationOptions(options)` | The `RequestLocalizationOptions` the API uses: `Accept-Language` only, UI cultures from the settings, formatting culture fixed to the default, parent-culture fallback, `Content-Language` echoed. |
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
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` is the prerelease `1.19.1-beta.1`, the only prerelease dependency (no stable version exists; the plan allows it for this instrumentation only).
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

Modules never write ProblemDetails, headers or translations themselves: they return an `Error` with a code (and parameters), and add the code's messages to their own `*ErrorMessages.resx` files.

## Tests

Unit: `tests/TemplateName.UnitTests/Web/` (result mapping, exception handler, error pipeline, middleware, current user, observability, masking) and `Localization/`. Integration: `Host/HostTests`, `Host/HttpSecurityTests`, `Host/TraceIdTests`, `Localization/LocalizationTests`.
