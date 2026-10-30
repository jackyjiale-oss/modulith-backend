# Plan 5c: API and database versioning policy — design

- Status: Approved 2026-10-29 (approach B and this design were approved in the design session); the final review amended 5.1, 5.3 and 5.4 to match the code
- Date: 2026-10-29
- Roadmap row: new, between 5b (Audit) and the social-login plan. It is not in `docs/blueprint-review.md` Section 6 yet; adding the row is part of this plan.
- Builds on: Plans 1–5b, all merged (main `5d12796`); ADR 0008 (literal `/api/v1` routes), ADR 0011 (OpenAPI snapshot), ADR 0006 (EF Core migrations)
- Source: `api_database_versioning_strategy.md` (supplied by the user, 2026-10-29)

## 1. Intent

**Outcome.** A team that starts a project from this template knows, before it writes its first breaking change, how API contracts and database schemas evolve here: when a change needs a new API version, how a column is renamed or removed without an outage for old mobile clients, and how an old version is retired on evidence. The rules are stated once, and CI enforces the ones a machine can check.

**Success looks like.**

1. One compatibility page (`docs/api-compatibility.md`) and one ADR (0031) state the policy of the source document in this template's terms: routes, modules, outbox, migrations.
2. A pull request that makes a breaking change to a published API document fails CI unless the author accepted it on purpose.
3. A migration that drops or renames a column or table fails the architecture tests unless it carries an explicit contract-phase marker.
4. Every request log carries the API version and, when the client sends it, the client's app version, so "is `v1` still used, and by which app versions" is a log query.
5. The register of API versions (active, deprecated, retired) is in the compatibility page, and a test fails when a version appears in the OpenAPI document without a row.
6. Nothing here changes an existing route, response shape, table or column. `/api/v1` stays literal; no package is added.

**Said by you:** the source document (principles in its sections 1 to 10, the checklist at its end) and "approve B": policy plus light enforcement, no `Asp.Versioning` yet.

**Assumed by me:** the document's warehouse and Flutter examples are illustrations; the template is generic, with mobile clients as the case that makes old versions linger. SQL Server stays the only database. The template has no consumer yet, so this plan constrains the future, not today's clients.

## 2. Non-goals

- **No `Asp.Versioning`, no `v2` group, no second OpenAPI document.** ADR 0008's trigger stands: the first breaking change that must coexist with the old contract adopts the library, in its own ADR. Section 6.3 records what that plan must do.
- **No `Deprecation` or `Sunset` headers.** The policy names them (RFC 9745, RFC 8594); the code that sends them arrives with the first deprecated version, because there is no version to attach them to now.
- **No usage dashboard, no alert.** The template emits the data (section 5.3); an operator builds the query.
- **No upgrade-path test** (apply migrations up to N-1, load data, apply N). It belongs with the `migrate` mode and the runbooks of Plan 6, which is also where the contract phase is run. The policy requires it of a project; the template does not ship it yet.
- **No client-side minimum-version enforcement** (a `426 Upgrade Required` for old app versions). The policy mentions it as an option; a project that needs it adds it.
- **No change to existing migrations.** None of them drops or renames a column or table in `Up` (checked 2026-10-29), so the guard needs no baseline.

## 3. Decisions

| # | Decision | Why | Rejected, and cost if you disagree |
|---|---|---|---|
| D1 | **Adopt the source document's principles as the policy:** API version and schema version are separate; version only for breaking changes; map old DTOs from the current schema; expand, migrate, contract for columns; retire on evidence. | They are sound, and they fit modules that own their tables and expose only an HTTP contract. | Writing a different policy: no reason to. |
| D2 | **Keep literal `/api/v1` until the first coexisting break.** ADR 0031 builds on ADR 0008 and supersedes nothing; ADR 0008's follow-up points to it. | A package and per-version OpenAPI pipeline with no second version is the cost ADR 0008 refused. | **Adopt `Asp.Versioning` now (approach C):** deprecation headers and version sets ready, at the price of a dependency, configuration and a versioned snapshot that nothing uses. The migration to it later is the same either way, because routes already read `/api/v1`. |
| D3 | **The breaking-change gate is our own classifier over the OpenAPI JSON,** run by a test against a baseline file CI supplies. No package, no external binary. | The document is already in the repo (ADR 0011) and the rules are a short list (section 5.1). A third-party differ is a download to pin and a licence to check for a list this short. | **`oasdiff` in CI:** broader rules, but a binary or container to pin and verify in every generated project. **No gate (approach A):** the table in the policy page is then advice only. |
| D4 | **The migration guard reads each migration's `UpOperations`** and requires `[ContractMigration]` on one that drops or renames a column or table. | The check is on what the migration does, not on its name or a comment. `Migration.UpOperations` is public API of EF Core, and the architecture tests already load every module assembly. | **Scanning migration source for `DropColumn`:** brittle against formatting; also matches `Down`. **Review only:** a destructive migration is easy to approve in a long generated diff. |
| D5 | **Usage data is two log properties, no new metric.** `ApiVersion` (from the request path) and `ClientVersion` (from `X-Client-Version`). | The ASP.NET Core metrics the host already exports carry `http.route`, which contains `/api/v1`, so usage per version exists. A client version is unbounded caller input, so it stays in the log, never a metric tag. | **A custom counter by client version:** a cardinality attack by any caller. **Reading `Asp.Versioning`'s feature:** needs D2 reversed. |
| D6 | **Retirement stays a documented process,** with a register in the compatibility page that a test keeps in step with the OpenAPI document. | The evidence (usage window, customer rollout, communication) is outside the code. What code can do is refuse to let a version exist unregistered. | **A `docs/api-versions.json` read by middleware:** a runtime dependency on a file for a decision no code takes yet. |
| D7 | **Escape hatch for an intended break is the Conventional Commit `!` in the pull request title.** CI turns it into `API_BREAKING_ACCEPTED=true`. | The repository already requires `!` and a `BREAKING CHANGE:` footer for a breaking API change (PR template, `pr-title.yml`), and release-please turns it into a major version. One signal, not two. | **A separate label or file of accepted breaks:** a second place to forget. |

## 4. The policy (what the compatibility page and ADR 0031 say)

The page is written for a developer about to change a contract. It has these sections, in this order:

1. **Principles**, the six of the source document, in one paragraph each.
2. **Is it breaking?** The table of the source document's section 3, made concrete for this template: the gate (section 5.1) enforces the rows it can; the rest are reviewed. A column states which.
3. **Making a breaking change.** Steps: confirm the old and new contracts must coexist (otherwise change in place and bump the major version); add the version (section 6.3); register it as Active; share handlers, keep the DTO per version; mark the old version Deprecated with a date.
4. **Changing the database: expand, migrate, contract.** The four phases of the source document, mapped to releases: expand and backfill ship in one release; the code moves to the new structure and the old one stops being read and written in the same or the next; the contract migration ships in a later release, after the checklist. A table of the template's tools: the migration, `[ContractMigration]`, the `migrate` command (Plan 6). The write strategy during transition (both columns written by the application, never a trigger or a computed copy assumed to stay in step) is named and must be tested.
5. **When can a column or table go?** The source document's checklist (section 6), unchanged.
6. **Version register.** `v1`: Active, since 2026-10-07 (ADR 0008), owner the template's maintainers, retirement criteria "none planned". One row per version; the test of D6 reads this table.
7. **Retiring a version.** Lifecycle (Active, Deprecated, Retirement announced, Retired), the evidence checklist of the source document's section 7, the headers to send once a version is deprecated, and the note that `v24` is never renamed `v1`.
8. **Avoiding sprawl.** The source document's section 9 as a short list.

ADR 0031 records the decisions of section 3 and the trigger of ADR 0008; the page is the working document.

## 5. Enforcement

### 5.1 Breaking-change gate (`OpenApiCompatibility`, in the architecture test project, linked into the integration test project for the baseline test)

- `OpenApiCompatibility.Compare(baselineJson, currentJson)` is a pure function returning `Breaking` and `Compatible` change lists, each entry naming the operation (`GET /api/v1/users/{id}`) and the change. It resolves `$ref` through `components.schemas`.
- **Breaking:** an operation or path removed; a required request parameter or request-body property added; an existing request parameter or request-body property that became required; a request body that became required; a type or format changed on a request or response property; a response property removed; a response property that stopped being required; a success status code removed; a request enum value removed; a security requirement added to an operation that had none.
- **Compatible, reported but not failing:** an operation added; an optional request parameter or property added; a response property added; a response enum value added. The page says a strict client may still reject the last two (the source document's section 3).
- A test, `OpenApi_changes_are_compatible_with_the_baseline`, in `Documentation/`, reads the current document the way `OpenApiSnapshotTests` does and the baseline from the file named by `OPENAPI_BASELINE`. With no baseline set it skips itself, saying why, so a developer's `dotnet test` is unaffected. With `API_BREAKING_ACCEPTED=true` it prints the breaking changes and passes.
- `ci.yml`, pull requests only: the checkout fetches the base branch, the base's `OpenApiSnapshotTests...verified.json` is written to a temp file, and the two variables are set (`API_BREAKING_ACCEPTED` from the title's `!:`). A push to `main` sets neither.
- `oneOf` and `anyOf`: identical raw JSON has its members compared one by one; differing raw JSON is breaking as a whole.
- The classifier has its own unit-style tests, written first, one per rule above plus the `$ref`, `allOf` and nullable cases; its tests live beside it in the architecture tests and need no Docker.

### 5.2 Migration guard (`MigrationTests`, architecture tests)

- `[ContractMigration("reason")]` lives in `Infrastructure.Common` (the project every module's persistence already references). It applies to a migration class, has a mandatory non-empty reason and does not inherit.
- The test enumerates every `Migration` in every module and building-block assembly, instantiates it and reads `UpOperations`. A `DropColumnOperation`, `RenameColumnOperation`, `DropTableOperation` or `RenameTableOperation` without the attribute fails, naming the migration. The attribute without such an operation also fails, so it cannot become decoration.
- `AlterColumn` that narrows a type or makes a column non-nullable is not checked (the operation does not say whether data fits); the page's checklist and the PR template cover it.
- The attribute is added by hand to the migration file once, before it is applied. `dotnet ef migrations add` regenerates nothing that removes it, and an applied migration is never edited (hard rule), so the marker is part of the contract migration's own first commit.

### 5.3 Usage data (`Web.Common`)

- `EnrichRequest` in `ObservabilityExtensions` adds `ApiVersion` (an integer, from `^/api/v([0-9]{1,4})(?:/|\z)` on the path, ASCII digits, at most four; absent off `/api`) and `ClientVersion` (the first `X-Client-Version` value when it matches `^[A-Za-z0-9._+\-]{1,64}\z`; anything else is dropped, not logged, so a caller cannot forge log content or grow it).
- Nothing else reads the header. It is optional; a missing one is not an error. CORS already allows any header (`HttpSecurityExtensions`), so a browser client needs no configuration.
- The `Serilog` request event is the only place these appear. `docs/services/api.md` lists both properties and the metric path (`http.route`).

### 5.4 Version register test (`ApiVersionRegisterTests`, architecture tests)

Reads the committed OpenAPI snapshot, collects the distinct `/api/vN` prefixes of its paths, and reads the register table of the compatibility page. A prefix without a row fails; a row `Active` or `Deprecated` without a prefix fails; a row `Retired` whose prefix is still served fails. It needs no Docker, like the other documentation tests.

## 6. Where it lands

### 6.1 Files

| Area | Change |
|---|---|
| `docs/adr/0031-api-and-database-compatibility-policy.md` | New. Listed in `docs/README.md` and `docs/architecture/overview.md` (`Every_adr_is_listed...`). |
| `docs/adr/0008-literal-v1-routes.md` | Follow-up line points to ADR 0031. Status unchanged. |
| `docs/api-compatibility.md` | New, section 4. Listed in `docs/README.md`. |
| `docs/coding-conventions.md` | The "public contracts" list (breaking changes) links the page; the migration naming row links the contract migration rule. |
| `CONTRIBUTING.md`, `.github/pull_request_template.md` | Checklist: breaking API change and "migration drops or renames a column or table (contract phase, `[ContractMigration]`), or none". |
| `build/template-content/CLAUDE.md` (the root `CLAUDE.md` imports it) | A hard rule: never remove or rename a column or table in the same change that stops using it; link to the page. |
| `docs/services/api.md`, `docs/building-blocks/web-common.md` | `ApiVersion`, `ClientVersion`, `X-Client-Version`. |
| `docs/blueprint-review.md` Section 6 | Row 5c; the API-versioning row of Section 4 points to ADR 0031. |
| `Infrastructure.Common` | `ContractMigrationAttribute`. |
| `Web.Common` | `EnrichRequest` change and its tests. |
| Integration tests | The baseline test (it links `OpenApiCompatibility`). |
| Architecture tests | `OpenApiCompatibility` and its tests, `MigrationTests`, `ApiVersionRegisterTests`. |
| `.github/workflows/ci.yml` | Baseline step for pull requests. |

The template-smoke script must still pass: every new file is either under `docs/` (shipped as is) or code, and nothing mentions a concrete project name.

### 6.2 Tests first

Each unit of section 5 has failing tests before its code: the classifier rule by rule; `MigrationTests` with a fixture migration that drops a column (with and without the attribute); `EnrichRequest` with a valid, missing, over-long and control-character header; the register test with an unregistered and a stale row.

### 6.3 What the first `v2` plan must do (recorded here, not built now)

Adopt `Asp.Versioning` with URL-segment versioning, which keeps `/api/v1` unchanged. Add a `/api/v2` group and a second OpenAPI document `v2`; make `OpenApiSnapshotTests` and the compatibility test run per document; put version-specific request and response types beside the endpoints (`Endpoints/V2/` in the module, a namespace segment by rule F4) and keep handlers shared when behaviour is the same; add the `Deprecation` and `Sunset` headers when `v1` is marked Deprecated; register both versions. Its own ADR supersedes ADR 0008 and updates ADR 0031's trigger.

## 7. Schedule

1. **Plan 5c, this design** (spec, plan, implementation). First, because every later plan adds endpoints and tables that this policy governs.
2. **Social login** (Google, Apple, Microsoft, Facebook; per-provider toggles by configuration), written and reviewed against the policy from its first spec.
3. **Push and SMS channels** (a separate Notifications plan).
4. **Phone OTP login**, after SMS.
5. **Plan 6 delivery** (the `migrate` mode, Docker, CD, runbooks) stays where the blueprint puts it. When it is written, it adds the upgrade-path test and the contract-phase runbook that this plan names.

Order, not dates: each plan starts when the one before is merged.

## 8. Risks

- **The classifier misses a break, or flags a compatible change.** It is a short, tested list, not a proof. The policy page says the gate catches the common breaks and review catches the rest; `API_BREAKING_ACCEPTED` and a plain fix to the rule are the two answers to a false alarm.
- **A project ignores the `!` convention and merges a break.** The gate fails the build without `!`, so ignoring it needs a wrong title, which `pr-title.yml` already polices.
- **Constructing a migration to read `UpOperations` throws** for a migration that uses provider checks. SQL Server is the only provider (ADR 0003) and every current migration builds; a failure names the migration, so it is found at once.
- **`ClientVersion` in logs is caller-controlled.** It is length- and character-limited, and never a metric tag.

## 9. Open questions

None blocking. Flagged for your review: the header name `X-Client-Version` (D5), and that the escape hatch is the title's `!` (D7). Both are one-line changes.
