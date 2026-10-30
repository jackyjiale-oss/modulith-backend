# 0031. API and database compatibility policy, with a breaking-change gate and a version register

- Status: Accepted
- Date: 2026-10-29
- Deciders: Lee Jia Le

## Context
ADR 0008 keeps the routes at a literal `/api/v1` until a breaking change needs a second version, and ADR 0011 snapshots the OpenAPI document so that no change to it is accidental. Neither says what counts as breaking, how a column that an old mobile client still depends on is renamed or removed, or how an old version is retired. The template has no consumer yet, so nothing is broken today, but every later plan adds endpoints and tables, and an app installed on a phone keeps calling the version it was built against long after a newer one ships.

What was missing was one statement of the rules, and the few checks a machine can make. A policy that only reads well is not followed in a long generated diff: a migration that drops a column looks like any other, and a renamed response property is one line in a snapshot. Two things must also not happen: the template must not take a package or a runtime component for a second version that does not exist, and it must not change an existing route, response shape, table or column.

## Options considered

**Which policy (D1).** The source was a strategy document supplied on 2026-10-29: API version and schema version are separate; version only for breaking changes; map old DTOs from the current schema; expand, migrate, contract for columns; retire on evidence. Writing a different policy had no reason behind it, so the options are about enforcement, below.

**How versioning is introduced (D2).**
1. **`Asp.Versioning` now (version sets, deprecation headers, one OpenAPI document per version).** Ready for `v2`, at the price of a dependency, configuration and a versioned snapshot that nothing uses; the cost ADR 0008 refused. The later move to it is the same either way, because routes already read `/api/v1`.
2. **Keep the literal group until the first break that must coexist.** Nothing to configure or maintain; the first `v2` plan adopts the library.

**How a breaking API change is detected (D3).**
1. **No gate.** The table in the policy is then advice, and a breaking change is found by a client.
2. **`oasdiff` or another differ in CI.** Broader rules, but a binary or container to pin and verify in every generated project, and a licence to check for a list this short.
3. **Our own classifier over the OpenAPI JSON,** run by a test against a baseline file CI supplies. The document is already in the repository (ADR 0011), the rules are a short list, and there is no package or binary.

**How a destructive migration is caught (D4).**
1. **Review only.** A drop is easy to approve in a long generated diff.
2. **Scanning migration source for `DropColumn`.** Brittle against formatting, and it also matches `Down`.
3. **Reading each migration's `UpOperations`** and requiring a marker. `Migration.UpOperations` is public EF Core API, and the architecture tests already load every assembly.

**How usage is measured (D5).**
1. **A custom counter by client version.** A client version is unbounded caller input, so any caller could grow the metric without limit.
2. **`Asp.Versioning`'s version feature.** Needs D2 reversed.
3. **Two request-log properties.** The ASP.NET Core metrics the host already exports carry `http.route`, which contains `/api/v1`, so use per version exists; the client's own version goes in the log only.

**How retirement is controlled (D6).**
1. **A `docs/api-versions.json` read by middleware.** A runtime dependency on a file, for a decision no code takes yet.
2. **A documented process and a register in the compatibility page, kept in step with the OpenAPI document by a test.** The evidence (usage window, customer rollout, communication) is outside the code; what code can do is refuse to let a version exist unregistered.

**How an intended break is accepted (D7).**
1. **A separate label or a file of accepted breaks.** A second place to forget.
2. **The Conventional Commit `!` in the pull-request title,** which the repository already requires for a breaking change (the pull-request template, `pr-title.yml`) and which release-please turns into a major version. One signal, not two.

## Decision
We chose the source document's policy, the literal routes kept, our own classifier, the `UpOperations` guard, two log properties, a register with a test, and the title's `!` (decisions D1 to D7 of the Plan 5c design). ADR 0031 builds on ADR 0008 and ADR 0011 and supersedes neither; the working document is [`docs/api-compatibility.md`](../api-compatibility.md).

- **Policy (D1).** The page restates the source document in this template's terms: the six principles, a table of what is breaking and which row the gate enforces, the steps of a breaking change, expand, migrate and contract mapped to releases, the checklist for removing a column or table, the lifecycle and evidence for retiring a version, and the sprawl list.
- **Routes (D2).** `/api/v1` stays literal and no package is added. The trigger of ADR 0008 stands: the first breaking change that must coexist with the old contract adopts `Asp.Versioning`, in its own ADR. That plan must adopt URL-segment versioning, which keeps `/api/v1` unchanged; add a `/api/v2` group and a second OpenAPI document `v2`; make `OpenApiSnapshotTests` and the compatibility test run once per document; put version-specific request and response types beside the endpoints (`Endpoints/V2/` in the module, a namespace segment by rule F4 of the coding conventions) and keep handlers shared where behaviour is the same; add `Deprecation` ([RFC 9745](https://www.rfc-editor.org/rfc/rfc9745)) and `Sunset` ([RFC 8594](https://www.rfc-editor.org/rfc/rfc8594)) headers when `v1` is marked Deprecated; and register both versions. Its ADR supersedes ADR 0008 and updates this one's trigger.
- **The gate (D3).** `OpenApiCompatibility.Compare` (integration test project, `Documentation/`) is a pure function over two OpenAPI documents, resolving `$ref` and merging `allOf`. `OpenApiBaselineTests.OpenApi_changes_are_compatible_with_the_baseline` runs it against the file named by `OPENAPI_BASELINE`, which the `OpenAPI baseline` step of `ci.yml` sets on a pull request from the base branch's committed snapshot; with no baseline set it skips itself, so a local run and a push to `main` are unaffected. Request side is the parameters and the request body, response side the bodies of the `2xx` responses.
  - **Breaking:** an operation or path removed; a required request parameter or request-body property added; **an existing request-body property that goes from optional to required** (and an optional parameter made required); a request body made required; a response property removed or no longer required; a success status removed; a request enum value removed; a security requirement added to an operation that had none; a type or format changed.
  - **Compatible, reported but not failing:** an operation added; an optional request parameter or property added; a response property added; a response enum value added. The page says that a strict client may still reject the last two.
  - **On the request side any type change is breaking, widening included.** Adding `"null"` to a type array, which is what making a request DTO property nullable does, trips the gate. The way through is the title's `!` or a new version. It is a false alarm for a client, accepted because the classifier cannot know that the server tolerates the wider input.
  - **Documented limits.** A media type present on only one side (JSON to multipart) is not compared. `oneOf` or `anyOf` lists that differ are breaking as a whole; an identical `oneOf` has its members compared one by one. Non-`2xx` responses, response headers and the meaning of a field are not compared.
  - **No baseline is edited to get past it.** A false alarm is answered by the `!` or by a fix to the rules.
- **The `!` and CI timing (D7).** `ci.yml` turns a title that matches `!:` after the type and scope into `API_BREAKING_ACCEPTED=true`, and the test then prints the breaking changes and passes. The workflow triggers on `opened`, `synchronize` and `reopened`, so **editing a title does not re-run it; the `!` takes effect on the next push.** `pr-title.yml` runs only the title lint on an edit. The failure message of `OpenApiCompatibility.Verdict` says this and names `docs/api-compatibility.md`.
- **The migration guard (D4).** `[ContractMigration("reason")]` (`Infrastructure.Common`, class-only, not inherited, reason required) marks a migration whose `Up` drops or renames a column or table. `MigrationTests.Every_migration_in_the_repository_is_additive_or_marked` instantiates every `Migration` in every module and building-block assembly and reads `UpOperations`: a `DropColumn`, `RenameColumn`, `DropTable` or `RenameTable` without the marker fails, a marker without such an operation fails, and a migration it cannot instantiate fails by name. `AlterColumn` that narrows a type or makes a column non-nullable is not checked; the page's checklist and review cover it. The marker is added by hand in the migration's own first commit, since an applied migration is never edited.
- **Usage data (D5).** `UseRequestLogging` adds `ApiVersion` (from `^/api/v([0-9]{1,4})(?:/|\z)` on the path, where `\z` is the end of the path, not `$`), an integer, absent off `/api`) and `ClientVersion` (the first `X-Client-Version` value when it is 1 to 64 characters of `A-Z a-z 0-9 . _ + -`; anything else is dropped, not logged, so a caller cannot forge log content or grow it). Nothing else reads the header, it is optional, and it is never a metric tag.
- **Register (D6).** The compatibility page holds the register (`Version`, `Status`, `Since`, `Owner`, `Retirement criteria`), with `v1` Active since 2026-10-07. `ApiVersionRegisterTests.Register_matches_the_committed_openapi_snapshot` reads it and the committed snapshot: a served `/api/vN` without a row fails, an `Active` or `Deprecated` row without a served version fails, a `Retired` row that is still served fails, and a status outside `Active`, `Deprecated`, `Retirement announced`, `Retired` fails. No Docker is needed.

## Consequences
- Positive: the rules are stated once, in terms of this template's routes, modules and migrations; a breaking API change, a destructive migration and an unregistered version each fail a build instead of reaching review; nothing changes an existing route, response shape, table or column, and no package is added.
- **The gate is a short list, not a proof.** It catches the common breaks; review catches the rest (semantics, error responses, tightened validation, authentication changed beyond "none to some"). Its limits are in the page, so that nobody reads a green gate as "compatible".
- **A `!` accepts a break, it does not make it safe.** The author can pass the gate by marking the title; the page says that a coexisting break needs a version, and `pr-title.yml` and review police the title.
- **Making a request property nullable needs the `!`,** because any request-side type change is breaking. That is a cost paid for a rule that cannot tell widening from narrowing.
- **A title edit needs a push.** Adding the `!` to a failing pull request means pushing a commit, even an empty one.
- **The migration guard reads `Up` only.** A narrowing `AlterColumn` can lose data and passes; so can `Sql(...)` that drops something. The checklist and review carry that.
- **Constructing a migration can throw** for one that uses provider checks. SQL Server is the only provider (ADR 0003) and every current migration builds; a failure names the migration.
- **`ClientVersion` is caller-controlled input in the log.** It is length- and character-limited, and never a metric tag.
- **Retirement stays a process.** The register keeps a version from existing unregistered; whether it may be retired is judged on usage, rollout and communication outside the code.
- **Not built, by design.** `Deprecation` and `Sunset` headers (no deprecated version exists to attach them to); a usage dashboard or alert (the data is emitted; an operator builds the query); an upgrade-path test, applying migrations up to N-1, loading data and applying N (it comes with the `migrate` mode and the runbooks of Plan 6, where the contract phase is also run; a project is required to have it, the template does not ship it yet); client-side minimum-version enforcement (`426 Upgrade Required`; a project that needs it adds it).
- Follow-up actions: the first coexisting break writes its own ADR (adopt `Asp.Versioning`, supersede ADR 0008) and does what the D2 bullet lists; Plan 6 adds the `migrate` mode, the upgrade-path test and the contract-phase runbook; the plans that add endpoints and tables from here on (social login first) are written and reviewed against the policy page.
