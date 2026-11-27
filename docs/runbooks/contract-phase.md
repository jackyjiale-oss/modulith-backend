# Runbook: contract phase (dropping or renaming a column or table)

The last of the four phases in [`../api-compatibility.md`](../api-compatibility.md#changing-the-database-expand-migrate-contract) (expand, migrate, transition and monitor, contract). The previous release **cannot** run on the schema afterwards, so this is the one change that goes out in maintenance mode ([ADR 0034](../adr/0034-maintenance-mode-for-contract-phase-releases.md)) and that cannot be rolled back by redeploying the old version.

## When

A column or table is no longer read or written by any release that can still run, and you want it gone. The expand release is in production, the release that moved the code to the new structure has been running for as long as your rollback and customer-deployment window requires, and the checklist "When can a column or table go?" in `api-compatibility.md` is complete. Never in the same change that stops using the column.

## Who

A developer writes the migration; the person who approves the `production` environment of the delivery pipeline decides the maintenance window. The database checks below need read access to the production database (`VIEW SERVER STATE` for the query-statistics check).

## Steps

1. **Prove the old structure has no reader.** All of these, for the column or table named in the migration:
   1. In the code: `git grep -n -i "<ColumnName>" -- src tests` and the same for the table. Look at the EF mappings and at every raw SQL string (Dapper reads, [ADR 0006](../adr/0006-ef-core-writes-dapper-reads.md), and the SQL of the cleanup and counting jobs). A hit that is only the migration history is expected.
   2. In the database, views, procedures, functions and triggers that mention the table:

      ```sql
      SELECT OBJECT_SCHEMA_NAME(referencing_id) AS [Schema], OBJECT_NAME(referencing_id) AS [Object]
      FROM sys.sql_expression_dependencies
      WHERE referenced_schema_name = N'auth' AND referenced_entity_name = N'Users';

      SELECT OBJECT_SCHEMA_NAME(object_id) AS [Schema], OBJECT_NAME(object_id) AS [Object]
      FROM sys.sql_modules
      WHERE definition LIKE N'%FullName%';
      ```

      Replace `auth`, `Users` and `FullName` with the module schema, table and column.
   3. In live traffic, statements still run against the column (the plan cache covers what ran since the server started or the plan was evicted, so run it after a full business cycle):

      ```sql
      SELECT TOP (50) qs.last_execution_time, qs.execution_count, st.text
      FROM sys.dm_exec_query_stats AS qs
      CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
      WHERE st.text LIKE N'%FullName%'
      ORDER BY qs.last_execution_time DESC;
      ```

      A statement from the current release in the result means something still reads the column: stop and find it. `SELECT *` and the migration scripts themselves also match.
   4. Outside the repository: reports, exports, imports, other services and customer integrations that read the database directly. Only their owners can answer; ask them and record the answers in the pull request.
2. **Write the migration** from the module's folder, with the command of the repository [`CLAUDE.md`](../../CLAUDE.md):

   ```bash
   dotnet ef migrations add Drop{What} \
     --project src/Modules/{Module}/TemplateName.Modules.{Module} \
     --startup-project src/Host/TemplateName.Api \
     --context {Module}DbContext \
     --output-dir Infrastructure/Persistence/Migrations
   ```

   Mark the migration in its own file (the one that holds `Up`, not the `.Designer.cs`), in the same commit; an applied migration is never edited:

   ```csharp
   [ContractMigration("Release 1.8 stopped reading and writing Users.FullName")]
   public partial class DropUsersFullName : Migration
   ```

   The reason names the release that stopped using the column or table. The architecture test `Every_migration_in_the_repository_is_additive_or_marked` fails for a drop or rename without the marker and for a marker on a migration that contracts nothing.
3. **Add the upgrade-ladder seed.** A migration marked `[ContractMigration]` needs a seed on the **migration before it**, so the contraction is proved over data: `tests/TemplateName.IntegrationTests/UpgradePath/Seeds/{Module}/{PreviousMigrationName}.seed.sql` with rows in the old shape, and `{PreviousMigrationName}.verify.sql`, which selects what is wrong and returns no rows once the migrations ran (a row lost, a value that did not reach the new structure). `{PreviousMigrationName}` is the class name without its timestamp. The rules for both files are in [`../coding-conventions.md`](../coding-conventions.md#migrations-that-touch-data-the-upgrade-path-ladder); `UpgradeLadderGuardTests` fails without the seed.
4. **Run the tests**: `dotnet test` (Docker needed for the ladder and the integration tests). Read the ladder's failure message, which names the seed and the migration.
5. **Take a database backup and prove it restores.** After the migration there is no way back to the previous release except a restore ([`../deployment/migrations.md`](../deployment/migrations.md#expand-then-contract)). Take it just before the release, with the tool your platform provides; the template ships none. Keep the key-ring certificate with it ([`restore-backup.md`](restore-backup.md)).
6. **Release.** Merge and release as usual. The `maintenance` job of `cd.yml` finds the commit production last ran (`build/scripts/resolve-deploy-base.sh`, the last successful deployment to the `production` environment) and runs `build/scripts/detect-contract-migration.sh` against it: a migration file added since that commit that mentions `ContractMigration` turns maintenance mode on, and the pipeline puts the maintenance page up **before** `migrate` and takes it down only after the new version passed its health check ([`../deployment/README.md`](../deployment/README.md#maintenance-mode)).
   - Docker: the project must have written `build/deploy/maintenance.sh`, or the release stops before `migrate` ([`../deployment/docker.md`](../deployment/docker.md#the-maintenance-hook)).
   - IIS: `deploy-iis.ps1` runs with `-Maintenance` ([`../deployment/iis.md`](../deployment/iis.md#maintenance-mode)).
   - A release that never completed its production deploy leaves no successful deployment, so the next release still sees its contract migration; nothing needs doing by hand.
   - If production was deployed by hand before the pipeline existed, there is no successful deployment on record and detection answers `false` for the first pipeline release: dispatch `cd.yml` by hand with `maintenance` set to `on` that once. If the lookup cannot answer (the job fails with a message), the same dispatch with `on` is the way past it.
   - Tell the users the window before it starts.

## Verify

- The deploy job is green and the maintenance page is gone.
- `GET /health/ready` answers 200 on every instance.
- The module's migration history holds the new migration last: `SELECT TOP (1) MigrationId FROM auth.__EFMigrationsHistory ORDER BY MigrationId DESC;` (the schema is the module's).
- The old column or table is gone: `SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = N'auth' AND TABLE_NAME = N'Users' AND COLUMN_NAME = N'FullName';` returns no row.
- A smoke test of the flows that touched the old structure.

## Roll back

There is no rollback of the schema by redeploying: the previous release fails on it. If any step after the maintenance page went up fails, the page stays up, nothing is restored and the run fails on purpose. Then: restore the database from the backup of step 5 (with its key-ring certificate, [`restore-backup.md`](restore-backup.md)), redeploy the previous version, and remove the maintenance page ([`../deployment/iis.md`](../deployment/iis.md#maintenance-mode), [`../deployment/docker.md`](../deployment/docker.md#the-maintenance-hook)). Writes made between the backup and the restore are lost, which is why the window is announced and the page is up. A migration that was already applied is never edited: fix forward with a new one.
