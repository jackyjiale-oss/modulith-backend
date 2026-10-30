# API and database compatibility

The rules for changing a published API contract or a database schema without breaking a client that has not been updated yet, and for retiring an old version on evidence. Read it before a change that touches a request or response shape, an endpoint, a column or a table. The decision behind it: [ADR 0031](adr/0031-api-and-database-compatibility-policy.md).

CI enforces the part a machine can check: the breaking-change gate on a pull request (`OpenApiCompatibility`, section "Is it breaking?"), the contract-migration guard (`MigrationTests`, section "Changing the database") and the version register test (`ApiVersionRegisterTests`, section "Version register"). Everything else here is for the author and the reviewer to judge.

## Principles

1. **An API version and a schema version are separate things.** A version in the URL (`/api/v1`) names a public contract; the schema of a module's tables is how that module stores data. A new API version does not create a new database, service layer or deployment, and a schema change does not by itself change the API.
2. **Do not version every change.** Add compatible things (an endpoint, an optional property) to the current version. A new version is for a break that cannot coexist with the old contract.
3. **A new API version is for a breaking change, and only when old and new clients must work at the same time.** If no client depends on the old contract, change it in place and mark the pull request breaking.
4. **Do not keep a column only to answer an old response.** Keep the contract DTOs apart from the entities and map the current model to the old DTO. The old column can go while the old API version stays, provided the old response can still be built (if the information is gone, the old version cannot be served from this schema, and that is a decision, not a refactoring).
5. **An old version, DTO or column is a temporary compatibility cost.** Each one has an owner, a retirement criterion and a place in the register below.
6. **Retire on evidence, not on assumption.** Usage, app versions, dependencies and a monitoring window decide; the passing of a release does not. Mobile apps stay installed for a long time.

## Is it breaking?

The question is: can an existing client keep working without changing its code or behaviour? The table lists the common changes; the last column says whether the gate (below) enforces the row or only review does. The rules are conventions, not laws: judge against the published contract and the clients that really exist.

| Change | New version needed? | Checked by |
|---|---|---|
| Fix a bug, or improve performance, without changing the contract | No | Review |
| Add an endpoint | No | Gate: reported as compatible |
| Add an optional request parameter or request property | Usually no; keep the default behaviour | Gate: reported as compatible |
| Add a response property | Usually no; a strict client or schema validator may reject an unknown property | Gate: reported as compatible |
| Add a value to a response enum | Usually no; a client with an exhaustive `switch` may fail | Gate: reported as compatible |
| Remove an endpoint, or a path | Yes | Gate: breaking |
| Remove or rename a response property | Yes | Gate: breaking |
| Make a response property optional (no longer in `required`) | Yes; clients read it as always present | Gate: breaking |
| Change the type or format of a request or response property | Yes (a number that becomes an object is the classic case) | Gate: breaking |
| Add a required request parameter or request property, or make an optional one required | Yes; existing requests become invalid | Gate: breaking |
| Make the request body required | Yes | Gate: breaking |
| Remove a value from a request enum | Yes | Gate: breaking |
| Remove a success status code (a `201` that becomes `200` shows as the `201` removed) | Yes | Gate: breaking |
| Add authentication to an operation that had none | Yes | Gate: breaking |
| Change authentication incompatibly in any other way (another scheme, other claims, a new permission on an operation that was already protected) | Usually yes | Review |
| Change what a field means, tighten validation, or change an error code or status | Yes, if a client can notice | Review |
| Remove a request property (the server then ignores what clients send) | Judge by the behaviour lost | Review |

### The breaking-change gate

On a pull request, `OpenApiBaselineTests.OpenApi_changes_are_compatible_with_the_baseline` compares the current OpenAPI document (`/openapi/v1.json`) with the base branch's committed `OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json` ([ADR 0011](adr/0011-in-repo-openapi-snapshot.md)) using `OpenApiCompatibility.Compare`, our own classifier, with no package or external tool. It fails when it finds a breaking change, and lists every difference it found, breaking or compatible. CI names the baseline file in `OPENAPI_BASELINE` (the `OpenAPI baseline` step of `ci.yml`, pull requests only). Without that variable the test skips itself, so a local `dotnet test` and a push to `main` are unaffected.

What it classifies, with `$ref` resolved and `allOf` members merged. The request side is the parameters and the request body; the response side is the bodies of the `2xx` responses.

- **Breaking:** an operation or a path removed; a required request parameter or request-body property added; an optional parameter or request-body property made required; a request body made required; a response property removed or no longer required; a success status removed; a request enum value removed; a security requirement added to an operation that had none; a type or format changed.
- **Compatible, reported and not failing:** an operation added; an optional request parameter or property added; a response property added; a response enum value added.

Its limits, which review covers:

- **Any type change on the request side is breaking, widening included.** Making a request DTO property nullable adds `"null"` to its type and trips the gate; so does changing `integer` to `number`. The way through is the `!` below, or a new version.
- **`oneOf` and `anyOf` are compared as a whole:** two lists that differ are breaking, even when a client would cope. An identical `oneOf` has its members compared one by one.
- **Media types are matched by name.** One present on only one side (JSON to multipart, say) is not compared.
- **Not compared at all:** non-`2xx` responses (error shapes and statuses), response headers, parameter defaults, descriptions, and the meaning of any field.
- **The gate is a short list, not a proof.** It catches the common breaks; a false alarm is handled by the `!` or by a fix to the classifier's rules, never by editing the baseline.

**Accepting an intended break.** Mark the pull-request title with the Conventional Commit `!` (`feat(api)!: return the price as an object`), and add the `BREAKING CHANGE:` footer ([`CONTRIBUTING.md`](../CONTRIBUTING.md)); release-please then bumps the major version. CI turns the `!` into `API_BREAKING_ACCEPTED=true`: the test prints the breaking changes and passes. **`ci.yml` runs on `opened`, `synchronize` and `reopened`, so editing the title does not re-run it: push a commit after adding the `!`.** The `commit-lint` check does re-run on a title edit, but it does not decide the gate. A `!` accepts the break; it does not make it safe. If old and new clients must coexist, add a version instead (next section).

## Making a breaking change

1. **Confirm that old and new contracts must coexist.** If not (no client has the old shape yet, or every client is ours and ships with the backend), change the contract in place, mark the title `!`, and say what clients must do in the `BREAKING CHANGE:` footer.
2. **Add the version.** `/api/v1` is a literal route group and there is one OpenAPI document, `v1` ([ADR 0008](adr/0008-literal-v1-routes.md)). The first break that has to coexist adopts `Asp.Versioning` with URL-segment versioning, which keeps `/api/v1` as it is: a `/api/v2` group, a second OpenAPI document `v2`, the snapshot and the gate run once per document, and each module's version-specific request and response types beside its endpoints (`Endpoints/V2/`). That change has its own ADR, which supersedes ADR 0008 and updates the trigger in ADR 0031.
3. **Register it.** Add the row to the version register as `Active`. `Register_matches_the_committed_openapi_snapshot` fails for a served version without a row.
4. **Share the behaviour, version the contract.** Handlers stay shared where behaviour is the same; each version has its own request and response types, and the old response type is filled from the current model (`ProductV1Dto` and `ProductV2Dto` from one entity).
5. **Deprecate the old version.** Set its register row to `Deprecated` with the date and the retirement criteria, tell clients, and start sending the headers (section "Retiring a version").

## Changing the database: expand, migrate, contract

For a schema change that old clients or old code can still observe (a rename, a removal, a split, a type that changes), work in four phases rather than one migration. The phases map to releases:

| Phase | What happens | Release | Tool in this template |
|---|---|---|---|
| **Expand** | Add the new column or table next to the old one; nothing is removed. Plan how old and new code read and write during the transition. | Release N | A migration that only adds (`dotnet ef migrations add`, [`docs/services/api.md`](services/api.md#migrations)). In production it is applied by the `migrate` mode that Plan 6 adds; until then only local start-up migration (`Database:ApplyMigrationsOnStartup`) exists. |
| **Migrate** | Backfill existing rows into the new structure, and move the code to it. | Release N (the backfill) and N or N+1 (the code) | The backfill is a migration or a batched job; the module's reads and writes move to the new structure. |
| **Transition and monitor** | Release compatible backend and client changes; watch for use of the old code path; check everything that may read the old structure. | N+1 up to the later release | Logs and metrics (section "Retiring a version"); the checklist below. |
| **Contract** | Confirm nothing reads or writes the old structure; remove it in a later migration, and remove the compatibility code. | A later release, after the checklist | A migration that drops or renames, marked `[ContractMigration]`. |

**Never remove or rename a column or table in the same change that stops using it, and never because a new API version exists.** A column goes when its last database dependency has moved, not when an API version appears; the old API version can often outlive the column, because the backend maps the current model to the old DTO.

**The write strategy during the transition is chosen and tested, not assumed.** While old and new code both run, keep the two structures consistent by writing both from the application in the same save. Do not rely on a trigger, or on a computed column or view, to keep a copy in step: it is one more thing that drifts. Cover the strategy with a test that writes through the old path and reads through the new one, and the reverse.

**The guard.** A migration whose `Up` drops or renames a column or table carries `[ContractMigration("reason")]` (`TemplateName.Infrastructure.Common.Persistence`; the reason is required and names the release that stopped using the column or table). The architecture test `Every_migration_in_the_repository_is_additive_or_marked` reads the `Up` operations of every migration in the solution (`DropColumn`, `RenameColumn`, `DropTable`, `RenameTable`) and fails for a contracting migration without the marker, for a marker on a migration that contracts nothing, and for a migration it cannot instantiate. `Down` is not read, and `AlterColumn` (a narrower type, a column made non-nullable) is not checked, because the operation does not say whether the data fits: that one is for the checklist and for review.

Add the marker by hand to the migration's own file (the one that holds `Up`, not the generated `.Designer.cs`), in the same commit that adds the migration. An applied migration is never edited, so the marker cannot be added later.

```csharp
[ContractMigration("Release 1.8 stopped reading and writing Users.FullName")]
public partial class DropUsersFullName : Migration
```

**The upgrade path.** A project tests that its migrations apply from the previous release to this one over data, and that a fresh database works. The template does not ship that test yet; it comes with the `migrate` mode and the runbooks of Plan 6, which is also where the contract phase is run.

## When can a column or table go?

All of these, before the contract migration is written:

- [ ] All backend queries and entity mappings use the new column or structure.
- [ ] Stored procedures, triggers, views, reports and scheduled jobs have been checked.
- [ ] Other services, customer integrations, imports and exports have been checked.
- [ ] Existing data has been migrated and validated.
- [ ] Reads and writes of the old column have stopped, by code review and suitable monitoring.
- [ ] The application has been tested in a production-like environment.
- [ ] A recovery or rollback plan has been considered, including whether deleted data can be restored.
- [ ] The removal is scheduled and documented.

A module owns its tables ([data ownership](architecture/overview.md#data-ownership)) and no other module may read them, so the dependencies to check are the module's own code, jobs and views, reports and exports that read the database directly, and integrations outside the repository.

## Version register

One row per API version; the test of this page (`Register_matches_the_committed_openapi_snapshot`) keeps it in step with the OpenAPI document. It fails for a version the document serves with no row, for an `Active` or `Deprecated` row whose version is not served, for a `Retired` row whose version is still served, and for a status that is not one of the four. Statuses: `Active`, `Deprecated`, `Retirement announced`, `Retired`. Every `Deprecated` or later row has an owner and the criteria for the next step. A retired version keeps its row.

| Version | Status | Since | Owner | Retirement criteria |
|---|---|---|---|---|
| v1 | Active | 2026-10-07 (ADR 0008) | The template's maintainers | None planned |

## Retiring a version

A version is retired in four steps, each of which has a date in the register and a communication to affected clients:

1. **Active:** supported.
2. **Deprecated:** a replacement exists; clients are encouraged to move.
3. **Retirement announced:** a date and migration instructions are published.
4. **Retired:** the version is disabled, after the checks below. The row stays.

The timing follows the product's customer commitments and release model; the template sets none.

**Evidence before retiring.** Check all of these:

- **Client usage:** do requests still reach the old version? The request log carries `ApiVersion` (the `n` of `/api/v{n}`) on every `/api` request, and the ASP.NET Core metrics the host exports carry `http.route`, which contains `/api/v1`: "is `v1` still used" is a log or metric query, and an operator builds it.
- **Client identification:** can the logs tell which app version and which customer? A client may send `X-Client-Version` (1 to 64 characters of letters, digits, `.`, `_`, `+` or `-`; anything else is dropped); the request log then carries it as `ClientVersion`. It is optional and a missing one is not an error. It is only ever in the log, never a metric tag, because a client version is unbounded caller input. Customer identification is the project's own.
- **Customer rollout:** have customers moved to an app that uses the new version?
- **Integrations:** are third-party or internal systems still on the old version?
- **Monitoring window:** has use of the old version stayed at zero, or under an agreed threshold, for a suitable period?
- **Communication:** have the affected customers been told the deprecation and retirement dates?
- **Support and rollback:** what happens when a forgotten client turns up?

For mobile apps consider telemetry, a minimum supported app version (the template does not enforce one; a project that needs it adds a `426 Upgrade Required` of its own) and a clear support policy.

**Headers.** Once a version is `Deprecated`, its responses carry `Deprecation` ([RFC 9745](https://www.rfc-editor.org/rfc/rfc9745)) and, once a date is set, `Sunset` ([RFC 8594](https://www.rfc-editor.org/rfc/rfc8594)). **The template does not send them yet**: there is no deprecated version to attach them to. The plan that adds the first deprecated version adds that code.

**Never renumber.** Version numbers name contracts. A project at `v24` does not need `v1` to `v23` any more, and need not rename `v24` to `v1`: that makes logs, documentation, clients and tools ambiguous. To simplify, retire old versions; to change the strategy, decide it deliberately with its own migration rules. A project that keeps cutting new versions for small changes should revisit what it counts as breaking and design its contracts to evolve compatibly.

## Avoiding sprawl

- Keep the register of active, deprecated and retired versions, with an owner and retirement criteria for every deprecated one.
- Keep API contract types apart from entities.
- Keep business logic in the shared handlers where behaviour is the same across versions.
- Test every supported version's contract. The gate and the snapshot cover the published document; a project adds behaviour tests per version.
- Test fresh installs and upgrades of the database (the upgrade-path test above).
- Track API usage and app versions in the logs (`ApiVersion`, `ClientVersion`).
- Document a column rename and the systems that depend on it, in the pull request and the module page.
- Remove obsolete DTOs, mappings, endpoints and schema elements after retirement.
- Do not support an old version indefinitely without a customer or business reason.

Before shipping a change, ask: is it visible to API clients; is it compatible for the clients that exist; if not, must old and new coexist; does a migration affect an endpoint, job, report or integration; can the old contract be kept by mapping from the current schema; what evidence will show that the old column or version is safe to remove; and who owns the migration, the monitoring, the communication and the retirement.
