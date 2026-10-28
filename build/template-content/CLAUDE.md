# CLAUDE.md

## Overview

`TemplateName` is a modular monolith on ASP.NET Core 10 with SQL Server. Each business module is two projects: `TemplateName.Modules.{Module}` (Domain, Application, Infrastructure and Endpoints as folders, `internal` by default) and `TemplateName.Modules.{Module}.Contracts` (the only public surface other modules may reference); a module with nothing to offer other modules ships the first project only (Notifications, Audit). Building blocks live under `src/BuildingBlocks/`, the host under `src/Host/`.

## Commands

```bash
dotnet build -c Release                 # warnings are errors in Release
dotnet test                             # unit, architecture and integration tests (Docker needed for the latter)
dotnet run --project src/Host/TemplateName.Api
dotnet format --verify-no-changes       # style check, same as CI
dotnet ef migrations add {Verb}{What} \
  --project src/Modules/{Module}/TemplateName.Modules.{Module} \
  --startup-project src/Host/TemplateName.Api \
  --context {Module}DbContext \
  --output-dir Infrastructure/Persistence/Migrations
```

## Where things go

- A module has `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`, `Resources/` (its error messages, so a module with no error code, such as Audit, has none) and `{Module}Module.cs`; the layout is fixed. A module that declares notification types adds `Templates/` (embedded Scriban files) at the project root, as Notifications does.
- Each command or query gets its own folder under `Application/{Aggregates}/{UseCase}/` holding the message, handler and validator.
- Modules talk to each other only through `*.Contracts` (integration events and public interfaces). Never reference another module's project or read its tables. Messages to users go through the Notifications module (an integration event in, email and in-app out); a module never sends mail itself. Today only Auth's types exist; see `docs/modules/notifications.md` before adding one.
- Audit a new entity with `[Audited]` (`TemplateName.SharedKernel.Auditing`) when an administrator may need to know who changed it; the Audit module records the field changes, in the same save, through the module's own outbox. Its key is `ValueGeneratedNever()`; a secret carries `[AuditIgnore]` or `[AuditMask]` (and a `byte[]` column `[AuditIgnore]`); a column with a default value is not audited. See `docs/modules/audit.md`.
- Tests mirror the source: `tests/{TestProject}/{Module or Area}/{Subject}Tests.cs`.

## Definition of done

- Tests are written first and are green.
- The matching page under `docs/` is updated in the same change.
- Every new error code exists in all three `.resx` languages (`en`, `ms`, `zh-Hans`).
- A significant or hard-to-reverse decision gets an ADR in `docs/adr/`.
- The commit message is a Conventional Commit; it becomes the changelog.

## Hard rules

- No secrets in files. Use user-secrets or environment variables.
- No `DateTime.Now` or `DateTime.UtcNow`; inject `TimeProvider`.
- No access to another module's tables.
- No new package without Central Package Management (`Directory.Packages.props`) and an allowed licence (`build/licenses/`); also read its terms for usage fees.
- Never use `--no-verify`.
- Never edit a migration that has been applied; add a new one.

## Read before editing

- [`docs/coding-conventions.md`](docs/coding-conventions.md)
- [`CONTRIBUTING.md`](CONTRIBUTING.md)
- [`docs/README.md`](docs/README.md) (index of every document)
- [`docs/architecture/overview.md`](docs/architecture/overview.md)
- [`docs/adr/`](docs/adr/)
