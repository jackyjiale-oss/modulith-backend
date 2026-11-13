# Project index (code map) and its generator — design

- Status: Draft for review (2026-11-13). The design was approved part by part in the design session (audience, approach, layout and shards, enforcement); this file is the written form.
- Date: 2026-11-13
- Scope: step 1 of 2 (this repository only). Step 2, a switch-aware index shipped into every generated project, gets its own spec after step 1 is merged and has run for a while (Section 9).
- Builds on: Plan 7a (template switches, main `03db88f`); `docs/README.md` and the data-ownership table in `docs/architecture/overview.md`, which stay as they are.
- Source: the user's request to index the project "so that future R&D is smoother and token efficient", and a survey of the existing navigation aids made for this design (summarised in Section 1).

## 1. Intent

**Outcome.** Anyone working on the template (a developer, or an AI agent that starts every session without memory) can answer the lookups below from one small entry file and at most one small generated file, instead of a repository-wide search or a 1,500-line read. The generated files cannot silently go stale.

**What exists today.** Documents and ADRs are well indexed and test-enforced (`docs/README.md`, the ADR table in `docs/architecture/overview.md`, `DocumentationTests`). The root `CLAUDE.md` is 10 lines and imports the 67-line generated-project guide; it gives path patterns only (`src/Modules/{Module}/...`). `overview.md` has a data-ownership table. There is no code map: nothing lists routes with handler and permission, permission codes, error codes with their resource keys, integration events and their consumers, background jobs, configuration keys with their owner, the test folder for a module, or the files that carry template-switch markers. `docs/modules/auth.md` alone is 1,583 lines.

**Success looks like.**

1. A reader answers each of these from `MAP.md` plus one shard: which file handles route X; who holds permission Y; which module owns error code Z and which resource key carries its text; which events a module publishes and which handlers consume them; which jobs exist and which config key schedules each; which module owns a configuration section; which test folder covers a module; which files carry switch markers and under which switch.
2. `MAP.md` is at most 120 lines and each shard at most 400 lines (a shard that outgrows that is split by module).
3. Changing a route, permission, error code, event, job, configuration key, test folder or switch marker without regenerating the shards fails the `project-index` check (a required check once the owner adds it, decision I6).
4. The index adds nothing to generated projects, and template generation and the matrix are unchanged.
5. The generator hard-codes no module name, so step 2 can run it unchanged inside a generated project.

**Said by you:** both steps, repository only first; hybrid (a short hand-written map plus generated tables, with a check that fails when they are stale); future modifications must update the index.

## 2. Non-goals

- Summaries written by a language model, vector or embedding indexes, per-file descriptions, IDE or language-server tooling.
- Replacing `docs/README.md`, the ADR tables or the data-ownership table.
- A line-number table of contents for large documents: it goes stale with every edit, and a grep for `^## ` costs about the same.
- Step 2 (shipping the index into generated projects).
- Measuring token use automatically; Section 7 sets a manual acceptance benchmark.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| I1 | Two steps. Step 1 (this spec) is repository only; step 2 ships the same generator into generated projects. | The user's choice. Step 1 cannot break generation; step 2 reuses it. |
| I2 | Hybrid: `MAP.md` is hand-written and holds only stable facts; the volatile facts live in generated shards. | A hand-written list rots; a generated list cannot carry judgement (where to start, pitfalls). |
| I3 | Files live in `docs/superpowers/index/`: `MAP.md` and one shard per topic. | `docs/superpowers/**` is already excluded from generation, so step 1 needs no `template.json` change for the index files. |
| I4 | The generator is `build/scripts/project-index.sh` (bash, grep, awk and `git ls-files`; no new dependency). It has two modes: with no argument it rewrites the shards; `--check` rewrites nothing and exits 1 when a committed shard differs from a fresh run. | Same tooling as the other scripts; runs under Git Bash and on `ubuntu-latest`. |
| I5 | The generator reads the tree it is run in (`src/Modules/*`, `src/BuildingBlocks/*`, `tests/*`) and hard-codes no module name. | A tree without a module has no rows for it, which makes step 2 switch-aware without markers. |
| I6 | Enforcement is a new repository-only workflow `.github/workflows/project-index.yml` with one job named `project-index` that runs `bash build/scripts/project-index.sh --check` on every pull request and push to `main`. The owner adds `project-index` to the required checks. | `ci.yml` ships into generated projects, so a step that calls a repository-only script there would break them. A separate workflow is excluded like `template-matrix.yml`. Until the owner requires it, it is informational. |
| I7 | The rule is written in two places: one line under "Maintaining the template" in the root `CLAUDE.md` (loaded every session) and the header of `MAP.md`: after changing any of the facts in Section 4, run `bash build/scripts/project-index.sh` and commit the result. | Agents read `CLAUDE.md` first; the CI check is the backstop. |
| I8 | Output is deterministic: `LC_ALL=C` sorting, LF line endings, no timestamps or run-dependent values. | `--check` compares bytes; Windows and Linux must agree. |
| I9 | The generator and its test script are repository-only: excluded in `.template.config/template.json` and listed in the `forbidden` list of `template-smoke.sh`, as the dead-reference check was. | A repository-only script must not appear in generated output (step 2 changes this deliberately). |

## 4. Shards: content and source

Each shard starts with a one-line banner (generated by the script, do not edit, how to regenerate) and a row count. Rows are sorted. A source that yields zero rows where the tree plainly has some (for example an `*Endpoints.cs` file with no route) is an error, not an empty table.

| Shard | Columns | Source |
|---|---|---|
| `routes.md` | module, method, route, handler or command type, permission or policy, file | the `*Endpoints.cs` files under `src/Modules/*/.../Endpoints/` |
| `permissions.md` | code, module, held by Admin by default, file | the `*Permissions.cs` and `*PermissionSource.cs` files |
| `errors.md` | code, module, HTTP status, resource key, file | the error-code declarations and the module `Resources/*.resx` files |
| `events.md` | integration event, publishing module, consuming handlers (module and file) | integration event types and `IntegrationEventHandler` implementations |
| `jobs.md` | job, module, configuration key, file | `IRecurringJob` implementations and the Jobs configuration |
| `config.md` | appsettings section, owning module or building block, documented in | `src/Host/*/appsettings.json` sections matched against the module and building-block docs |
| `tests.md` | source module or area to test folder per test project; `Foo.cs` to `FooTests.cs` where the names pair | the folder structure under `src/` and `tests/` |
| `markers.md` | file, kind (`//#if`, `<!--#if`, `%%#if`, bare `#if`), switch expression | `git grep` for lines that are marker lines by the same rule the smoke script uses for a leftover marker |

The first plan task proves each parse by running it on the tree and comparing with an independent count (for routes, the 42 `*Endpoints.cs` files and the module docs' `## Endpoints` sections that `EndpointDocumentationTests` already forces to be complete; for errors, the catalogue the architecture tests build). The plan names the exact patterns; this spec fixes the columns and the failure rule.

## 5. `MAP.md`

At most 120 lines, hand-written, stable facts only (no counts or name lists that change). It holds: the module and building-block layout and what each owns in one line; the naming and folder conventions an agent needs to guess a path; a "where to look for what" table that sends each lookup of Section 1 to one shard; pitfalls (generated files are not edited, markers are comments and never `#else`, the all-on tree is the repository); how to regenerate; and the rule of I7. `--check` also fails when a backticked repository path in `MAP.md` no longer exists, and when a shard exists that `MAP.md` does not name.

## 6. Keeping it honest

- `--check` in the `project-index` job (I6) catches stale shards, a missing path in `MAP.md` and an unlisted shard.
- A merge conflict in a shard is resolved by regenerating, never by hand; `MAP.md` says so.
- Dependabot pull requests change package versions and lock files only; none of the shards reads them, so they pass untouched.
- `docs/repository-management.md` Section 3 and its phase table gain the check name when the owner makes it required.
- Optional, not in this spec: a local Claude Code hook that runs `--check` at the end of a turn.

## 7. Tests of the generator, and acceptance

- `build/scripts/tests/project-index.test.sh`, in the style of `dead-reference-check.test.sh`: a small fixture tree with expected shard output byte for byte; a stale-shard case that must make `--check` exit 1 and name the shard; a missing-path case for `MAP.md`; a zero-row case for a source that must yield rows; a determinism case (two runs, identical bytes); a module-absent case (a tree without one module has no rows for it).
- The `scripts` job already runs the script tests; the `project-index` job runs the check against the real tree.
- Acceptance benchmark, done by hand once when the plan completes: five lookups taken from Section 1, each answered from `MAP.md` plus one shard; record the number of lines read for each against the lines a search-and-read would have taken. The result goes in the plan's final report; it does not gate the merge.

## 8. Risks and points the plan must settle

- **Parsing C# with grep and awk is brittle.** The patterns stay narrow, each source has the zero-row failure rule, and routes are cross-checked against the module docs. If one source cannot be parsed reliably in bash, the plan may replace that shard's parser with a small `dotnet` tool run only by the generator, and says so in a ruling.
- **Config ownership is a heuristic.** A section's owner is matched against the module and building-block docs; an unmatched section is listed as `unassigned` and fails `--check` so that a new section gets an owner deliberately.
- **Runtime.** `--check` must finish in seconds; the plan measures it on Git Bash and `ubuntu-latest`.
- **The required check.** Adding `project-index` to the required list is a branch-protection change that only the owner can make; until then a stale index is reported but does not block a merge.
- **Marker lines in documents that quote markers.** Shipped documents must not contain a marker-shaped line (ADR 0035 found this the hard way); `markers.md` uses the smoke script's leftover-marker rule so a quoted marker is not listed as a real one.
- **Workflow and script exclusions.** `project-index.yml`, `project-index.sh` and its test need `template.json` excludes and `forbidden` entries, or `template-smoke.sh` fails; the drift check will catch a missing exclude.

## 9. Step 2 (outline only; its own spec later)

Ship `build/scripts/project-index.sh` into generated projects and run it there to write `docs/code-map.md` (with the shards it needs), add the rule to the shipped `build/template-content/CLAUDE.md`, and add a guarded `--check` step to the shipped `ci.yml` (skipped when the script is absent). The markers shard is dropped in generated projects. Because the generator scans its own tree, a project generated without Notifications, Sample or Redis has no rows for them; step 2's tests generate the eight switch combinations and check that no removed part appears in the output.
