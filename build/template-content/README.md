# TemplateName

> Describe what TemplateName does.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0.401 or a later 10.0 feature band (pinned in `global.json`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (local SQL Server, the Aspire dashboard and the integration tests)

## Quick start

```bash
cp .env.example .env                 # then set your own SQL_SA_PASSWORD
docker compose up -d                 # SQL Server and the Aspire dashboard
git init                             # skip if this folder is already a repository
git config core.hooksPath .githooks  # commit-message check (CONTRIBUTING.md)
dotnet user-secrets set "ConnectionStrings:Database" \
  "Server=localhost,1433;Database=TemplateName;User Id=sa;Password=<from .env>;TrustServerCertificate=True" \
  --project src/Host/TemplateName.Api
dotnet run --project src/Host/TemplateName.Api
```

In Development the API applies the module migrations on startup.

| What | URL |
|---|---|
| API reference (Scalar) | <https://localhost:5001/scalar/v1> |
| Health (readiness) | <https://localhost:5001/health/ready> |
| Aspire dashboard (logs, traces, metrics) | <http://localhost:18888> |

## Modules

| Module | Document |
|---|---|
| Sample (leave requests) | [`docs/modules/sample.md`](docs/modules/sample.md) |

A new module gets its own page, copied from [`docs/modules/_template.md`](docs/modules/_template.md).

## Testing

```bash
dotnet test                                              # unit, architecture and integration tests (Docker must be running)
dotnet test --project tests/TemplateName.UnitTests         # unit tests only
dotnet test --project tests/TemplateName.ArchitectureTests # architecture rules only
dotnet test --config-file coverage.testconfig.json --coverlet --results-directory TestResults   # with coverage, as CI does
```

CI (`.github/workflows/ci.yml`) also checks formatting (`dotnet format --verify-no-changes`), builds in Release with warnings as errors, and fails below 80 % line coverage on SharedKernel, Application.Common and the module Domain and Application namespaces.

## Conventions

- [`docs/coding-conventions.md`](docs/coding-conventions.md): naming, files and code style
- [`CONTRIBUTING.md`](CONTRIBUTING.md): branches, Conventional Commits and pull requests
- [`docs/adr/`](docs/adr/): architecture decisions

Generated from [Modulith Backend](https://github.com/jackyjiale-oss/modulith-backend).

## Licence

Add a LICENSE file for this project.
