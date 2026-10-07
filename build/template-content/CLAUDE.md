# CLAUDE.md

## Overview

`TemplateName` is a modular monolith on ASP.NET Core 10 with SQL Server. Each business module is two projects: `TemplateName.Modules.{Module}` (Domain, Application, Infrastructure and Endpoints as folders, `internal` by default) and `TemplateName.Modules.{Module}.Contracts` (the only public surface other modules may reference). Building blocks live under `src/BuildingBlocks/`, the host under `src/Host/`.

## Commands

```bash
dotnet build -c Release                 # warnings are errors in Release
dotnet test                             # unit, architecture and integration tests (Docker needed for the latter)
dotnet run --project src/Host/TemplateName.Api
dotnet format --verify-no-changes       # style check, same as CI
dotnet ef migrations add {Verb}{What} \
  --project src/Modules/{Module}/TemplateName.Modules.{Module} \
  --startup-project src/Host/TemplateName.Api \
  --context {Module}DbContext
```

## Where things go

- A module has `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`, `Resources/` and `{Module}Module.cs`; the layout is fixed.
- Each command or query gets its own folder under `Application/{Aggregates}/{UseCase}/` holding the message, handler and validator.
- Modules talk to each other only through `*.Contracts` (integration events and public interfaces). Never reference another module's project or read its tables.
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
- No new package without Central Package Management (`Directory.Packages.props`) and an allowed licence.
- Never use `--no-verify`.
- Never edit a migration that has been applied; add a new one.

## Read before editing

- [`docs/coding-conventions.md`](docs/coding-conventions.md)
- [`CONTRIBUTING.md`](CONTRIBUTING.md)
- [`docs/architecture/overview.md`](docs/architecture/overview.md)
- [`docs/adr/`](docs/adr/)
