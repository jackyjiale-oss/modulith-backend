# Infrastructure.Common (`TemplateName.Infrastructure.Common`)

## Purpose

The infrastructure every module shares: module `DbContext` registration and conventions on SQL Server with audit and soft-delete interceptors (ADR 0006), migrations, the Dapper connection factory, the Data Protection based secret protector (ADR 0017), the per-module transactional outbox (ADR 0007), the in-process integration event publisher and the per-module inbox (ADR 0018), optimistic-concurrency handling, `Idempotency-Key` support with its own `platform` schema, the recurring-job scheduler (state in `platform.JobStates`) and the retention cleanup of the outbox, the inbox and the idempotency keys that runs on it (ADR 0026, 0028), and the standard resilience pipelines for outbound HTTP calls (ADR 0029). It depends on SharedKernel, Application.Common, EF Core SQL Server, Dapper, `Microsoft.Extensions.Http.Resilience` (Polly) and ASP.NET Core.

Code: `src/BuildingBlocks/TemplateName.Infrastructure.Common/`.

## Public types and extension methods

| Extension / type | Does |
|---|---|
| `services.AddInfrastructureCommon(configuration)` | Registers `TimeProvider.System` (if none), binds `ConnectionStrings`, `Outbox`, `Idempotency` and `Retention` (validated on start) and `Database`, the `IDbConnectionFactory`, the EF interceptors, `ConcurrencyExceptionHandler`, the `platform` context and `InfrastructureErrorMessages`, Dapper's `DateOnly` type handler, the singleton `IIntegrationEventPublisher` (`InProcessIntegrationEventPublisher`), the scoped `IOutboxMessageContext`, the singleton `ISecretProtector` (`DataProtectionSecretProtector`) with `AddDataProtection()` and no key store, and the recurring-job scheduler (`Jobs` options validated on start, each job's `Jobs:{Name}` options checked on start, the singleton `RecurringJobRunner`, `RecurringJobBackgroundService` and the `TemplateName.Platform` meter's instruments) with the hourly job `IdempotencyKeysCleanup`. `Retention` is also checked against `Outbox:MaxAttempts` on start (the inbox window, below). Call it before `AddWebCommon` (so the concurrency handler runs before the global one) and before any module. |
| `services.AddModuleDbContext<TContext>(schema, connectionStringName = "Database", includeInMigrations = true)` | Registers a module context on SQL Server: migrations history `__EFMigrationsHistory` in `schema`, retry on transient failures, the audit, soft-delete and domain-events-to-outbox interceptors, a `ready` health check named after the schema, and (by default) a place in `MigrateModuleDatabasesAsync`. The connection string is read when a context is created. |
| `configurationBuilder.ApplyDefaultConventions()` | In `ConfigureConventions`: every `DateTime`/`DateTime?` is `datetime2(3)`, stored as UTC (local values converted, unspecified taken as UTC) and read back as `DateTimeKind.Utc`. Every `DateTimeOffset`/`DateTimeOffset?` gets the same `datetime2(3)` column holding its UTC instant and is read back with offset zero (the original offset is not kept), so Dapper reads both kinds as UTC `DateTime`. |
| `modelBuilder.ApplySoftDeleteQueryFilters()` | At the end of `OnModelCreating`: the named query filter `ModelConventions.SoftDeleteFilterName` (`"SoftDelete"`, `!IsDeleted`) on every root `ISoftDeletable` entity. See deleted rows with `IgnoreQueryFilters(["SoftDelete"])`. |
| `serviceProvider.MigrateModuleDatabasesAsync(cancellationToken)` | Applies pending migrations of every registered context in registration order (`platform` first); validates `ConnectionStrings:Database` first. |
| `modelBuilder.ApplyOutbox()` | Maps `OutboxMessages` and `OutboxMessageConsumers` into the context's default schema; call after `HasDefaultSchema`. |
| `services.AddOutbox<TContext>(domainEventsAssembly)` | Registers the outbox of `TContext`: the event-type map (every `IDomainEvent` in the assembly), a singleton `OutboxDispatcher<TContext>` and `OutboxBackgroundService<TContext>`. |
| `OutboxDispatcher<TContext>.ProcessBatchAsync(cancellationToken)` | Claims and dispatches one batch; returns the number claimed. Tests call it directly with `Outbox:Enabled = false`. |
| `modelBuilder.ApplyInbox()` | Maps `InboxMessages` (`MessageId`, `Consumer` up to 400 characters, `ProcessedAt`; primary key `PK_InboxMessages` on `MessageId`, `Consumer`, 816 bytes, under SQL Server's 900-byte clustered key limit) into the context's default schema; call after `HasDefaultSchema`. |
| `services.AddInbox<TContext>()` | Registers `Inbox<TContext>` as scoped and the daily job `{Context}InboxCleanup` (`NotificationsInboxCleanup`) that deletes rows processed more than `Retention:Inbox` ago. |
| `Inbox<TContext>` | `HasProcessedAsync(messageId, consumer, cancellationToken)`; `Record(messageId, consumer)` stages the row for the caller's own save; static `IsDuplicate(DbUpdateException)` is true only for SQL Server error 2627 or 2601 on `PK_InboxMessages`. |
| `InboxMessage` | The inbox row; `ConsumerMaxLength` 400 (longer names are rejected by `Inbox<TContext>` with `ArgumentOutOfRangeException`). |
| `OutboxRetryPolicy.NextAttemptAt(attemptCount, utcNow, maxAttempts)` | 5 s, 30 s, 2 min and 10 min after attempts 1 to 4, then 1 h; `null` (abandon) once `maxAttempts` is reached. `RetryWindow(maxAttempts)` is the span of those delays: 12 min 35 s for the default 5, one more hour for each attempt above 5. |
| `IRecurringJob`, `JobResult` | A recurring job: `Name` (unique, PascalCase letters and digits, its row and its `Jobs:{Name}` section), `DefaultInterval`, `RunAsync(cancellationToken)` returning `JobResult(Processed)`. |
| `services.AddRecurringJob<TJob>()` | Registers `TJob` (scoped, resolved in its own scope for every run) with the scheduler. The same type twice is registered once; two types with one name, or an invalid name, make the runner throw `InvalidOperationException` on first use, and so stop the host at start. |
| `RecurringJobRunner.RunDueAsync(include, cancellationToken)` | Claims and runs every enabled due job that `include` accepts (all when null), one at a time; returns the number run. The background service calls it with `null`; tests pass a job-name filter with `Jobs:Enabled = false`. |
| `BatchedCleanupJob` | Base class of the retention cleanup jobs: a subclass supplies `Name`, `DefaultInterval` (public) and the `protected` `RetentionPeriod` (`null` or zero turns the job off) and `DeleteSql` (a `DELETE TOP (@Batch)` statement over `@Cutoff` that reports the rows it deleted); the base class computes `cutoff = now - retention`, runs the statement with `NOCOUNT OFF` (a session default of `NOCOUNT ON` cannot hide the row count), repeats it and returns `JobResult(deleted)`. A subclass can name the table of an entity with `DelimitedTable<TEntity>()`, the job's name part with `ContextNameOf(type)`, and replace the cutoff by overriding `CutoffAt(utcNow)` (the idempotency job does). See [Cleanup jobs](#cleanup-jobs). |
| `OutboxOptions`, `IdempotencyOptions`, `RetentionOptions`, `DatabaseOptions`, `JobsOptions` | The bound option classes. |
| `services.AddExternalHttpClient(name, pipeline = ResiliencePipelines.Default)` | Registers the named `HttpClient` behind a resilience pipeline (total timeout, retries with exponential backoff and jitter, circuit breaker, attempt timeout) whose settings are `Resilience:Pipelines:{pipeline}`, validated on start. Returns the `IHttpClientBuilder`, so `AddTypedClient` and `ConfigureHttpClient` chain as usual. See [HTTP resilience](#http-resilience). |
| `ResiliencePipelines` | The constants `Default` (`"external-default"`), `Critical` (`"external-critical"`) and `Hibp` (`"hibp"`). |
| `endpoint.WithIdempotency()` | Opts an endpoint in to `Idempotency-Key` handling. |
| `app.UseIdempotency()` | The idempotency middleware: after `UseRouting` and authentication, before the endpoints. |

Error codes (messages in `Resources/InfrastructureErrorMessages.resx`, `.ms.resx`, `.zh-Hans.resx`):

| Code | HTTP | Message (en) |
|---|---|---|
| `concurrency.conflict` | 409 | The resource was changed by another request. Reload it and try again. |
| `idempotency.invalid_key` | 400 | The Idempotency-Key header must be a single value of 1 to 100 characters. |
| `idempotency.in_progress` | 409 | A request with this Idempotency-Key is still being processed. Retry later. |
| `idempotency.key_reused` | 422 | This Idempotency-Key was already used for a different request. |

## Configuration

`ConnectionStrings:Database`, `Database:ApplyMigrationsOnStartup`, `Outbox:*`, `Idempotency:TimeToLive`, `Idempotency:InProgressTimeout`, `Retention:*`, `Jobs:*` (with `Jobs:{Name}:Enabled` and `Interval`) and `Resilience:Pipelines:{pipeline}:*`, with defaults and validation, are listed in [`docs/services/api.md`](../services/api.md#configuration).

## Persistence

- **Interceptors** (added to every module context, in this order): `AuditableEntityInterceptor` stamps `CreatedAt/By` on add and `UpdatedAt/By` on update; `SoftDeleteInterceptor` turns the deletion of an `ISoftDeletable` entity into an update of only `IsDeleted`, `DeletedAt` and `DeletedBy` (so a soft delete does not touch `UpdatedAt/By`); `DomainEventsToOutboxInterceptor` writes domain events to the outbox. Time comes from `TimeProvider`, the user from `ICurrentUser`.
- **Soft delete covers the entity only.** Owned or cascade-deleted dependents that EF deletes with it are hard-deleted unless they are `ISoftDeletable` themselves; owned types cannot carry the query filter.
- **Dapper bypasses EF query filters.** Every Dapper query on a soft-deletable table adds `IsDeleted = 0` itself, and each Dapper query handler has an integration test proving deleted rows stay hidden (ADR 0006).
- **Dapper reads `datetime2` as `DateTimeKind.Unspecified`;** the columns hold UTC, so query handlers mark them `DateTimeKind.Utc` before returning them. `DateOnly` works through the registered type handler.
- **Concurrency:** a `DbUpdateConcurrencyException` (for example a stale `RowVersion`) becomes 409 `concurrency.conflict`.
- **Retries:** contexts use SQL Server's retrying execution strategy; a handler that opens its own transaction must run it through `Database.CreateExecutionStrategy()`.

## Outbox

Each module context that raises domain events maps its own `OutboxMessages` / `OutboxMessageConsumers` (ADR 0007):

- Domain events are serialized to `OutboxMessages` in the same `SaveChanges` as the aggregate, then cleared from it.
- `OutboxBackgroundService<TContext>` (while `Outbox:Enabled`) drains the due messages, then waits `PollingInterval`. A failed poll is logged and retried at the next tick, so a database outage does not stop the host.
- Each batch is claimed with `UPDLOCK, READPAST` and a lease (`LockedUntil = now + LeaseDuration`); several dispatchers or instances skip each other's rows. A dispatcher whose lease ran out starts no further message, and its final update applies only while it still holds the lease.
- **Delivery is at least once per handler:** a consumer row records each handler that succeeded, so a retry runs only the failed ones, but a crash or an expired lease can run a handler twice. Handlers must be idempotent.
- **No ordering guarantee:** failed messages are retried later while newer ones go ahead.
- Handlers resolve the same scoped `DbContext` as the dispatcher; changes they leave tracked are saved with their consumer row, atomically.
- After `MaxAttempts` failures a message is abandoned: it stays with `ProcessedAt` null and its last `Error`. Processed rows are deleted by the outbox cleanup job after `Retention:Outbox` (7 days); abandoned messages are never deleted, and are counted in a gauge ([Cleanup jobs](#cleanup-jobs)).
- Each message's scope carries an `IOutboxMessageContext` with the message's `Id` and `OccurredAt`, set before the handlers run, so a handler sees the same id on every retry.

## Recurring jobs

A job implements `IRecurringJob` and is registered with `AddRecurringJob<TJob>()` by the module (or building block) that owns its data. `RecurringJobBackgroundService` ticks every `Jobs:TickInterval` (30 s) while `Jobs:Enabled`, and calls `RecurringJobRunner.RunDueAsync`:

- **Start.** Whether or not `Jobs:Enabled` is set, the background service checks every registered job on host start: a duplicate or invalid name, or a `Jobs:{Name}` value that cannot be read (`Interval = 12 hours`) or is out of range, stops the host with a message naming each job's section. If a job's options still fail to resolve later (a runner used without that check), that job is skipped with an Error log naming it, and the other jobs run.

- **State.** Each job has a row in `platform.JobStates` (`Name` primary key, `NextRunAt`, `LockedUntil`, `LastStartedAt`, `LastFinishedAt`, `LastStatus` `Succeeded` or `Failed`, `LastError` up to 2000 characters, `LastProcessed`, `RowVersion`). A job without a row gets one, due at once, the first time a runner considers it; a disabled job (`Jobs:{Name}:Enabled = false`) never gets one and never runs.
- **Claim.** One job per claim, the most overdue first: an `UPDATE ... OUTPUT` over a `UPDLOCK, READPAST` CTE (the outbox's statement shape) of a row with `NextRunAt <= now` and no live lease, which sets `LockedUntil = now + Jobs:LeaseDuration` (15 min). Instances ticking together skip each other's rows, so a job runs on one instance at a time; a crashed instance's lease simply expires and the job is claimed again. Each pass runs a job at most once.
- **Run.** The job is resolved in its own scope and runs under a token cancelled when the lease ends (or the host stops). A job that throws, or is cancelled at the lease end, is logged and recorded `Failed` with its error; the other jobs still run.
- **Finish.** One update writes the outcome and `NextRunAt = finish + interval` (`Jobs:{Name}:Interval`, else the job's `DefaultInterval`), so a missed interval runs once rather than in a burst. The update matches the lease the runner claimed, so a runner whose lease ran out never overwrites the outcome of the runner that re-claimed the job (it logs a warning). If the host stops mid-run nothing is written and the job runs again once the lease expires.
- **Failures of the scheduler itself** (the database is unreachable) are logged once per tick; the next tick tries again.
- **Metrics** on the meter `TemplateName.Platform` (exported with the host's OpenTelemetry metrics): `platform.jobs.runs{job,outcome}` (`succeeded`, `failed`), `platform.jobs.duration{job}` in seconds, `platform.jobs.processed{job}` (the `JobResult.Processed` of successful runs).
- **Cheap constructors.** On host start every registered job is resolved once, in a scope, to read its `Name` and `DefaultInterval`; a job's constructor must not open a connection or do work.

### Cleanup jobs

A cleanup deletes what has outlived its retention (ADR 0028). The building block supplies the shape, `BatchedCleanupJob`, and the jobs for what it owns; each module derives its own jobs for its own tables.

- **Bounded.** The job repeats its `DELETE TOP (@Batch)` statement with `@Batch = Retention:BatchSize` (1000) and `@Cutoff = now - retention` until a batch deletes fewer rows than `@Batch` or `Jobs:MaxBatchesPerRun` (100) batches have run. A backlog drains over several runs; each batch is its own transaction.
- **Strictly older.** A row goes only when its age column is before the cutoff, so a row exactly at the retention stays until the next run. `now` is `TimeProvider`.
- **Off.** A retention of `null` (an empty value in configuration) or zero turns the job off: a run returns `JobResult(0)`. The scheduler's own `Jobs:{Name}:Enabled = false` stops it altogether.
- **Names.** PascalCase, without dots: `{Context}OutboxCleanup` and `{Context}InboxCleanup` where `{Context}` is the context's name without `DbContext` (`AuthOutboxCleanup`, `SampleOutboxCleanup`, `NotificationsInboxCleanup`), and `IdempotencyKeysCleanup`.
- **Outbox cleanup.** `AddOutbox<TContext>()` also registers `OutboxCleanupJob<TContext>` (daily). Each batch deletes `OutboxMessages` with `ProcessedAt < cutoff` and the `OutboxMessageConsumers` of those messages in the same transaction (no foreign key joins the tables, so nothing cascades). The predicate is served by the filtered index on `ProcessedAt` (`WHERE ProcessedAt IS NOT NULL`); a test reads the statement's execution plan.
- **Dead letters stay.** Pending messages and abandoned ones (`ProcessedAt IS NULL`, `AttemptCount >= Outbox:MaxAttempts`) are never deleted. After every run, whether or not the retention is on, the job sets the gauge `platform.outbox.abandoned{context}` (meter `TemplateName.Platform`; `context` is the context's name without `DbContext`) to the number of abandoned messages. The gauge reports the last value until the next run; alert on it being above zero.
- **Inbox cleanup.** `AddInbox<TContext>()` also registers `InboxCleanupJob<TContext>` (daily), which deletes `InboxMessages` with `ProcessedAt < cutoff`, `cutoff = now - Retention:Inbox` (7 days by default, so a row processed exactly 7 days ago stays until the next run). The predicate is served by the index on `ProcessedAt`; a test reads the statement's execution plan. A retention of `null` or zero turns it off; the consumers of the module keep working, the table just grows.
- **Idempotency cleanup.** `AddInfrastructureCommon` registers `IdempotencyCleanupJob` (hourly), which deletes `platform.IdempotencyKeys` with `ExpiresAt < now`: stored responses past `Idempotency:TimeToLive` and in-progress leases that ran out. A key exactly at its expiry stays until the next run. The middleware already treats an expired key as free (the next request with it runs again), so deleting one changes no outcome; it keeps stored responses from outliving their time to live. The expiry is in the row, so no `Retention` value applies and none turns the job off; `Jobs:IdempotencyKeysCleanup:Enabled = false` does. The predicate is served by the index on `ExpiresAt`.
- **Inbox window.** The outbox retries a failed message 5 s, 30 s, 2 min and 10 min after attempts 1 to 4 and 1 h after every later attempt, up to `Outbox:MaxAttempts`. With the default 5 the retries span 12 min 35 s (the 1 h step is never reached); each attempt above 5 adds an hour (`OutboxRetryPolicy.RetryWindow`). An inbox row deleted before a redelivery would let a consumer run twice, so `Retention:Inbox` is checked on start twice: it may not be above zero and below one day, and it must be longer than the retry window of `Outbox:MaxAttempts` (`InboxWindowValidator`). The one-day floor outlasts the default and every `MaxAttempts` up to 28 (23 h 12 min 35 s); from 29 the retries run longer than a day, and with `Retention:Inbox` at 1 day the host refuses to start. A deployment that raises `Outbox:MaxAttempts` far must raise `Retention:Inbox` to match (the 7-day default covers every allowed value, 100 attempts being just under 4 days). Zero or `null` turns the inbox cleanup off, which skips the check (ADR 0028).

## Integration events and the inbox

Modules tell each other about facts with integration events, published in process from the outbox (ADR 0018):

- **Publishing.** A domain event handler in the publishing module builds a `{Noun}{PastTenseVerb}IntegrationEvent` from its `.Contracts` project with `Id = IOutboxMessageContext.MessageId` and calls `IIntegrationEventPublisher.PublishAsync`. `InProcessIntegrationEventPublisher` runs the handlers `AddApplicationHandlers` recorded for the event type (`IntegrationEventHandlerRegistration`) in registration order, each resolved as its own type and run in its own async scope (its own `DbContext`), so a handler that fails, or cannot even be created, does not stop the others. All of them run even when one fails; then one failure is rethrown as it is and several as an `AggregateException`, so the outbox records the publishing handler as failed and retries it with the same id: the consumers that succeeded run again and return after one inbox query, and the failing one gets another attempt on the outbox's backoff schedule. Cancellation is rethrown at once.
- **Consuming.** The consuming module calls `ApplyInbox()` and `AddInbox<TContext>()`. A handler checks `HasProcessedAsync(event.Id, consumer)`, writes its rows, calls `Record(event.Id, consumer)` and saves once: the inbox row commits with its rows or not at all. If the save throws a `DbUpdateException` for which `Inbox<TContext>.IsDuplicate` is true, a concurrent delivery already processed the event: clear the change tracker and return. Any other failure propagates, and the publish is retried. The consumer name is stable (by convention the handler's `FullName`); renaming it makes the next delivery of an old event run again.
- **Consumers only write rows.** They run inside the publisher's outbox dispatch, so slow work (sending mail, calling another system) is queued for a worker of the consuming module.
- **No ordering**, as for the outbox; processed inbox rows are deleted by the [inbox cleanup](#cleanup-jobs) after `Retention:Inbox`.

## Secret protector

`ISecretProtector` (`Application.Common.Security`) is implemented by `internal sealed DataProtectionSecretProtector` (`Security/`) with the Data Protection purpose `TemplateName.Secrets.v1`, shared by every module so that one module can unprotect what another protected (ADR 0017). `AddInfrastructureCommon` registers `AddDataProtection()` **without** persistence: the module that owns the key ring adds `SetApplicationName` and the key store (Auth: `PersistKeysToDbContext<AuthDbContext>` into `auth.DataProtectionKeys`). A host that registers no key store gets Data Protection's default (keys on the local file system), which does not survive several instances; a module that stores protected values for others must therefore register one. Changing the purpose makes every value protected so far unreadable. The purpose was `TemplateName.Auth.Secrets.v1` before the protector moved here, so values protected under it (pending outbox messages in a developer database) can no longer be read.

## Idempotency

`Idempotency-Key` makes an unsafe request safe to retry, including two copies of it arriving at the same time. Only endpoints marked `WithIdempotency()` are affected; a request without the header runs as usual.

1. The key must be one header value of 1 to 100 characters, else 400 `idempotency.invalid_key`. Keys are case-sensitive.
2. The scope is the signed-in user's id, or `anonymous`. The request hash is the SHA-256 of the method, path and body.
3. The first request inserts an in-progress row in `platform.IdempotencyKeys`; its primary key (`Scope`, `Key`) makes a concurrent duplicate fail, so the endpoint runs once and the duplicate gets 409 `idempotency.in_progress`. The row is a **lease**: it expires `Idempotency:InProgressTimeout` (five minutes by default) after the request started, and it carries the request's owner token `LockId`, a fresh GUID.
4. A response below 500 is buffered, stored (status, content type, `Location`, body) and then sent; storing it moves `ExpiresAt` to `Idempotency:TimeToLive` from then. A retry with the same key and body within that time gets the stored response with `Idempotency-Replayed: true`; with a different body, 422 `idempotency.key_reused`; while the first is still running, 409 `idempotency.in_progress`.
5. A 5xx response or an exception deletes the row, so the client can retry. A key whose row has expired (a stored response past `TimeToLive`, or an in-progress row past its lease) runs again: the next request deletes the expired row, only while it is still expired, and inserts its own.
6. Storing the response and deleting the row both match `LockId`, so a request that outlived its lease cannot overwrite or release the row of the request that took the key over (it logs a warning instead). If the insert fails on the primary key, the row is read back: when its `LockId` is the request's own (the insert committed, then a transient error made EF's retry insert again), the request carries on as the owner; otherwise it is answered from that row as in step 4.

`platform.IdempotencyKeys`: `Scope nvarchar(100)`, `Key nvarchar(100)` (binary collation `Latin1_General_100_BIN2`), `RequestHash varbinary(32)`, `StatusCode`, `ContentType`, `ResponseBody`, `Location`, `CreatedAt`, `ExpiresAt`, `LockId uniqueidentifier NULL`; primary key (`Scope`, `Key`). Migrations: `InitialPlatform`, `AddIdempotencyLockId` (`Idempotency/Migrations/`). `LockId` is nullable so the column can be added to a populated table: completed rows written before it need no owner, and in-progress rows written before it (no owner token, expiry still `TimeToLive`) cannot be stored or released by the new code, so they answer 409 until that original expiry and are then reclaimed. The `platform` schema also gets a `ready` health check.

Known limitations:
- A replayed 4xx ProblemDetails keeps the **original** `traceId` in its body; the `X-Trace-Id` header is the new request's.
- `anonymous` is one namespace shared by every unauthenticated caller, so two anonymous clients can collide on a key.
- Stored response bodies may hold personal data. They live until `TimeToLive` expires; the hourly job `IdempotencyKeysCleanup` then deletes them (an expired row is also deleted when the same key is reused first), so one can outlive its time to live by up to an hour.
- The request hash ignores the query string, and stored responses have no size limit.
- A request that runs longer than `InProgressTimeout` loses its lease: a retry after that runs the endpoint a second time, and the first request's response is then neither stored nor replayed. Keep `InProgressTimeout` above the slowest idempotent request. Leases compare against the clock of whichever instance handles the retry, so clock skew between instances shortens or lengthens them.
- If a request fails in a way that neither stores nor deletes its row (the store itself fails, or the process dies), the key answers 409 until the lease ends, then runs again.
- The OpenAPI document does not yet describe the `Idempotency-Key` header or the 400/409/422 responses on idempotent endpoints.

## HTTP resilience

An outbound HTTP call fails in ways a request of ours never should: a server that hangs, a 503 that passes in a second, a provider that is down for minutes. `AddExternalHttpClient` gives a named client the same answer to all three, from `Microsoft.Extensions.Http.Resilience` (Polly; ADR 0029) instead of a hand-written loop per caller.

A call goes through four strategies, outermost first:

1. **Total timeout** (`TotalTimeout`): everything below, the delays between attempts included, ends when it runs out.
2. **Retry** (`Retries`, `BaseDelay`): exponential backoff with jitter; none is added when `Retries` is 0. Only a safe method is retried: a POST, PUT, PATCH, DELETE or CONNECT is sent once, whatever the answer, unless the pipeline sets `RetryUnsafeMethods` (below). A `Retry-After` header on a retried answer is honoured (the total timeout still bounds it).
3. **Circuit breaker** (`Breaker:*`): it opens when `FailureRatio` of the attempts in the last `SamplingDuration` failed and there were at least `MinimumThroughput` of them. While open it rejects calls at once, without reaching the server, for `BreakDuration`; then it lets one call through (half-open) and closes again if that call succeeds. It counts attempts, not calls, so a call with 3 retries adds up to 4.
4. **Attempt timeout** (`AttemptTimeout`): one attempt that runs longer is cancelled and counts as a failure (and is retried).

**What counts as a failure** is the standard predicate of the package, for the retry and the breaker alike: a 5xx status, 408, 429, an `HttpRequestException` (network error) and the attempt timeout. Any other 4xx status is a final answer: it is returned at once, never retried and never held against the breaker. After the last retry the last response is returned as it is (a 503 stays a 503 for the caller to read); only a timeout or an open breaker throws.

**What the caller sees.** `HttpClient.SendAsync` can throw, besides the usual `HttpRequestException`, `Polly.Timeout.TimeoutRejectedException` (the total timeout, or the attempt timeout of the last attempt) and `Polly.CircuitBreaker.BrokenCircuitException` (the breaker is open). Both derive from `Polly.ExecutionRejectedException`. A caller that must keep working without the service catches that base type, as the Auth breached-password check does (it also keeps its own 2 s budget, and a cancelled caller token still propagates).

**The built-in pipelines** (every key is `Resilience:Pipelines:{pipeline}:{key}`; a host can change any of them):

| Key | `external-default` | `external-critical` | `hibp` | Validation (on start) |
|---|---|---|---|---|
| `TotalTimeout` | `00:00:30` | `00:01:30` | `00:00:02` | above zero |
| `AttemptTimeout` | `00:00:10` | `00:00:10` | `00:00:01` | above zero, not above `TotalTimeout` |
| `Retries` | `3` | `5` | `1` | 0 to 10 |
| `BaseDelay` | `00:00:02` | `00:00:02` | `00:00:00.2` | above zero |
| `RetryUnsafeMethods` | `false` | `false` | `false` | a boolean |
| `Breaker:FailureRatio` | `0.5` | `0.5` | `0.5` | above 0, at most 1 |
| `Breaker:MinimumThroughput` | `10` | `10` | `10` | at least 2 (Polly's minimum) |
| `Breaker:SamplingDuration` | `00:00:30` | `00:00:30` | `00:00:30` | at least `00:00:00.5` |
| `Breaker:BreakDuration` | `00:00:30` | `00:00:30` | `00:00:30` | at least `00:00:00.5` |

A pipeline name that is not built in starts from the `external-default` values. The delays are jittered: the five delays of `external-critical` add up to between about 23 s and 46 s, which is why its total is 90 s. A pipeline of your own that raises `Retries` must raise `TotalTimeout` with it (and keep `AttemptTimeout` within it), or the total cuts the last retries short.

**Unsafe methods are not retried.** A POST, PUT, PATCH, DELETE or CONNECT is sent once on every pipeline, even when the answer is a 503 or the connection drops, because a blind repeat can do the work twice (a second SMS, a second charge). The breaker still counts their failures. Set `Resilience:Pipelines:{pipeline}:RetryUnsafeMethods` to `true` only for a pipeline whose endpoints are idempotent or carry an idempotency key the provider honours.

**Time.** The strategies measure time with the registered `TimeProvider`, so a test drives a timeout, a delay or the break with a `FakeTimeProvider`. The client has no `HttpClient.Timeout` of its own (`Timeout.InfiniteTimeSpan`): the total timeout is the limit.

**Metrics.** Polly's meter `Polly` is exported with the host's OpenTelemetry metrics (`AddObservability` adds it next to `TemplateName.Platform`, when an OTLP endpoint is set): the retry, breaker and timeout events and the attempt and pipeline durations, tagged with the pipeline and strategy names (`{pipeline}-retry`, `{pipeline}-breaker`, `{pipeline}-total-timeout`, `{pipeline}-attempt-timeout`).

**Not covered.** MailKit SMTP is not HTTP; the email delivery worker keeps its own retry and dead-letter schedule. No pipeline exists yet for an SMS or an email provider (nothing calls one); the next section shows how to add it.

### Add a pipeline for a new provider

A future SMS or email provider that speaks HTTP (so not SMTP) gets a pipeline name of its own, so its settings are its own:

```csharp
// In the provider module's registration: the client "sms-provider" on a pipeline of the same name.
services.AddExternalHttpClient("sms-provider", "sms-provider")
    .ConfigureHttpClient(client => client.BaseAddress = new Uri("https://sms.example.com/"));

// Or share the settings of a built-in pipeline:
services.AddExternalHttpClient("email-api", ResiliencePipelines.Critical);
```

```json
{
  "Resilience": {
    "Pipelines": {
      "sms-provider": { "AttemptTimeout": "00:00:05", "TotalTimeout": "00:00:20", "Retries": 2 }
    }
  }
}
```

A name that is not built in needs no further code: it starts from the `external-default` values and reads only the keys you set. Two clients on one pipeline name share its settings but each has its own breaker, so a provider that is down does not open the circuit of another. Then:

- Catch `ExecutionRejectedException` where a failure must degrade the feature instead of failing the request.
- A provider is called with POST, which the pipeline does not retry. Set `RetryUnsafeMethods` for it only when the call carries an idempotency key the provider honours.
- Add a test like `HibpResilienceTests` (the real caller over the pipeline, a scripted handler and a `FakeTimeProvider`) for a timeout and an open breaker.
- Document the keys in [`docs/services/api.md`](../services/api.md#resilience) and the behaviour in the module's own document.

## How to use from a module

```csharp
// {Module}Module.cs
services.AddModuleDbContext<SampleDbContext>(SampleDbContext.Schema);   // schema "sample"
services.AddOutbox<SampleDbContext>(typeof(SampleModule).Assembly);

// Infrastructure/Persistence/SampleDbContext.cs
internal sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options), IUnitOfWork
{
    internal const string Schema = "sample";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.ApplyDefaultConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SampleDbContext).Assembly);
        modelBuilder.ApplyOutbox();
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}

// Endpoints: opt an unsafe endpoint in to Idempotency-Key
group.MapPost("/", SubmitAsync).WithIdempotency();

// A Dapper query handler
await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
var row = await connection.QuerySingleOrDefaultAsync<LeaveRequestResponse>(
    new CommandDefinition("SELECT … FROM sample.LeaveRequests WHERE Id = @Id AND IsDeleted = 0", new { Id = id }, cancellationToken: cancellationToken));

// Publishing: a domain event handler of the publishing module
await publisher.PublishAsync(new LeaveRequestApprovedIntegrationEvent(messageContext.MessageId, messageContext.OccurredAt, …), cancellationToken);

// Consuming: an integration event handler of another module (its context calls ApplyInbox(); the module calls AddInbox<TContext>())
var consumer = GetType().FullName!;
if (await inbox.HasProcessedAsync(integrationEvent.Id, consumer, cancellationToken))
{
    return;
}

db.Add(/* this module's rows */);
inbox.Record(integrationEvent.Id, consumer);
try
{
    await db.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateException exception) when (Inbox<ReportingDbContext>.IsDuplicate(exception))
{
    db.ChangeTracker.Clear();   // a concurrent delivery processed it first
}
```

## Tests

Unit: `tests/TemplateName.UnitTests/Infrastructure/` (UTC converter, retry policy, migrations, concurrency handler, idempotency options, secret protector including its purpose). Unit: `Application/InProcessIntegrationEventPublisherTests` (a scope and one instance per handler, a handler that cannot be created, failures collected, cancellation, scanning registers each handler once). Integration (SQL Server in Testcontainers): `Persistence/PersistenceTests`, `Outbox/OutboxTests` (including concurrent dispatchers and lost leases), `Outbox/OutboxMessageContextTests`, `Inbox/InboxTests` (including two concurrent records of one event and the 400-character consumer limit) and `Idempotency/IdempotencyTests`. The cleanup jobs have `Outbox/OutboxCleanupJobTests`, `Inbox/InboxCleanupJobTests` and `Idempotency/IdempotencyCleanupJobTests`: the cutoff to the second (a row exactly at the retention stays), retention off, a backlog drained over runs, a session that defaults to `NOCOUNT ON`, the scheduler running the job, and the execution plan of the job's own statement (`SqlExecutionPlan`, `SET SHOWPLAN_XML ON`) using its index. The retention rules, including the inbox window against `Outbox:MaxAttempts`, are in unit `Jobs/RetentionOptionsTests` and `OutboxRetryPolicyTests`. The resilience pipelines are in unit `Http/ResiliencePipelineTests` (a scripted `HttpMessageHandler` and a `FakeTimeProvider`: the retry counts, the retried and the final statuses, unsafe methods not retried unless `RetryUnsafeMethods`, the attempt and total timeouts, the breaker opening, failing fast and half-opening, one breaker per client, the defaults, the configuration override, the validators and the start-up validation); the `hibp` pipeline under the real checker is in `Auth/HibpResilienceTests`.
