# Template Switches (Plan 7a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dotnet new modulith-backend` takes three switches (`notifications`, `includeSample`, `caching`); every combination generates a project that builds, passes its tests and names no removed part, proven by a CI matrix.

**Architecture:** Switches are template-engine conditionals written as comments (so the repository stays the all-on tree and builds as it is) plus `sources.modifiers` excludes in `template.json`. One script, `template-smoke.sh`, generates, checks and tests a combination; a new `dead-reference-check.sh` is its dead-reference check; a new workflow runs the combinations. No post-generation script.

**Tech Stack:** dotnet template engine (`dotnet new`), bash, GitHub Actions, xunit.v3 4.0.2 + Shouldly.

**Spec:** `docs/superpowers/specs/2026-11-03-template-switches-design.md` (approved 2026-11-04). Branch for the work: a new `feature/template-switches` cut from `main` after PR #23 merges.

## Global Constraints

- Three symbols in `template.json`: `notifications` (bool, default true), `includeSample` (bool, default true), `caching` (choice `redis` | `memory`, default `redis`).
- `dotnet new modulith-backend -n Acme.Hr` with no options generates exactly what it generates today.
- Nothing changes a route, a response, a table or a column of the all-on service (ADR 0031).
- The template repository itself still builds and tests as it is: the switch markers are comments. **Only on-only regions are written** (content present when the switch is on). Never an `#else`, and never a region present only when a switch is off: the repository is the all-on tree and would compile and run it. Content that exists only when a switch is off is a file excluded by a modifier when the switch is on, written so it passes or skips in the all-on repository.
- `caching` = `memory` removes `TemplateName.Platform.Redis`, its tests, its page and the StackExchange.Redis package (only if no remaining project uses it). The runtime default of `Caching:Provider` stays `memory` in both; no new guard is built (`CachingServiceCollectionExtensions` already fails fast). `CachingProvider.None` stays a runtime option.
- `notifications` off is pure removal. Auth is not changed; the generated README and `docs/modules/auth.md` say a project must add its own consumer to deliver verification and reset codes.
- Tests that depend on a removed module are excluded by file, or wrapped in conditionals where one file mixes concerns. Nothing is refactored onto test-only seams.
- Matrix: `{notifications, includeSample, caching}` = `{on, on, redis}`, `{off, off, memory}`, `{off, on, redis}`, `{on, off, redis}`, `{on, on, memory}`; each runs the dead-reference check, a Release build, the unit, architecture and integration tests.
- No post-generation script. No new package. `TemplateName` is the placeholder; never a concrete name. Conventional Commits; no `--no-verify`; the matching `docs/` page is updated in the same change; warnings are errors in Release.
- Integration tests cannot run on this machine (Docker is broken): they are compiled here (`dotnet build`) and run in CI only.

### Engine syntax (proved by generating, 2026-11-04)

Each marker sits on its own line; the engine removes the marker lines and, when the condition is false, the lines between them. Expressions: `notifications`, `includeSample`, `caching == "redis"` (and `!x`, `||`, `&&`).

| File type | Marker |
|---|---|
| `.cs`, `appsettings.json` | `//#if (expr)` … `//#endif` |
| `.csproj`, `.slnx`, `.props`, `.resx`, `.md` | `<!--#if (expr)-->` … `<!--#endif-->` |
| `.yml`, `Dockerfile`, `.sh` | `#if (expr)` … `#endif` (comment lines in those languages) |
| Mermaid block in `docs/architecture/overview.md` | `%%#if (expr)` … `%%#endif`, via `SpecialCustomOperations` (below) |

A `.md` file never uses the bare `#if` form (it is a heading). The `//#if` form also does not uncomment `////` lines; that is why only on-only regions exist.

```json
"SpecialCustomOperations": {
  "docs/architecture/overview.md": {
    "operations": [
      { "type": "conditional", "configuration": { "if": ["<!--#if"], "else": ["<!--#else"], "elseif": ["<!--#elseif"], "endif": ["<!--#endif"], "trim": "true", "wholeLine": "true", "evaluator": "C++" } },
      { "type": "conditional", "configuration": { "if": ["%%#if"], "else": ["%%#else"], "elseif": ["%%#elseif"], "endif": ["%%#endif"], "trim": "true", "wholeLine": "true", "evaluator": "C++" } }
    ]
  }
}
```

A custom config replaces the defaults for the files it matches, which is why that file lists both operations. A `sources.modifiers` entry looks like `{ "condition": "(!notifications)", "exclude": ["src/Modules/Notifications/**"] }` inside the first `sources` item.

### Decisions this plan adds to the spec

- **The OpenAPI snapshot is dropped** (the verified file, `OpenApiSnapshotTests`, and the tests in the architecture project that read it) whenever `notifications` or `includeSample` is off, because one file lists every route and cannot be conditional. The `OpenAPI baseline` step of `ci.yml` already only warns when the base branch has no snapshot.
- **Four lock files are not generated** (`src/Host/TemplateName.Api`, and the unit, architecture and integration test projects) when any switch is off, because their contents differ per combination and Dependabot would rewrite any marker in them. The README says to run `dotnet restore` once and commit them. The other lock files are identical in every combination (asserted in Task 2).
- **Dead-reference scope:** everything in the generated tree except `bin`, `obj`, `packages.lock.json` and `docs/adr/**` (decision history). ADRs whose subject is a removed part ship with it only when the part ships: 0019 and 0021 (notifications), 0027 (redis). A line is allowed when it contains `--notifications false`, `--includeSample false` or `--caching memory` (the sentence that says the part was left out), or when its file is in the script's allowlist (generic building blocks that name the optional Redis project on purpose, ADR 0027).
- **The three pair combinations** (`{off,on,memory}`, `{off,off,redis}`, `{on,off,memory}`) run on pushes to `main` and by `workflow_dispatch`, not on pull requests.
- **`template-matrix` is a separate workflow** (`template-matrix.yml`) excluded from generation, so generated projects do not carry five idle jobs.
- **`DispatchNotificationsAsync`** (integration test base) keeps its name and its outbox half in every combination; only the delivery half is on-only. Tests that read `EmailSender`, `InAppPusher` or Notifications types are the ones dropped, not every test that dispatches.

## Review Focus

1. Auth publishes an integration event that no handler consumes (`notifications` off in production): the outbox row completes (`ProcessedAt` set), it is not retried and does not pile up. Pinned in Task 5.
2. `--caching memory` with `Caching:Provider=redis` set: the host fails to start and the message names `AddPlatformRedis`. Pinned in Task 3.
3. A leftover marker (a typo in a marker, a file type that does not process it) leaves `#if (` text in generated output, or removes lines from the all-on output. Pinned by the leftover-marker check (Task 2) and the all-on comparison with `main` (Task 6).
4. A generated project's first `dotnet restore --locked-mode` after turning a switch off: the four missing lock files are created by plain `dotnet restore`, no other lock file changes. Pinned in Task 2 and re-proved on all-off in Task 6.
5. A combination without the OpenAPI snapshot keeps a green pipeline: no remaining test reads the snapshot. Pinned in Task 4 (architecture tests in the `includeSample` off combination fail if one still does).

---

## File map

| Path | Responsibility |
|---|---|
| `.template.config/template.json` | Symbols, modifiers, `SpecialCustomOperations`, excludes for template-only files |
| `build/scripts/dead-reference-check.sh` (new, not generated) | Fails when a generated tree still names a removed part |
| `build/scripts/tests/dead-reference-check.test.sh` (new, not generated) | Cases for the check; run by `template-smoke.sh` |
| `build/scripts/template-smoke.sh` | Gains the switch arguments and the new checks |
| `.github/workflows/template-matrix.yml` (new, not generated) | The five (and three) combinations |
| Source, config, test and doc files with markers | Listed per task |

---

### Task 1: The dead-reference check

**Files:**
- Create: `build/scripts/dead-reference-check.sh`
- Create: `build/scripts/tests/dead-reference-check.test.sh`
- Modify: `.template.config/template.json` (add both new files and `.github/workflows/template-matrix.yml` to the first source's `exclude`)

**Interfaces:**
- Produces: `bash build/scripts/dead-reference-check.sh <generated-dir> <notifications: true|false> <includeSample: true|false> <caching: redis|memory>`. Exit 0 clean; exit 1 with one `path:line: text` per hit on stdout; exit 2 on a usage error. Patterns (ERE, case-sensitive), applied only for a removed part: notifications `Notifications|Mailpit|mailpit|\bnotify\b`; sample `Modules\.Sample|SampleModule|SampleDbContext|SampleError|modules/sample|Sample module|LeaveRequest|leave request`; redis `Platform\.Redis|PlatformRedis|platform-redis|StackExchange|[Vv]alkey`. Allowlist (path regex, relative to the tree): redis `^src/BuildingBlocks/[^/]+\.Infrastructure\.Common/Caching/` and `^src/BuildingBlocks/[^/]+\.Web\.Common/Security/`; notifications `^src/BuildingBlocks/[^/]+\.Infrastructure\.Common/Jobs/BatchedCleanupJob\.cs$`. Each allowlist entry carries a one-line reason comment in the script.

- [ ] **Step 1: Write the failing test** `dead-reference-check.test.sh`, in the style of `build/scripts/tests/maintenance-scripts.test.sh` (a temp root, one throwaway tree per case, ends with `dead-reference-check: passed`). Cases, each asserting the exit code and, for hits, that stdout names the file and line:
  - all three switches on, tree full of every pattern: exit 0.
  - `notifications=false`, `src/A.cs` containing `Modules.Notifications`: exit 1, stdout contains `src/A.cs:1`.
  - same text in `docs/adr/0020-x.md`, in `obj/x.cs`, in `bin/x.dll.txt`, in `src/packages.lock.json`: exit 0.
  - a line `Notifications was left out: --notifications false` in `README.md`: exit 0; the same line without the flag text: exit 1.
  - `caching=memory`, `PlatformRedis` in `src/BuildingBlocks/Acme.Infrastructure.Common/Caching/X.cs`: exit 0; in `src/Host/Acme.Api/Program.cs`: exit 1.
  - `includeSample=false`, `LeaveRequest` in `tests/T.cs`: exit 1; `Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>` (the tracing sampler): exit 0.
  - `caching=redis`, `Platform.Redis` anywhere: exit 0.
  - wrong argument count or `notifications=maybe`: exit 2.
- [ ] **Step 2: Run it to verify it fails.** `bash build/scripts/tests/dead-reference-check.test.sh` — Expected: FAIL (script missing).
- [ ] **Step 3: Implement** `dead-reference-check.sh` with the interface above. Use `grep -rnE -I` over the tree with the exclusions as `--exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude=packages.lock.json` and a path filter for `docs/adr/`; filter allowed lines and allowlisted paths in a `while read` loop so the reason comment sits next to each rule.
- [ ] **Step 4: Run the test again.** Expected: `dead-reference-check: passed`.
- [ ] **Step 5: Commit.**

```bash
git add build/scripts/dead-reference-check.sh build/scripts/tests/dead-reference-check.test.sh .template.config/template.json
git commit -m "test: add the dead-reference check for generated projects"
```

---

### Task 2: Symbols, smoke arguments and the new checks (red for every switch)

**Files:**
- Modify: `.template.config/template.json` (the three symbols; `SpecialCustomOperations`; the lock-file modifier)
- Modify: `build/scripts/template-smoke.sh`

**Interfaces:**
- Consumes: Task 1's `dead-reference-check.sh`.
- Produces: `bash build/scripts/template-smoke.sh [--notifications true|false] [--includeSample true|false] [--caching redis|memory] [--integration] [--generate-only] [--output DIR]`. `--output` keeps the generated project in `DIR` (must not exist; use a short path such as `C:\w\g1`); `--generate-only` stops after the generation checks (about two minutes); `--integration` adds `dotnet test --project tests/$project_name.IntegrationTests --no-build -c Release` after the unit and architecture tests. No arguments behaves exactly as today. The switch values are passed to `dotnet new` as `--notifications`, `--includeSample`, `--caching`.
- Produces: the lock-file modifier `{ "condition": "(!notifications || !includeSample || caching == \"memory\")", "exclude": ["src/Host/TemplateName.Api/packages.lock.json", "tests/TemplateName.UnitTests/packages.lock.json", "tests/TemplateName.ArchitectureTests/packages.lock.json", "tests/TemplateName.IntegrationTests/packages.lock.json"] }`.

- [ ] **Step 1: Add the symbols and the lock-file modifier** to `template.json` (symbol names and defaults as in Global Constraints; `caching` choices `redis` and `memory`), plus the `SpecialCustomOperations` block above.
- [ ] **Step 2: Extend `template-smoke.sh`.** Add argument parsing and pass the values to `dotnet new`. New steps, each failing through the existing `fail` helper:
  1. Before installing the template: run `bash build/scripts/tests/dead-reference-check.test.sh`.
  2. Leftover markers: no file of the generated tree (excluding `bin`, `obj`) matches `(//|<!--|^[[:space:]]*)#(if|elseif|else|endif)([[:space:]]|\(|-->|$)`. If a real hit is a legitimate preprocessor line, narrow the pattern with an explicit path exclusion and a comment, never a blanket one.
  3. Part presence, with the generated project name substituted: for each part, its paths exist when kept and do not exist when removed. notifications: `src/Modules/Notifications`, `tests/$p.UnitTests/Notifications`, `tests/$p.IntegrationTests/Notifications`, `docs/modules/notifications.md`, `docs/adr/0019-embedded-scriban-notification-templates.md`, `docs/adr/0021-signalr-hub-tickets.md`. includeSample: `src/Modules/Sample`, `tests/$p.UnitTests/Sample`, `tests/$p.IntegrationTests/Sample`, `docs/modules/sample.md`. caching: `src/BuildingBlocks/$p.Platform.Redis`, `tests/$p.IntegrationTests/Redis`, `tests/$p.UnitTests/Platform`, `docs/building-blocks/platform-redis.md`, `docs/adr/0027-optional-redis-through-one-project.md`. The OpenAPI snapshot `tests/$p.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json` exists only when both `notifications` and `includeSample` are on.
  4. `bash build/scripts/dead-reference-check.sh "$out" <values>`.
  5. After `--generate-only`'s checks, stop with `template-smoke: passed (generate only)`.
  6. Restore: with every switch on, `dotnet restore --locked-mode` as today. With any switch off: record the SHA-256 of every `packages.lock.json`, run `dotnet restore`, then assert the lock files that appeared are exactly the four of the modifier and that no lock file that existed before changed; then `dotnet restore --locked-mode` must pass.
  7. Add the three new template-only files to the `forbidden` list (`build/scripts/dead-reference-check.sh`, `build/scripts/tests/dead-reference-check.test.sh`, `.github/workflows/template-matrix.yml`).
- [ ] **Step 3: Verify the all-on default is unchanged.** Run `bash build/scripts/template-smoke.sh --generate-only`. Expected: `template-smoke: passed (generate only)`.
- [ ] **Step 4: Verify the red states.** For each of `--notifications false`, `--includeSample false`, `--caching memory` run `bash build/scripts/template-smoke.sh --generate-only <switch>`. Expected for each: FAIL lines from the part-presence check (the part is still generated) and the dead-reference check (hits listed). These are the tests Tasks 3 to 5 turn green.
- [ ] **Step 5: Commit.**

```bash
git add .template.config/template.json build/scripts/template-smoke.sh
git commit -m "build: add the template switches and their smoke checks"
```

---

### Task 3: `caching` = `memory`

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Caching/CachingProviderWithoutRedisTests.cs`
- Modify with markers (`caching == "redis"` regions): `src/Host/TemplateName.Api/Program.cs` (`using TemplateName.Platform.Redis;` and the comment plus `AddPlatformRedis` block); `src/Host/TemplateName.Api/TemplateName.Api.csproj`, `tests/TemplateName.UnitTests/TemplateName.UnitTests.csproj` and `tests/TemplateName.ArchitectureTests/TemplateName.ArchitectureTests.csproj` (the `Platform.Redis` project reference); `tests/TemplateName.IntegrationTests/TemplateName.IntegrationTests.csproj` (`Testcontainers.Redis`); `TemplateName.slnx`; `build/docker/Dockerfile` (the `COPY` line); `docker-compose.yml` (the `valkey` service and the two comment lines naming it); `src/Host/TemplateName.Api/appsettings.json` (the `Caching:Redis` block; the .NET JSON configuration provider accepts the trailing comma that may remain); `Directory.Packages.props` (`StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, `Testcontainers.Redis`: wrap each only after `grep` shows no remaining `PackageReference` to it); `tests/TemplateName.ArchitectureTests/Assemblies.cs` (the `using`, the `PlatformRedis` property and its entries in `BuildingBlocks`); `tests/TemplateName.ArchitectureTests/LayeringTests.cs` (the `Only_the_host_references_Platform_Redis` test and its helpers); `tests/TemplateName.ArchitectureTests/DocumentationTests.cs` (`"platform-redis"` in the building-block list); `tests/TemplateName.IntegrationTests/Caching/CachingProviderTests.cs` (the Redis-dependent members and `using StackExchange.Redis;`); `build/scripts/container-verify.sh` (the comment line naming `AddPlatformRedis`).
- Modify with a modifier `(caching == "memory")` excluding: `src/BuildingBlocks/TemplateName.Platform.Redis/**`, `tests/TemplateName.IntegrationTests/Redis/**`, `tests/TemplateName.UnitTests/Platform/**`, `tests/TemplateName.IntegrationTests/Infrastructure/ValkeyFixture.cs`, `docs/building-blocks/platform-redis.md`, `docs/adr/0027-optional-redis-through-one-project.md`. A second modifier `(caching == "redis")` excludes `tests/TemplateName.IntegrationTests/Caching/CachingProviderWithoutRedisTests.cs`.
- Modify docs (on-only regions; rewrite a sentence so the Redis clause sits on its own line): `build/template-content/README.md` (the "Optional: Redis" section and the links), `docs/README.md` (the `platform-redis` and ADR 0027 rows), `docs/architecture/overview.md` (the Redis node, its arrows, the ADR 0027 row; `%%#if` inside the diagram), `docs/services/api.md`, `docs/modules/auth.md`, `docs/modules/notifications.md`, `docs/building-blocks/infrastructure-common.md`, `docs/building-blocks/web-common.md`, `docs/deployment/production-checklist.md`, `docs/deployment/docker.md`, `CONTRIBUTING.md`. Add to `docs/building-blocks/infrastructure-common.md`, off the Redis markers, one sentence: a project generated with `--caching memory` that later sets `Caching:Provider=redis` has to add the `Platform.Redis` project back; the host then refuses to start and says so.

**Interfaces:**
- Consumes: Task 2's smoke options and checks; the `Redis/RedisTestHosts.cs` pattern for building a host with settings (it is excluded in the memory combination, so copy the `WithWebHostBuilder` idea, not the class).
- Produces: nothing later tasks consume except the conditional pattern.

- [ ] **Step 1: Write the failing test** `CachingProviderWithoutRedisTests` in the Caching folder, a test `Redis_provider_without_the_project_fails_at_start`: it calls `Assert.SkipWhen(Type.GetType("TemplateName.Platform.Redis.RedisCachingOptions, TemplateName.Platform.Redis") is not null, "The Redis project is referenced.")`, then builds a host from `IntegrationTestWebAppFactory` with `Caching:Provider` = `redis` and asserts that starting it throws and that the exception message contains `AddPlatformRedis`. In the all-on repository it skips and compiles; in the memory combination it runs.
- [ ] **Step 2: Run the red smoke state.** `bash build/scripts/template-smoke.sh --generate-only --caching memory` — Expected: FAIL (unchanged from Task 2).
- [ ] **Step 3: Apply the markers, modifiers and doc edits** listed in Files.
- [ ] **Step 4: Generate and check.** `bash build/scripts/template-smoke.sh --generate-only --caching memory` — Expected: `template-smoke: passed (generate only)`. Then `bash build/scripts/template-smoke.sh --generate-only` (all-on) — Expected: passes, so nothing leaked into the default.
- [ ] **Step 5: Build and run the unit and architecture tests of the combination.** `bash build/scripts/template-smoke.sh --caching memory --output C:/w/g1` — Expected: `template-smoke: passed`. If a compile error names a file not listed in Files, mark it with the same rule (exclude the file in the modifier when the whole file needs the part, otherwise wrap the members) and rerun.
- [ ] **Step 6: Compile the integration tests of the combination.** `dotnet build C:/w/g1/tests/Acme.Smoke.IntegrationTests -c Release --no-restore` — Expected: 0 errors, 0 warnings.
- [ ] **Step 7: In the template repository itself** run `dotnet build -c Release` and `dotnet test --project tests/TemplateName.UnitTests -c Release` and `...ArchitectureTests`. Expected: green with the counts of `main` (2706 unit, 131 architecture) plus the new skipped test.
- [ ] **Step 8: Commit.**

```bash
git add -A
git commit -m "feat(template): add the caching switch"
```

---

### Task 4: `includeSample` = `false`

**Files:**
- Modify with markers (`includeSample` regions): `src/Host/TemplateName.Api/Program.cs` (`using`, `AddSampleModule`, `MapSampleEndpoints`); the three project files that reference it (`TemplateName.Api.csproj`, `TemplateName.UnitTests.csproj`, `TemplateName.ArchitectureTests.csproj`); `TemplateName.slnx` (the `Sample` folder block); `build/docker/Dockerfile`; `tests/TemplateName.ArchitectureTests/Assemblies.cs`; `tests/TemplateName.ArchitectureTests/AuditingTests.cs` (the `LeaveRequest` entry of the audited-entity list and the `ignored["LeaveRequest"]` assertion; the test names that say "five" stay as they are, only the list changes); `tests/TemplateName.ArchitectureTests/DocumentationTests.cs` (the `"Sample"` `InlineData` rows and the `/api/v1/sample/...` row); `tests/TemplateName.UnitTests/Localization/ErrorMessageLocalizerTests.cs`, `tests/TemplateName.UnitTests/Web/ErrorResponsePipelineTests.cs` and `tests/TemplateName.UnitTests/SharedKernel/Auditing/EntityChangesRecordedEventTests.cs` (only the members that use Sample types; confirm by compiling); the integration files that use Sample types: `Audit/AuditTrailEndToEndTests.cs`, `Audit/AuditConsumerTests.cs`, `Audit/AuditPersistenceTests.cs`, `Audit/AuditTrailEndpointTests.cs`, `Audit/AuditTrailsCleanupJobTests.cs`, `Idempotency/IdempotencyTests.cs`, `Idempotency/IdempotencyCleanupJobTests.cs`, `Localization/LocalizationTests.cs`, `Auth/AuthSeederTests.cs`, `Notifications/AdminDeliveryTests.cs`, `Outbox/OutboxCleanupJobTests.cs` (whole file in markers when every test uses Sample, otherwise the members only); the docs that name Sample: `build/template-content/README.md`, `docs/README.md`, `docs/architecture/overview.md` (node, arrows, the module table row, the sentence "four modules today"), `docs/coding-conventions.md`, `docs/modules/audit.md`, `docs/modules/auth.md`, `docs/building-blocks/web-common.md`, `docs/building-blocks/application-common.md`.
- Modify `template.json` modifiers: `(!includeSample)` excludes `src/Modules/Sample/**`, `tests/TemplateName.UnitTests/Sample/**`, `tests/TemplateName.IntegrationTests/Sample/**`, `docs/modules/sample.md`; `(!notifications || !includeSample)` excludes `tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json` and `tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.cs`.
- Modify with the marker condition `(notifications && includeSample)`: the members of `tests/TemplateName.ArchitectureTests/ApiVersionRegisterTests.cs` (`Register_matches_the_committed_openapi_snapshot`) and `OpenApiCompatibilityTests.cs` (the member that reads the snapshot) that read the snapshot file.

**Interfaces:**
- Consumes: Tasks 2 and 3's pattern. `docs/modules/audit.md` keeps describing Auth's audited entities; only the Sample example is removed.
- Produces: the `(!notifications || !includeSample)` snapshot modifier Task 5 relies on.

- [ ] **Step 1: Red state.** `bash build/scripts/template-smoke.sh --generate-only --includeSample false` — Expected: FAIL (as in Task 2). The architecture tests of the combination are the second red test: after Step 3 they must pass, and they fail if `AuditingTests` still lists `LeaveRequest` or a snapshot test still reads the missing file.
- [ ] **Step 2: Apply the markers, modifiers and doc edits** listed in Files. Where the Audit integration tests exist to prove Audit through an audited entity, wrap the whole file when it needs Sample; do not rewrite any test onto Auth entities.
- [ ] **Step 3: Generate and check.** `bash build/scripts/template-smoke.sh --generate-only --includeSample false` — Expected: `template-smoke: passed (generate only)`; the all-on `--generate-only` still passes.
- [ ] **Step 4: Build and run the unit and architecture tests.** `bash build/scripts/template-smoke.sh --includeSample false --output C:/w/g2` — Expected: `template-smoke: passed`. Fix any further compile error with the Task 3 rule.
- [ ] **Step 5: Compile the integration tests.** `dotnet build C:/w/g2/tests/Acme.Smoke.IntegrationTests -c Release --no-restore` — Expected: 0 errors, 0 warnings.
- [ ] **Step 6: In the repository** run the Task 3 Step 7 commands. Expected: the same counts.
- [ ] **Step 7: Commit.**

```bash
git add -A
git commit -m "feat(template): add the includeSample switch"
```

---

### Task 5: `notifications` = `false`

**Files:**
- Modify with markers (`notifications` regions): `src/Host/TemplateName.Api/Program.cs` (`using`, `AddNotificationsModule`, `MapNotificationsEndpoints`, `MapNotificationsHub`); the four project files (`TemplateName.Api.csproj` and the three test projects, including the two `EmbeddedResource` lines in the unit and integration projects); `TemplateName.slnx`; `build/docker/Dockerfile`; `docker-compose.yml` (the `mailpit` service and its header lines); `src/Host/TemplateName.Api/appsettings.json` (the `Notifications` block); `Directory.Packages.props` (`Scriban`, `MailKit`, each only after `grep` shows no remaining use); `tests/TemplateName.ArchitectureTests/Assemblies.cs`; `build/scripts/container-verify.sh` (the comment lines naming Notifications).
- Integration test infrastructure (markers): `Infrastructure/IntegrationTestWebAppFactory.cs` (the Notifications `using`s, the `EmailSender` and `InAppPusher` properties, the `Notifications:Delivery:Enabled` setting, the `IEmailTransport`, `IInAppNotificationPusher` and `INotificationTypeSource` registrations); `Infrastructure/IntegrationTestBase.cs` (the `EmailSender.Clear()` and `InAppPusher.Reset()` calls, and the delivery half of `DispatchNotificationsAsync`: `deliveries` and `delivered` become on-only, the outbox half and the method name stay).
- Modify with a modifier `(!notifications)` excluding: `src/Modules/Notifications/**`, `tests/TemplateName.UnitTests/Notifications/**`, `tests/TemplateName.IntegrationTests/Notifications/**`, `tests/TemplateName.IntegrationTests/Infrastructure/RecordingEmailSender.cs`, `.../RecordingInAppNotificationPusher.cs`, `.../NotificationHubClient.cs`, `tests/TemplateName.IntegrationTests/Auth/AuthFlowTests.cs`, `.../Auth/MfaFlowTests.cs`, `tests/TemplateName.IntegrationTests/Redis/RedisBackplaneTests.cs`, `docs/modules/notifications.md`, `docs/adr/0019-embedded-scriban-notification-templates.md`, `docs/adr/0021-signalr-hub-tickets.md`.
- Members wrapped (markers) because they read the recorded emails or use Notifications types: `Auth/PasswordTests.cs`, `Auth/RegistrationTests.cs`, `Auth/AdminUserTests.cs`, `Auth/ExternalLoginManagementTests.cs`, `Auth/ExternalLoginTests.cs`, `Auth/IntegrationEventPublishingTests.cs` (the tests that call `Factory.EmailSender` or `Factory.InAppPusher`; the ones that only dispatch and read `Factory.IntegrationEvents` stay), `Auth/AuthSeederTests.cs`, `Audit/AuditTrailEndToEndTests.cs`, `Persistence/CleanupSchemaTests.cs`, `Inbox/InboxCleanupJobTests.cs`, `tests/TemplateName.UnitTests/Web/FixedWindowLimiterFactoryTests.cs`, `tests/TemplateName.UnitTests/Infrastructure/OutboxAbandonedGaugeTests.cs` and `tests/TemplateName.UnitTests/Platform/SignalRBackplaneTests.cs` (whichever of these the compiler flags; `Platform/**` is already excluded in the memory combination, so the last one only matters for `{off, on, redis}`).
- Create: `tests/TemplateName.IntegrationTests/Outbox/OutboxWithoutHandlersTests.cs` (runs in every combination).
- Docs (markers): `build/template-content/README.md` (the Mailpit lines, the Notifications row, and a "Notifications left out" paragraph that reads correctly in the all-on repository: "A project generated with `--notifications false` has no consumer for Auth's integration events; add your own to deliver verification and reset codes"), `docs/modules/auth.md` (its 27 mentions: each clause on its own marked line; plus the same note), `docs/README.md`, `docs/architecture/overview.md`, `docs/services/api.md`, `docs/coding-conventions.md`, `docs/modules/audit.md`, `docs/building-blocks/web-common.md`, `docs/building-blocks/infrastructure-common.md`, `docs/deployment/docker.md`, `docs/deployment/production-checklist.md`, `build/template-content/CLAUDE.md` (the sentences saying Notifications ships and that messages go through it; the second clause stays only when the module ships).

**Interfaces:**
- Consumes: the Task 4 snapshot modifier; Tasks 2 to 4's pattern.
- Produces: nothing for later tasks.

- [ ] **Step 1: Write the failing test** `OutboxWithoutHandlersTests` (class in the Outbox folder, derived from `IntegrationTestBase`): `Message_for_an_event_without_a_handler_is_marked_processed` registers a user through `/api/v1/auth/register` on a host built with `WithWebHostBuilder` that removes every `IIntegrationEventHandler<>` registration, runs `OutboxDispatcher<AuthDbContext>.ProcessBatchAsync` until it returns 0, and asserts that no `auth` outbox row has `ProcessedAt` null and none has an attempt count above 0. If it fails in the all-on repository, the dispatcher retries messages with no consumer: stop and report it, because that is a production finding (Review Focus 1), not a test problem.
- [ ] **Step 2: Red state.** `bash build/scripts/template-smoke.sh --generate-only --notifications false` — Expected: FAIL.
- [ ] **Step 3: Apply the markers, modifiers and doc edits.**
- [ ] **Step 4: Generate and check.** `bash build/scripts/template-smoke.sh --generate-only --notifications false` — Expected: `template-smoke: passed (generate only)`; the all-on `--generate-only` still passes.
- [ ] **Step 5: Build and run the unit and architecture tests.** `bash build/scripts/template-smoke.sh --notifications false --output C:/w/g3` — Expected: `template-smoke: passed`.
- [ ] **Step 6: Compile the integration tests.** `dotnet build C:/w/g3/tests/Acme.Smoke.IntegrationTests -c Release --no-restore` — Expected: 0 errors, 0 warnings. Fix the next compile error with the Task 3 rule until clean.
- [ ] **Step 7: Count what the combination keeps.** `dotnet test --project C:/w/g3/tests/Acme.Smoke.UnitTests -c Release --no-build --list-tests` and the same for the integration project; write both counts, and the number of integration tests dropped against the repository, into the commit message body so the coverage cost is on record.
- [ ] **Step 8: In the repository** run the Task 3 Step 7 commands. Expected: the same counts plus the new test (it runs, because the repository has the integration project; it is verified in CI).
- [ ] **Step 9: Commit.**

```bash
git add -A
git commit -m "feat(template): add the notifications switch"
```

---

### Task 6: All three off, lock files, all-on comparison

**Files:**
- Modify: whichever files the all-off generation exposes (expected: only combined conditions, for example `tests/TemplateName.IntegrationTests/Redis/RedisBackplaneTests.cs` needs `(notifications && caching == "redis")` where it is wrapped).

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: All off, full.** `bash build/scripts/template-smoke.sh --notifications false --includeSample false --caching memory --output C:/w/g4` — Expected: `template-smoke: passed`, including the lock-file assertion (exactly four lock files created, none changed). Then `dotnet build C:/w/g4/tests/Acme.Smoke.IntegrationTests -c Release --no-restore` — Expected: 0 errors, 0 warnings.
- [ ] **Step 2: The three pair combinations compile.** For `{off, on, memory}`, `{off, off, redis}` and `{on, off, memory}`: generate with `--output C:/w/g5` (and `g6`, `g7`), then `dotnet build` the generated solution `-c Release`. Expected: 0 errors, 0 warnings each. Fix any file whose conditions only break in a pair by writing the combined condition.
- [ ] **Step 3: The all-on generation equals `main`'s.** Export `origin/main` with `git archive origin/main | tar -x -C C:/w/base`; install each tree into its own private hive and generate `Acme.Smoke` from both with no options; run `diff -r C:/w/base-out C:/w/new-out`. Expected: no output (identical file lists and contents). Keep the commands in the commit message body; this check is run once, because `main` keeps moving.
- [ ] **Step 4: Remove the scratch folders** `C:\w\g1` to `g7`, `base`, `base-out`, `new-out`, `sx*`.
- [ ] **Step 5: Commit** (only if a file changed) `fix(template): combine the switch conditions`.

---

### Task 7: The matrix workflow

**Files:**
- Create: `.github/workflows/template-matrix.yml`
- Modify: `docs/repository-management.md`

**Interfaces:**
- Consumes: the smoke options of Task 2.

- [ ] **Step 1: Write the workflow.** Name `Template matrix`; triggers `pull_request` (opened, synchronize, reopened), `push` to `main`, `workflow_dispatch`; `permissions: contents: read`; the same `concurrency` and `env` as `ci.yml`. Job `template-matrix`, `name: template-matrix (${{ matrix.name }})`, `runs-on: ubuntu-latest`, `timeout-minutes: 45`, `strategy: fail-fast: false`, `matrix.include` of the five combinations (names `all-on`, `all-off`, `no-notifications`, `no-sample`, `memory-cache`, each with a `switches` string such as `--notifications false --includeSample false --caching memory`; `all-on` has an empty string). Steps: `actions/checkout@v7` (`persist-credentials: false`), `actions/setup-dotnet@v6` with `global-json-file: global.json`, then a `run` that reads `SWITCHES` from `env:` and calls `read -r -a args <<< "$SWITCHES"; bash build/scripts/template-smoke.sh --integration "${args[@]}"` (no matrix value is interpolated into the script). A second job `template-matrix-pairs` with the same steps and the three pair combinations (`no-notifications-memory`, `no-notifications-no-sample`, `no-sample-memory`), `if: github.event_name != 'pull_request'`.
- [ ] **Step 2: Lint it.** `actionlint` is not installed here; push the branch and read the `actionlint` check of the pull request. Expected: green. (The `ci.yml` job named `actionlint` lints every workflow file.)
- [ ] **Step 3: Update `docs/repository-management.md`:** the new workflow, the check names `template-matrix (all-on)` and so on, and the rule that they join the required checks only after the matrix has run once on a pull request (Section 3 style row).
- [ ] **Step 4: Commit and push; read the first runs.** For each combination: green, or the failing test named. A failing integration test in a combination is the real test of Tasks 3 to 5: fix it with the Task 3 rule, push again. Do not mark this step done until all five legs are green on the pull request.
- [ ] **Step 5: Commit.**

```bash
git add .github/workflows/template-matrix.yml docs/repository-management.md
git commit -m "ci: run the template switch matrix"
```

---

### Task 8: Decision record, README and roadmap

**Files:**
- Create: `docs/adr/0035-template-switches-by-engine-conditionals.md`
- Modify: `build/template-content/README.md` (a "Switches" section), `docs/README.md` and `docs/architecture/overview.md` (the ADR 0035 row in both ADR tables), `docs/blueprint-review.md` (Section 6: row 7 split into 7a, 7b, 7c; 7a done)

**Interfaces:**
- Consumes: the decisions of this plan and the spec (S1 to S7).

- [ ] **Step 1: Write the ADR** "Template switches by engine conditionals, tests dropped with their module": context, the seven decisions S1 to S7 of the spec, and this plan's additions (snapshot dropped, four lock files not generated, ADRs as history in the dead-reference scope, pair combinations off the pull-request path), consequences (coverage lost per switch: counts from Task 5 Step 7; a project without Sample has no integration tests for idempotency, localization and audit through it).
- [ ] **Step 2: Write the README "Switches" section** (shipped to every project): the three symbols and their invocations, what each removes, the Auth-events note, the test-coverage note, the OpenAPI snapshot note, and the one command after turning a switch off (`dotnet restore`, then commit the lock files). Every sentence naming a part contains the matching `--… false` text so the dead-reference check allows it.
- [ ] **Step 3: Index the ADR** in both tables, and update `docs/blueprint-review.md`.
- [ ] **Step 4: Verify.** `bash build/scripts/template-smoke.sh` (all-on, about 15 minutes) — Expected: `template-smoke: passed`; `dotnet test --project tests/TemplateName.ArchitectureTests -c Release` — Expected: green (the ADR index tests).
- [ ] **Step 5: Commit.**

```bash
git add -A
git commit -m "docs: record the template switches decision"
```

---

## Self-review

- **Spec coverage:** S1 Task 2; S2 Task 3; S3 Tasks 5 and 8; S4 Global Constraints and Tasks 2 to 5; S5 Tasks 3 to 5; S6 Tasks 6 and 7 (the pairs decided); S7 Task 2. Section 4's three removal rows: Tasks 3, 4, 5. Section 5: Tasks 1, 2, 7. Section 6: Tasks 7 and 8. Section 7: Task 6 (the all-on comparison, run once) and Task 2 (marker and part checks on every run; `DocumentationTests` and `LayeringTests` run in every generated project). Section 8: comment syntax (the table above), mixed test files (Task 5 lists them), matrix cost (45-minute timeouts, pairs off pull requests, all-on kept as the fast `template-smoke`), lock files (Task 2), Windows path length (`path-guard-check.sh` runs inside the smoke script on every generated project).
- **Gaps against the spec, stated:** the spec's Section 4 omitted the OpenAPI snapshot and the lock-file handling; this plan adds both (see Decisions).
- **Names:** `dead-reference-check.sh` arguments, the smoke options and the symbol names are identical in every task that uses them.
