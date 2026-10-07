# Modulith Backend

[![CI](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Modulith is a `dotnet new` template for production-ready backends: an ASP.NET Core 10 modular monolith on SQL Server. Each business module owns its code, schema and migrations behind a compiler-enforced boundary, and talks to other modules only through its `Contracts` project. The plumbing every service needs (errors, validation, persistence, reliable events, observability and HTTP hardening) is already wired and tested, so a new project starts with features instead of infrastructure.

- Modular monolith with two projects per module (`Modules.{Module}` and `Modules.{Module}.Contracts`), `internal` by default
- `Result`/`Error` pattern mapped to RFC 9457 ProblemDetails, every error carrying `code` and `traceId`
- Error messages in English, Malay and Simplified Chinese from `.resx` files, chosen by `Accept-Language`, with translation completeness checked by architecture tests
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

Pre-release, implementing Plan 1 (foundation and core baseline). Expect breaking changes until the first release. The Malay and Simplified Chinese messages are drafts awaiting native review (release checklist in [`CONTRIBUTING.md`](CONTRIBUTING.md), Section 5).

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

## Adding a language / adding an error message

Error messages are `.resx` resources keyed by the error code (ADR 0009). Clients get the stable `code` and `params`; only `detail` follows `Accept-Language` (`en`, `ms`, `zh-Hans`; anything else gets English).

**Add an error message**

1. Declare the error in the module's `Domain/{Aggregates}/{Aggregate}Errors.cs`, for example `Error.NotFound("leave.not_found", $"Leave request '{id}' was not found.") with { Parameters = new Dictionary<string, object?> { ["id"] = id } }`. Parameters are returned to clients: no secrets or personal data.
2. Add the code as a key to `Resources/{Module}ErrorMessages.resx` (English), `.ms.resx` and `.zh-Hans.resx`, with the same `{placeholders}` in every language. Mark a translation you could not have reviewed with `<comment>Draft – needs native review</comment>`.
3. Run `dotnet test --project tests/TemplateName.ArchitectureTests`: `TranslationTests` fails on a missing key, an extra key, a placeholder mismatch, or an error code without an English message.

A new module creates `Resources/{Module}ErrorMessages.cs` (an empty `internal sealed class`) next to its three `.resx` files, calls `services.AddErrorMessages<{Module}ErrorMessages>()` in `Add{Module}Module`, and adds the marker to `Assemblies.ErrorMessageResources` in the architecture tests.

**Add a language**

1. Add a `{Marker}.{culture}.resx` next to every `*ErrorMessages.resx` (under `src/BuildingBlocks/` and `src/Modules/`) with every key translated.
2. Add the culture to `Localization:SupportedUICultures` in `appsettings.json`.
3. Add the culture to `TranslationTests` and its FluentValidation messages to `ValidationMessageTranslations` (Web.Common) if FluentValidation does not ship that exact culture.
4. Have a native speaker review the new strings before the next release.

Generated projects carry the same recipe in their own `README.md`.

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
