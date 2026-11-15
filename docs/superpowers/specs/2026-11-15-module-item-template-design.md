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
