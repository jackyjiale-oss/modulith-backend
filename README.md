# Modulith Backend

[![CI](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Modulith is a `dotnet new` template for production-ready backends: an ASP.NET Core 10 modular monolith on SQL Server. Each business module owns its code, schema and migrations behind a compiler-enforced boundary, and talks to other modules only through its `Contracts` project. The plumbing every service needs (errors, validation, persistence, reliable events, observability and HTTP hardening) is already wired and tested, so a new project starts with features instead of infrastructure.

- Modular monolith with two projects per module (`Modules.{Module}` and `Modules.{Module}.Contracts`), `internal` by default
- `Result`/`Error` pattern mapped to RFC 9457 ProblemDetails, every error carrying `code` and `traceId`
- Injected command and query handlers with Scrutor decorators for validation (FluentValidation) and logging; no MediatR
- EF Core for writes and Dapper for reads, one `DbContext` and schema per module, SQL Server-ordered sequential GUIDs
- Per-module transactional outbox with leased, multi-instance-safe dispatch
- `Idempotency-Key` support for unsafe requests
- Serilog with sensitive-data masking, OpenTelemetry traces and metrics, Aspire dashboard locally
- Security headers, CORS, rate limiting, forwarded headers, request size limits and health checks
- OpenAPI with Scalar and literal `/api/v1` routes
- Architecture tests (NetArchTest), unit tests and Testcontainers integration tests; CI with an 80 % coverage gate and a template smoke test
- Conventional Commits enforced by a git hook and CI

## Status

Pre-release, implementing Plan 1 (foundation and core baseline). Expect breaking changes until the first release.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0.401 or a later 10.0 feature band (pinned in `global.json`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) for local SQL Server and the integration tests
- Git and Bash (Git Bash on Windows) for the hooks and scripts

## Using the template

```bash
git clone https://github.com/jackyjiale-oss/modulith-backend.git
cd modulith-backend
dotnet new install .
```

Once Plan 7 publishes the package, install it from NuGet instead: `dotnet new install Modulith.Backend.Templates`.

Then generate a project:

```bash
dotnet new modulith-backend -n Acme.Hr
cd Acme.Hr
```

From here the generated project's own `README.md` takes over. `dotnet new uninstall .` (from the repository folder) removes the template again.

## Repository layout

```text
.github/                 CI workflow, Dependabot, pull request and issue templates (also generated)
.template.config/        dotnet new template definition
build/scripts/           commit-message check, template smoke test, GitHub repository setup
build/template-content/  files that replace their root counterparts in generated projects (README, CHANGELOG, CLAUDE.md)
docs/                    coding conventions, ADRs, module documents, plans
src/BuildingBlocks/      SharedKernel, Application.Common, Infrastructure.Common, Web.Common
src/Host/                TemplateName.Api, the ASP.NET Core host
src/Modules/             business modules (Sample shows every pattern)
tests/                   unit, architecture and integration tests
```

`TemplateName` is the placeholder that `dotnet new` replaces with the project name.

## Documentation

- [`BACKEND_TEMPLATE_BLUEPRINT.md`](BACKEND_TEMPLATE_BLUEPRINT.md): the original blueprint
- [`docs/blueprint-review.md`](docs/blueprint-review.md): the review of the blueprint; it wins where they differ
- [`docs/coding-conventions.md`](docs/coding-conventions.md): naming, files and code style
- [`CONTRIBUTING.md`](CONTRIBUTING.md): branches, Conventional Commits and pull requests
- [`docs/repository-management.md`](docs/repository-management.md): repository settings, branch protection and releases
- [`SECURITY.md`](SECURITY.md): reporting a vulnerability

Maintainers: after changing anything that ships, run `bash build/scripts/template-smoke.sh`.

## Licence

[MIT](LICENSE). Projects generated from the template do not inherit this licence; their owners choose their own.
