# API host (`TemplateName.Api`)

The one deployable process: `src/Host/TemplateName.Api`. `Program.cs` registers the building blocks and modules, builds the middleware pipeline, maps health checks, OpenAPI and the module endpoints under `/api/v1`, (in Development) migrates the databases on start, and seeds the Auth module's roles and permissions. This page lists what the host does and every setting it reads. Architecture: [`docs/architecture/overview.md`](../architecture/overview.md).

## Service registration

Order matters; `Program.cs` marks it with comments.

| # | Call | Registers |
|---|---|---|
| 1 | `builder.AddObservability()` | Serilog, the request-activity listener, OpenTelemetry when `OTEL_EXPORTER_OTLP_ENDPOINT` is set ([web-common](../building-blocks/web-common.md#observability)) |
| 2 | `AddInfrastructureCommon(configuration)` | `TimeProvider`, `ConnectionStrings`/`Database`/`Outbox`/`Idempotency` options, the Dapper connection factory, EF interceptors, the concurrency exception handler, the `platform` context ([infrastructure-common](../building-blocks/infrastructure-common.md)). Before `AddWebCommon`, so the concurrency handler runs before the global one. |
| 3 | `AddWebCommon()` | ProblemDetails customization, the global exception handler, `ICurrentUser`, `ThrowOnBadRequest` ([web-common](../building-blocks/web-common.md)) |
| 4 | `AddApiLocalization(configuration)` | The `Localization` options, request localization, FluentValidation translations |
| 5 | `ConfigureHttpJsonOptions` | `JsonStringEnumConverter`: enums are strings in JSON (`"Pending"`) |
| 6 | `AddOpenApi("v1")` | The OpenAPI document `v1` |
| 7 | `AddHealthChecks()` | Health checks; each module context adds its own `ready` check |
| 8 | `AddHttpSecurity(configuration)` | CORS, the global rate limiter, forwarded headers |
| — | *(Auth plan)* `AddPermissionAuthorization()` | The permission authorization handler behind `RequirePermission(code)` and the fallback policy: every endpoint requires an authenticated user unless it says `.AllowAnonymous()` ([web-common](../building-blocks/web-common.md)). Not called yet: it lands with `UseAuthentication`/`UseAuthorization` in the pipeline, because once authorization services are registered `WebApplication` adds `UseAuthorization` ahead of `UseRouting` by itself (unless the pipeline calls it), where the fallback policy would answer 401 to every request. |
| 9 | `AddAuthModule(configuration)`, `AddSampleModule()` (one line per module) | The module's context, outbox, handlers, validators and error messages; the Auth module also the JWT bearer scheme, the permission checker and its cache, and the seeder ([Auth module](../modules/auth.md)) |
| 10 | `AddOptions<KestrelServerOptions>()…Bind("Kestrel")` | Binds the whole `Kestrel` section lazily, so `Kestrel:Limits:*` apply and test overrides work |
| 11 | `AddApplicationDecorators()` | Validation and logging decorators around every handler registered above; always last |

## Start-up

Before the pipeline serves requests, the host runs two steps, in this order:

1. **Migrations**, when `Database:ApplyMigrationsOnStartup` is on (Development; see [Migrations](#migrations)).
2. **Auth seeding**, when `Auth:Seed:RunOnStartup` is on (the default in every environment): `SeedAuthModuleAsync` creates the system roles, syncs the permissions every module declares, keeps `SuperAdmin` on every permission and, when `Auth:Seed:AdminEmail` and `Auth:Seed:AdminPassword` are both set, creates the first administrator. It is idempotent and runs under a database lock, so several instances may start together. An invalid permission declaration or an invalid `Auth:Seed` value stops the host with a message naming it. The database must already be migrated: outside Development, run the `migrate` mode first (Plan 6) or turn the key off and seed from there. Details: [Auth module](../modules/auth.md#background-processing).

## Pipeline

| Slot | Middleware | Notes |
|---|---|---|
| 1 | `UseForwardedHeaders` | Honors `X-Forwarded-For` / `X-Forwarded-Proto` from trusted proxies only (`ForwardedHeaders` section). First, so everything after sees the client's address and scheme. |
| 1a | `UseApiLocalization` | Picks the UI culture from `Accept-Language`. Before the exception handler, because culture is async-local: set later, 500 and malformed-body responses would come back in English (ADR 0009). Also sets the default thread cultures for background work. |
| 2 | `UseExceptionHandler` | `ConcurrencyExceptionHandler` (409 `concurrency.conflict`), then `GlobalExceptionHandler` (400 `request.malformed` for bad bodies, else 500 `server.unexpected_error`). |
| 3 | `UseStatusCodePages` | Body-less 4xx/5xx responses (unknown route, wrong method) become ProblemDetails with `code` `http.{status}`. |
| 4 | `UseTraceIdHeader` | `X-Trace-Id` = the W3C trace id, also the `traceId` of every problem response. |
| 5 | `UseSecurityHeaders` | `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`. |
| 6 | `UseHsts` + `UseHttpsRedirection` | Every environment except Development. |
| 7 | `UseRequestLogging` | One Serilog event per request with `UserId`; health checks at `Verbose`, failures (exception or 5xx) at `Error`. |
| 8 | `UseRouting` | |
| 9 | `UseCors` | Default policy from `Cors:AllowedOrigins`. |
| — | *(Auth plan)* `UseAuthentication` / `UseAuthorization` | Go between slots 9 and 10, so the rate limiter's `user:{sub}` partition sees the signed-in user. |
| 10 | `UseRateLimiter` | Global fixed-window limiter; 429 `rate_limit.exceeded` with `Retry-After`. |
| 11 | `UseIdempotency` | `Idempotency-Key` on endpoints marked `WithIdempotency()`; after routing because it reads endpoint metadata. |
| 12 | Endpoints | `/health/live`, `/health/ready` (both exempt from rate limiting), OpenAPI and Scalar outside Production, then the `/api/v1` group with every module's endpoints, and the Auth module's `GET /.well-known/jwks.json` at the root (`MapAuthWellKnownEndpoints`). |

Slots 4 and 5 register their headers with `Response.OnStarting` before the rest of the pipeline runs, so they are written when the response starts and survive the exception handler clearing the response headers. `Content-Language` is the exception: responses written by the exception handler (500, malformed body, concurrency conflict) do not carry it, although their `detail` is still localized (ADR 0009).

## Configuration

Sections are bound lazily through `IOptions<T>` and, where marked, validated on start (`ValidateDataAnnotations().ValidateOnStart()`): an invalid value stops the host at boot with the option's error. Environment variables use `Section__Key` (`Outbox__BatchSize=50`), arrays `Section__Key__0`. No secret belongs in `appsettings*.json`.

### `ConnectionStrings`

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `Database` | empty in `appsettings.json` | required, on start: the host does not start without it | SQL Server connection string for every module context and the Dapper connection factory. Supply it with user secrets (`dotnet user-secrets set "ConnectionStrings:Database" "…" --project src/Host/TemplateName.Api`) or the environment (`ConnectionStrings__Database`). |

A module context can name another entry (`AddModuleDbContext<T>(schema, connectionStringName: "…")`); none does today. Contexts read the string when they are created, never in `Program.cs`, so `WebApplicationFactory` overrides apply.

### `Database`

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `ApplyMigrationsOnStartup` | `false` (`true` in `appsettings.Development.json`) | none | Migrate every module database before the pipeline starts. Development only. |

### `Outbox`

Shared by every module's dispatcher (ADR 0007).

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `Enabled` | `true` | — | Runs the background dispatchers. Integration tests set `false` and dispatch by hand. |
| `PollingInterval` | `00:00:05` | `00:00:01` to `01:00:00` | Pause between polls once the due messages are drained. |
| `BatchSize` | `20` | 1 to 500 | Most messages one dispatcher claims per batch; a full batch is followed at once by the next. |
| `MaxAttempts` | `5` | 1 to 100 | Failed attempts after which a message is abandoned (kept unprocessed with its last error). |
| `LeaseDuration` | `00:01:00` | `00:00:10` to `1.00:00:00` | How long a claimed batch stays hidden from other dispatchers; keep it longer than the slowest batch. |

### `Idempotency`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `TimeToLive` | `1.00:00:00` (one day) | `00:01:00` to `30.00:00:00` | How long a key and its stored response are kept; a reused expired key runs again. |
| `InProgressTimeout` | `00:05:00` (five minutes) | `00:00:05` to `01:00:00`, and not longer than `TimeToLive` | How long a key stays leased to the request running with it. |

The first request with a key holds it as a lease for `InProgressTimeout`; duplicates get 409 meanwhile. Storing the response replaces the lease with `TimeToLive`. If the request dies or its response cannot be stored, the next request with the key after the lease ends runs the endpoint again instead of getting 409 for the whole time to live, so keep `InProgressTimeout` longer than the slowest idempotent request: one that outlives its lease can run twice. Details: [infrastructure-common](../building-blocks/infrastructure-common.md#idempotency).

### `Cors`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `AllowedOrigins` | `[]` (no cross-origin caller) | `*` is rejected | Origins allowed to call with credentials, e.g. `https://app.example.com`. Any header and method are allowed for them. |

The policy exposes `X-Trace-Id`, `Location`, `Retry-After` and `Idempotency-Replayed` (`Access-Control-Expose-Headers`), so browser code on an allowed origin can read them.

### `RateLimiting`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `GlobalPermitLimit` | `300` | 1 or more | Requests per partition per window. |
| `GlobalWindow` | `00:01:00` | `00:00:01` to `1.00:00:00` | Fixed window length. |

The partition is `user:{sub}` for an authenticated caller, else `ip:{remote address}` as left by the forwarded-headers middleware, so a spoofed `X-Forwarded-For` cannot choose a partition. Requests over the limit are rejected at once (no queue).

### `ForwardedHeaders`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `KnownProxies` | `[]` | each a valid IP address | Trusted proxy addresses, e.g. `10.0.0.5`. |
| `KnownNetworks` | `[]` | each a valid CIDR network | Trusted proxy networks, e.g. `10.0.0.0/24`. |

With both lists empty only loopback proxies are trusted (the ASP.NET Core default); configuring either replaces that default.

### `Localization`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `DefaultCulture` | `en` | required; must be in `SupportedUICultures` | Fallback UI culture, and the formatting culture of every request and of background work. |
| `SupportedUICultures` | `["en", "ms", "zh-Hans"]` | at least one; each a culture the runtime knows (needs ICU, `InvariantGlobalization` off) | Languages a client may ask for with `Accept-Language`. A configured list replaces the default list. |

### `Serilog`

Read with `ReadFrom.Configuration`, so every Serilog setting works here.

| Key | Default | Meaning |
|---|---|---|
| `MinimumLevel:Default` | `Information` (`Debug` in Development) | Minimum level. |
| `MinimumLevel:Override` | `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System`: `Warning` | Per-source levels. |

Console output is a readable template in Development and compact JSON (`RenderedCompactJsonFormatter`) elsewhere. Every event carries `Application` and `Environment`; destructured objects have values of properties named like `password`, `token`, `secret`, `otp` or `apikey` replaced by `***`.

### `OTEL_EXPORTER_OTLP_ENDPOINT`

| Key | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset (`http://localhost:4317` in `appsettings.Development.json`, the Aspire dashboard from `docker-compose.yml`) | When set, logs, traces and metrics are exported over OTLP. Environment variable or configuration key. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | gRPC | `http/protobuf` switches to HTTP; Serilog then posts logs to `{endpoint}/v1/logs`. |

The endpoint decides which OpenTelemetry services are registered, so it is **read once, when the host is configured**; `WebApplicationFactory` overrides cannot change it. The protocol is read lazily when Serilog is configured. Traces cover ASP.NET Core (without `/health`), `HttpClient`, SqlClient and EF Core; metrics cover ASP.NET Core, `HttpClient` and the runtime.

### `Kestrel:Limits`

| Key | Default | Meaning |
|---|---|---|
| `Kestrel:Limits:MaxRequestBodySize` | `10485760` (10 MiB) | Largest request body; larger bodies are rejected. |

`Program.cs` binds the whole `Kestrel` section to `KestrelServerOptions` explicitly (Kestrel itself binds only endpoints from it), so any `Kestrel:Limits:*` key works.

### `Auth:Seed`

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `RunOnStartup` | `true` | none | Seed the Auth module after the migration step ([Start-up](#start-up)). The integration tests set `false` and seed from the harness. |
| `AdminEmail` | empty (`admin@localhost.test` in `appsettings.Development.json`) | when seeding: a plain email address, up to 256 characters | The first administrator's email. |
| `AdminPassword` | empty | when seeding: within `Auth:Password:MinLength`/`MaxLength` | The first administrator's password. **Never in a file**: `dotnet user-secrets set "Auth:Seed:AdminPassword" "…" --project src/Host/TemplateName.Api` or `Auth__Seed__AdminPassword`. Without it no administrator is seeded. **Remove it after the first start**: the account then exists, and an account with that email, even a soft-deleted one, is never re-created (each start logs a warning while the key is still set). |

The other `Auth` keys (password rules, email, links, JWT) are listed in the [Auth module](../modules/auth.md#configuration) document.

### Other keys

| Key | Default | Meaning |
|---|---|---|
| `AllowedHosts` | `*` | ASP.NET Core host filtering. Set it to the public host names in production. |

## Health

| Endpoint | Checks | Use |
|---|---|---|
| `GET /health/live` | none: 200 while the process serves requests | Liveness probe |
| `GET /health/ready` | every check tagged `ready`: one EF Core `DbContext` check per schema (`platform`, `auth`, `sample`, plus one per added module) | Readiness probe, load balancer |

Both are exempt from rate limiting, logged at `Verbose` when healthy, and left out of traces.

## OpenAPI and Scalar

Outside Production (Development, Testing, and any other non-Production environment) the host serves the OpenAPI document at `/openapi/v1.json` and the Scalar reference at `/scalar/v1`. Production maps neither. The document is pinned by `OpenApiSnapshotTests` (ADR 0011): an intended API change is accepted by replacing the committed `.verified.json` with the `.received.json` the failing test writes.

## Environments

| | Development | Testing | Production |
|---|---|---|---|
| Used by | `dotnet run` (`launchSettings.json`) | the integration tests (`WebApplicationFactory`) | deployments |
| Migrations on start | yes | no (the test harness migrates once) | no (`migrate` mode, Plan 6) |
| Log output | readable console, `Debug` | compact JSON, `Warning` (set by the tests) | compact JSON, `Information` |
| OTLP export | to `http://localhost:4317` | off | when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| HSTS + HTTPS redirection | no | yes | yes |
| `exceptionDetails` in 500 responses | yes | no | no |
| OpenAPI + Scalar | yes | yes | no |
| Auth seeding on start | yes; the administrator `admin@localhost.test` once its password is in user secrets | no (the test harness seeds after each reset) | yes (roles and permissions; an administrator only when both settings are supplied) |
| JWT signing key (`Auth:Jwt:SigningKeys`) | an ephemeral key when none is configured | an ephemeral key when none is configured | required: the host does not start without one ([Auth module](../modules/auth.md#access-tokens)) |

Local URLs: `https://localhost:5001` and `http://localhost:5000`.

## Migrations

`MigrateModuleDatabasesAsync` applies the pending migrations of every context registered with `AddModuleDbContext` (`includeInMigrations: true`, the default), in registration order: `platform` first, then each module. It validates `ConnectionStrings:Database` first, so a missing connection string fails with the options error.

- **Development:** `Database:ApplyMigrationsOnStartup` is `true`, so the host migrates before it starts serving.
- **Production:** never on start. The API's `migrate` command-line mode (`dotnet TemplateName.Api.dll migrate`, Plan 6) runs the same method, optionally with a DDL-privileged connection string. There are no EF migration bundles (ADR 0006).
- **Adding a migration:** `dotnet ef migrations add {Verb}{What} --project src/Modules/{Module}/TemplateName.Modules.{Module} --startup-project src/Host/TemplateName.Api --context {Module}DbContext --output-dir Infrastructure/Persistence/Migrations`. Never edit a migration that has been applied; add a new one.

## Known limitations

HTTP security:
- When the connection has no remote IP address (a Unix domain socket), the forwarded-headers middleware accepts `X-Forwarded-For` from it whatever the trusted lists say. Do not listen on a socket that untrusted clients can reach.
- Behind a TLS-terminating proxy that does not send `X-Forwarded-Proto`, HTTPS redirection cannot work out the port; set `HTTPS_PORT` (or `https_port`) or forward the scheme.
- CORS preflight requests are answered before the rate limiter, so they are not counted.
- IPv6 and IPv4-mapped addresses are partition keys as plain strings, so one client can appear as two addresses.
- The limiter keeps its counters in memory per instance: with N instances a client gets up to N × `GlobalPermitLimit` (a Redis-backed limiter is Plan 5).
- A bare `Accept-Language: zh` gets English, because `zh` is not a supported culture and `zh-Hans` is its child, not its parent.

Observability:
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` is `1.19.1-beta.1`, the only prerelease dependency: no stable release exists ([ADR 0013](../adr/0013-prerelease-ef-core-opentelemetry-instrumentation.md)). The SqlClient instrumentation is stable.
- With OTLP export on, traces record request URLs, so secrets in query strings reach the trace store. Never put secrets in query strings.
- `RequestActivityBackgroundService` keeps a listener on ASP.NET Core's activity source, so every request has a W3C trace id (`traceId`, `X-Trace-Id`) even when OpenTelemetry is off.

Idempotency: see [infrastructure-common](../building-blocks/infrastructure-common.md#idempotency).
