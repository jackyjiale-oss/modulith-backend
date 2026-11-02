# Migrations in a deployment

How a deployed database gets its schema: the `migrate` mode, the two database logins, and how a release stays expand-then-contract. The decision is in [ADR 0033](../adr/0033-delivery-pipeline.md) (and ADR 0006 before it); the development side, adding a migration, is [`services/api.md`](../services/api.md#migrations).

## The `migrate` mode

```bash
dotnet TemplateName.Api.dll migrate          # from a publish folder
docker run --rm -e ConnectionStrings__Database <image> migrate
```

`migrate` is a first argument of the API binary, recognised exactly (`migrate`, lower case, first); any other argument runs the normal web host. It builds the host, so every module's registrations are known, calls `MigrateModuleDatabasesAsync` (the same code path as development and the tests), and exits **without starting the host**: no Kestrel, outbox dispatcher, scheduler or delivery worker runs, and nothing is seeded. The first start of the API afterwards seeds the roles, permissions and, when configured, the first administrator.

- **Exit code** 0 on success, 1 on any failure (no connection string, a login that is refused, a DDL failure, an unreachable server, a cancellation). A failure is logged at Error with the exception; the log never holds the connection string, though SqlClient's message may name the server and the login.
- **Order**: `platform` first, then each module in registration order, each with its own history table in its own schema. A context that is not part of the migrations (`includeInMigrations: false`) is skipped.
- **Idempotent**: a second run applies nothing and exits 0. `container-verify.sh` runs it twice.
- **It reads `ConnectionStrings:Database`**, like the API, and ignores `Database:ApplyMigrationsOnStartup`, which stays a Development-only convenience (ADR 0006). The running API outside Development never migrates.
- **The database must exist, or the login must be allowed to create it.** EF Core creates a missing database when the login may; in production create the database yourself and make the DDL login its owner.

## Two logins

| Login | Used by | Rights | Where it lives |
|---|---|---|---|
| **Migration (DDL)** | the `migrate` process only | create and alter schemas, tables, indexes, constraints (typically `db_owner` on this one database) | the environment secret `MIGRATION_CONNECTION_STRING`, for the one `migrate` step; the IIS script reads it from `ConnectionStrings__Database` for its `migrate` call |
| **Runtime** | the running API | read and write data; no DDL | the API's own `ConnectionStrings__Database` (an environment variable of the container, the app pool or the orchestrator) |

There is no `ConnectionStrings:Migrations` key: `migrate` reads the same key as the API and the job sets it to the DDL login **for that one process**, so the DDL secret is never in the runtime settings and cannot reach the running API by accident. The job passes it by name (`docker run -e ConnectionStrings__Database`), taken from the step's environment, so it is on no command line.

What the runtime login needs is read and write on every module's tables, plus `EXECUTE` where a module calls a stored procedure; it needs no right to create or alter anything. **This split has not been exercised**: the integration tests and `container-verify.sh` use one privileged login for both. Prove it on a staging database before relying on it: deploy with a runtime login that is only `db_datareader` and `db_datawriter` (plus what your own procedures need) and watch for a permission error at start-up, in the seeding and in the first requests.

## Running it by hand

Do this when a pipeline cannot reach the database, or to apply a release's migrations before a roll-out:

```bash
# bash: the connection string stays in this shell's environment
read -rs ConnectionStrings__Database && export ConnectionStrings__Database
dotnet TemplateName.Api.dll migrate && echo migrated
```

```powershell
# set ConnectionStrings__Database to the DDL connection string in this session only (not in a script or a profile), then, in the publish folder:
dotnet TemplateName.Api.dll migrate; $LASTEXITCODE
```

Run it from a publish folder or from the image; `dotnet run --project src/Host/TemplateName.Api -- migrate` works from a source tree as well. Run it before the new version starts, and one deploy at a time. A release commit can make staging deploys overlap (an `edge` and a release run, [`README.md`](README.md#cdyml)); EF Core serialises concurrent migrators on the history table (since EF Core 9), which this template has not tested.

## Expand, then contract

The pipeline runs `migrate` **before** it replaces the application, so for a short time the **old code runs on the new schema**: that is why every migration is expand-only (ADR 0031) and why the IIS script rolls back files but never the database. A schema change that old code can still observe goes out in phases, spread over releases, as [`api-compatibility.md`](../api-compatibility.md#changing-the-database-expand-migrate-contract) describes:

1. **Release N, expand.** A migration that only adds (a column, a table, an index); code that works with both shapes; a backfill if needed.
2. **Release N+1, move and watch.** The code uses the new structure; check that nothing reads the old one.
3. **A later release, contract.** A migration that drops or renames, marked `[ContractMigration("reason")]` (an architecture test refuses one without the marker). It runs through the same `migrate`, in the same position in the pipeline.

A contract migration is the one change after which the previous release **cannot** run on the schema, so a rollback to it fails ([`docker.md`](docker.md#rolling-back), [`iis.md`](iis.md#the-script)). Restore the database from a backup first, or do not contract until the previous release is out of reach.

**The upgrade-path test is not shipped.** ADR 0031 requires a project to test that its migrations apply from the previous release to this one over data, and that a fresh database works. The template has no such test; `container-verify.sh` proves only that a fresh database migrates twice. Write one in your project (apply the migrations up to the previous release, load data, apply the rest).

## When a migration fails

`migrate` exits 1 and the job stops: `deploy-staging` or `deploy-production` is red and `roll-out.sh` is not called, so the old version keeps serving; the IIS script stops with the site untouched. Read the Error line of the job log, fix the migration or the database, and run again; a migration that applied before the failing one stays applied (each runs in its own transaction), and the next run resumes after it. **Never edit a migration that has been applied**; add a new one.
