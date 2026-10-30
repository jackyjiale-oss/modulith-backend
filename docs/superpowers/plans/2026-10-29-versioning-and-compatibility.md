# API and Database Versioning Policy (Plan 5c) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the API and database compatibility policy and the checks a machine can enforce: a breaking-change gate on the OpenAPI document, a guard on destructive migrations, request-log properties for API and client version, and a version register kept in step with the OpenAPI document.

**Architecture:** Policy lives in one ADR (0031) and one page (`docs/api-compatibility.md`). Enforcement is three small, separately tested units: a pure OpenAPI classifier (integration test project), a migration guard over `Migration.UpOperations` (architecture tests, plus one attribute in `Infrastructure.Common`), and two properties added to the existing request-log enrichment (`Web.Common`). No package is added and no route, table or response changes.

**Tech Stack:** .NET 10, xunit.v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, NetArchTest, EF Core `Migration`/`MigrationOperation`, `System.Text.Json.Nodes`, Serilog, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-29-versioning-and-compatibility-design.md` (approved 2026-10-29). Read it with this plan; section numbers below refer to it.

## Global Constraints

- `TemplateName` is the placeholder; never hard-code a concrete project name. Files under `build/template-content/` replace their root counterparts in generated output.
- No new package (Central Package Management, licence check); `/api/v1` stays literal; no existing route, response shape, table or column changes.
- No `DateTime.Now`/`UtcNow`; tests first and green; the matching `docs/` page is updated in the same task; commit messages are Conventional Commits with a scope from `CONTRIBUTING.md` Section 2.3 (`persistence`, `observability`, `ci`, `adr`, `template`) and end with the session's co-author trailer.
- `X-Client-Version` is accepted when it is 1 to 64 characters of `A-Z a-z 0-9 . _ + -`; anything else is dropped, never logged. The first header value wins.
- Escape hatch for an intended break: `API_BREAKING_ACCEPTED=true`, set by CI only from a pull-request title matching `^[a-z]+(\([^)]*\))?!:`. Baseline file: `OPENAPI_BASELINE`.
- Rules for a breaking OpenAPI change are exactly the list in spec 5.1; anything else is reported as compatible or ignored. Change messages read `{METHOD} {path}: {description}`.
- Warnings are errors in Release; `dotnet format --verify-no-changes` must pass.

## Review Focus

- A self-referencing schema (a `$ref` cycle) must not hang or overflow the classifier (Task 3).
- A baseline file that is missing skips the check with a reason; one that is present but is not JSON fails naming the file, never passes silently (Task 3).
- `X-Client-Version` carrying CR/LF, a space, non-ASCII, 65 characters, or two header values must never reach the log verbatim (Task 2).
- `/api/v1x/…`, `/apiv1`, `/api/v99999999999/…` and `/API/V1/…` must not throw; the last matches, the others yield no `ApiVersion` (Task 2).
- A migration that cannot be instantiated or whose `Up` throws must fail the guard naming the migration, not crash the test run (Task 1). A register row written `V1` or with extra spaces must still match `/api/v1` (Task 4).

---

### Task 0: Short-path workspace

The build rejects project paths over a limit (`Directory.Build.targets`, error `TN0001`); `.claude/worktrees/…` is too long, which is why earlier plans used `C:\w\…`.

- [ ] **Step 1:** Leave the long-path worktree (`ExitWorktree` with `action: "keep"`), then from the main checkout remove it (`git worktree remove ".claude/worktrees/docs-versioning-policy"`; it is clean because the spec and this plan are committed) and run `git worktree add C:\w\p5c -b feature/versioning-policy worktree-docs-versioning-policy`. Expected: `C:\w\p5c` holds the spec and this plan.
- [ ] **Step 2:** In `C:\w\p5c` run `dotnet build -c Release`. Expected: 0 warnings, 0 errors (this proves the path is short enough before any change). Do all later work there.
- [ ] **Step 3:** Delete the now-unused branch: `git branch -d worktree-docs-versioning-policy`.

### Task 1: Contract-migration guard

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Persistence/ContractMigrationAttribute.cs`
- Create: `tests/TemplateName.ArchitectureTests/MigrationGuard.cs`, `tests/TemplateName.ArchitectureTests/MigrationTests.cs`
- Modify: `docs/building-blocks/infrastructure-common.md` (the migrations part: one paragraph on the attribute and the guard), `docs/coding-conventions.md` (the EF migrations row of Section 6: add "a migration that drops or renames a column or table carries `[ContractMigration]`")

**Interfaces:**
- Produces: `namespace TemplateName.Infrastructure.Common.Persistence; [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)] public sealed class ContractMigrationAttribute(string reason) : Attribute { public string Reason { get; } }`. The constructor throws `ArgumentException` for null, empty or whitespace.
- Produces (test project, `internal static class MigrationGuard`): `IReadOnlyList<string> Violations(IEnumerable<Type> migrationTypes)`. A type is read by `Activator.CreateInstance(type, nonPublic: true)` cast to `Migration`, then `UpOperations`. Messages: `"{FullName} drops or renames a column or table ({OperationType.Name}) and has no [ContractMigration]"`, `"{FullName} has [ContractMigration] but drops or renames no column or table"`, `"{FullName} could not be read: {exception.Message}"`.

- [ ] **Step 1: Write the failing tests in `MigrationTests`** with fixture migrations (`internal sealed class`, overriding `Up`) nested in the test class: `DropsColumn`, `DropsColumnMarked` (`[ContractMigration("2026-11 contract of Name")]`), `RenamesTable`, `AddsColumnOnly`, `MarkedButAddsOnly`, `Throws` (its `Up` throws `InvalidOperationException("boom")`). Tests:
  - `Guard_flags_a_dropped_column_without_the_marker`: `Violations([typeof(DropsColumn)])` has one entry containing `DropColumnOperation` and `[ContractMigration]`.
  - `Guard_flags_a_renamed_table_without_the_marker`: one entry containing `RenameTableOperation`.
  - `Guard_accepts_a_marked_contraction_and_an_additive_migration`: empty for `[DropsColumnMarked, AddsColumnOnly]`.
  - `Guard_flags_a_marker_on_a_migration_that_contracts_nothing`: one entry containing `drops or renames no column or table`.
  - `Guard_names_a_migration_it_cannot_read`: one entry containing the fixture's full name and `boom`.
  - `Attribute_requires_a_reason` (`[Theory]`: `null`, `""`, `"  "`): `new ContractMigrationAttribute(reason!)` throws `ArgumentException`.
  - `Every_migration_in_the_repository_is_additive_or_marked`: collects every non-abstract subclass of `Migration` in `Assemblies.All` (assert the count is greater than 0) and `MigrationGuard.Violations(...)` is empty.
- [ ] **Step 2:** Run `dotnet test tests/TemplateName.ArchitectureTests -c Release`. Expected: compile failure (`ContractMigrationAttribute`, `MigrationGuard` missing).
- [ ] **Step 3:** Implement the attribute (a primary-constructor class; `ArgumentException.ThrowIfNullOrWhiteSpace`) and `MigrationGuard`. Contracting operations are `DropColumnOperation`, `RenameColumnOperation`, `DropTableOperation`, `RenameTableOperation`; `Down` is never read; `AlterColumn` is not checked (spec 5.2).
- [ ] **Step 4:** Run the architecture tests. Expected: all pass, including the real-repository test (no existing `Up` contracts, checked 2026-10-29).
- [ ] **Step 5:** Write the two doc edits. Run `dotnet build -c Release` and `dotnet format --verify-no-changes`. Expected: clean.
- [ ] **Step 6: Commit** `feat(persistence): add the contract-migration marker and its architecture guard`.

### Task 2: `ApiVersion` and `ClientVersion` in the request log

**Files:**
- Modify: `src/BuildingBlocks/TemplateName.Web.Common/Observability/ObservabilityExtensions.cs` (`EnrichRequest`)
- Test: `tests/TemplateName.UnitTests/Web/ObservabilityExtensionsTests.cs`
- Modify: `docs/services/api.md` (pipeline row 7, `UseRequestLogging`), `docs/building-blocks/web-common.md` (the `UseRequestLogging` row): both new properties, the `X-Client-Version` header, and "usage per version is also in the ASP.NET Core metrics, whose `http.route` contains `/api/v1`".

**Interfaces:**
- Consumes: `IDiagnosticContext.Set(string, object?, bool destructureObjects)`, as the existing `UserId` test asserts (`Set("UserId", userId, destructureObjects: false)`).
- Produces: `EnrichRequest` also calls `Set("ApiVersion", <int>, destructureObjects: false)` and `Set("ClientVersion", <string>, destructureObjects: false)`; `internal const string ClientVersionHeader = "X-Client-Version"`.

- [ ] **Step 1: Write the failing tests** beside the existing enrichment tests, using `DefaultHttpContext` and `Substitute.For<IDiagnosticContext>()`:
  - `Request_enrichment_adds_the_api_version_of_an_api_route` (`[Theory]`): `/api/v1/auth/login`→1, `/api/v1`→1, `/api/v2/x/`→2, `/api/v12/x`→12, `/API/V1/x`→1; `Received(1).Set("ApiVersion", expected, destructureObjects: false)`.
  - `Request_enrichment_adds_no_api_version_off_the_versioned_api` (`[Theory]`): `/health/live`, `/openapi/v1.json`, `/api/version`, `/api/v1x/y`, `/apiv1`, `/api/v99999999999/x`; `DidNotReceive().Set("ApiVersion", Arg.Any<object?>(), Arg.Any<bool>())`.
  - `Request_enrichment_adds_a_valid_client_version` (`[Theory]`): `1.4.2`, `2.0.0+build.5`, `abc_DEF-1`, a 64-character value; `Set("ClientVersion", value, destructureObjects: false)`.
  - `Request_enrichment_drops_an_invalid_client_version` (`[Theory]`): `""`, 65 characters, `"1.0 beta"`, `"1.0\r\nX: y"`, `"é1"`; `DidNotReceive().Set("ClientVersion", …)`.
  - `Request_enrichment_uses_the_first_client_version_header_value`: two values `1.0`, `2.0` log `1.0`.
- [ ] **Step 2:** Run `dotnet test tests/TemplateName.UnitTests -c Release`. Expected: the new tests fail, the rest pass.
- [ ] **Step 3:** Extend `EnrichRequest`. Match the path with a `[GeneratedRegex(@"^/api/v(\d{1,4})(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]` (the digit cap makes overflow impossible) and validate the header with a second anchored `[GeneratedRegex(@"^[A-Za-z0-9._+\-]{1,64}$")]`. Take `StringValues.FirstOrDefault()`.
- [ ] **Step 4:** Run the unit tests. Expected: all pass. Make the docs edits; run `dotnet build -c Release` and `dotnet format --verify-no-changes`.
- [ ] **Step 5: Commit** `feat(observability): log the api version and the client version of every request`.

### Task 3: Breaking-change gate

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Documentation/OpenApiCompatibility.cs`, `OpenApiCompatibilityTests.cs`, `OpenApiBaselineTests.cs`
- Modify: `.github/workflows/ci.yml`, `docs/repository-management.md` (one sentence under the check names: the `build-test` job also compares the OpenAPI document with the base branch's)

**Interfaces:**
- Produces (namespace `TemplateName.IntegrationTests.Documentation`):
  - `public sealed record OpenApiChange(string Operation, string Description)`
  - `public sealed record OpenApiCompatibilityReport(IReadOnlyList<OpenApiChange> Breaking, IReadOnlyList<OpenApiChange> Compatible)`
  - `public static class OpenApiCompatibility` with `public const string BaselineVariable = "OPENAPI_BASELINE"`, `public const string AcceptedVariable = "API_BREAKING_ACCEPTED"`, `OpenApiCompatibilityReport Compare(string baselineJson, string currentJson)`, `string ReadBaseline(string path)` (returns the file's text; throws `InvalidOperationException` naming the path when the text is not a JSON object) and `string? Verdict(OpenApiCompatibilityReport report, bool breakingAccepted)` (null when there is nothing breaking or it is accepted; otherwise a message listing each `{Operation}: {Description}` and naming both ways out: `!` in the pull-request title, or a new API version per `docs/api-compatibility.md`).
- Consumes: `IntegrationTestBase(factory)`, `Client`, `Ct`, `JsonSnapshot.Normalize`, `RepositoryPaths` (as `OpenApiSnapshotTests` does).

Classifier behaviour (spec 5.1; OpenAPI 3.1.1 as the host emits it: `type` may be a string or an array, `$ref` into `components.schemas`). An operation is `"{METHOD} {path}"`. Request side = parameters and the `requestBody` schema; response side = the schemas of 2xx responses. `$ref` is resolved before comparing; `allOf` members are merged (union of `properties` and `required`); `oneOf`/`anyOf` are compared as a whole and any difference is breaking (a documented limit); a schema already being visited on the current path is not entered again.

- [ ] **Step 1: Write the failing classifier tests** in `OpenApiCompatibilityTests`, each building two minimal documents with a local helper and asserting on `Compare`. Breaking, one test per rule: `Removed_operation_is_breaking`, `Removed_path_is_breaking`, `Added_required_request_parameter_is_breaking`, `Optional_request_parameter_made_required_is_breaking`, `Added_required_body_property_is_breaking`, `Request_body_made_required_is_breaking`, `Changed_property_type_is_breaking` (request and response variants, including `["string"]` to `["string","null"]`), `Changed_property_format_is_breaking`, `Removed_response_property_is_breaking`, `Response_property_no_longer_required_is_breaking`, `Removed_success_status_code_is_breaking`, `Removed_request_enum_value_is_breaking`, `Security_added_to_an_open_operation_is_breaking`, `Removed_property_inside_a_referenced_array_item_is_breaking`, `Allof_members_are_merged_before_comparing`, `Changed_oneof_is_breaking`. Compatible and unreported: `Added_operation_is_compatible`, `Added_optional_request_parameter_or_property_is_compatible`, `Added_response_property_is_compatible`, `Added_response_enum_value_is_compatible`, `Removed_non_success_response_is_ignored`, `Identical_documents_have_no_changes`. Review focus: `Recursive_schema_terminates` (a `Node` schema with `children: items: $ref Node`, identical in both documents, returns empty lists; and with a removed property in `Node`, one breaking change, not a hang). `Verdict`: null for no breaking changes; null when accepted; a message containing the operation, the description and `docs/api-compatibility.md` otherwise. `ReadBaseline`: returns the text of a valid JSON object file; `Baseline_that_is_not_json_names_the_file` throws an `InvalidOperationException` whose message contains the path.
- [ ] **Step 2:** Run `dotnet test tests/TemplateName.IntegrationTests -c Release`. Expected: compile failure. (The classifier tests need no Docker; the rest of that project does, so locally rely on CI for the others.)
- [ ] **Step 3:** Implement `OpenApiCompatibility` with `System.Text.Json.Nodes`; keep one private method per rule group (operations, parameters, request body, responses, schema walk) so each stays small. Report order: operation, then description, ordinal.
- [ ] **Step 4:** Run the classifier tests only. Expected: pass.
- [ ] **Step 5: Write `OpenApiBaselineTests`** (`IntegrationTestBase`), one test `OpenApi_changes_are_compatible_with_the_baseline`: reads `OpenApiCompatibility.BaselineVariable`; unset, empty, or a missing file → `Assert.Skip("No OpenAPI baseline: set OPENAPI_BASELINE to a verified snapshot from the base branch.")`; otherwise reads the baseline with `ReadBaseline`, fetches `/openapi/v1.json`, normalises it with `JsonSnapshot.Normalize(body, "servers")`, and asserts `Verdict(Compare(baseline, current), breakingAccepted: string.Equals(Environment.GetEnvironmentVariable(AcceptedVariable), "true", StringComparison.Ordinal))` is null.
- [ ] **Step 6: `ci.yml`, job `build-test`:** set `fetch-depth: 0` on its checkout (the token is dropped after checkout, so the base branch must come with it); add before the Test step, `if: github.event_name == 'pull_request'`, `env: { BASE_REF: ${{ github.base_ref }}, PR_TITLE: ${{ github.event.pull_request.title }} }` (the title goes through `env`, never interpolated into the script), a step that writes `git show "origin/$BASE_REF:tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json"` to `$RUNNER_TEMP/openapi-baseline.json` (when `git show` fails, set nothing, so the test skips), appends `OPENAPI_BASELINE=…` to `$GITHUB_ENV`, and appends `API_BREAKING_ACCEPTED=true` when `printf '%s' "$PR_TITLE" | grep -Eq '^[a-z]+(\([^)]*\))?!:'`. A push to `main` runs none of it. Validate with `actionlint` if installed; otherwise read the YAML once for indentation. Edit `docs/repository-management.md`.
- [ ] **Step 7:** Run `dotnet build -c Release`, `dotnet format --verify-no-changes`, the classifier tests. Expected: clean, pass.
- [ ] **Step 8: Commit** `feat(ci): fail a pull request that breaks the published OpenAPI document`.

### Task 4: Policy documents and the version register

**Files:**
- Create: `docs/adr/0031-api-and-database-compatibility-policy.md`, `docs/api-compatibility.md`
- Create: `tests/TemplateName.ArchitectureTests/ApiVersionRegister.cs`, `ApiVersionRegisterTests.cs`
- Modify: `docs/adr/0008-literal-v1-routes.md` (follow-up line: "Compatibility policy, the gate and the register: ADR 0031."), `docs/README.md` (the ADR table after line 69; a row for the page under "Start here"; the opening paragraph's list of tests gains `ApiVersionRegisterTests` and the breaking-change gate), `docs/architecture/overview.md` (the ADR table after the 0030 row)

**Interfaces:**
- Produces (test project, `internal static class ApiVersionRegister`): `IReadOnlyList<(string Version, string Status)> Parse(string markdown)` reads the table under the `## Version register` heading up to the next `## ` heading (data rows only, version lower-cased and trimmed, e.g. `v1`); `IReadOnlySet<string> PathVersions(string openApiJson)` returns the distinct `vN` (lower-case) of every path starting `/api/v{N}/`; `IReadOnlyList<string> Violations(IReadOnlyList<(string Version, string Status)> register, IReadOnlySet<string> pathVersions)`. Statuses: `Active`, `Deprecated`, `Retirement announced`, `Retired`.
- Page register table columns: `| Version | Status | Since | Owner | Retirement criteria |`, first row `| v1 | Active | 2026-10-07 (ADR 0008) | The template's maintainers | None planned |`.

- [ ] **Step 1: Write the failing tests** in `ApiVersionRegisterTests`: `Parse_reads_the_register_rows` (header and separator skipped, `V1` and extra spaces normalised, rows after the next `##` ignored); `Path_versions_are_the_distinct_api_prefixes` (`/api/v1/a`, `/api/v1/b`, `/health` → `{v1}`); `Violations_flag_a_served_version_without_a_row`; `Violations_flag_an_active_or_deprecated_row_that_is_not_served` (both statuses); `Violations_accept_a_retired_or_announced_row_that_is_not_served`; `Violations_flag_an_unknown_status`; and the repository check `Register_matches_the_committed_openapi_snapshot`, reading `docs/api-compatibility.md` and the committed `…OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json` through `RepositoryPaths.Root`, asserting no violations and that the register has at least one row.
- [ ] **Step 2:** Run the architecture tests. Expected: compile failure.
- [ ] **Step 3:** Implement `ApiVersionRegister`, then write `docs/api-compatibility.md` with the eight sections of spec 4 in order, naming the real pieces: `OpenApiCompatibility`, `OPENAPI_BASELINE`, `API_BREAKING_ACCEPTED` and the title's `!`; `[ContractMigration]` and `MigrationTests`; `X-Client-Version`, `ApiVersion` and `ClientVersion`; the source document's tables and checklists restated, not linked. Write ADR 0031 in the format of ADR 0030 (Status Accepted, Date 2026-10-29, Deciders Lee Jia Le; Context, Options considered, Decision, Consequences, Follow-up) recording D1–D7 and the `Asp.Versioning` trigger of spec 6.3. Make the index edits.
- [ ] **Step 4:** Run the architecture tests. Expected: all pass, including `Every_adr_is_listed_in_the_docs_index_and_the_architecture_overview`.
- [ ] **Step 5:** `dotnet build -c Release`, `dotnet format --verify-no-changes`. Expected: clean.
- [ ] **Step 6: Commit** `docs(adr): add the compatibility policy, ADR 0031 and the version register`.

### Task 5: Contributor rules, roadmap, final verification

**Files:**
- Modify: `build/template-content/CLAUDE.md` (Hard rules: "Never remove or rename a column or table in the same change that stops using it; contract in a later release with `[ContractMigration]` ([`docs/api-compatibility.md`](docs/api-compatibility.md))"; Read before editing gains the page), `CONTRIBUTING.md` (Section 3 line 132: the PR checklist items; Section 4 if it lists pages), `.github/pull_request_template.md` (replace the migration line with "Migration included and reviewed (or none needed); a column or table dropped or renamed is a contract migration with `[ContractMigration]`; applied migrations are never edited"; extend the breaking-change line with "see `docs/api-compatibility.md`"), `docs/coding-conventions.md` (Section 7 intro: a breaking change to one of these needs a new API version per the page), `docs/blueprint-review.md` (Section 4 API-versioning row points to ADR 0031; Section 6 gains row **5c. API and database versioning policy** between 5b and the next row, linking the spec, this plan, the ADR and the page, "PR to be linked")

- [ ] **Step 1:** Make the edits. The root `CLAUDE.md` imports the template-content file; do not edit it.
- [ ] **Step 2:** Run `dotnet build -c Release` (expected 0 warnings, 0 errors), `dotnet format --verify-no-changes` (check its own exit code), `dotnet test tests/TemplateName.UnitTests -c Release` and `dotnet test tests/TemplateName.ArchitectureTests -c Release` (separate calls). Expected: all green.
- [ ] **Step 3:** Run `bash build/scripts/template-smoke.sh`. Expected `template-smoke: passed`. It is unreliable locally (segfaults, hangs, a bogus link-check failure after a Cygwin fork error); if it fails that way, rely on CI's `template-smoke` and say so.
- [ ] **Step 4: Commit** `docs(template): add the contraction rule to the contributor guides and the roadmap`.
- [ ] **Step 5:** Push `feature/versioning-policy` and open the pull request `feat(adr): add the API and database versioning policy` (a policy and its checks, not a breaking change: no `!`).
- [ ] **Step 6:** Integration tests need Docker, which cannot start on this machine; CI `build-test` is the source of truth for `OpenApiBaselineTests` and the unchanged suite. Confirm in CI that the baseline step ran and the baseline test did not skip, and that the existing snapshot test is unchanged.

## Self-review notes

- Spec coverage: 5.1 → Task 3; 5.2 → Task 1; 5.3 → Task 2; 5.4 → Task 4; section 4 and the ADR → Task 4; 6.1 file table → Tasks 1–5 (each row has an owner); section 7 schedule → the roadmap row of Task 5 and the spec itself; section 8 risks → Review Focus.
- Names used across tasks: `ContractMigrationAttribute`, `MigrationGuard.Violations`, `OpenApiCompatibility.Compare/ReadBaseline/Verdict`, `BaselineVariable`, `AcceptedVariable`, `ApiVersionRegister.Parse/PathVersions/Violations`, `ClientVersionHeader` are each defined once above.
