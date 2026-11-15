# The `modulith-module` item template (Plan 7b) — design

- Status: Draft for review (2026-11-15). The design was approved part by part in the design session (the wiring level, the scaffold content, the Contracts flag, the source of the template files, then the interface, what is generated, the wiring and the verification); this file is the written form.
- Date: 2026-11-15
- Roadmap row: 7b in `docs/blueprint-review.md` Section 6 (7a, the switches, is merged; 7c is pack, publish, README, runbooks and the v1.0 cut).
- Builds on: Plans 1 to 7a (main `ab442a0`), the review's "`dotnet new modulith-module` item template" addition (Section 5, item 2) and the short-name decision (Section 6, "not plain `modulith`"), ADR 0035 (switches by engine conditionals), `docs/modules/_template.md`.
- Source: `BACKEND_TEMPLATE_BLUEPRINT.md` and `docs/blueprint-review.md` (the review wins).

## 1. Intent

**Outcome.** Someone who generated a project from `modulith-backend` adds a new business module with one command and gets a module that builds, migrates, is registered in the host, passes every architecture rule and is documented, with no hand edits to shared files.

**Success looks like.**

1. `dotnet new modulith-module -n Billing --prefix Acme.Shop --allow-scripts yes`, run at the solution root of a generated project, leaves a solution that builds with warnings as errors and passes its unit, architecture and integration tests.
2. The same holds for a multi-word name (`LeaveManagement`), with `--contracts true`, and in a project generated with every switch off.
3. A shared file the user has edited so that an anchor is gone does not break the command: the edit is skipped and the exact lines to add are printed.
4. Running the command again for an existing module changes nothing.
5. The template repository itself and the all-on generated project change in no route, response, table or column (ADR 0031): only anchor comments are added to shared files.

**Said by you:** the template does the wiring itself, with a checklist fallback when an anchor is missing; the scaffold is a wired skeleton with no business code (no routes, no entities); the outbox is included; the `.Contracts` project is a flag, off by default (a module with nothing to offer others ships the first project only, as Audit and Notifications do); the template files are hand-written placeholder files, kept honest by a CI test that adds a module to a generated project.

**Assumed (not said):** the template is used inside a generated project, so every name follows that project's own prefix; the repository is installed as a template from a path today (publishing is 7c), and one install finds both templates.

## 2. Non-goals

- Publishing or packing the template (7c). The item template works from a local `dotnet new install`.
- Generating business code: aggregates, commands, queries, endpoints, events. A module's first use case is written by hand and test first.
- Removing a module. Deleting one stays a manual change.
- Discovering modules by convention in the host. Registration stays explicit, as today.
- Any new building block, package or runtime behaviour.

## 3. Decisions

- **Two templates, one install.** `templates/modulith-module/` has its own `.template.config/template.json` (short name `modulith-module`, type `item`, identity `Modulith.Module.Templates`). The solution template excludes `templates/**`, so generated projects do not carry it. `dotnet new install .` of the repository registers both.
- **Hand-written placeholder files** (not a compiled reference project, not a script that copies Audit). A repo-only CI job generates a project and adds a module with the item template, so drift fails there.
- **The wiring script ships in the generated project** as `build/scripts/add-module.sh`. The item template's post-action calls it; a user can run it again by hand. Bash is already required (Git Bash on Windows) by the project's other scripts.
- **Anchors, not pattern matching.** Each shared file gets a named anchor comment, outside every switch region so it survives every switch combination. The script inserts text after an anchor; it never rewrites code.
- **Fail soft on a missing anchor, fail hard on a bad prefix.** A missing anchor skips that one edit and lists the lines to add; the exit code is non-zero so CI notices. A prefix with no `src/Host/{prefix}.Api` stops before any edit and removes what the engine just wrote.
- **Idempotency comes from the engine.** `dotnet new` refuses to overwrite existing files, so a second run for the same name fails on the file conflict before the script runs.

## 4. The interface

```bash
dotnet new modulith-module -n Billing --prefix Acme.Shop [--contracts true] --allow-scripts yes
```

- `-n` / `--name`: the module name, PascalCase, `^[A-Z][A-Za-z0-9]*$`. Derived forms: kebab-case for the route segment and `docs/modules/{kebab}.md` (`LeaveManagement` becomes `leave-management`), lower-case for the DB schema (`leavemanagement`, per `docs/coding-conventions.md`).
- `--prefix`: the generated project's own name (`Acme.Shop`), required. An item template cannot read it from the solution; the generated README shows the exact command with the project's name already filled in.
- `--contracts` (bool, default false): also create `{P}.Modules.{M}.Contracts` and its wiring.
- `--allow-scripts yes` is the engine's own flag for post-actions; without it the engine asks.

## 5. What is generated

Under `src/Modules/{M}/{P}.Modules.{M}/`:

- the `.csproj`: the same four project references and the ASP.NET Core framework reference as Audit; test access to `internal` types comes from `src/Directory.Build.props` (`InternalsVisibleTo`), so there is nothing per module;
- `{M}Module.cs`: `Add{M}Module(this IServiceCollection, IConfiguration)` registering the DbContext (`AddModuleDbContext`), the outbox (`AddOutbox`), the handlers (`AddApplicationHandlers`), the error messages (`AddErrorMessages<{M}ErrorMessages>`), the permission source and the unit of work; and `Map{M}Endpoints(this IEndpointRouteBuilder)`, which maps nothing yet;
- `Infrastructure/Persistence/{M}DbContext.cs`: internal, `IUnitOfWork`, schema constant, `ApplyDefaultConventions`, `ApplyConfigurationsFromAssembly`, `ApplyOutbox`; and `Application/Abstractions/IUnitOfWork.cs`;
- `Application/{M}Permissions.cs` and `{M}PermissionSource.cs` with an empty list and a comment where to add the first permission;
- `Resources/{M}ErrorMessages.cs` (an empty internal sealed class) and the three empty `.resx` files (`en`, `ms`, `zh-Hans`);
- the `Domain/`, `Application/` and `Infrastructure/` folders (a `.gitkeep` where a folder would otherwise be empty);
- `docs/modules/{kebab}.md`, copied from `_template.md` with `{Module}` replaced and "None." under every section that has nothing yet.

With `--contracts true`: `src/Modules/{M}/{P}.Modules.{M}.Contracts/` with a csproj that references only SharedKernel (the `ContractsTests` rule) and an empty `Public` folder with a `.gitkeep`.

The script then generates the first migration (`dotnet ef migrations add Create{M}Schema` through the host, as in the project's `CLAUDE.md`), runs a plain `dotnet restore` to write the new `packages.lock.json` files (the Dockerfile `COPY` needs them and locked-mode CI needs them committed), builds once, and prints the files to commit.

## 6. The wiring

| File | Insert, at its anchor |
|---|---|
| `src/Host/{P}.Api/Program.cs` | the `using`; `builder.Services.Add{M}Module(builder.Configuration);`; `api.Map{M}Endpoints();` (three anchors: usings, module registrations, endpoint mapping) |
| `src/Host/{P}.Api/{P}.Api.csproj`, and the `.csproj` of the Unit, Architecture and Integration test projects | `ProjectReference` to the module, and to its Contracts project when asked |
| `{P}.slnx` | a `<Folder Name="/src/Modules/{M}/">` with the project or projects |
| `build/docker/Dockerfile` | a `COPY` of each new `.csproj` and its `packages.lock.json` |
| `tests/{P}.ArchitectureTests/Assemblies.cs` | the usings; an entry in `Modules`; one in `Contracts` when asked; one in `ErrorMessageResources` |
| `docs/README.md` | a table row before the `_template.md` row |
| `build/scripts/check-commit-msg.sh` | the lower-case scope appended to `ALLOWED_SCOPES` (no anchor: the script edits the value of that one variable) |
| `CONTRIBUTING.md` | the scope appended to the module scopes row of the scopes table (an inline anchor comment at the end of that row) |

The anchors are plain comment lines added to the template's shared files in the same change (`// modulith:module-registrations` and the like, `<!-- modulith:module-references -->` in project files, `# modulith:module-copies` in the Dockerfile). The smoke test's check that a generated file equals its source without marker lines still holds, because an anchor is an ordinary line.

**A new architecture rule** fails when a module project exists under `src/Modules` but is not in `Assemblies.Modules` (and a Contracts project not in `Assemblies.Contracts`). Today nothing does, so a skipped `Assemblies.cs` edit would make the other rules silently ignore the module. The rule is part of this plan, not a later hardening.

## 7. Verification

- **CI (repo-only):** a new job in `template-matrix.yml`, `module-template (...)`, with two legs. Each generates a project, adds a module with the item template installed from the checkout, then restores, checks formatting, builds and runs the unit, architecture and integration tests. Leg `all-on-contracts`: all switches on, `-n Billing --contracts true`. Leg `all-off-multiword`: all switches off, `-n LeaveManagement`, which exercises the kebab and lower-case forms and the anchors with parts removed. The check names join the required list of `docs/repository-management.md` after their first green run, as the matrix did.
- **Script tests:** `build/scripts/tests/add-module.test.sh` on a small fake tree: every edit lands once; a missing anchor skips that edit, prints it and exits non-zero; a bad prefix removes what the engine wrote; a second run changes nothing.
- **Template-repo guards:** the smoke test's drift and dead-reference checks keep passing; `templates/**` is excluded from the solution output and a smoke check asserts that no `templates/` folder and no `modulith:` anchor leaks outside the shared files that are meant to carry it.

## 8. Documents and housekeeping

- ADR 0036: the item template, anchors with soft failure, and the script shipping in the generated project, with the alternatives rejected (files only, built-in post-actions only, a compiled reference module, deriving from Audit).
- `build/template-content/README.md`: an "Adding a module" recipe with the exact command and a list of what it changes; the generated `CLAUDE.md` "Where things go" names the command; `docs/modules/_template.md` and `CONTRIBUTING.md` point to it.
- `docs/blueprint-review.md` row 7b marked done when merged; `docs/repository-management.md` lists the new checks; `docs/README.md` indexes the ADR.
- The project index (`bash build/scripts/project-index.sh`) is regenerated for the new tests, the new script and the anchors.

## 9. Risks and points the plan must settle

The plan's first task is a throwaway spike of the first four points below, because none was verified in the design session; each has a stated fallback.

- **The run-script post-action.** How it locates and runs `build/scripts/add-module.sh`, what it does without `--allow-scripts` in CI, and whether a non-zero exit is visible to the caller. Fallback: the template prints the command to run by hand and the CI job runs it.
- **Derived forms.** Whether the engine's kebab-case and lower-case forms exist in SDK 10.0.401. Fallback: the script computes both from `-n` and renames the generated files.
- **One install, two templates.** Whether `dotnet new install .` finds a template in a nested folder next to the solution template. Fallback: install both paths explicitly (`.` and `./templates/modulith-module`).
- **Where tests reach the module.** `InternalsVisibleTo` comes from `src/Directory.Build.props` (verified), so a new module under `src` needs no change; the spike confirms that a module built from the template is visible to the three test projects.
- **Windows.** The script runs under Git Bash; the CI legs run on Ubuntu. A local run on Windows is part of the spike. A project's migration step uses `dotnet ef` through the local tool manifest, which a project generated from this template already has.
- **Integration tests cannot run on this machine** (Docker is broken): their part of the CI legs is verified in CI only.
- **Anchor drift.** A refactor of `Program.cs` or `Assemblies.cs` that removes an anchor is caught only by the CI legs; the plan adds a unit test that every anchor the script uses exists in the template's shared files.

## 10. Spike results

The spike (Plan 7b, Task 1) ran a throwaway item template (symbols `prefix`, `contracts`, `sourceName` `ModuleName`, derived symbols, a run-script post-action) and a generated project (`dotnet new modulith-backend -n Acme.Smoke`) against SDK 10.0.401, on Windows 11 with Git Bash and a private hive (`--debug:custom-hive`). Ubuntu could not be run: every Linux statement below is unverified.

- **D1, the post-action.** A run-script post-action (`executable: bash`, `args: build/scripts/add-module.sh`) works, and its working directory is the output directory (the `-o` folder, which is the solution root when `-o` is not given). The assumptions that need correcting:
  - `--allow-scripts yes` runs it. `--allow-scripts no` still writes the files, does not run the script and makes `dotnet new` exit 104. **With no flag in a non-interactive shell the engine prompts `[Y(yes)|N(no)]`, gets an empty line every time and loops forever** (more than 100 MB of "Invalid input" in 15 s, killed by `timeout`, exit 124; the same with stdin from `/dev/null` and with stdin closed). Every CI step, script and doc example must therefore pass `--allow-scripts yes`; a bare `dotnet new modulith-module` in automation hangs.
  - A non-zero script exit makes `dotnet new` exit 105 ("Post action failed"), whatever the script's code (1 and 7 both gave 105); the generated files stay on disk. The exit code is visible to the caller, so the CI legs can rely on it. The engine prints the post-action's `manualInstructions` and "Actual command" on failure.
  - **Symbols are not substituted into the post-action's `args`.** `ModuleName` and `ProjectPrefix` reach the script literally, and so do `%name%`, `{name}`, `{{name}}` and `$name`. On Windows `bash.exe` additionally drops `{` and `}` from such arguments (`{{name}}` arrived as `name`). **Chosen fallback (replaces the spec's "print the command" fallback, which is not needed): the post-action passes no arguments and the template ships a marker file at the output root, `.modulith-module`, whose content is substituted by the engine** (name, prefix, kebab form, lower-case form, one value per line; content replacement was verified to work in an extension-less dot file). The script reads it, does its work and deletes it. The `contracts` flag needs no marker: a `sources` modifier `"condition": "(!contracts)"` excludes the Contracts project folder, so the script tests whether `src/Modules/{M}/{P}.Modules.{M}.Contracts` exists (verified: present with `--contracts true`, absent otherwise).
  - A second run for the same module never reaches the post-action: the engine lists the files it would overwrite and exits 73 without writing anything (verified with and without `--contracts`), as section 3 assumes. A missing `--prefix` exits 102 ("Missing mandatory option(s)") before any file is written. `--allow-scripts` accepts `No|Prompt|Yes` (case-insensitive). With no `-n` the name is the output folder's name, so the guide should always show `-n`.
- **D2, derived forms.** Both exist in SDK 10.0.401: a `derived` symbol with `valueSource: name`, `valueTransform: kebabCase` (or `lowerCase`), and `forms` entries `{"identifier": "kebabCase"}` and `{"identifier": "lowerCase"}`. Observed (kebab, lower): `LeaveManagement` gives `leave-management`, `leavemanagement`; `HRPayroll` gives `hr-payroll`, `hrpayroll`; `XMLParser` gives `xml-parser`, `xmlparser`; `Billing` gives `billing`, `billing`; **`Billing2` gives `billing-2`, `billing2` and `A2B` gives `a-2-b`, `a2b`** (the kebab form splits at a digit boundary). The fallback (script computes both) is not needed, but the route segment and the docs file name for a name with a digit come out as `billing-2`; the plan either accepts that (and uses the engine's form everywhere, carried to the script through the marker file) or narrows the name regex to letters. A derived symbol's `replaces` and `fileRename` both work, so the docs file name `module-kebab.md` became `billing-2.md`; the tokens must not collide with other text (the spike used `module-kebab` and `module-lower`).
- **D3, one install, two templates.** `dotnet new install <repo root>` registers both: with `templates/modulith-module/.template.config/template.json` next to the root `.template.config`, the install printed `modulith-backend` and `modulith-module`, and `dotnet new list modulith` listed both; `dotnet new modulith-module` then ran from that install. The explicit second install path is not needed. **The root template must exclude `templates/**`**: without it the generated project carried `templates/modulith-module/` including its `.template.config` (the root `.template.config/**` exclude matches only the top-level folder); with `"templates/**"` added to the first `sources.exclude` the generated project had no `templates` folder. Installing the unmodified `C:/w/b7` lists only `modulith-backend`, as expected before the folder exists.
- **D4, names and test access.** `sourceName` renames both content and file or folder names. A parameter's `replaces` renames content only: **without `"fileRename": "ProjectPrefix"` on the `prefix` symbol the folder and file names stayed `ProjectPrefix.Modules.Billing`** (content became `Acme.Shop`); with `fileRename` they became `Acme.Shop.Modules.Billing`. The `prefix` symbol therefore needs both `replaces` and `fileRename`. A module generated this way into the generated project, referenced from the Unit, Architecture and Integration test projects, built with 0 errors with an `internal` type read from a file in each test project, and the module assembly carries the three `InternalsVisibleTo` attributes (from `src/Directory.Build.props`, renamed with the project). A plain `dotnet restore` writes the new project's `packages.lock.json` (`RestorePackagesWithLockFile` is on).
- **D5, formatting and anchors.** The baseline holds: `dotnet format --verify-no-changes --no-restore` on a restored `Acme.Smoke` project exits 0 (about 44 s). The result that settles the using placement: **the format check does not enforce using order.** A `using Acme.Smoke.Modules.Billing;` added after the last using (out of order) exits 0, and the same line inserted in sorted position (after `Acme.Smoke.Modules.Auth`) also exits 0, each with the matching `AddBillingModule` call and a stub providing the namespace. The generated `Program.cs` already has `using Scalar.AspNetCore;` before the `Acme.Smoke.` lines (sorted for `TemplateName`, not for `Acme`), and a `using System.Linq;` placed after non-System usings also passed, so no sort rule is configured. Controls show the check is live: three spaces of extra indentation on a statement fails with `WHITESPACE: Fix whitespace formatting`; an unused `using System.Net;` is not reported by `dotnet format` (so a using that the wiring adds but nothing uses will not fail it). The plan inserts the using at the usings anchor and need not sort. One related finding: a file whose namespace differs from its folder raises `IDE0130` and fails the check, so the item template's generated files must keep namespace and folder in step (the spike's files did).
- **Windows against Linux.** Everything above ran on Windows (Git Bash). Not verified on Ubuntu: the post-action's `bash` lookup (on Windows `bash.exe` in `WindowsApps` can shadow Git's bash outside Git Bash), the brace stripping (probably a `bash.exe` argument-handling effect that the marker-file fallback avoids), the `--allow-scripts` prompt loop (the engine's own code, so almost certainly the same), and `dotnet format` ordering (same SDK logic, so expected to agree).
