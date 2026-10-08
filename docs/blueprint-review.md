# Blueprint Review and Amendments

**Reviews:** `BACKEND_TEMPLATE_BLUEPRINT.md` (Draft v1.0)
**Date:** 2026-10-06
**Status:** Accepted. Where this document and the blueprint disagree, this document wins until the blueprint is updated.

The blueprint is unusually thorough: the security rules, token model, schema and testing must-haves are better than most production systems. The problems are scope, a handful of internal contradictions that would surface as bugs, and some complexity that works against the stated goals ("lean", "fast delivery"). This review records each challenge, the decision taken and the reason.

---

## 1. Decisions taken (with the author)

| # | Topic | Blueprint | Decision | Why |
|---|---|---|---|---|
| D1 | Module project layout | 5 projects per module (~35 projects total) | **2 projects per module**: `Modules.X` (Domain / Application / Infrastructure / Endpoints as folders, `internal` by default) + `Modules.X.Contracts` | Layer projects force every type to be `public`, which weakens the *module* boundary, the one that matters most in a modular monolith. Layering inside a module is enforced by architecture tests on namespaces instead. Faster builds, fewer csproj files to keep in sync. |
| D2 | Database providers | SQL Server + PostgreSQL switch | **SQL Server only for v1** | EF migrations are provider-specific (two migration sets per module), Dapper SQL differs, and Section 11 already uses SQL Server-only types (`DATETIME2`, `ROWVERSION`, `ISJSON`). PostgreSQL becomes a post-v1 switch. |
| D3 | v1 scope | Phases 0–8 | **Lean MVP** (see Section 6) | The blueprint is three products: a template, an identity server and a notification service. Shipping a usable template early and adding modules afterwards beats a long march to "everything". |

---

## 2. Correctness issues (would become bugs)

### 2.1 A shared `platform.OutboxMessages` contradicts "no module reads another module's tables"
Section 7.8 puts one outbox in the `platform` schema, written "inside the business transaction". With one `DbContext` per module, each module's context would have to map and migrate the same `platform` table. That breaks module rule 1 and makes migrations from different contexts fight over one table.
**Amendment:** every module has its **own** outbox in its own schema (`sample.OutboxMessages`, `auth.OutboxMessages`, ...) plus an `OutboxMessageConsumers` table that records which handler has processed which message (per-handler idempotency). A generic `OutboxDispatcher<TContext>` serves every module.

### 2.2 "Notifications committed in the same transaction" is impossible across modules
Section 10.2 has Auth raise an event and Notifications write `notify.*` rows "in same transaction (outbox)". Different modules use different `DbContext`s and so different transactions. **Amendment:** Auth writes to `auth.OutboxMessages`. The dispatcher publishes an integration event, and the Notifications module consumes it idempotently (inbox) and creates its rows. Delivery is reliable and eventually consistent.

### 2.3 `AuditTrailInterceptor` writing to `audit.*` from every module's context
Same problem as 2.1. **Amendment:** the interceptor turns entity changes into outbox messages in the owning module's schema, and the Audit module consumes them. This is deferred to the Platform plan.

### 2.4 UUID v7 does not give sequential inserts on SQL Server
`Guid.CreateVersion7()` puts the timestamp in the first bytes, but SQL Server orders `uniqueidentifier` by bytes 10–15 first. To SQL Server, v7 IDs look random, so the clustered-index benefit claimed in ADR-0005 does not apply.
**Amendment:** `SharedKernel.SequentialGuid.Create(DateTimeOffset)` puts the timestamp in bytes 10–15 (SQL Server order). It is unit-tested with `System.Data.SqlTypes.SqlGuid`, which implements SQL Server's comparison rules. Revisit if PostgreSQL is added (there, v7 is correct).

### 2.5 `TransactionDecorator` cannot work generically with one `DbContext` per module
A generic decorator doesn't know which module's unit of work to commit. EF `SaveChanges` is already atomic, and the outbox interceptor writes events in the same `SaveChanges`. **Amendment:** drop `TransactionDecorator`; handlers call their module's `IUnitOfWork.SaveChangesAsync`.

### 2.6 Dapper bypasses EF global query filters
Soft-delete and tenant filters (7.6) only apply to EF queries. Every Dapper read model must filter `IsDeleted = 0` (and `TenantId`) by hand, or deleted and cross-tenant data leaks. **Amendment:** a must-test rule: each Dapper query handler gets an integration test proving soft-deleted rows are invisible.

### 2.7 Permission cache key needs data the request doesn't have
9.3 caches permissions under `perm:{userId}:{permissionsVersion}`, but `permissionsVersion` is not in the JWT, so building the key needs a DB read on every request. That defeats the cache. **Amendment (Auth plan):** cache under `perm:{userId}`, explicitly remove it on any role or permission change, and keep the HybridCache L1 lifetime for this entry short (30 s), because L1 isn't invalidated across instances.

### 2.8 Phase 2 (auth) needs email before Phase 4 (notifications) exists
Email verification and password reset need to send mail. **Amendment:** the Auth plan ships a minimal `IEmailSender` (SMTP to Mailpit). The Notifications plan later replaces it with templated, outbox-backed delivery.

### 2.9 Hangfire dashboard can't be protected by a JWT permission
A browser navigating to `/jobs` sends no bearer token. **Amendment:** the dashboard is off by default. When enabled it uses a separate scheme (cookie/BFF, or basic auth over HTTPS) or is exposed only on an internal port.

### 2.10 SignalR with JWT puts the token in the query string
WebSockets send the token as `?access_token=`, which ends up in request logs. **Amendment (Notifications plan):** strip or mask `access_token` in request logging and proxy logs.

### 2.11 Rate limiting gaps
- The built-in ASP.NET Core limiter has **no Redis backend**. "Redis-backed when scaled out" needs a third-party package or per-instance limits divided by instance count. Record the choice in an ADR.
- `otp-send` "per target" needs the request **body**, which the limiter middleware can't see. Enforce it in the handler from `VerificationCodes.CreatedAt` (the resend cooldown already exists in the schema).
- Partitioning by IP behind a load balancer needs `ForwardedHeaders` with **known proxies**. Without them, everyone shares one bucket, or, if headers are trusted blindly, clients can spoof `X-Forwarded-For`.

### 2.12 Idempotency race
7.9 doesn't cover two concurrent requests with the same key. **Amendment:** insert an "in progress" row first (unique key). A concurrent duplicate gets `409 idempotency.in_progress`. 5xx responses are not stored, so the client can retry.

### 2.13 Small spec bugs
- `Error` is declared `sealed`, but validation needs a subtype that carries the field-error dictionary. `Error` becomes an unsealed record, with `sealed record ValidationError : Error`.
- Coverage: Section 12.5 says 80 % for Domain/Application, but `ci.yml` uses thresholds `70 80` across everything. **Amendment:** 80 % line coverage on SharedKernel, Application.Common and module `Domain`/`Application` namespaces only.
- `cd.yml` bundles migrations with `--project src/Host/TemplateName.Api`, but migrations live in the module projects (`--project` = module, `--startup-project` = Api), and `-r linux-x64` bundles won't run on the IIS (Windows) target.
- `cd.yml` runs on push to `main` in parallel with `ci.yml` and doesn't depend on it, so a red build can still deploy. CD must call the CI workflow as a reusable job and `needs` it.

---

## 3. Licensing updates (verify current terms before release)

| Package | Status (as of 2025) | Decision |
|---|---|---|
| MediatR, AutoMapper | Commercial licence from v13/v15 | Not used (blueprint already avoids MediatR) |
| FluentAssertions | v8+ commercial (Xceed) | **Shouldly** (blueprint listed both) |
| MassTransit | v9+ commercial; v8 remains Apache-2.0 | When RabbitMQ is added: pin v8 or evaluate Rebus/Wolverine; record in an ADR |
| QuestPDF | Free Community licence below a revenue threshold | Keep, with an ADR noting the threshold |
| Hangfire | LGPL core | Fine; Pro features not used |

---

## 4. Simplifications

| Area | Blueprint | Simplified to | Why |
|---|---|---|---|
| Platform projects | 9 `Platform.*` projects | A separate project **only when excluding it removes a NuGet dependency** (Hangfire, Azure Blob, ...); everything else is a folder in `Infrastructure.Common` | Same modularity, fewer projects |
| Decorators | Logging, Validation, Authorization, Transaction, Caching | **Logging, Validation** | Authorization is already enforced at the endpoint; Transaction is broken (2.5); caching is clearer as an explicit `HybridCache.GetOrCreateAsync` in the query handler |
| API versioning | URL-segment versioning library | Literal `/api/v1` route group; adopt `Asp.Versioning` when the first breaking `v2` endpoint appears | YAGNI; the route shape stays the same |
| Correlation ID | Separate `CorrelationId` enricher | Use the W3C `traceId` everywhere (logs, `X-Trace-Id` header, ProblemDetails) | One ID, natively propagated by OpenTelemetry |
| Dev observability | Seq + Aspire Dashboard | **Aspire Dashboard only** (logs via the OTLP Serilog sink, plus traces and metrics) | One container instead of two; Seq stays an optional add-on |
| Vulnerable-package check | `grep High\|Critical` on CLI output | NuGet Audit at restore (`NuGetAuditMode=all`, `NU1903`/`NU1904` as errors) | Built in, fails locally as well as in CI |
| Migrations in CD | One EF bundle per `DbContext` | A `migrate` command-line mode of the API (`dotnet TemplateName.Api.dll migrate`), which runs the same `MigrateModuleDatabasesAsync` used in dev and tests | One artifact, one code path, works for Docker and IIS; the migration job can use a DDL-privileged connection string |
| Module toggles | Config switch **and** template switch | Template switch removes modules; config selects **providers** within a module | Avoids maintaining two toggle mechanisms |
| Template switches | 14 switches from the start | Introduced with the module they control; v1 ships `notifications`, `includeSample`, `caching` | Fewer combinations to test in the matrix |
| Idempotency scope | Middleware on all POSTs | Opt-in per endpoint: `.WithIdempotency()` | Explicit, and no cost on endpoints that don't need it |

---

## 5. Additions

1. **Template-ready from day one.** The repo has a valid `.template.config/template.json` from Plan 1, and CI generates a project from it and builds it on every PR. Retrofitting template conditionals at the end (blueprint Phase 8) is where templates usually break.
2. **`dotnet new modulith-module` item template** (Packaging plan): scaffolds a new module (both projects, DbContext, outbox, endpoints, an architecture-test entry). Over a project's lifetime this saves more time than most feature switches.
3. **Pluggable auth (post-v1):** `--auth builtin|external`. Many client projects will use Entra ID, Keycloak or Auth0. Then the Auth module shrinks to JWT validation + RBAC, and the riskiest code (credential handling) isn't yours to maintain.
4. **Forwarded headers** configuration and **`DbUpdateConcurrencyException` → 409** mapping in the core baseline.
5. **Prerequisites documented:** .NET SDK 10.0.4xx, Docker Desktop (Testcontainers), PowerShell or Git Bash.

---

## 6. Revised roadmap (one implementation plan per row)

Each plan produces working, tested software on its own.

| Plan | Contents | Blueprint sections |
|---|---|---|
| **1. Foundation & core baseline** | Repo, SharedKernel, handlers + decorators, ProblemDetails, observability, HTTP security, persistence + interceptors, per-module outbox, idempotency, Sample module (proves the pipeline), architecture tests, CI, template smoke, **i18n (Section 8), cursor pagination (Section 9), docs + changelog + licence checks (Section 11)**. Action plan: `docs/superpowers/plans/2026-10-06-foundation-and-core-baseline.md` | 4–7, 7.14, 11.1, 11.4, 12, 13.2, 14, Phase 0–1 |
| 2. Auth core | Identity in `auth` schema, register / verify email / login / forgot / reset / change password, JWT ES256 + JWKS (key from config), refresh rotation + reuse detection, sessions, logout(-all), RBAC + `.RequirePermission()`, permission cache, auth audit log, admin users/roles, minimal SMTP `IEmailSender`, Mailpit | 9 (core), Phase 2 |
| 3. Notifications | Integration events + inbox, types, Scriban templates, email + in-app (SignalR) channels, delivery worker with retries / dead-letter, preferences, quiet hours, security notifications wired to auth events | 10 (email + in-app), Phase 4 (part) |
| 4. MFA | TOTP + recovery codes, MFA challenge flow, step-up, trusted devices | 9 (MFA), Phase 5 (part) |
| 5. Platform services | HybridCache + Redis, Hangfire + cleanup jobs, HttpClient resilience, audit trail via outbox | 8.1–8.4, 8.9, Phase 3 |
| 6. Delivery pipeline | Dockerfile (non-root, healthcheck), `cd.yml` gated on CI, `migrate` mode, IIS script, CodeQL, gitleaks | 13.3–13.5, 16, Phase 7 |
| 7. Packaging → **v1.0** | Switches (`notifications`, `includeSample`, `caching`), matrix generation CI, `modulith-module` item template, pack + publish, README / runbooks | 15, Phase 8 |

**Post-v1 backlog:** OTP / magic link, passkeys, social login, API keys, impersonation, SMS / push / webhook channels, digest, optional read-only DAB sidecar over reporting views (Section 9), file storage, exports, feature flags, multi-tenancy + tenant SSO, OpenIddict, BFF, RabbitMQ, PostgreSQL, Aspire AppHost, k6 / ZAP, learning-track repo.

---

## 7. ADRs

ADRs are numbered sequentially when they are written, not reserved up front. Plan 1 writes:

| # | Title | Replaces blueprint ADR |
|---|---|---|
| 0001 | Modular monolith, two projects per module, layering enforced by architecture tests | 0001 |
| 0002 | Result pattern for expected failures | 0002 |
| 0003 | SQL Server only for v1 | — |
| 0004 | SQL Server-ordered sequential GUIDs instead of UUID v7 | 0005 |
| 0005 | Injected handlers with Scrutor decorators instead of MediatR | 0003 |
| 0006 | EF Core for writes, Dapper for reads | 0004 |
| 0007 | Per-module outbox with per-handler consumer tracking | 0008 |
| 0008 | Literal `/api/v1` routes until a breaking change requires versioning | — |
| 0009 | Internationalization: stable error codes, localized details, `.resx` per module | — |
| 0010 | Cursor (keyset) pagination; Data API builder not used as the API layer | — |

Later plans add the remaining blueprint ADRs (JWT/BFF, server-side permissions, HybridCache, Hangfire, password hashing, tenancy) when they introduce the decision.

---

## 8. Internationalization (i18n)

Blueprint 7.14 gives two lines to localization ("`Accept-Language` support; error messages resolved from resource files by error `code`"). That is the right core idea; the decisions below make it implementable and close the gaps that usually bite.

### 8.1 Decisions

| # | Decision | Why |
|---|---|---|
| I1 | **`code` is the contract, `detail` is a convenience.** Every ProblemDetails keeps its stable `code` (`leave.not_found`) and adds `params` (e.g. `{ "id": "…" }`) so React/Flutter clients can render their own translations. The server also localizes `detail` from `Accept-Language` for clients that just display it. Enum values (`"Pending"`) and codes are **never** localized. | Clients own most UI text; a server-only approach forces every wording change through a backend deploy. |
| I2 | **Supported UI cultures: `en` (neutral/default), `ms`, `zh-Hans`.** `zh-CN`/`zh-SG` fall back to `zh-Hans` through .NET's parent-culture chain; anything unsupported falls back to `en`. | Matches the Malaysian market named in the blueprint; `zh-Hans` rather than bare `zh` keeps room for `zh-Hant` later without remapping. |
| I3 | **Only the *UI* culture varies per request; the formatting culture is fixed to `en`.** (`SupportedCultures = [en]`, `SupportedUICultures = [en, ms, zh-Hans]`.) | Messages are localized, but any `ToString()`/`string.Format` in code, logs or SQL parameters behaves identically for every caller. Prevents "works in English, breaks in Malay" bugs (decimal separators, date formats). |
| I4 | **`.resx` resources, one set per assembly that defines error codes** (`Web.Common`, `Infrastructure.Common`, each module), behind an `IErrorMessageLocalizer` that searches all registered sources. | Native to .NET (`IStringLocalizer`, satellite assemblies, culture fallback, IDE editors) and keeps each module's text inside the module, which preserves D1. |
| I5 | **Localization is applied once, at the HTTP boundary** (`ProblemDetailsOptions.CustomizeProblemDetails`), not in the domain. Domain errors carry an English default message plus named `Parameters`; templates use named placeholders (`{id}`) so translators can reorder words. | Domain stays culture-agnostic and testable; one place to change. |
| I6 | **`UseRequestLocalization` runs before `UseExceptionHandler`.** | Culture is async-local: if it is set *inside* the exception-handler middleware, the handler (outside) still sees the default culture and 500/400 responses come back in English. |
| I7 | **Background work never inherits the server's OS culture.** At startup, `DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture` = `en`. Work done for a specific user (notifications) passes that user's `Locale` explicitly. | A server installed with `ms-MY` regional settings would otherwise silently change outbox handler and job output. |
| I8 | **Culture configuration is validated at boot** (`CultureInfo.GetCultureInfo(name, predefinedOnly: true)` for every supported culture). | Containers running in invariant-globalization mode or without ICU fail fast with a clear message instead of serving English everywhere. |
| I9 | **Translation completeness is enforced by architecture tests:** every key in a neutral `.resx` exists in each translation with the same `{placeholders}`, and every `Error` defined in a module's `*Errors` class has a neutral entry. | Missing translations are found in CI, not by users. |
| I10 | Validation messages use FluentValidation's built-in localization (driven by `CurrentUICulture`), extended through a custom `LanguageManager` for any supported culture it lacks. | No per-rule boilerplate; consistent with the rest of the ecosystem. |

### 8.2 Impact on later plans

| Plan | i18n work |
|---|---|
| 2. Auth | `Users.Locale` set at registration (from `Accept-Language` if not supplied); a `locale` claim provider placed **before** `Accept-Language` so a signed-in user's saved preference wins; auth error codes added to `AuthErrorMessages.resx` (en/ms/zh-Hans). |
| 3. Notifications | Templates per type × channel × language (already in blueprint 10.4); render with the **recipient's** `Locale` with fallback `zh-Hans → en`; never use the request culture of whoever triggered the event. Store `TimeZone` as IANA IDs. |
| 5. Platform | Output/HybridCache keys for localized responses must vary by UI culture (`Vary: Accept-Language`). |
| 6. Delivery | Docker image must ship ICU (`InvariantGlobalization=false`; use the `-extra` variant if adopting chiseled images), or I8 stops the app at boot. |

### 8.3 Open item

Malay and Chinese strings written during implementation are drafts. They need review by a native speaker before v1.0; the README lists this as a release task.

---

## 9. Pagination: cursor pagination vs Data API builder (DAB)

### 9.1 They are not the same kind of thing

- **Cursor (keyset) pagination** is a *technique*: "give me the next 20 rows after this position" (`WHERE (CreatedAt, Id) < (@lastCreatedAt, @lastId) ORDER BY CreatedAt DESC, Id DESC`).
- **Data API builder** is a *product*: a separate Microsoft service (v2.0 in 2026) that reads a `dab-config.json` and exposes database tables, views and stored procedures as REST/GraphQL (and MCP) endpoints with no code. DAB itself paginates with cursors (`$first`, `$after`, `nextLink`).

So the real choice is **"generate the API with DAB" vs "keep our own endpoints and add cursor pagination to them."**

### 9.2 Comparison against this template's requirements

| Requirement (from blueprint / this review) | DAB as the API layer | Own endpoints + cursor pagination |
|---|---|---|
| Domain rules (`LeaveRequest.Approve` can't approve twice) | ❌ Writes go straight to tables; aggregates are bypassed | ✅ |
| Domain events + per-module outbox (2.1) | ❌ No `SaveChanges`, so no events and no outbox rows | ✅ |
| Audit fields, soft delete (EF interceptors) | ❌ Interceptors never run; needs DB triggers instead | ✅ |
| Module boundaries (rule 1: no cross-module table access) | ❌ Any configured table in any schema is reachable | ✅ |
| Error contract (ProblemDetails + `code` + `traceId`) and i18n (Section 8) | ❌ DAB's own error format | ✅ |
| Idempotency keys, rate-limit policies, permission model (`module.resource.action`) | ⚠️ Partly: DAB has role-based permissions and database policies, but not ours | ✅ |
| One deployable unit (Docker + IIS) | ❌ Second service to deploy, secure and monitor | ✅ |
| Stable pagination under concurrent inserts | ✅ keyset | ✅ keyset |
| Speed to expose read-only data (reports, admin lookups) | ✅ minutes, config only | ⚠️ a query handler per list |

**Decision:** **cursor pagination in our own endpoints** is the template standard (ADR 0010). DAB contradicts the template's core value: behavior lives in the domain, and every write raises events. Its strength is fast, read-only data access.

**Where DAB could still fit (post-v1 backlog, optional):** a read-only sidecar over dedicated **SQL views** for reporting, back-office lookups or AI agents (DAB's SQL MCP Server). It would use a separate read-only database login, never write, and stay out of the template's default output.

### 9.3 Cursor pagination design (replaces blueprint Section 6 "Pagination")

| # | Decision | Why |
|---|---|---|
| P1 | **Request:** `?pageSize=20&cursor=<opaque>&sort=-createdAt&includeTotalCount=false`, plus endpoint filters. `pageSize` defaults to 20, max 100. | Keeps the blueprint's names (`pageSize`, `sort`); one opaque `cursor` parameter for both directions. |
| P2 | **Response:** `{ "items": [], "pageSize": 20, "nextCursor": "…", "previousCursor": null, "totalCount": 123 }`. A cursor is `null` when there is no page in that direction; `totalCount` appears only when requested. | Mobile infinite scroll needs only `nextCursor`; admin grids get previous/next and an optional total. |
| P3 | **The cursor encodes direction, sort signature, a filter hash and the last row's key values.** It is base64url JSON with a version field, and clients must treat it as opaque. | One parameter handles both directions. A changed sort or filter is detected (`400 pagination.cursor_mismatch`) instead of returning silently wrong pages. |
| P4 | **Every sort ends with the unique `Id` as a tie-breaker; sortable fields come from a per-endpoint allow-list** that maps API names to SQL columns defined in code; sortable columns are non-nullable. | Keyset needs a total order. The allow-list blocks SQL injection through dynamic `ORDER BY` and sorting on unindexed columns. |
| P5 | **No page numbers, no jumping to page N.** Total count is opt-in because it costs an extra `COUNT_BIG(*)`. | Offset pagination degrades linearly with depth and duplicates or skips rows when data changes between requests. Endpoints that truly need page jumps must justify it in an ADR. |
| P6 | Cursors aren't signed. Tampering only moves the client's position, because authorization and tenant filters are always applied server-side and cursor values are SQL parameters. A malformed token returns `400 pagination.invalid_cursor`. | Signing would need a shared Data Protection key ring across instances; it isn't worth it for this threat. |
| P7 | Each allowed sort has a supporting index `(filter columns…, SortColumn, Id)`; migration review checks this. | Keyset is only fast when the index matches the `ORDER BY`. |
| P8 | Fetch `pageSize + 1` rows to know whether another page exists; backward pages run with reversed comparisons and order, then get re-reversed. | No extra query for `hasNext`. |

---

## 10. Project name: Modulith

| Item | Value | Note |
|---|---|---|
| Brand / display name | **Modulith Backend** | "Modulith" is the industry term for a modular monolith, so the name states the architecture. No personal names in any identifier. |
| `dotnet new` short name | `modulith-backend` (solution), `modulith-module` (item template, Plan 7) | **Not** plain `modulith`: the NuGet packages `Modulith` and `Ardalis.Modulith` already ship templates under that name, and two installed templates with the same short name make `dotnet new modulith` ambiguous. |
| NuGet package ID | `Modulith.Backend.Templates` | Free on nuget.org as of 2026-10-07. If publishing to nuget.org, reserve the `Modulith.Backend.*` prefix. |
| Repository | <https://github.com/jackyjiale-oss/modulith-backend> (public, default branch `main`) | kebab-case name per `docs/coding-conventions.md` 6.2. Settings, branch-protection phases and workflow: `docs/repository-management.md`. |
| Generated code placeholder | `TemplateName` (unchanged) | `dotnet new` replaces it with the consumer's project name, e.g. `Acme.Hr`. |

---

## 11. Documentation, changelog, CLAUDE.md and licence

The blueprint mentions `docs/modules/`, a `CHANGELOG.md` and a README, but not how they stay accurate or what a **generated** project should receive. A template repo has two audiences: people maintaining Modulith, and teams who run `dotnet new modulith-backend`. Several files therefore exist twice: once for the template repo and once as the version a generated project receives.

### 11.1 What ships where

| File | In the template repo | In a generated project | How |
|---|---|---|---|
| `LICENSE` | MIT, "Modulith contributors" | **Not copied.** A client's project is the client's code; it must not inherit our licence by accident. | excluded in `template.json` |
| `README.md` | What Modulith is, install, `dotnet new` options, contributing | What *this service* is, quick start, modules, docs index | `build/template-content/README.md` → `README.md` |
| `CHANGELOG.md` | Modulith's own release history | Fresh file starting at `Unreleased` | `build/template-content/CHANGELOG.md` → `CHANGELOG.md` |
| `.release-please-manifest.json` | current template version (starts at `0.0.0`) | `{ ".": "0.0.0" }` | `build/template-content/` override |
| `CLAUDE.md` | `@build/template-content/CLAUDE.md` import, plus template-maintainer rules (placeholders, smoke test, exclusions) | Project guide for agents | `build/template-content/CLAUDE.md` → `CLAUDE.md` |
| `CONTRIBUTING.md`, `docs/coding-conventions.md`, `docs/adr/*` | yes | yes (with `TemplateName` replaced) | copied as-is |
| `docs/architecture`, `docs/services`, `docs/building-blocks`, `docs/modules` | yes | yes | copied as-is |
| `BACKEND_TEMPLATE_BLUEPRINT.md`, `docs/blueprint-review.md`, `docs/superpowers/**` | yes | no | excluded |
| `THIRD-PARTY-NOTICES.md` | generated at release | generated at their release | release workflow |
| `SECURITY.md`, `docs/repository-management.md`, `build/scripts/configure-github-repo.sh` | yes (point at the Modulith GitHub repo) | no | excluded |

### 11.2 Changelog

| # | Decision | Why |
|---|---|---|
| C1 | **`CHANGELOG.md` is generated by release-please from Conventional Commits** on `main`; nobody edits it by hand except to polish wording in the release PR. | The commit rules already exist (CONTRIBUTING.md); a hand-written changelog drifts. |
| C2 | release-please runs on every push to `main` and maintains a release PR titled `chore(release): release <version>`. Merging that PR tags `v<version>`, updates `CHANGELOG.md` and bumps `<Version>` in `Directory.Build.props` (xml `extra-files` updater). | One version source for assemblies, the template package and Docker tags (Plan 6). |
| C3 | Sections: `feat` → **Added**, `fix` → **Fixed**, `perf` → **Changed**, `revert` → **Reverted**, `deps` scope → **Dependencies**; breaking changes get their own section automatically. `refactor`, `test`, `ci`, `build`, `chore`, `style` and `docs` are hidden. | Readers see behavior changes, not churn. |
| C4 | The scope (`sample`, `auth`, …) appears in every entry, so per-module history can be read from the one changelog. There are no per-module changelog files. | One source of truth. Module docs link to the changelog filtered by scope. |
| C5 | Database schema history is the EF migrations per module; each module doc lists its migrations. | A migration *is* the schema changelog. |

### 11.3 Service, building-block and module documents

```
docs/
├─ README.md                    index of all docs
├─ architecture/overview.md     module map (Mermaid), request lifecycle, data ownership, links to ADRs
├─ services/api.md              the host: pipeline order, every configuration section with defaults, health, environments, migrations
├─ building-blocks/             shared-kernel.md, application-common.md, infrastructure-common.md, web-common.md
├─ modules/_template.md         the required outline for a module doc
├─ modules/sample.md            one per module, named after the module (lowercase)
├─ adr/                         decisions
└─ runbooks/                    operational procedures (Plan 6)
```

A module document must contain these sections, in this order: **Purpose and boundaries, Endpoints, Domain model, Error codes, Events, Configuration, Data, Background processing, Observability, Testing.**

**Docs are part of the definition of done:** a change that adds or alters an endpoint, error code, configuration key, event or table updates the matching document in the same PR. Tests enforce the parts that can be checked:
- every module and building block has a document
- module documents have the required sections
- every error code a module defines appears in its document
- every mapped `/api/v1/{module}/…` endpoint appears in its module's document (`METHOD /route`)

### 11.4 Licence compliance

- The template is **MIT** (`LICENSE`); the generated project chooses its own.
- CI runs a licence check (the `nuget-license` .NET tool or equivalent) against an allow-list in `build/licenses/allowed-licenses.json`: `MIT`, `Apache-2.0`, `BSD-2-Clause`, `BSD-3-Clause`. Anything else fails the build until an ADR adds it (e.g. Hangfire's LGPL-3.0 in Plan 5). This automates the blueprint's "check licences of every third-party package".
- `THIRD-PARTY-NOTICES.md` is generated by the release workflow and included in the template package. It isn't committed on every PR, so Dependabot bumps don't fail CI.

### 11.5 CLAUDE.md

Kept short, because Claude Code loads it into every session. It **links** to long documents instead of `@importing` them. It contains: a one-paragraph overview; the commands (build, test, run, add a migration, format, smoke test); where things go; the definition of done (tests, docs, translations, ADR); and hard rules (no secrets, no `DateTime.UtcNow`, no cross-module table access, no new packages without the licence check, never skip hooks).
