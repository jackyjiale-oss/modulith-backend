# The `modulith-module` item template Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dotnet new modulith-module -n Billing --prefix Acme.Shop` adds a wired, building, documented module skeleton to a generated project with no hand edits.

**Architecture:** A second template (`templates/modulith-module/`, hand-written placeholder files) generates the module's files. A post-action runs `build/scripts/add-module.sh`, which ships in generated projects and inserts text at named anchor comments in the shared files, then migrates, restores and builds. A new architecture rule and two CI legs keep it honest.

**Tech Stack:** dotnet new template engine (SDK 10.0.401), bash, xUnit and NetArchTest, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-15-module-item-template-design.md`

## Global Constraints

- Short name `modulith-module`, type `item`, identity `Modulith.Module.Templates`; lives in `templates/modulith-module/`; the solution template excludes `templates/**`.
- Command: `dotnet new modulith-module -n Billing --prefix Acme.Shop [--contracts true] --allow-scripts yes`.
- `-n` matches `^[A-Z][A-Za-z0-9]*$`; route segment and `docs/modules/{kebab}.md` are kebab-case (`LeaveManagement` → `leave-management`); DB schema is lower-case (`leavemanagement`); `--prefix` is required; `--contracts` is bool, default false.
- The script is `build/scripts/add-module.sh`, ships in generated projects, reads its anchors from `build/scripts/module-anchors.txt`, inserts text only (never rewrites code), and each anchor sits outside every `#if` switch region.
- A missing anchor skips that one edit, prints the lines to add and ends non-zero; a prefix with no `src/Host/{prefix}.Api` stops before any edit and removes what the engine wrote; a second run changes nothing.
- The skeleton: csproj mirroring Audit's, `{M}Module.cs` (`Add{M}Module`, `Map{M}Endpoints`, no routes), `{M}DbContext` with the outbox, an empty permission source, three empty `.resx` (`en`, `ms`, `zh-Hans`), `docs/modules/{kebab}.md`; the Contracts csproj references only SharedKernel.
- CI check names: `module-template (all-on-contracts)` and `module-template (all-off-multiword)`.
- Every `dotnet new modulith-module` call passes `--allow-scripts yes`: with no flag a non-interactive run loops forever on the engine's prompt (spike D1). The post-action takes no arguments; the template ships a marker file `.modulith-module` (line 1 the name, line 2 the prefix, substituted by the engine) that the script reads and deletes. The `prefix` symbol needs both `replaces` and `fileRename` (D4). Generated files keep namespace and folder in step (IDE0130 fails the format check).
- Warnings are errors; `dotnet format` clean; tests first; Conventional Commits, header at most 72 characters, ending with the attribution line; docs in the same change; ADR 0036.
- After changing anything that ships, run `bash build/scripts/template-smoke.sh`, then again with `--notifications false`, `--includeSample false` and `--caching memory` (repo `CLAUDE.md`); then `bash build/scripts/project-index.sh` with new files staged.

## Review Focus

1. A name that collides with an existing module (`Audit`): the script refuses and nothing changes. Pinned in Task 5.
2. A name that is not PascalCase (`billing`, `Bill_ing`, `2Billing`): refused, generated files removed. Pinned in Task 5.
3. Shared files with CRLF line endings (Windows `autocrlf`): anchors still match and inserted lines keep CRLF. Pinned in Task 6.
4. A project folder path with spaces: nothing breaks. Pinned in Task 6.
5. An acronym name (`HRPayroll` → `h-r-payroll`) and a digit (`Billing2` → `billing2`): the docs file name follows the repo's own `DocumentPaths.KebabCase` rule (every capital splits, digits do not), not the engine's `kebabCase`, or `DocumentationTests` cannot find the page. Pinned in Task 5.

---

### Task 1: Spike the five unverified points

**Files:**
- Modify: `docs/superpowers/specs/2026-11-15-module-item-template-design.md` (append `## 10. Spike results`)
- Scratch only (not committed): a throwaway item template under the session scratchpad

**Interfaces:**
- Produces: for each of D1 to D4 below, the verified behaviour or the chosen fallback, written into spec section 10; later tasks read it.

- [ ] **Step 1: Build a throwaway item template** with one symbol `prefix` (`replaces: "ProjectPrefix"`, required), `sourceName: "ModuleName"`, file and folder names containing both placeholders, a derived symbol using the `kebabCase` form and one using `lowerCase`, and a run-script post-action calling `bash check.sh` that exits 0 or 1 by an env var.
- [ ] **Step 2: Install and run it into temp folders with a private hive** (`--debug:custom-hive`) and record: **D1** post-action behaviour with `--allow-scripts yes`, `--allow-scripts no` and with no flag in a non-interactive shell, and whether a non-zero script exit makes `dotnet new` exit non-zero; **D2** whether the `kebabCase` and `lowerCase` forms exist in SDK 10.0.401 and what `HRPayroll` and `Billing2` give; **D3** whether `dotnet new install <repo root>` registers both the root template and a template under `templates/<name>/.template.config`, and what `dotnet new list modulith` prints; **D4** whether `ProjectPrefix` is renamed in file and folder names (`fileRename` or `sourceName`) and whether a module built from the placeholders can be seen by the three test projects (`InternalsVisibleTo` from `src/Directory.Build.props`); **D5** whether `dotnet format --verify-no-changes` fails when a `using Acme.Smoke.Modules.Billing;` is added to a generated `Program.cs` after the last existing using (out of order) and whether it passes when the line is inserted in sorted position among the `using Acme.Smoke.` lines.
- [ ] **Step 3: Append `## 10. Spike results`** to the spec: one bullet per D1 to D5 with the verified behaviour and, where the spec's fallback applies, which one is chosen. Run it on Windows (Git Bash) and note any difference from Ubuntu.
- [ ] **Step 4: Commit** `docs: record the module template spike results`.

### Task 2: Anchors in the shared files and their guard test

**Files:**
- Create: `build/scripts/module-anchors.txt`, `build/scripts/tests/module-anchors.test.sh`
- Modify: `src/Host/TemplateName.Api/Program.cs`, `src/Host/TemplateName.Api/TemplateName.Api.csproj`, the three test `.csproj`, `TemplateName.slnx`, `build/docker/Dockerfile`, `tests/TemplateName.ArchitectureTests/Assemblies.cs`, `CONTRIBUTING.md`
- Modify: `.template.config/template.json` (exclude `build/scripts/tests/module-anchors.test.sh`)

**Interfaces:**
- Produces: `module-anchors.txt`, one line per anchor, `key|relative path|exact anchor text`, with `TemplateName` in the paths (the engine renames them in a generated project). Keys: `program-registrations`, `program-endpoints`, `api-references`, `unit-references`, `arch-references`, `integration-references`, `slnx-modules`, `docker-copies`, `assemblies-modules`, `assemblies-contracts`, `assemblies-errors`, `contributing-scopes`; plus `program-usings` and `assemblies-usings` (spike D5: `dotnet format` enforces no using order, so a using goes at the end of the usings anchor). Anchor text: `// modulith:module-usings`, `// modulith:module-registrations`, `// modulith:module-endpoints`, `<!-- modulith:module-references -->`, `<!-- modulith:modules -->`, `# modulith:module-copies`, `// modulith:modules`, `// modulith:contracts`, `// modulith:error-messages`, `<!-- modulith:module-scopes -->`.

- [ ] **Step 1: Write `module-anchors.test.sh`.** For every line of `module-anchors.txt`: the file exists; the anchor text occurs exactly once in it; its line is not between an `#if` and `#endif` marker (track depth over `//#if`, `<!--#if`, `#if` lines). Expected now: FAIL, no anchors.
- [ ] **Step 2: Run** `bash build/scripts/tests/module-anchors.test.sh`; expected FAIL.
- [ ] **Step 3: Add the anchor lines.** Each goes at the end of its list, outside any marker region: usings, registrations and endpoints after the Audit lines in `Program.cs`; `<!-- modulith:module-references -->` after the Audit `ProjectReference` in each project file; `<!-- modulith:modules -->` before the `/src/Host/` folder in the `.slnx`; `# modulith:module-copies` after the Audit `COPY`; in `Assemblies.cs` after the last using and after each list's last entry, outside any marker region; `<!-- modulith:module-scopes -->` at the end of the Modules row of the scopes table. Write `module-anchors.txt`.
- [ ] **Step 4: Run the guard test**; expected PASS. Run `dotnet build -c Release` (all anchors are comments; expected 0 warnings) and `dotnet format --verify-no-changes`.
- [ ] **Step 5: Run `bash build/scripts/template-smoke.sh --generate-only`**, expected `passed (generate only)`: generated files equal their source minus marker lines.
- [ ] **Step 6: Commit** `feat(template): add module wiring anchors to the shared files`.

### Task 3: The architecture rule for unlisted modules

**Files:**
- Create: `tests/TemplateName.ArchitectureTests/ModuleRegistrationTests.cs`

**Interfaces:**
- Produces: `internal static IReadOnlyList<string> ModuleRegistrationTests.UnlistedProjects(string modulesDirectory, IEnumerable<string> listedModules, IEnumerable<string> listedContracts)` returning the project names under `modulesDirectory` (`*/*/*.csproj`) that are in neither list (a project whose name ends `.Contracts` is checked against `listedContracts`).

- [ ] **Step 1: Write the tests.** `Every_module_project_on_disk_is_listed_in_Assemblies` calls it with `RepositoryPaths.Root/src/Modules`, the names of `Assemblies.Modules` and `Assemblies.Contracts`, and asserts the result is empty. `An_unlisted_module_project_is_reported` builds a temp folder `Billing/X.Modules.Billing/X.Modules.Billing.csproj` and asserts `["X.Modules.Billing"]`. `An_unlisted_contracts_project_is_reported` does the same for a `.Contracts` name.
- [ ] **Step 2: Run** `dotnet test --project tests/TemplateName.ArchitectureTests --filter-class "*ModuleRegistrationTests"`; expected FAIL (method undefined).
- [ ] **Step 3: Implement `UnlistedProjects`** with `Directory.GetFiles(modulesDirectory, "*.csproj", SearchOption.AllDirectories)` and ordinal name comparison; naming rules follow the other architecture tests (Async suffix and so on do not apply to a static helper).
- [ ] **Step 4: Run it again**; expected PASS. Run `dotnet test --project tests/TemplateName.ArchitectureTests` whole; expected PASS.
- [ ] **Step 5: Commit** `test(architecture): fail when a module project is not registered`.

### Task 4: The item template files

**Files:**
- Create: `templates/modulith-module/.template.config/template.json`, the content tree under `templates/modulith-module/` (below), `build/scripts/tests/module-template-files.test.sh`
- Modify: `.template.config/template.json` (add `templates/**` to `sources[0].exclude`; exclude the new test)

**Interfaces:**
- Consumes: the Task 1 results (file-rename mechanism, derived forms, install layout).
- Produces: `template.json` with symbols `name` (`sourceName: "ModuleName"`), `prefix` (required, with both `replaces: "ProjectPrefix"` and `fileRename: "ProjectPrefix"`, D4), `contracts` (bool, default false), derived `moduleLower` (the `lowerCase` form, D2) and no kebab symbol (the script computes the kebab form, Task 5); a `sources` modifier with `condition: "(!contracts)"` excluding the Contracts tree; the marker file `.modulith-module` at the output root containing `ModuleName` then `ProjectPrefix`, one per line (D1). Content tree: `src/Modules/ModuleName/ProjectPrefix.Modules.ModuleName/{ProjectPrefix.Modules.ModuleName.csproj, ModuleNameModule.cs, Application/Abstractions/IUnitOfWork.cs, Application/ModuleNamePermissions.cs, Application/ModuleNamePermissionSource.cs, Infrastructure/Persistence/ModuleNameDbContext.cs, Resources/ModuleNameErrorMessages.cs + .resx + .ms.resx + .zh-Hans.resx, Domain/.gitkeep}`, `src/Modules/ModuleName/ProjectPrefix.Modules.ModuleName.Contracts/{csproj, Public/.gitkeep}`, `docs/modules/ModuleName.md` (the script renames it to its kebab-case name), `.modulith-module`.

- [ ] **Step 1: Write `module-template-files.test.sh`.** With a private hive, `dotnet new install templates/modulith-module`, generate into a temp dir twice (`-n LeaveManagement --prefix Acme.Shop --contracts true` and `-n Billing --prefix Acme.Shop`, both `--allow-scripts no`) and assert: the exact file list of the spec's section 5 (Contracts tree only in the first); `namespace Acme.Shop.Modules.LeaveManagement` and `internal const string Schema = "leavemanagement";` in the DbContext; `docs/modules/LeaveManagement.md` exists and starts `# LeaveManagement module`; `.modulith-module` holds exactly the lines `LeaveManagement` and `Acme.Shop`; no `ModuleName` or `ProjectPrefix` left in any name or content outside that file's own substituted values; the test ignores the exit code of `dotnet new` (104 once Task 7 adds the post-action and `--allow-scripts no` skips it) and asserts the files; the Contracts csproj has exactly one `ProjectReference` (SharedKernel).
- [ ] **Step 2: Run it**; expected FAIL (no template).
- [ ] **Step 3: Write `template.json` and the files.** Copy Audit's csproj and Sample's DbContext, `IUnitOfWork` and error-messages shapes with the names replaced; `ModuleNameModule.AddModuleNameModule(this IServiceCollection, IConfiguration)` registers `AddModuleDbContext<ModuleNameDbContext>(ModuleNameDbContext.Schema)`, `AddOutbox<ModuleNameDbContext>(assembly)`, `AddApplicationHandlers(assembly)`, `AddErrorMessages<ModuleNameErrorMessages>()`, `TryAddEnumerable` of the permission source, and `IUnitOfWork` to the DbContext; `MapModuleNameEndpoints(this IEndpointRouteBuilder app)` returns `app`. The docs page is `_template.md` with `{Module}` replaced and "None." under every section (tables removed).
- [ ] **Step 4: Run the test**; expected PASS.
- [ ] **Step 5: Commit** `feat(template): add the modulith-module item template files`.

### Task 5: `add-module.sh` validation, preflight and cleanup

**Files:**
- Create: `build/scripts/add-module.sh`, `build/scripts/tests/add-module.test.sh`
- Modify: `.template.config/template.json` (exclude `build/scripts/tests/add-module.test.sh`)

**Interfaces:**
- Produces: `add-module.sh [--name NAME --prefix PREFIX]` run from the solution root; with no arguments it reads the marker `.modulith-module` (line 1 the name, line 2 the prefix) and deletes it when done; `--name` and `--prefix` override for a manual re-run; Contracts are detected by whether `src/Modules/{M}/{P}.Modules.{M}.Contracts` exists (no flag). Exit 0 on success, 1 on any skipped edit or failed step, 2 on invalid input. Functions: `kebab_case NAME` (stdout; the repo's `DocumentPaths.KebabCase` rule: `.` becomes `-`, every capital after the first character is preceded by `-`, all lower-case; digits do not split), `validate_name NAME` (return 2 on a name outside `^[A-Z][A-Za-z0-9]*$`), `remove_generated NAME PREFIX` (deletes `src/Modules/NAME`, `docs/modules/{kebab}.md`).
- Test file helpers: a fake tree built by the test from the repo's anchor-bearing files with `TemplateName` replaced by `Acme.Shop` (as the engine would) and the engine's output simulated by copying the template tree.

- [ ] **Step 1: Write the tests** (each asserts the exit code and the tree): `kebab_case` gives `LeaveManagement`→`leave-management`, `HRPayroll`→`h-r-payroll`, `Billing2`→`billing2`, `Audit`→`audit`; a full run renames `docs/modules/LeaveManagement.md` to `docs/modules/leave-management.md`; with no arguments and no marker it exits 2; `billing`, `Bill_ing`, `2Billing` exit 2 and `src/Modules/<name>` is gone; prefix `Wrong.Name` with no `src/Host/Wrong.Name.Api` exits 2 before any edit, removes `src/Modules/Billing` and leaves every other file byte-identical (compare checksums before and after); `--name Audit` with Audit's `AddAuditModule` already in `Program.cs` exits 2 and changes nothing.
- [ ] **Step 2: Run** `bash build/scripts/tests/add-module.test.sh`; expected FAIL (script missing).
- [ ] **Step 3: Implement the functions and the argument, name and prefix checks** (`set -euo pipefail`, quote every path, `cd` to the solution root given by the caller's cwd). The "already wired" check greps `Program.cs` for `Add{M}Module(`. Cleanup runs only for the names this call received.
- [ ] **Step 4: Run the tests**; expected PASS.
- [ ] **Step 5: Commit** `feat(template): add add-module.sh input checks and cleanup`.

### Task 6: `add-module.sh` edits at the anchors

**Files:**
- Modify: `build/scripts/add-module.sh`, `build/scripts/tests/add-module.test.sh`

**Interfaces:**
- Consumes: `module-anchors.txt` (Task 2) and the functions of Task 5.
- Produces: `insert_before_anchor KEY LINE...` (inserts the lines immediately **before** the anchor line of KEY in its file, so the anchor stays last and a later module goes after an earlier one; copies the anchor line's indentation; keeps the file's line endings; does nothing if the first LINE is already present; records KEY and LINE in `skipped` when the anchor is missing) and `edit_all NAME PREFIX CONTRACTS` applying the spec's section 6 table: `Program.cs` (using, `builder.Services.Add{M}Module(builder.Configuration);`, `api.Map{M}Endpoints();`), the four project files, the `.slnx` folder, the Dockerfile `COPY` lines, `Assemblies.cs` (usings, `Modules`, `Contracts` when asked, `ErrorMessageResources`), the `docs/README.md` row before the `_template.md` row, `ALLOWED_SCOPES` and the scopes-table row. On a skipped edit it prints `add by hand: <file>: <line>` for each and the script exits 1.

- [ ] **Step 1: Add tests.** Full run on the fake tree: every file contains its new line exactly once; a second run leaves all checksums unchanged (exit 0); with one anchor removed that edit is skipped, `add by hand:` names the file and line, exit 1 and the other edits are present; a CRLF copy of every shared file ends with all lines CRLF and still matches; the whole run works with the fake tree under a path containing spaces; the Contracts lines are written when the Contracts project folder exists and none when it does not.
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement `insert_before_anchor` and `edit_all`** with awk (POSIX, no GNU extensions; detect `\r` on the anchor line and reuse it); the scope edit appends `|{lower-kebab}` before the closing quote of `ALLOWED_SCOPES` and, in `CONTRIBUTING.md`, before the anchor on the Modules row.
- [ ] **Step 4: Run the tests**; expected PASS.
- [ ] **Step 5: Commit** `feat(template): wire a new module into the shared files`.

### Task 7: Post steps and the post-action

**Files:**
- Modify: `build/scripts/add-module.sh`, `build/scripts/tests/add-module.test.sh`, `templates/modulith-module/.template.config/template.json`

**Interfaces:**
- Produces: `finish_module NAME PREFIX` running, with `DOTNET` overridable for tests, `dotnet tool restore`, `dotnet ef migrations add Create{M}Schema --project src/Modules/{M}/{P}.Modules.{M} --startup-project src/Host/{P}.Api --context {M}DbContext --output-dir Infrastructure/Persistence/Migrations`, `dotnet restore`, `dotnet build --no-restore -c Release`, then printing `commit these:` and the changed paths from `git status --short`. A `postActions` entry (`executable: bash`, `args: build/scripts/add-module.sh`, no other arguments: symbols are not substituted into arguments, spike D1) runs the script, which reads the marker file.

- [ ] **Step 1: Add a test** with a `DOTNET` stub that records its arguments: the four commands run in that order with the exact arguments above; a failing `ef` step exits 1 and the later steps do not run.
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement `finish_module`**, call it from the end of the script after `edit_all`, and add the post-action to the item template's `template.json`; update the Task 4 test to expect exit 104 from `dotnet new` under `--allow-scripts no`.
- [ ] **Step 4: Run the script test**; expected PASS.
- [ ] **Step 5: Commit** `feat(template): migrate, restore and build after adding a module`.

### Task 8: Template-repo plumbing and the smoke flags

**Files:**
- Modify: `build/scripts/template-smoke.sh`, `.template.config/template.json` (confirm the excludes of Tasks 2, 4, 5), `build/scripts/dead-reference-check.sh` only if it flags `templates/`

**Interfaces:**
- Produces: `template-smoke.sh --add-module NAME [--module-contracts]`: after generation and the path-length guard, installs `templates/modulith-module` into the same private hive and runs `dotnet new modulith-module -n NAME --prefix Acme.Smoke [--contracts true] --allow-scripts yes` in the generated project; later steps (restore, build, tests, `--gates`) run on the result. A new generation check fails if the generated project has a `templates/` folder.

- [ ] **Step 1: Add the generation checks first**: a `templates/` folder in the generated tree is a failure, and so is a `modulith:module` anchor in any generated file that is not listed in the generated `build/scripts/module-anchors.txt`. Run `template-smoke.sh --generate-only`; expected PASS.
- [ ] **Step 2: Add the flags** to the usage text and parser. Run `bash build/scripts/template-smoke.sh --add-module Billing --module-contracts` locally (the integration part is CI-only); expected `template-smoke: passed`, with the module built and the unit and architecture tests green.
- [ ] **Step 3: Run the same with `--notifications false --includeSample false --caching memory --add-module LeaveManagement`**; expected PASS.
- [ ] **Step 4: Commit** `ci(template): let the smoke test add a module with the item template`.

### Task 9: The CI job

**Files:**
- Modify: `.github/workflows/template-matrix.yml`

**Interfaces:**
- Produces: job `module-template`, `name: module-template (${{ matrix.name }})`, `runs-on: ubuntu-latest`, `timeout-minutes: 45`, `fail-fast: false`, on pull requests, pushes to `main` and manual runs; legs `all-on-contracts` (switches `""`, args `--add-module Billing --module-contracts`) and `all-off-multiword` (switches `--notifications false --includeSample false --caching memory`, args `--add-module LeaveManagement`), each running `bash build/scripts/template-smoke.sh --integration --gates "${args[@]}"` with the switches passed through `env` as the existing job does.

- [ ] **Step 1: Add the job**, copying the structure of `template-matrix` (checkout, setup-dotnet from `global.json`, the env-var read of the switches); update the file's header comment.
- [ ] **Step 2: Validate** with `actionlint` if installed, otherwise rely on the `actionlint` CI check on the PR (Task 11); both legs must appear in the PR's checks.
- [ ] **Step 3: Commit** `ci: run the module item template in the matrix`.

### Task 10: Documents and the index

**Files:**
- Create: `docs/adr/0036-module-item-template.md`
- Modify: `build/template-content/README.md` (recipe "Adding a module", with the command using `TemplateName` so the engine renames it), `build/template-content/CLAUDE.md` ("Where things go"), `docs/modules/_template.md` and `CONTRIBUTING.md` (pointer), `docs/blueprint-review.md` (row 7b done), `docs/repository-management.md` (the two new checks, as a phase 7 row, not yet required), `docs/README.md` (index of the ADR), `docs/superpowers/index/MAP.md` (generated)

- [ ] **Step 1: Write ADR 0036** with context, the decision (spec section 3) and the alternatives rejected (files only; built-in post-actions only; a compiled reference module; deriving from Audit).
- [ ] **Step 2: Write the recipe and the other pointers**; the generated README sentence naming the command is its own line, per the repo's marker rules.
- [ ] **Step 3: Run `bash build/scripts/project-index.sh`** after `git add` of every new file; review the diff of `MAP.md` (placeholder files under `templates/` must not appear as real modules).
- [ ] **Step 4: Run `bash build/scripts/template-smoke.sh`** and the three switch-off runs; expected PASS each.
- [ ] **Step 5: Commit** `docs: document the module item template (ADR 0036)`.

### Task 11: Verify on CI and hand over

**Files:** none

- [ ] **Step 1: Push the branch only on the user's approval and open the PR**; wait for every check.
- [ ] **Step 2: Confirm** both `module-template` legs, the five matrix legs and `project-index` pass; read the module legs' logs for the printed `commit these:` list.
- [ ] **Step 3: Report to the user**; after the merge and one green run on `main`, the owner adds the two new check names to the branch protection (`docs/repository-management.md`), and the worktree and branches are removed.
