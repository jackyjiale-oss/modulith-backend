# 0006. EF Core for writes, Dapper for reads

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
Each module owns its data: one `DbContext` per module, in its own schema, with its own migrations history table (blueprint 7.6, review D1). Writes need change tracking, interceptors (audit stamping, soft delete, and later domain events to the outbox) and atomic `SaveChanges`. Read models are mostly flat projections, list endpoints use keyset pagination (ADR 0010), and some reads call stored procedures or functions. Hand-written SQL is the clearest and fastest fit for those. The review found one trap in mixing the two (Section 2.6): global query filters, such as soft delete and later tenant, apply only to EF queries, so a Dapper query that forgets `IsDeleted = 0` returns deleted rows.

## Options considered
1. EF Core for reads and writes — one tool, query filters always apply; but list and report queries become LINQ that is harder to tune, and stored procedures and keyset SQL are awkward through EF.
2. Dapper for reads and writes — full control of SQL; but no change tracking, no interceptors, no migrations, so audit, soft delete and the outbox would be written by hand in every module.
3. EF Core for writes, Dapper for reads — EF keeps the aggregate consistency features where they matter, and reads are plain SQL; the soft-delete (and tenant) filter must be repeated by hand in every Dapper query.

## Decision
We chose option 3.

Writes (namespace `TemplateName.Infrastructure.Common.Persistence`):
- A module registers its context with `AddModuleDbContext<TContext>(schema)`: SQL Server, history table `__EFMigrationsHistory` in the module schema, retry on transient failures, the connection string read lazily from `ConnectionStrings`, and a `ready` health check named after the schema. `AddInfrastructureCommon` must be called first; it validates `ConnectionStrings:Database` on start.
- `ConfigureConventions` calls `ApplyDefaultConventions()`: every `DateTime` is `datetime2(3)`, stored as UTC and read back with `DateTimeKind.Utc`.
- `OnModelCreating` ends with `ApplySoftDeleteQueryFilters()`: the EF Core 10 named filter `SoftDelete` (`!IsDeleted`) on every `ISoftDeletable` entity. Code that must see deleted rows calls `IgnoreQueryFilters(["SoftDelete"])`, which leaves other named filters (such as tenant) in force.
- `AuditableEntityInterceptor` stamps `CreatedAt/By` and `UpdatedAt/By`; `SoftDeleteInterceptor` turns a delete into an update of `IsDeleted`, `DeletedAt` and `DeletedBy`. Time comes from `TimeProvider`, the user from `ICurrentUser`.
- A `DbUpdateConcurrencyException` becomes 409 ProblemDetails with code `concurrency.conflict`.

Reads:
- Query handlers open a connection with `IDbConnectionFactory.OpenConnectionAsync` and run Dapper SQL, including stored procedure and function calls.
- **Dapper bypasses EF query filters.** Every Dapper query on a soft-deletable table filters `IsDeleted = 0` (and `TenantId` once tenancy exists) itself, and every Dapper query handler has an integration test proving that soft-deleted rows are invisible.

Migrations:
- Production and test environments deploy migrations as EF migration bundles (`efbundle`) in CD and never migrate on application start. `Database:ApplyMigrationsOnStartup` is `true` only in `appsettings.Development.json`, where the host calls `MigrateModuleDatabasesAsync` to migrate every module context in registration order.

## Consequences
- Positive: aggregates get audit, soft delete and (later) the outbox in one atomic save; reads are explicit SQL that is easy to tune and to page by keyset; each module's schema and migrations stay independent.
- Negative / trade-offs accepted: two data-access styles to learn; the soft-delete and tenant filters are duplicated in Dapper SQL and protected only by tests; the retrying execution strategy means a handler that opens its own transaction must run it through `Database.CreateExecutionStrategy()`.
- Follow-up actions: the Sample module's Dapper query filters `IsDeleted = 0` and has the soft-delete integration test (Task 11); the outbox interceptor joins the same save (Task 9); the tenant filter becomes a second named filter when tenancy is added.
