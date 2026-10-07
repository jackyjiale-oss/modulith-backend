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

## Recipes

### Adding an error message or a language

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

### Adding a paged list endpoint

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

## Documentation

The index is [`docs/README.md`](docs/README.md).

- [`docs/architecture/overview.md`](docs/architecture/overview.md): module map, request lifecycle, data ownership, decisions
- [`docs/services/api.md`](docs/services/api.md): pipeline, every configuration section with defaults, health, environments, migrations
- [`docs/building-blocks/`](docs/building-blocks/): SharedKernel, Application.Common, Infrastructure.Common, Web.Common
- [`docs/modules/`](docs/modules/): one page per module
- [`docs/adr/`](docs/adr/): architecture decisions
- [`docs/coding-conventions.md`](docs/coding-conventions.md): naming, files and code style
- [`CONTRIBUTING.md`](CONTRIBUTING.md): branches, Conventional Commits, pull requests, releases and licences
- [`CHANGELOG.md`](CHANGELOG.md): release history, generated by release-please

## Releases and licences

release-please (`.github/workflows/release.yml`) keeps a `chore(release): release x.y.z` pull request open from the Conventional Commits on `main`; merging it tags `vx.y.z`, writes `CHANGELOG.md`, bumps `<Version>` in `Directory.Build.props` and attaches a generated `THIRD-PARTY-NOTICES.md` to the GitHub Release. Add the repository secret `RELEASE_PLEASE_TOKEN` so CI runs on that pull request ([`CONTRIBUTING.md`](CONTRIBUTING.md), Section 5). The `licenses` CI job checks every NuGet dependency against [`build/licenses/allowed-licenses.json`](build/licenses/allowed-licenses.json); see [`build/licenses/README.md`](build/licenses/README.md).

Generated from [Modulith Backend](https://github.com/jackyjiale-oss/modulith-backend).

## Licence

Add a LICENSE file for this project.
