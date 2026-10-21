# API host (`TemplateName.Api`)

The one deployable process: `src/Host/TemplateName.Api`. `Program.cs` registers the building blocks and modules, builds the middleware pipeline, maps health checks, OpenAPI and the module endpoints under `/api/v1`, migrates the databases on start when told to (local development), and seeds the Auth module's roles and permissions. This page lists what the host does and every setting it reads. Architecture: [`docs/architecture/overview.md`](../architecture/overview.md).

## Service registration

Order matters; `Program.cs` marks it with comments.

| # | Call | Registers |
|---|---|---|
| 1 | `builder.AddObservability()` | Serilog, the request-activity listener, OpenTelemetry when `OTEL_EXPORTER_OTLP_ENDPOINT` is set ([web-common](../building-blocks/web-common.md#observability)) |
| 2 | `AddInfrastructureCommon(configuration)` | `TimeProvider`, `ConnectionStrings`/`Database`/`Outbox`/`Idempotency`/`Retention`/`Jobs`/`Caching` options, the Dapper connection factory, EF interceptors, the concurrency exception handler, the `platform` context, the recurring-job scheduler ([infrastructure-common](../building-blocks/infrastructure-common.md)). Before `AddWebCommon`, so the concurrency handler runs before the global one. |
| 3 | `AddWebCommon()` | ProblemDetails customization, the global exception handler, `ICurrentUser`, `ThrowOnBadRequest` ([web-common](../building-blocks/web-common.md)) |
| 4 | `AddApiLocalization(configuration)` | The `Localization` options, request localization, FluentValidation translations |
| 5 | `ConfigureHttpJsonOptions` | `JsonStringEnumConverter`: enums are strings in JSON (`"Pending"`) |
| 6 | `AddOpenApi("v1", options => options.AddBearerSecurity())` | The OpenAPI document `v1`, with the `Bearer` security scheme required by every operation that names a permission or policy ([Web.Common](../building-blocks/web-common.md)) |
| 7 | `AddHealthChecks()` | Health checks; each module context adds its own `ready` check |
| 8 | `AddHttpSecurity(configuration)` | CORS, the global rate limiter, the `auth-strict` rate-limit policy, forwarded headers |
| 9 | `AddPermissionAuthorization()` | The permission authorization handler behind `RequirePermission(code)` and the **fallback policy**: every endpoint requires an authenticated user unless it says `.AllowAnonymous()` ([web-common](../building-blocks/web-common.md)). It needs the explicit `UseAuthentication` and `UseAuthorization` of the pipeline (slots 1a and 10): without them `WebApplication` adds `UseAuthorization` ahead of `UseRouting` by itself, where no endpoint is known and the fallback policy answers 401 to every request. |
| 10 | `AddAuthModule(configuration)`, `AddNotificationsModule(configuration)`, `AddSampleModule()` (one line per module) | The module's context, outbox, handlers, validators and error messages; the Auth module also the JWT bearer scheme (the default authentication scheme), the permission checker and its cache, and the seeder ([Auth module](../modules/auth.md)); the Notifications module also its delivery worker, SignalR, the `HubTicket` authentication scheme (not a default scheme: only the hub's policy uses it) and the rate-limit policy of its ticket route ([Notifications module](../modules/notifications.md)) |
| 10a | `AddPlatformRedis(configuration)` | Nothing unless `Caching:Provider` is `redis`; then the Redis connection, Redis as the `HybridCache` second level behind a circuit, and the `ready` check `redis` ([platform-redis](../building-blocks/platform-redis.md)). Optional: a solution without Redis deletes this line and the project. |
| 11 | `AddOptions<KestrelServerOptions>()…Bind("Kestrel")` | Binds the whole `Kestrel` section lazily, so `Kestrel:Limits:*` apply and test overrides work |
| 12 | `AddApplicationDecorators()` | Validation and logging decorators around every handler registered above; always last |

## Start-up

Before the pipeline serves requests, the host runs two steps, in this order:

1. **Migrations**, when `Database:ApplyMigrationsOnStartup` is on (off by default; set it locally for development: user secrets, an environment variable, or a git-ignored `appsettings.Development.json`; see [Migrations](#migrations)).
2. **Auth seeding**, when `Auth:Seed:RunOnStartup` is on (the default in every environment): `SeedAuthModuleAsync` creates the system roles, syncs the permissions every module declares, keeps `SuperAdmin` on every permission and, when `Auth:Seed:AdminEmail` and `Auth:Seed:AdminPassword` are both set, creates the first administrator. It is idempotent and runs under a database lock, so several instances may start together. An invalid permission declaration or an invalid `Auth:Seed` value stops the host with a message naming it. The database must already be migrated: outside Development, run the `migrate` mode first (Plan 6) or turn the key off and seed from there. Details: [Auth module](../modules/auth.md#background-processing).

## Pipeline

| Slot | Middleware | Notes |
|---|---|---|
| 1 | `UseForwardedHeaders` | Honors `X-Forwarded-For` / `X-Forwarded-Proto` from trusted proxies only (`ForwardedHeaders` section). First, so everything after sees the client's address and scheme. |
| 1a | `UseAuthentication` | Validates the `Authorization: Bearer` access token (ES256 JWT, [Auth module](../modules/auth.md#access-tokens)) and sets the request's user. It never rejects: a missing, malformed, forged or expired token leaves the request anonymous, and slot 10 decides whether that is allowed. Before localization, so the signed-in user's saved `locale` claim is visible to it (decision D7). It runs outside the exception handler (slot 2), so an exception thrown inside authentication, for example from an authentication event, is not turned into a ProblemDetails response: authentication events must not throw. |
| 1b | `UseApiLocalization` | Picks the UI culture from the signed-in user's `locale` claim, else from `Accept-Language` (an unsupported claim falls through to the header). Before the exception handler, because culture is async-local: set later, 500 and malformed-body responses would come back in English (ADR 0009). Also sets the default thread cultures for background work. |
| 2 | `UseExceptionHandler` | `ConcurrencyExceptionHandler` (409 `concurrency.conflict`), then `GlobalExceptionHandler` (400 `request.malformed` for bad bodies, else 500 `server.unexpected_error`). |
| 3 | `UseStatusCodePages` | Body-less 4xx/5xx responses (401 or 403 from slot 10, unknown route, wrong method) become ProblemDetails with `code` `http.{status}`. |
| 4 | `UseTraceIdHeader` | `X-Trace-Id` = the W3C trace id, also the `traceId` of every problem response. |
| 5 | `UseSecurityHeaders` | `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`. |
| 6 | `UseHsts` + `UseHttpsRedirection` | Every environment except Development. |
| 7 | `UseRequestLogging` | One Serilog event per request with `UserId`; health checks at `Verbose`, failures (exception or 5xx) at `Error`. `RequestPath` is the path without the query string, so a hub ticket sent as `access_token` never reaches it; `access_token` values are also masked in every other log event and in traces ([web-common](../building-blocks/web-common.md#observability)). |
| 8 | `UseRouting` | |
| 9 | `UseCors` | Default policy from `Cors:AllowedOrigins`. Before authorization, so a preflight (which carries no token) is answered without a 401. |
| 9a | `UseRateLimiter` | Global fixed-window limiter, plus the `auth-strict` policy on the endpoints that ask for it; 429 `rate_limit.exceeded` with `Retry-After`. After routing (the endpoint's rate-limit metadata is known) and authentication (the `user:{sub}` partition sees the signed-in user), and before authorization, so the 401 and 403 responses that slot 10 produces are counted too. |
| 10 | `UseAuthorization` | After routing, so it reads the endpoint's metadata. An endpoint with `.AllowAnonymous()` passes; one with `RequirePermission(code)` needs that permission (403 for a signed-in caller without it); every other endpoint, and a request that matched no endpoint, falls under the fallback policy and needs an authenticated user. An anonymous caller gets 401 `http.401` (localized, with `traceId` and `WWW-Authenticate: Bearer`). |
| 11 | `UseIdempotency` | `Idempotency-Key` on endpoints marked `WithIdempotency()`; after routing because it reads endpoint metadata. |
| 12 | Endpoints | `/health/live`, `/health/ready` (both anonymous and exempt from rate limiting), OpenAPI and Scalar outside Production (anonymous), then the `/api/v1` group with every module's endpoints, the Auth module's anonymous `GET /.well-known/jwks.json` at the root (`MapAuthWellKnownEndpoints`), and the Notifications SignalR hub at `/hubs/notifications` (`MapNotificationsHub`: WebSockets only, authorized by the JWT bearer scheme or a single-use hub ticket, [Notifications module](../modules/notifications.md#realtime-hub)). Slots 1a to 10 apply to the hub's negotiate and WebSocket requests like to any endpoint. Everything else is protected by the fallback policy. |

Slots 4 and 5 register their headers with `Response.OnStarting` before the rest of the pipeline runs, so they are written when the response starts and survive the exception handler clearing the response headers. `Content-Language` is the exception: responses written by the exception handler (500, malformed body, concurrency conflict) do not carry it, although their `detail` is still localized (ADR 0009).

## Configuration

Sections are bound lazily through `IOptions<T>` and, where marked, validated on start (`ValidateDataAnnotations().ValidateOnStart()`): an invalid value stops the host at boot with the option's error. Environment variables use `Section__Key` (`Outbox__BatchSize=50`), arrays `Section__Key__0`. No secret belongs in `appsettings*.json`.

### `ConnectionStrings`

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `Database` | empty in `appsettings.json` | required, on start: the host does not start without it | SQL Server connection string for every module context and the Dapper connection factory. Supply it with user secrets (`dotnet user-secrets set "ConnectionStrings:Database" "…" --project src/Host/TemplateName.Api`) or the environment (`ConnectionStrings__Database`). |

| `Redis` | empty in `appsettings.json` | required, on start, only when `Caching:Provider` is `redis` | The Redis server, a StackExchange.Redis configuration string (`localhost:6379` for the compose `valkey` service). A password belongs in user secrets or the environment (`ConnectionStrings__Redis`). `abortConnect` and the timeouts are set from `Caching:Redis` whatever the string says ([`Caching`](#caching)). |

A module context can name another entry (`AddModuleDbContext<T>(schema, connectionStringName: "…")`); none does today. Contexts read the string when they are created, never in `Program.cs`, so `WebApplicationFactory` overrides apply.

### `Database`

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `ApplyMigrationsOnStartup` | `false`; set `true` locally (user secrets, an environment variable, or a git-ignored `appsettings.Development.json`) | none | Migrate every module database before the pipeline starts. Local development only. |

### `Outbox`

Shared by every module's dispatcher (ADR 0007).

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `Enabled` | `true` | — | Runs the background dispatchers. Integration tests set `false` and dispatch by hand. |
| `PollingInterval` | `00:00:05` | `00:00:01` to `01:00:00` | Pause between polls once the due messages are drained. |
| `BatchSize` | `20` | 1 to 500 | Most messages one dispatcher claims per batch; a full batch is followed at once by the next. |
| `MaxAttempts` | `5` | 1 to 100 | Failed attempts after which a message is abandoned (kept unprocessed with its last error). Retries are due 5 s, 30 s, 2 min and 10 min after attempts 1 to 4 and 1 h after each later one, so the default retries for 12 min 35 s and each attempt above 5 adds an hour. `Retention:Inbox` must be longer than that span (checked with `Retention`, below). |
| `LeaseDuration` | `00:01:00` | `00:00:10` to `1.00:00:00` | How long a claimed batch stays hidden from other dispatchers; keep it longer than the slowest batch. |

### `Idempotency`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `TimeToLive` | `1.00:00:00` (one day) | `00:01:00` to `30.00:00:00` | How long a key and its stored response are kept; a reused expired key runs again. |
| `InProgressTimeout` | `00:05:00` (five minutes) | `00:00:05` to `01:00:00`, and not longer than `TimeToLive` | How long a key stays leased to the request running with it. |

The first request with a key holds it as a lease for `InProgressTimeout`; duplicates get 409 meanwhile. Storing the response replaces the lease with `TimeToLive`. If the request dies or its response cannot be stored, the next request with the key after the lease ends runs the endpoint again instead of getting 409 for the whole time to live, so keep `InProgressTimeout` longer than the slowest idempotent request: one that outlives its lease can run twice. Expired rows are deleted every hour by the job `IdempotencyKeysCleanup`, which has no retention setting: `Jobs:IdempotencyKeysCleanup:Enabled = false` turns it off. Details: [infrastructure-common](../building-blocks/infrastructure-common.md#idempotency).

### `Retention`

How long the building blocks keep what they own before a cleanup job deletes it (ADR 0028; the jobs are in [infrastructure-common](../building-blocks/infrastructure-common.md#cleanup-jobs)). A row goes only when strictly older than the retention. Set a retention to `00:00:00` (or empty) to turn that cleanup off; modules keep their own retentions in their own sections (`Auth:Retention` and `Notifications:Retention`, below).

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `BatchSize` | `1000` | 1 to 10000 | Most rows one delete statement removes; a run repeats it up to `Jobs:MaxBatchesPerRun` times. |
| `Outbox` | `7.00:00:00` (7 days) | not negative | How long a published outbox message (and its consumer rows) is kept after `ProcessedAt`. Abandoned messages are never deleted. The job `{Context}OutboxCleanup` runs daily. |
| `Inbox` | `7.00:00:00` (7 days) | not negative; zero or at least `1.00:00:00`; and longer than the outbox's retry span for `Outbox:MaxAttempts` (12 min 35 s by default, a day at 28 attempts and more than a day from 29; a longer `MaxAttempts` needs a longer `Inbox`) | How long an inbox row is kept after `ProcessedAt`, so it outlasts outbox redelivery and a late retry never runs a consumer twice. The job `{Context}InboxCleanup` (today `NotificationsInboxCleanup`) runs daily. |

### `Caching`

Where `HybridCache` keeps its entries (ADR 0027; [infrastructure-common](../building-blocks/infrastructure-common.md#caching), [platform-redis](../building-blocks/platform-redis.md)). The only entry today is the Auth module's permission set (`auth:perm:{userId}`, 30 seconds).

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `Provider` | `memory` | `memory`, `redis` or `none` (any case); `redis` also needs `AddPlatformRedis` in the host and `ConnectionStrings:Redis` | `memory`: in memory per instance. `redis`: in memory per instance with Redis as the shared second level, so a set one instance loaded is served to the others. `none`: no cache at all, every read goes to the database (for diagnosing). |
| `Redis:InstanceName` | `TemplateName:` | required (provider `redis`) | The prefix of every Redis key: `{InstanceName}{module}:{entity}:{id}`. |
| `Redis:FailureThreshold` | `3` | 1 to 100 (provider `redis`) | Consecutive failed Redis calls after which Redis is skipped for `BreakDuration`. |
| `Redis:BreakDuration` | `00:00:30` | `00:00:01` to `01:00:00` (provider `redis`) | How long Redis is skipped (reads miss to the database, writes are dropped) once the threshold is reached; then it is tried again. |
| `Redis:CommandTimeout` | `00:00:00.500` | `00:00:00.050` to `00:00:30` (provider `redis`) | The connect, sync and async timeouts: the most a request waits for a Redis that does not answer. |

With Redis down the API keeps serving: cache reads go to the database, one warning is logged per outage and `/health/ready` answers 200 `Degraded`. An instance keeps a permission set in memory for 30 seconds, and a change made through another instance is not pushed to it, so a removed permission can still be used on another instance for up to 30 seconds, with or without Redis ([platform-redis](../building-blocks/platform-redis.md#cross-instance-invalidation-the-honest-limit)).

### `Jobs`

The recurring-job scheduler ([infrastructure-common](../building-blocks/infrastructure-common.md#recurring-jobs)).

| Key | Default | Validation | Meaning |
|---|---|---|---|
| `Enabled` | `true` | — | Runs the scheduler's background service. Integration tests set `false` and run jobs through `RecurringJobRunner.RunDueAsync`. |
| `TickInterval` | `00:00:30` | `00:00:05` to `01:00:00`, on start | How often the scheduler looks for due jobs. |
| `LeaseDuration` | `00:15:00` | `00:01:00` to `1.00:00:00`, on start | How long a claimed run keeps other instances off its job; a run still going at the end is cancelled and recorded `Failed`. |
| `MaxBatchesPerRun` | `100` | 1 to 10000, on start | Most batches a batched job (a cleanup) runs in one run. |
| `{JobName}:Enabled` | `true` | — | `false` stops that job on this instance. |
| `{JobName}:Interval` | the job's own default | `00:00:01` to `365.00:00:00`, on start (even with `Enabled` false) | Time from the end of one run to the next. |

`{JobName}` is the job's name (PascalCase, no dots), for example `Jobs__AuthRefreshTokensCleanup__Interval=12:00:00`. An invalid or unreadable value for a registered job stops the host at start with a message naming `Jobs:{JobName}`; a section that names no registered job is ignored.

### `Resilience`

The pipelines of outbound HTTP clients registered with `AddExternalHttpClient` (ADR 0029; behaviour, the failure predicate and how to add a pipeline: [infrastructure-common](../building-blocks/infrastructure-common.md#http-resilience)). Each pipeline has its own section, `Resilience:Pipelines:{pipeline}`, validated on start (a pipeline no client uses is not checked). The built-in pipelines are `external-default`, `external-critical` (the default with 5 retries and a 90 s total) and `hibp` (the Auth breached-password lookup); any other name starts from the `external-default` values. A key you leave out keeps the default of its pipeline.

| Key | Default (`external-default`; `external-critical`; `hibp`) | Validation | Meaning |
|---|---|---|---|
| `TotalTimeout` | `00:00:30`; `00:01:30`; `00:00:02` | above zero | The most time one call may take in all: every attempt and the delays between them. |
| `AttemptTimeout` | `00:00:10`; `00:00:10`; `00:00:01` | above zero, not above `TotalTimeout` | The most time one attempt may take; a slower one is cancelled, counts as a failure and is retried. |
| `Retries` | `3`; `5`; `1` | 0 to 10 | Repeats after the first attempt of a call that failed with a 5xx, 408 or 429 status, a network error or an attempt timeout; 0 turns retrying off. Any other 4xx is never retried, and an unsafe method only with `RetryUnsafeMethods`. |
| `BaseDelay` | `00:00:02`; `00:00:02`; `00:00:00.2` | above zero | The first retry delay; later ones grow exponentially, with jitter. |
| `RetryUnsafeMethods` | `false` | a boolean | Retries a POST, PUT, PATCH, DELETE or CONNECT too. Off: such a request is sent once, whatever the answer (a blind repeat can send twice); set it only for endpoints that are idempotent or carry an idempotency key. The breaker counts their failures either way. |
| `Breaker:FailureRatio` | `0.5` | above 0, at most 1 | The share of failed attempts in `Breaker:SamplingDuration` at which the circuit breaker opens. |
| `Breaker:MinimumThroughput` | `10` | at least 2 | The fewest attempts in the sampling window before the ratio counts. |
| `Breaker:SamplingDuration` | `00:00:30` | at least `00:00:00.5` | The window the ratio is measured over. |
| `Breaker:BreakDuration` | `00:00:30` | at least `00:00:00.5` | How long an open breaker rejects calls (without a request) before one probe call is let through. |

For example `Resilience__Pipelines__hibp__Retries=0` (the pipeline name is part of the key; a name with a hyphen, as `external-default`, works as an environment variable name in a container but not in every shell). A raised `Retries` needs a `TotalTimeout` that holds the delays: the five delays of `external-critical` add up to between about 23 s and 46 s, which is why its total is 90 s. Total and attempt timeout, retries and the breaker apply per client; two clients on one pipeline name share the settings, not the breaker.

### `Cors`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `AllowedOrigins` | `[]` (no cross-origin caller) | `*` is rejected | Origins allowed to call with credentials, e.g. `https://app.example.com`. Any header and method are allowed for them. |

The policy exposes `X-Trace-Id`, `Location`, `Retry-After` and `Idempotency-Replayed` (`Access-Control-Expose-Headers`), so browser code on an allowed origin can read them.

**The notification hub.** A browser on an allowed origin calls `POST /api/v1/notifications/hub-tickets` and the hub's negotiate request through this policy like any route. Browsers do not apply CORS to the WebSocket request itself, and the hub does not check its `Origin`: the hub accepts no cookie, so there is no ambient credential for a hostile page to use (cross-site WebSocket hijacking), and a page cannot get a ticket without the user's access token ([ADR 0021](../adr/0021-signalr-hub-tickets.md)).

### `RateLimiting`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `GlobalPermitLimit` | `300` | 1 or more | Requests per partition per window. |
| `GlobalWindow` | `00:01:00` | `00:00:01` to `1.00:00:00` | Fixed window length. |
| `AuthStrictPermitLimit` | `10` | 1 or more | Requests per client address per window, shared by every endpoint with the `auth-strict` policy (`RequireRateLimiting(RateLimitPolicies.AuthStrict)`): `POST /api/v1/auth/register`, `/email/confirm`, `/email/resend-confirmation`, `/login`, `/token/refresh`, `/password/forgot`, `/password/reset`, `/password/change`, `/step-up` and `/mfa/verify`. |
| `AuthStrictWindow` | `00:01:00` | `00:00:01` to `1.00:00:00` | The `auth-strict` fixed window length. |

The global partition is `user:{sub}` for an authenticated caller, else `ip:{remote address}`; the `auth-strict` partition is always `ip:{remote address}`. The address is the one left by the forwarded-headers middleware, so a spoofed `X-Forwarded-For` cannot choose a partition. An `auth-strict` endpoint is also under the global limiter. Requests over a limit are rejected at once (no queue).

**Sizing `auth-strict` behind NAT.** The `auth-strict` budget is **one bucket per client address shared by all ten routes** above (refresh included), 10 per minute by default. Many users behind one NAT or carrier-grade NAT address (an office, a campus, a mobile network) share that bucket, so their logins and token refreshes together can exhaust it and get 429s. For such deployments raise `RateLimiting:AuthStrictPermitLimit` (and make sure the forwarded-headers settings give the real client address). A separate, larger policy for refresh is a planned follow-up.

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
| `SupportedUICultures` | `["en", "ms", "zh-Hans"]` | at least one; each a culture the runtime knows (needs ICU, `InvariantGlobalization` off) | Languages a response can use, chosen from the signed-in user's saved `locale` claim, else from `Accept-Language`. A configured list replaces the default list. |

### `Serilog`

Read with `ReadFrom.Configuration`, so every Serilog setting works here.

| Key | Default | Meaning |
|---|---|---|
| `MinimumLevel:Default` | `Information`; `Debug` if set locally (user secrets, an environment variable, or a git-ignored `appsettings.Development.json`) | Minimum level. |
| `MinimumLevel:Override` | `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System`: `Warning` | Per-source levels. |

Console output is a readable template in Development and compact JSON (`RenderedCompactJsonFormatter`) elsewhere. Every event carries `Application` and `Environment`; destructured objects have values of properties named like `password`, `token`, `secret`, `otp` or `apikey` replaced by `***`.

### `OTEL_EXPORTER_OTLP_ENDPOINT`

| Key | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset; set locally (user secrets, an environment variable, or a git-ignored `appsettings.Development.json`) to `http://localhost:4317`, the Aspire dashboard from `docker-compose.yml` | When set, logs, traces and metrics are exported over OTLP. Environment variable or configuration key. |
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
| `AdminEmail` | empty; `admin@localhost.test` set locally (user secrets, an environment variable, or a git-ignored `appsettings.Development.json`) | when seeding: a plain email address, up to 256 characters | The first administrator's email. |
| `AdminPassword` | empty | when seeding: within `Auth:Password:MinLength`/`MaxLength` | The first administrator's password. **Never in a file**: `dotnet user-secrets set "Auth:Seed:AdminPassword" "…" --project src/Host/TemplateName.Api` or `Auth__Seed__AdminPassword`. Without it no administrator is seeded. **Remove it after the first start**: the account then exists, and an account with that email, even a soft-deleted one, is never re-created (each start logs a warning while the key is still set). |

The Auth module reads the sections below, each with its keys, defaults and ranges in the [Auth module](../modules/auth.md#configuration) document. Nine of them are validated on start (`Auth:Password`, `Auth:Links`, `Auth:Verification`, `Auth:RefreshToken`, `Auth:Lockout`, `Auth:Mfa`, `Auth:StepUp`, `Auth:Retention` and `Auth:Jwt`), so an out-of-range value or a bad signing key stops the host. `Auth:DataProtection` is read as it is (a missing name falls back to `TemplateName`), and `Auth:Seed` is checked when the seeder runs.

| Section | Holds |
|---|---|
| `Auth:Password` | Length limits, history count, the breached-password check. |
| `Auth:Links` | The front-end URLs the confirmation and reset links open. |
| `Auth:Verification` | Lifetimes of the confirmation and reset links, the resend cooldown. |
| `Auth:RefreshToken` | Sliding and absolute session lifetimes. |
| `Auth:Lockout` | Failed attempts and lockout duration. |
| `Auth:Mfa` | Two-step verification: `Issuer` (the authenticator issuer name, no `:`), `ChallengeLifetime` and `MaxChallengeAttempts` (the sign-in challenge's lifetime and wrong-code limit), and `TrustedDevicesEnabled` and `TrustedDeviceLifetime` (whether and for how long a device can be remembered). |
| `Auth:StepUp` | `MaxAge`: how long a step-up (a fresh `auth_time`) satisfies a protected action. |
| `Auth:Retention` | How long time-bound rows are kept before a cleanup job deletes them (ADR 0028), each a `TimeSpan` that is not negative, with zero or empty turning that cleanup off: `VerificationCodes` (`7.00:00:00`), `RefreshTokens` (`30.00:00:00`, after the token's own expiry), `Sessions` (`90.00:00:00`, after revocation or expiry; the tokens go with them), `MfaChallenges` (`1.00:00:00`), `TrustedDevices` (`30.00:00:00`, after expiry or revocation) and `AuditLogs` (empty: kept, until an archive exists). The jobs `AuthVerificationCodesCleanup` and `AuthMfaChallengesCleanup` run hourly, the other four daily; the keys and the rules are in [Auth module: Cleanup](../modules/auth.md#cleanup). |
| `Auth:Jwt` | Issuer, audience, lifetime, skew and the signing keys (required outside Development and Testing). |
| `Auth:DataProtection` | The Data Protection application name. |
| `Auth:Seed` | Above. |

### `Notifications:Email`

The SMTP server the Notifications email channel sends through ([Notifications module](../modules/notifications.md#email-channel)); Mailpit locally. Validated on start. Until Plan 3 these keys were `Auth:Email`: the host **refuses to start** while any `Auth:Email` key is still set, in any source (a settings file, user secrets, `Auth__Email__*` in the environment), with `Auth:Email has moved to Notifications:Email (see docs/modules/notifications.md#configuration).` (D15). Rename the keys; their values and meaning are unchanged.

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `Host` | `localhost` | required | The SMTP host. The default (`localhost:1025`, no TLS) is Mailpit from `docker-compose.yml`. |
| `Port` | `1025` | 1 to 65535 | The SMTP port. |
| `UseTls` | `false` | must be `true` when `Username` is set, unless `Host` is a loopback address (`localhost`, `127.0.0.1`, `::1`) | STARTTLS. Turn it on for every real server; the start-up check keeps the credentials from travelling in clear. |
| `Username` | empty | — | The SMTP account; empty means no authentication. **Never in a file**: user secrets or `Notifications__Email__Username`. |
| `Password` | empty | — | The SMTP password. **Never in a file**: user secrets or `Notifications__Email__Password`. |
| `From` | `no-reply@localhost.test` | an email address | The sender address. |
| `FromName` | `TemplateName` | required, at most 100 characters | The display name next to the sender address. |
| `Timeout` | `00:00:30` | `00:00:01` to `00:05:00` | How long connecting and sending may each take. |

### `Notifications:Delivery`

The Notifications delivery worker ([Notifications module](../modules/notifications.md#background-processing)). Validated on start.

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `Enabled` | `true` | — | Runs the delivery worker. Integration tests set `false` and dispatch by hand. |
| `PollingInterval` | `00:00:05` | `00:00:01` to `01:00:00` | Pause between polls once the due deliveries are drained. |
| `BatchSize` | `10` | 1 to 100 | Most deliveries one worker claims per batch; a full batch is followed at once by the next. |
| `MaxAttempts` | `5` | 1 to 10 | Attempts before a delivery is dead-lettered. Retries are due 1 min, 5 min, 15 min and 1 h after failed attempts 1 to 4; with the default the 5th failure dead-letters, and the 6 h step is used only when `MaxAttempts` is raised. |
| `LeaseDuration` | `00:05:00` | `00:00:30` to `01:00:00` | How long a claimed batch stays hidden from other workers; keep it longer than the slowest batch of sends. No delivery starts with less than a fifth of it left (at least 30 seconds, at most half); the unstarted rest of the batch is released. |

### `Notifications:Hub`

The notification hub and its single-use tickets ([Notifications module](../modules/notifications.md#realtime-hub), [ADR 0021](../adr/0021-signalr-hub-tickets.md)). Validated on start.

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `TicketLifetime` | `00:00:30` | `00:00:05` to `00:05:00` | How long a ticket from `POST /api/v1/notifications/hub-tickets` can be used, once. |
| `ConnectionLifetime` | `00:10:00` | `00:01:00` to `01:00:00` | How long a connection opened with a ticket stays open; then the hub closes it and the client reconnects with new tickets. |
| `TicketsPerMinute` | `30` | 1 to 1000 | Tickets one user may get per minute (fixed window per `user:{sub}`, on top of the global limiter); a connection takes two. Beyond it, 429 `rate_limit.exceeded`. |

### `Notifications:Templates`

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `ProductName` | `TemplateName` | required, at most 100 characters | The product's name as recipients know it: `{{ product_name }}` in every notification template and the email footer ([Notifications module](../modules/notifications.md#templates-and-rendering)). |

### `Notifications:Retention`

How long the Notifications module keeps its time-bound rows before a cleanup job deletes them (ADR 0028; the jobs and their rules are in [Notifications module: Cleanup](../modules/notifications.md#cleanup)). Each key is a `TimeSpan` that is not negative: a row goes only when strictly older than it, and `00:00:00` or an empty value turns that cleanup off. Validated on start. The batch size and the most batches per run are the building-block settings under [`Retention`](#retention) and [`Jobs`](#jobs).

| Key | Default | Validation (on start) | Meaning |
|---|---|---|---|
| `HubTickets` | `01:00:00` (1 hour) | not negative | How long a hub ticket is kept after its `ExpiresAt`, consumed or not. The job `NotificationsHubTicketsCleanup` runs hourly. |
| `Notifications` | `180.00:00:00` (180 days) | not negative | How long a notification is kept after its `CreatedAt`; its deliveries, dead-lettered ones included, go with it, and one with a delivery still pending stays. The job `NotificationsRowsCleanup` runs daily. |
| `InAppNotifications` | `180.00:00:00` (180 days) | not negative | How long an inbox row is kept after its `CreatedAt`, read or unread. The job `NotificationsInAppCleanup` runs daily. |

### Other keys

| Key | Default | Meaning |
|---|---|---|
| `AllowedHosts` | `*` | ASP.NET Core host filtering. Set it to the public host names in production. |

## Health

| Endpoint | Checks | Use |
|---|---|---|
| `GET /health/live` | none: 200 while the process serves requests | Liveness probe |
| `GET /health/ready` | every check tagged `ready`: one EF Core `DbContext` check per schema (`platform`, `auth`, `notify`, `sample`, plus one per added module), and with `Caching:Provider = redis` the check `redis` (a `PING`) | Readiness probe, load balancer |

The `redis` check fails as `Degraded`, not `Unhealthy`: with Redis down `/health/ready` answers **200** with the body `Degraded`, because the instance still serves from the database and a load balancer must not drain every instance for a cache outage. A database check that fails still answers 503 `Unhealthy`.

Both are anonymous, exempt from rate limiting, logged at `Verbose` when healthy, and left out of traces.

## OpenAPI and Scalar

Outside Production (Development, Testing, and any other non-Production environment) the host serves the OpenAPI document at `/openapi/v1.json` and the Scalar reference at `/scalar/v1`. Production maps neither. The document declares the `Bearer` scheme (`http`, `bearer`, `JWT`) and marks every operation whose endpoint carries authorization data (`RequirePermission`, `RequireAuthorization`) and no `.AllowAnonymous()` with it, so Scalar can send an access token. The document is pinned by `OpenApiSnapshotTests` (ADR 0011): an intended API change is accepted by replacing the committed `.verified.json` with the `.received.json` the failing test writes.

## Environments

| | Development | Testing | Production |
|---|---|---|---|
| Used by | `dotnet run` (`launchSettings.json`) | the integration tests (`WebApplicationFactory`) | deployments |
| Migrations on start | when `Database:ApplyMigrationsOnStartup` is set locally | no (the test harness migrates once) | no (`migrate` mode, Plan 6) |
| Log output | readable console, `Information` (`Debug` when set locally) | compact JSON, `Warning` (set by the tests) | compact JSON, `Information` |
| OTLP export | when `OTEL_EXPORTER_OTLP_ENDPOINT` is set locally (`http://localhost:4317`) | off | when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |
| HSTS + HTTPS redirection | no | yes | yes |
| `exceptionDetails` in 500 responses | yes | no | no |
| OpenAPI + Scalar | yes | yes | no |
| Auth seeding on start | yes; the administrator once `Auth:Seed:AdminEmail` and `Auth:Seed:AdminPassword` are set locally (the README uses `admin@localhost.test`) | no (the test harness seeds after each reset) | yes (roles and permissions; an administrator only when both settings are supplied) |
| JWT signing key (`Auth:Jwt:SigningKeys`) | an ephemeral key when none is configured | an ephemeral key when none is configured | required: the host does not start without one ([Auth module](../modules/auth.md#access-tokens)) |

Local URLs: `https://localhost:5001` and `http://localhost:5000`.

## Migrations

`MigrateModuleDatabasesAsync` applies the pending migrations of every context registered with `AddModuleDbContext` (`includeInMigrations: true`, the default), in registration order: `platform` first, then each module. It validates `ConnectionStrings:Database` first, so a missing connection string fails with the options error.

- **Local development:** `Database:ApplyMigrationsOnStartup` is `false` in `appsettings.json`; set it to `true` locally (user secrets, an environment variable, or a git-ignored `appsettings.Development.json`) and the host migrates before it starts serving. Without it, a fresh database stops the host at Auth seeding (`Invalid object name`).
- **Production:** never on start. The API's `migrate` command-line mode (`dotnet TemplateName.Api.dll migrate`, Plan 6) runs the same method, optionally with a DDL-privileged connection string. There are no EF migration bundles (ADR 0006).
- **Adding a migration:** `dotnet ef migrations add {Verb}{What} --project src/Modules/{Module}/TemplateName.Modules.{Module} --startup-project src/Host/TemplateName.Api --context {Module}DbContext --output-dir Infrastructure/Persistence/Migrations`. Never edit a migration that has been applied; add a new one.

## Known limitations

HTTP security:
- When the connection has no remote IP address (a Unix domain socket), the forwarded-headers middleware accepts `X-Forwarded-For` from it whatever the trusted lists say. Do not listen on a socket that untrusted clients can reach.
- Behind a TLS-terminating proxy that does not send `X-Forwarded-Proto`, HTTPS redirection cannot work out the port; set `HTTPS_PORT` (or `https_port`) or forward the scheme.
- CORS preflight requests are answered before the rate limiter, so they are not counted. 401 and 403 responses are counted: the limiter (slot 9a) runs before authorization (slot 10), so an anonymous request to a protected or unknown route and a signed-in request without the permission each use up the caller's allowance (an anonymous caller is partitioned by address, a signed-in one by `user:{sub}`).
- An anonymous request to a route that does not exist gets 401, not 404: the fallback policy also covers requests that matched no endpoint, so anonymous callers cannot probe which routes exist. A signed-in caller gets 404.
- IPv6 and IPv4-mapped addresses are partition keys as plain strings, so one client can appear as two addresses.
- The limiter keeps its counters in memory per instance: with N instances a client gets up to N × `GlobalPermitLimit` (a Redis-backed limiter is Plan 5).
- A bare `Accept-Language: zh` gets English, because `zh` is not a supported culture and `zh-Hans` is its child, not its parent.

Notifications:
- The SignalR hub pushes only to the connections on the instance that delivered the notification, and the per-user hub ticket limit is counted per instance (no Redis backplane until Plan 5); the inbox endpoints are always complete. More: [Notifications: Operations and known limits](../modules/notifications.md#operations-and-known-limits).
- A rejected sender address (a wrong `Notifications:Email:From`) fails every email; it is retried for about 81 minutes before the emails are dead-lettered: watch the retry Warnings and the dead-letter metric.

Observability:
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` is `1.19.1-beta.1`, the only prerelease dependency: no stable release exists ([ADR 0013](../adr/0013-prerelease-ef-core-opentelemetry-instrumentation.md)). The SqlClient instrumentation is stable.
- With OTLP export on, traces record request URLs, so secrets in query strings reach the trace store. Never put secrets in query strings.
- `RequestActivityBackgroundService` keeps a listener on ASP.NET Core's activity source, so every request has a W3C trace id (`traceId`, `X-Trace-Id`) even when OpenTelemetry is off.

Idempotency: see [infrastructure-common](../building-blocks/infrastructure-common.md#idempotency).
