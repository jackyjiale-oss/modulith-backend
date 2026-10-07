# Infrastructure.Common (`TemplateName.Infrastructure.Common`)

## Purpose

The infrastructure every module shares: module `DbContext` registration and conventions on SQL Server with audit and soft-delete interceptors (ADR 0006), migrations, the Dapper connection factory, the per-module transactional outbox (ADR 0007), optimistic-concurrency handling, and `Idempotency-Key` support with its own `platform` schema. It depends on SharedKernel, Application.Common, EF Core SQL Server, Dapper and ASP.NET Core.

Code: `src/BuildingBlocks/TemplateName.Infrastructure.Common/`.

## Public types and extension methods

| Extension / type | Does |
|---|---|
| `services.AddInfrastructureCommon(configuration)` | Registers `TimeProvider.System` (if none), binds `ConnectionStrings`, `Outbox` and `Idempotency` (validated on start) and `Database`, the `IDbConnectionFactory`, the EF interceptors, `ConcurrencyExceptionHandler`, the `platform` context and `InfrastructureErrorMessages`, and Dapper's `DateOnly` type handler. Call it before `AddWebCommon` (so the concurrency handler runs before the global one) and before any module. |
| `services.AddModuleDbContext<TContext>(schema, connectionStringName = "Database", includeInMigrations = true)` | Registers a module context on SQL Server: migrations history `__EFMigrationsHistory` in `schema`, retry on transient failures, the audit, soft-delete and domain-events-to-outbox interceptors, a `ready` health check named after the schema, and (by default) a place in `MigrateModuleDatabasesAsync`. The connection string is read when a context is created. |
| `configurationBuilder.ApplyDefaultConventions()` | In `ConfigureConventions`: every `DateTime`/`DateTime?` is `datetime2(3)`, stored as UTC (local values converted, unspecified taken as UTC) and read back as `DateTimeKind.Utc`. |
| `modelBuilder.ApplySoftDeleteQueryFilters()` | At the end of `OnModelCreating`: the named query filter `ModelConventions.SoftDeleteFilterName` (`"SoftDelete"`, `!IsDeleted`) on every root `ISoftDeletable` entity. See deleted rows with `IgnoreQueryFilters(["SoftDelete"])`. |
| `serviceProvider.MigrateModuleDatabasesAsync(cancellationToken)` | Applies pending migrations of every registered context in registration order (`platform` first); validates `ConnectionStrings:Database` first. |
| `modelBuilder.ApplyOutbox()` | Maps `OutboxMessages` and `OutboxMessageConsumers` into the context's default schema; call after `HasDefaultSchema`. |
| `services.AddOutbox<TContext>(domainEventsAssembly)` | Registers the outbox of `TContext`: the event-type map (every `IDomainEvent` in the assembly), a singleton `OutboxDispatcher<TContext>` and `OutboxBackgroundService<TContext>`. |
| `OutboxDispatcher<TContext>.ProcessBatchAsync(cancellationToken)` | Claims and dispatches one batch; returns the number claimed. Tests call it directly with `Outbox:Enabled = false`. |
| `OutboxRetryPolicy.NextAttemptAt(attemptCount, utcNow, maxAttempts)` | 5 s, 30 s, 2 min and 10 min after attempts 1 to 4, then 1 h; `null` (abandon) once `maxAttempts` is reached. |
| `OutboxOptions`, `IdempotencyOptions`, `DatabaseOptions` | The bound option classes. |
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

`ConnectionStrings:Database`, `Database:ApplyMigrationsOnStartup`, `Outbox:*` and `Idempotency:TimeToLive`, with defaults and validation, are listed in [`docs/services/api.md`](../services/api.md#configuration).

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
- After `MaxAttempts` failures a message is abandoned: it stays with `ProcessedAt` null and its last `Error`. Processed rows are not purged yet (cleanup job, Plan 5).

## Idempotency

`Idempotency-Key` makes an unsafe request safe to retry (review 2.12). Only endpoints marked `WithIdempotency()` are affected; a request without the header runs as usual.

1. The key must be one header value of 1 to 100 characters, else 400 `idempotency.invalid_key`. Keys are case-sensitive.
2. The scope is the signed-in user's id, or `anonymous`. The request hash is the SHA-256 of the method, path and body.
3. The first request inserts an in-progress row in `platform.IdempotencyKeys`; its primary key (`Scope`, `Key`) makes a concurrent duplicate fail, so the endpoint runs once and the duplicate gets 409 `idempotency.in_progress`.
4. A response below 500 is buffered, stored (status, content type, `Location`, body) and then sent. A retry with the same key and body within `Idempotency:TimeToLive` gets the stored response with `Idempotency-Replayed: true`; with a different body, 422 `idempotency.key_reused`; while the first is still running, 409 `idempotency.in_progress`.
5. A 5xx response or an exception deletes the row, so the client can retry. A key whose row has expired runs again.

`platform.IdempotencyKeys`: `Scope nvarchar(100)`, `Key nvarchar(100)` (binary collation `Latin1_General_100_BIN2`), `RequestHash varbinary(32)`, `StatusCode`, `ContentType`, `ResponseBody`, `Location`, `CreatedAt`, `ExpiresAt`; primary key (`Scope`, `Key`). Migration: `InitialPlatform` (`Idempotency/Migrations/`). The `platform` schema also gets a `ready` health check.

Known limitations:
- A replayed 4xx ProblemDetails keeps the **original** `traceId` in its body; the `X-Trace-Id` header is the new request's.
- `anonymous` is one namespace shared by every unauthenticated caller, so two anonymous clients can collide on a key.
- Stored response bodies may hold personal data. They live until `TimeToLive` expires, and expired rows are deleted only when the same key is reused; a purge job comes with Plan 5.
- The request hash ignores the query string, and stored responses have no size limit.
- In-progress rows carry no owner token: if a request fails in a way that neither stores nor deletes its row (for example the store itself fails, or the process dies), the key answers 409 until it expires.
- The OpenAPI document does not yet describe the `Idempotency-Key` header or the 400/409/422 responses on idempotent endpoints.

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
```

## Tests

Unit: `tests/TemplateName.UnitTests/Infrastructure/` (UTC converter, retry policy, migrations, concurrency handler, idempotency options). Integration (SQL Server in Testcontainers): `Persistence/PersistenceTests`, `Outbox/OutboxTests` (including concurrent dispatchers and lost leases) and `Idempotency/IdempotencyTests`.
