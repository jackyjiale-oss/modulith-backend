# Modulith Backend

[![CI](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Modulith is a `dotnet new` template for production-ready backends: an ASP.NET Core 10 modular monolith on SQL Server. Each business module owns its code, schema and migrations behind a compiler-enforced boundary, and talks to other modules only through its `Contracts` project. The plumbing every service needs (errors, validation, persistence, reliable events, observability and HTTP hardening) is already wired and tested, so a new project starts with features instead of infrastructure.

- Modular monolith with two projects per module (`Modules.{Module}` and `Modules.{Module}.Contracts`), `internal` by default
- `Result`/`Error` pattern mapped to RFC 9457 ProblemDetails, every error carrying `code` and `traceId`
- Error messages in English, Malay and Simplified Chinese from `.resx` files, chosen by `Accept-Language`, with translation completeness checked by architecture tests
- Injected command and query handlers with Scrutor decorators for validation (FluentValidation) and logging; no MediatR
- EF Core for writes and Dapper for reads, one `DbContext` and schema per module, SQL Server-ordered sequential GUIDs
- Cursor (keyset) pagination for list endpoints, with allow-listed sorts and opaque cursors that detect a changed sort or filter
- Per-module transactional outbox with leased, multi-instance-safe dispatch
- `Idempotency-Key` support for unsafe requests
- Serilog with sensitive-data masking, OpenTelemetry traces and metrics, Aspire dashboard locally
- Security headers, CORS, rate limiting, forwarded headers, request size limits and health checks
- OpenAPI with Scalar and literal `/api/v1` routes
- Auth module: registration with email confirmation, login with lockout, ES256 access tokens with a JWKS, rotating refresh tokens with reuse detection, sessions, password reset and change, role-based permissions, administration endpoints and an audit log (Mailpit catches the emails locally)
- Architecture tests (NetArchTest), unit tests and Testcontainers integration tests; CI with an 80 % coverage gate and a template smoke test
- Conventional Commits enforced by a git hook and CI

## Status

Pre-release: Plan 1 (foundation and core baseline) and Plan 2 (the Auth module) are implemented; notifications, MFA and delivery follow. Expect breaking changes until the first release. The Malay and Simplified Chinese messages are drafts awaiting native review (release checklist in [`CONTRIBUTING.md`](CONTRIBUTING.md), Section 5).

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

## Run the backend

The repository itself runs as a project named `TemplateName`: this is how to try the Auth module before generating your own. A generated project's `README.md` has the same steps with its own name.

```bash
cp .env.example .env                 # then set your own SQL_SA_PASSWORD
docker compose up -d                 # SQL Server, the Aspire dashboard and Mailpit
dotnet user-secrets set "ConnectionStrings:Database" \
  "Server=127.0.0.1,1433;Database=TemplateName;User Id=sa;Password=<from .env>;TrustServerCertificate=True" \
  --project src/Host/TemplateName.Api
dotnet user-secrets set "Auth:Seed:AdminPassword" "<value>" --project src/Host/TemplateName.Api
dotnet run --project src/Host/TemplateName.Api
```

In Development the API applies the migrations and seeds the roles, the permissions and the first administrator, `admin@localhost.test`, with the password you put in user secrets (12 to 128 characters; choose your own, never commit it). Sign in as that administrator with `POST /api/v1/auth/login` in Scalar (<https://localhost:5001/scalar/v1>) and send the `accessToken` as a bearer token. Register a user the same way: the confirmation email is not sent anywhere; **Mailpit** catches it, and its inbox is at <http://localhost:8025>. Remove `Auth:Seed:AdminPassword` from user secrets after the first start. Health is at <https://localhost:5001/health/ready>.

## Repository layout

```text
.github/                 CI and release workflows, Dependabot, pull request and issue templates (also generated)
.template.config/        dotnet new template definition
build/licenses/          licence allow-list and licence-URL mappings for the licence check
build/scripts/           commit-message check, template smoke test, GitHub repository setup
build/template-content/  files that replace their root counterparts in generated projects (README, CHANGELOG, CLAUDE.md, release manifest, Version.props)
docs/                    architecture, host, building-block and module documents, ADRs, conventions, plans
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

## Adding a paged list endpoint

List endpoints page by cursor (keyset), never by offset (ADR 0010). Clients send `pageSize` (default 20, max 100), `cursor`, `sort` (`-createdAt`, `startDate,-createdAt`) and `includeTotalCount`, and get `{ items, pageSize, nextCursor, previousCursor, totalCount? }`. The Sample module's `GET /api/v1/sample/leave-requests` (`Application/LeaveRequests/List/`) is the worked example.

1. **Allow-list the sort fields.** Add `{Aggregates}SortFields` next to the query: one `SortField(apiName, "[Column]", typeof(T))` per sortable field (camelCase API name, a bracketed column constant, a non-nullable column), the `Id` tie-breaker, `Allowed` and `Default`. Column names are written into SQL, so they are only ever these constants, never request text.
2. **Index every allowed sort.** In the entity configuration add one index per sort, `(filter columns…, SortColumn, Id)`, for example `HasIndex(x => new { x.EmployeeId, x.CreatedAt, x.Id })`, then generate a migration with `dotnet ef migrations add` (see the module page). Migration review checks that each allowed sort has its index. Combinations without one (a multi-term sort such as `startDate,-createdAt`, or a filter that does not lead the sort's index, such as `employeeId` with `sort=startDate` in the Sample module) fall back to a scan plus a top-N sort: allow-list only covered sorts, or add the index.
3. **Write the query, validator and handler.** `List{Aggregates}Query(filters…, CursorPageRequest Page) : IQuery<CursorPage<{Item}Response>>`; the validator checks `Page.PageSize` is 1 to `CursorPageRequest.MaxPageSize` (`.OverridePropertyName("PageSize")`, so the error key is `pageSize`). The handler:
   - parses the sort with `SortSpecification.Parse(page.Sort, Allowed, Id, Default)`;
   - hashes the canonical filter text with `CursorCodec.ComputeFilterHash("employeeId=…;status=…")` and, when a cursor was sent, decodes it with `CursorCodec.Decode(cursor, sort, filterHash)` (return the error on failure);
   - builds `KeysetSqlBuilder.Build(sort, cursor, page.PageSize)` and runs `SELECT TOP (@Take) … WHERE IsDeleted = 0 AND {filters} AND {WhereClause} ORDER BY {OrderByClause}` with Dapper, adding `Take` and the filter values to `keyset.Parameters`; with `IncludeTotalCount` it also runs `SELECT COUNT_BIG(*)` with the same filters and no keyset;
   - marks `DateTime` columns `DateTimeKind.Utc` and returns `CursorPageBuilder.Build(rows, keyset, cursor, sort, filterHash, row => [.. sort.Terms.Select(term => ValueOf(row, term.Field))], totalCount)`. Key values come from the fetched rows, so they are exactly what SQL Server stored.
4. **Map the endpoint.** `group.MapGet("/", ListAsync)` with an `[AsParameters]` request record whose parameters carry `[FromQuery(Name = "camelCase")]` (otherwise OpenAPI lists them in PascalCase), `.Produces<CursorPage<{Item}Response>>()` and `.ProducesValidationProblem()`.
5. **Test it** against SQL Server: walking forward and backward visits every row once, rows inserted between pages cause no duplicates, ties on the sort value are broken by `Id`, filters and sort apply, soft-deleted rows never appear, and a changed sort or a tampered cursor gets `400 pagination.cursor_mismatch` or `400 pagination.invalid_cursor`.

Generated projects carry the same recipes in their own `README.md`.

## Documentation

These ship with every generated project (index: [`docs/README.md`](docs/README.md)):

- [`docs/architecture/overview.md`](docs/architecture/overview.md): module map, request lifecycle, data ownership, decisions
- [`docs/services/api.md`](docs/services/api.md): the host's pipeline, every configuration section, health, environments, migrations
- [`docs/building-blocks/`](docs/building-blocks/): SharedKernel, Application.Common, Infrastructure.Common, Web.Common
- [`docs/modules/`](docs/modules/): one page per module, from [`_template.md`](docs/modules/_template.md)
- [`docs/adr/`](docs/adr/): architecture decision records 0001 to 0017
- [`docs/coding-conventions.md`](docs/coding-conventions.md): naming, files and code style
- [`CONTRIBUTING.md`](CONTRIBUTING.md): branches, Conventional Commits, pull requests, releases and licences

These are about the template repository only and are not generated:

- [`BACKEND_TEMPLATE_BLUEPRINT.md`](BACKEND_TEMPLATE_BLUEPRINT.md): the original blueprint
- [`docs/blueprint-review.md`](docs/blueprint-review.md): the review of the blueprint; it wins where they differ
- [`docs/superpowers/plans/`](docs/superpowers/plans/): implementation plans
- [`docs/repository-management.md`](docs/repository-management.md): repository settings, branch protection and releases
- [`SECURITY.md`](SECURITY.md): reporting a vulnerability

Maintainers: after changing anything that ships, run `bash build/scripts/template-smoke.sh`. It installs the template into a private template hive in a temporary folder, so your installed templates are left alone.

## Releases and licences

release-please turns the Conventional Commits on `main` into a release PR (`chore(release): release x.y.z`); merging it tags `vx.y.z`, writes `CHANGELOG.md`, bumps `<Version>` in `Version.props` (imported by `Directory.Build.props`; generated projects get their own, starting at `0.0.0`) and attaches a generated `THIRD-PARTY-NOTICES.md` to the GitHub Release (`.github/workflows/release.yml`, `release-please-config.json`). The `licenses` CI job fails when a NuGet dependency's licence is not in [`build/licenses/allowed-licenses.json`](build/licenses/allowed-licenses.json) (MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause); [`build/licenses/README.md`](build/licenses/README.md) has the local command and the verified exceptions.

## Licence

[MIT](LICENSE). Projects generated from the template do not inherit this licence; their owners choose their own.
