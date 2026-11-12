# Template switches and the generation matrix (Plan 7a) — design

- Status: Draft for review (2026-11-03). The design was approved part by part in the design session (the three switches, the mechanism, the test policy, the matrix, the documents); this file is the written form.
- Date: 2026-11-03
- Roadmap row: 7a, the first of three parts of row 7, "Packaging → v1.0", in `docs/blueprint-review.md` Section 6 (7b is the `modulith-module` item template, 7c is pack and publish, README and runbooks, and the v1.0 cut).
- Builds on: Plans 1 to 6 and the maintenance screen (main `ea41c40`); the review's decisions on switches (Section 4: "Module toggles", "Template switches"), ADR 0027 (optional Redis through one project), `docs/repository-management.md`.
- Source: `BACKEND_TEMPLATE_BLUEPRINT.md` 15.1 and Phase 8, as amended by `docs/blueprint-review.md` (the review wins: three switches in v1, introduced with the module they control).

## 1. Intent

**Outcome.** A project generated with any combination of the three v1 switches builds, passes its tests, and contains no reference to a part that was removed: not in the solution file, the Dockerfile, the compose file, the host, the three test projects, the architecture tests, the documents or the generated README. A matrix in CI proves every one of those on every pull request.

**Success looks like.**

1. `dotnet new modulith-backend -n Acme.Hr` with no options generates exactly what it generates today (every switch defaults to on).
2. Each of `--notifications false`, `--includeSample false` and `--caching memory` removes its part completely and the result builds with warnings as errors and passes its unit, architecture and integration tests.
3. All three off at once does the same.
4. The template repository itself still builds and tests as it is: the switch markers are comments, so the all-on tree is valid without the template engine.
5. Nothing changes a route, a response, a table or a column of the all-on service (ADR 0031).

**Said by you:** tests that need a removed module are dropped or conditionally compiled (not refactored onto test-only seams); `notifications` off is pure removal and Auth's integration events are the seam; five matrix combinations with integration tests in each; `caching` is `redis` or `memory`, default `redis`.

## 2. Non-goals

- The blueprint's other switches (`database`, `messaging`, `otp`, `passkeys`, `social`, `apiKeys`, `oauthServer`, `bff`, `multiTenancy`, `fileStorage`). The review defers each until its module exists.
- The `modulith-module` item template (7b), the NuGet package, publishing, and the v1.0 cut (7c).
- Replacing Sample as the platform's test bed in the template repository. Sample stays; a project generated without it loses the tests that run through it.
- A cache provider `none` as a switch value. `CachingProvider.None` stays a runtime option for diagnosing.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| S1 | Three symbols in `template.json`: `notifications` (bool, default true), `includeSample` (bool, default true), `caching` (choice `redis` \| `memory`, default `redis`). | The review's v1 set. A switch removes a module or project; configuration still picks providers inside what remains. |
| S2 | `caching` = `redis` keeps `TemplateName.Platform.Redis`; `memory` removes the project, its tests, its page and the StackExchange.Redis package. The runtime default of `Caching:Provider` stays `memory` in both. | `Platform.Redis` is the one project that exists only to drop a NuGet dependency (ADR 0027). Choosing `memory` and then setting `Caching:Provider=redis` already fails fast with a clear message from `CachingServiceCollectionExtensions`, so no new guard is built. |
| S3 | `notifications` off is pure removal. Auth is not changed: it keeps publishing its integration events to nobody. The generated README and `docs/modules/auth.md` say a project must add its own consumer to deliver verification and reset codes. | Auth has no code dependency on Notifications (it publishes events through its Contracts project). Guard rails in Auth would add conditionals to a module the switch does not own. |
| S4 | Template-engine conditionals written as comments in the source, plus `sources.modifiers` excludes in `template.json`. No post-generation script. | The repository stays valid and all-on without the engine; the engine's own conditionals are what the blueprint (15.1) prescribes. |
| S5 | Tests that depend on a removed module are excluded by file, or wrapped in conditionals where one file mixes concerns. Nothing is refactored onto test-only seams. | Conventional for templates and the smallest change; the all-on CI in the template repository keeps running the full suite. The cost is stated in the generated README: a project without Sample has no integration tests for idempotency, localization and audit through it. |
| S6 | The matrix has five generations: all on, all off, and each switch off alone. Each runs the dead-reference check, a Release build, the unit and architecture tests and the integration tests. | Each switch is proven independently and the extreme case is proven together. The pairs that are not the all-off case (for example `notifications` and `includeSample` both off with `caching` = `redis`) are not generated; the plan decided to add them as three more combinations that run on pushes to `main` and `workflow_dispatch`, not on pull requests. Integration tests run in every combination because a conditionally compiled test that fails only at run time would otherwise ship. |
| S7 | `build/scripts/template-smoke.sh` takes the switch values as arguments and stays the one script that generates, checks and tests. The matrix runs it; the existing all-on job keeps its name as the fast required check. | One generation script, one set of checks. |

## 4. What each switch removes

| Switch value | Removed from the generated project |
|---|---|
| `notifications` = false | `src/Modules/Notifications/**`; its tests under all three test projects; `docs/modules/notifications.md`; the `Program.cs` lines `AddNotificationsModule`, `MapNotificationsEndpoints`, `MapNotificationsHub` and their `using`; the project reference and the `COPY` lines (`build/docker/Dockerfile`); the entries in `TemplateName.slnx`, `TemplateName.Api.csproj`, the test `.csproj` files and `Assemblies.cs`; the `mailpit` service in `docker-compose.yml`; the lines of the docs indexes, `CLAUDE.md` and the README that name it. |
| `includeSample` = false | `src/Modules/Sample/**`; its tests; `docs/modules/sample.md`; the same kinds of references as above (`AddSampleModule`, `MapSampleEndpoints`, the project reference, `COPY`, `.slnx`, `Assemblies.cs`); every test that runs through Sample (parts of the Idempotency, Localization, Audit, Auth seeding and Notifications delivery tests, and `AuditingTests`). |
| `caching` = memory | `src/BuildingBlocks/TemplateName.Platform.Redis/**`; its tests (the `Redis` integration tests and the `Platform` unit tests for the limiter, health check, backplane, resilient cache and availability); `docs/building-blocks/platform-redis.md`; `AddPlatformRedis`; the reference, `COPY`, `.slnx` and `Assemblies.cs` entries; the `valkey` service in `docker-compose.yml`; the `Caching:Redis` block of `appsettings.json`; the package from `Directory.Packages.props` only if no remaining project uses it. |

`packages.lock.json` files are not regenerated by a CI step (the plan's decision): the four lock files whose contents depend on the switches (the API, UnitTests, ArchitectureTests and IntegrationTests projects) are not generated when `(!notifications || !includeSample || caching == "memory")`, and the generated project runs `dotnet restore` once and commits them; every other lock file is identical in every combination. The OpenAPI snapshot (`OpenApiSnapshotTests` and its `verified.json`) is not generated when `(!notifications || !includeSample)`, because one file lists every route.

## 5. The matrix and the dead-reference check

- Combinations: `{notifications, includeSample, caching}` = `{on, on, redis}` (all on), `{off, off, memory}` (all off), `{off, on, redis}`, `{on, off, redis}`, `{on, on, memory}`.
- Dead-reference check (new, in `template-smoke.sh`): after generation, a search of the generated tree (excluding `bin`, `obj` and the lock files) for the names of the removed parts (`Modules.Notifications`, `Modules.Sample`, `Platform.Redis`, `notifications`, `valkey`, `mailpit`, as appropriate to the combination). Any hit fails, except the lines of `docs/` and the README that say the part was omitted.
- Jobs: one job per combination in the separate workflow `.github/workflows/template-matrix.yml` (`template-matrix`; the plan added the three pairs as a second job, `template-matrix-pairs`, which runs on pushes to `main` and `workflow_dispatch` only), each on `ubuntu-latest` with Docker for the SQL Server container. Required checks stay as `docs/repository-management.md` lists them until the matrix has run once; then `template-matrix` joins that list.

## 6. Documents and housekeeping

- A new ADR 0035, "Template switches by engine conditionals, tests dropped with their module" (S1 to S7).
- `build/template-content/README.md` gains a "Switches" section: the three symbols, what each removes, the Auth-events note (S3) and the test-coverage note (S5). `docs/README.md`, `docs/architecture/overview.md` and `build/template-content/CLAUDE.md` carry conditionals so no sentence or link names a removed part.
- `docs/repository-management.md` (template repository only): the matrix job and the check name.
- `docs/blueprint-review.md` Section 6: row 7 split into 7a, 7b, 7c, with 7a marked done when merged.

## 7. Tests of the template itself

- The matrix is the test. In addition, the existing `DocumentationTests` and `LayeringTests` run inside every generated project, so a removed part that leaves a dangling doc or layer rule fails there.
- A short test in `template-smoke.sh` that the all-on generation is byte-identical to today's apart from the markers' removal (the file list and the contents of a sample of files), so default behaviour cannot drift.

## 8. Risks and points the plan must settle

- **Comment syntax per file type.** The engine's conditional comments differ for C#, XML (`.csproj`, `.slnx`), Markdown, YAML, JSON and a Dockerfile. The plan's first task proves, by generating, which syntax each of those files accepts and what to do for one that accepts none (a `sources.modifiers` exclusion of a whole file, with the kept file written without the part).
- **Mixed test files.** A file that mixes Sample, Notifications and other concerns (`AuthSeederTests`, `AuditTrailEndToEndTests`, `AdminDeliveryTests`) needs in-file conditionals; the plan lists each and its reason.
- **Matrix cost.** Five jobs, each with a generation, a build and the integration tests. Expect about the cost of the existing `build-test` job per combination; the plan sets timeouts and decides whether the all-on matrix job can reuse the required `template-smoke`.
- **Lock files** of a generated project differ per combination; the check regenerates them rather than shipping five sets.
- **Integration tests cannot run on this machine** (Docker is broken): every change to them is verified in CI only.
- **Windows path length** of a generated project's longest path is already guarded by `path-guard-check.sh`; the matrix runs it on each combination.
