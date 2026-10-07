# Coding Conventions

Rules for naming types, members, variables, files and the names that leave the process (routes, JSON, database, logs). Commit, branch and pull-request rules live in [`CONTRIBUTING.md`](../CONTRIBUTING.md).

Most rules are **enforced automatically**: by `.editorconfig` (built with `EnforceCodeStyleInBuild`, so violations fail Release builds and CI), by architecture tests, or by the commit hook. The last column of each table says which. Rules marked *review* are checked in code review.

To change a rule: open a PR that updates this file, plus `.editorconfig` or the architecture tests where they enforce it. Add an ADR if the change is significant.

---

## 1. General principles

| # | Rule | Enforced by |
|---|---|---|
| G1 | Identifiers are **English, US spelling**, to match the .NET libraries you call (`Canceled`, `Initialize`, `Serialize`, `Normalize`, `Behavior`, `Color`, `License`). Comments and docs may use any spelling. | review |
| G2 | Use the domain's language (`LeaveRequest`, `Approve`, `Employee`), not technical stand-ins (`Record`, `Process`, `Item`). | review |
| G3 | **No abbreviations** except this allow-list: `Id`, `Db`, `Api`, `Http`, `Https`, `Url`, `Uri`, `Json`, `Xml`, `Sql`, `Utc`, `Jwt`, `Otp`, `Totp`, `Mfa`, `Sms`, `Html`, `Csv`, `Pdf`, `IO`, `UI`. Write `cancellationToken`, `configuration`, `request`, `message`, never `ct`, `cfg`, `req`, `msg`. | review |
| G4 | **Acronym casing:** two letters all caps (`IO`, `UI`); three or more PascalCase (`HttpClient`, `JwtOptions`, `OtpCode`, `SqlConnection`). `Id` is a word, so write `Id`, never `ID`. | review |
| G5 | **Forbidden type-name suffixes:** `Manager`, `Helper`, `Helpers`, `Util`, `Utils`, `Utility`, `Impl`, `Dto`, `Settings`, `Info`. They say nothing about responsibility. Name the role instead (see Section 3). | architecture test |
| G6 | A name says **what** something is or does, not its type: `approvedRequests`, not `list2` or `requestList`. | review |

---

## 2. Casing quick reference

| Identifier | Style | Example | Enforced by |
|---|---|---|---|
| Namespace | PascalCase, **matches the folder path** | `TemplateName.Modules.Sample.Domain.LeaveRequests` | `.editorconfig` (IDE0130) |
| Class, record, struct, enum, delegate | PascalCase noun | `LeaveRequest`, `LeaveRequestStatus` | `.editorconfig` (IDE1006) |
| Interface | `I` + PascalCase | `ILeaveRequestRepository`, `IAuditable` | `.editorconfig` |
| Generic type parameter | `T`, or `T` + PascalCase | `T`, `TCommand`, `TResponse` | `.editorconfig` |
| Method, local function | PascalCase verb phrase | `Approve`, `GetByIdAsync` | `.editorconfig` |
| Async method (returns `Task`, `ValueTask`, `IAsyncEnumerable`) | PascalCase + **`Async`** | `SaveChangesAsync`, `HandleAsync` | `.editorconfig` + architecture test |
| Property, event | PascalCase | `CreatedAt`, `IsDeleted` | `.editorconfig` |
| Private instance field | `_camelCase` | `_domainEvents` | `.editorconfig` |
| `const` and `static readonly` field (any access) | PascalCase | `MaxReasonLength`, `None` | `.editorconfig` |
| Non-private instance field | not allowed; use a property | — | review |
| Parameter, local variable | camelCase | `leaveRequest`, `cancellationToken` | `.editorconfig` |
| Local `const` | PascalCase | `const int MaxRetries = 3;` | `.editorconfig` |
| Enum member | PascalCase | `Pending`, `Approved` | `.editorconfig` |
| Test method | Sentence with underscores, first word capitalized | `Approve_twice_fails_with_not_pending` | review |

---

## 3. Class names: by role

Every type plays one of these roles. Its name follows the role's pattern, and its location follows the module layout (Section 6).

| Role | Pattern | Example | Enforced by |
|---|---|---|---|
| Aggregate / entity | singular noun | `LeaveRequest` | review |
| Value object | singular noun, `record` | `DateRange`, `EmailAddress` | review |
| Domain event | `{Noun}{PastTenseVerb}DomainEvent` | `LeaveRequestApprovedDomainEvent` | architecture test |
| Integration event | `{Noun}{PastTenseVerb}IntegrationEvent` (in `*.Contracts`) | `UserRegisteredIntegrationEvent` | architecture test |
| Command | `{Verb}{Noun}Command` | `ApproveLeaveRequestCommand` | architecture test |
| Query | `Get{Noun}Query`, `List{Nouns}Query`, `Search{Nouns}Query` | `GetLeaveRequestByIdQuery` | architecture test |
| Handler (command/query/event) | `{MessageName}Handler` | `ApproveLeaveRequestCommandHandler`, `LeaveRequestSubmittedDomainEventHandler` | architecture test |
| Validator | `{MessageName}Validator` | `SubmitLeaveRequestCommandValidator` | architecture test |
| Read model / response | `{Noun}Response` | `LeaveRequestResponse` | review |
| HTTP request body | `{Verb}{Noun}Request` | `SubmitLeaveRequestRequest` | review |
| Errors catalogue | `{Aggregate}Errors` (static) | `LeaveRequestErrors` | architecture test (i18n) |
| Permission catalogue | `{Module}Permissions` (static) | `LeavePermissions` | review |
| Repository | `I{Aggregate}Repository` / `{Aggregate}Repository` | `ILeaveRequestRepository` | architecture test |
| Unit of work | `IUnitOfWork` (one per module, module namespace) | — | review |
| DbContext | `{Module}DbContext` | `SampleDbContext` | architecture test |
| EF entity configuration | `{Entity}Configuration` | `LeaveRequestConfiguration` | architecture test |
| Options (bound config) | `{Section}Options`; if that collides with a framework type, `Api{Section}Options` | `OutboxOptions`, `ApiCorsOptions` | architecture test (no `*Settings`) |
| Middleware | `{Purpose}Middleware` | `SecurityHeadersMiddleware` | review |
| Exception handler | `{Purpose}ExceptionHandler` | `GlobalExceptionHandler` | review |
| EF interceptor | `{Purpose}Interceptor` | `SoftDeleteInterceptor` | review |
| Decorator | `{Concern}Decorator` | `ValidationDecorator` | review |
| Hosted service | `{Purpose}BackgroundService` | `OutboxBackgroundService` | review |
| Health check | `{Dependency}HealthCheck` | `SmtpHealthCheck` | review |
| Extension methods class | `{Area}ServiceCollectionExtensions`, `{Area}EndpointRouteBuilderExtensions`, or `{ExtendedType}Extensions` | `ResultExtensions` | review |
| Module entry point | `{Module}Module` (the only public type in a module) | `SampleModule` | architecture test |
| Endpoint group | `{Aggregate}Endpoints` | `LeaveRequestEndpoints` | review |
| Exception | `{Reason}Exception` | `TenantNotResolvedException` | analyzer (CA1710) |
| Localization resource marker | `{Area}ErrorMessages` | `SampleErrorMessages` | architecture test (i18n) |
| Enum | singular noun; `[Flags]` enums plural | `LeaveRequestStatus`, `DeviceTransports` | review |
| Test class | `{Subject}Tests` | `LeaveRequestTests` | review |
| Test double | `Fake{X}`, `Stub{X}`, `Recording{X}`, `Test{X}` | `TestCurrentUser`, `RecordingTestEventHandler` | review |
| Test base class | `{Kind}TestBase` | `IntegrationTestBase` | review |

**Type rules**

| # | Rule | Enforced by |
|---|---|---|
| T1 | Classes are `sealed` unless designed for inheritance. Commands, queries, events, requests, responses and value objects are `record`s. | architecture test (handlers), review |
| T2 | Module types are `internal`. Only `{Module}Module`, EF migrations and model snapshots are public. | architecture test |
| T3 | Abstract base types have **no `Base` suffix** (`Entity`, `AggregateRoot`). `Base` is allowed only on test fixtures. | review |
| T4 | Interfaces name a service with a noun (`ILeaveRequestRepository`) or a capability with an adjective (`IAuditable`). Don't add an interface for a single implementation unless it is a test or infrastructure seam. | review |
| T5 | Persisted enums have **explicit numeric values that never change or get reused**. `None = 0` only on `[Flags]`. No `Enum` suffix. | review |
| T6 | One aggregate per folder; the folder is named after the aggregate (plural): `Domain/LeaveRequests/`. | review |

---

## 4. Members

| # | Rule | Example | Enforced by |
|---|---|---|---|
| M1 | Methods are verbs or verb phrases. Aggregates expose **domain verbs**, not setters. | `Approve(approverId)`, not `SetStatus(...)` | review |
| M2 | Every method returning `Task`, `ValueTask` or `IAsyncEnumerable` ends in `Async`, including handlers and endpoint methods. Exceptions: test methods, and overrides whose name the framework fixes. | `HandleAsync`, `GetByIdAsync` | `.editorconfig` + architecture test |
| M3 | `CancellationToken` is the **last** parameter, named `cancellationToken`. | `GetByIdAsync(Guid id, CancellationToken cancellationToken)` | architecture test (CA2016 forwards it) |
| M4 | Repository lookups are `Get{By…}Async` and return `T?` when nothing is found. No exceptions for "not found". | `Task<LeaveRequest?> GetByIdAsync(...)` | review |
| M5 | `Try{Verb}` methods return `bool` and use an `out` parameter. | `TryParse(string text, out DateRange range)` | review |
| M6 | Boolean properties, fields and locals start with `Is`, `Has`, `Can` or `Should`. | `IsDeleted`, `HasExpired`, `canApprove` | review |
| M7 | Collections are plural and exposed read-only. | `IReadOnlyList<IDomainEvent> DomainEvents` | review |
| M8 | **Time:** UTC `DateTime` is `{Event}At`; `DateOnly` is `{Name}Date`; `TimeSpan` is named for its meaning with no unit (`Lifetime`, `Timeout`, `Interval`, `Duration`, `TimeToLive`). If a raw number is unavoidable, the name carries the unit (`MaxRequestBodySizeBytes`). | `CreatedAt`, `StartDate`, `PollingInterval` | review |
| M9 | **Identifiers:** an entity's own key is `Id`; a reference to another entity is `{Entity}Id`. | `EmployeeId`, `ApproverId` | review |
| M10 | Counts are `{Noun}Count`; limits are `Max{Noun}` / `Min{Noun}`. | `AttemptCount`, `MaxAttempts` | review |
| M11 | Factories are `Create` or a domain verb on the type. | `LeaveRequest.Submit(...)`, `SequentialGuid.Create(...)` | review |

---

## 5. Variables and parameters

| # | Rule | Good | Bad | Enforced by |
|---|---|---|---|---|
| V1 | camelCase, full words | `leaveRequest`, `cancellationToken` | `lr`, `req`, `ct` | `.editorconfig` (casing), review (words) |
| V2 | Name by **role**, not type | `approver`, `requester` | `user1`, `user2`, `userObj` | review |
| V3 | No Hungarian notation or type suffixes | `employees` | `strName`, `employeeList`, `arrIds` | review |
| V4 | Booleans read as a question | `isExpired`, `hasPermission` | `expired`, `flag`, `check` | review |
| V5 | Collections are plural | `pendingRequests` | `pendingRequest`, `data` | review |
| V6 | One-letter names only for loop indices (`i`, `j`) and one-line lambdas with obvious types (`.Select(x => x.Id)`). Multi-line lambdas name the parameter. | `requests.Where(request => request.IsOverdue(now))` | `foreach (var r in requests) { … 20 lines … }` | review |
| V7 | `var` when the type is obvious from the right-hand side; explicit type otherwise. | `var request = new SubmitLeaveRequestCommand(...)` | `var x = service.Get();` | `.editorconfig` |
| V8 | Use `_` to discard values you don't need. | `_ = await task;` | `var unused = ...` | review |
| V9 | Don't reuse a variable for a different meaning. | — | — | review |

---

## 6. Files and folders

### 6.1 C# files

| # | Rule | Example | Enforced by |
|---|---|---|---|
| F1 | **One top-level type per file; the file is named after the type.** | `LeaveRequest.cs` | `.editorconfig` (IDE0161 file-scoped namespaces) + review |
| F2 | Generic type: `{Name}.cs`; when a non-generic type with the same name exists, the generic one is `{Name}OfT.cs`. | `Result.cs` + `ResultOfT.cs` | review |
| F3 | Nested types stay in the parent's file. Partial classes are `{Type}.{Aspect}.cs`. | `ValidationDecorator.cs`, `SampleDbContext.Outbox.cs` | review |
| F4 | Folder = namespace segment, PascalCase. Category folders are plural (`Events`, `Migrations`, `Resources`); use-case folders are named after the use case (`Submit`, `Approve`, `GetById`). | `Application/LeaveRequests/Submit/` | `.editorconfig` (IDE0130) |
| F5 | **Module layout is fixed:** `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`, `Resources/`, `{Module}Module.cs`. | — | architecture test (layering) |
| F6 | **Project folder = project name = assembly name = root namespace.** `TemplateName.{Area}` for building blocks, `TemplateName.Modules.{Module}` and `TemplateName.Modules.{Module}.Contracts` for modules. | `src/Modules/Sample/TemplateName.Modules.Sample/` | review |
| F7 | Tests live in `tests/{TestProject}/{Module or Area}/…/{Subject}Tests.cs`. | `tests/TemplateName.UnitTests/Sample/LeaveRequestTests.cs` | review |

### 6.2 Other files

| Kind | Convention | Example |
|---|---|---|
| Conventional root files | UPPERCASE | `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `CLAUDE.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` |
| Documentation | kebab-case | `docs/coding-conventions.md`, `docs/architecture/overview.md` |
| Module / building-block / service docs | lowercase name of the unit; templates start with `_` | `docs/modules/sample.md`, `docs/modules/_template.md`, `docs/building-blocks/web-common.md`, `docs/services/api.md` |
| Generated-project overrides | same file name under `build/template-content/` | `build/template-content/README.md` |
| ADR | `NNNN-kebab-title.md` | `docs/adr/0007-per-module-outbox.md` |
| Plans | `YYYY-MM-DD-kebab-title.md` | `2026-10-06-foundation-and-core-baseline.md` |
| Runbooks | kebab-case | `docs/runbooks/rotate-jwt-signing-keys.md` |
| Scripts | kebab-case | `build/scripts/check-commit-msg.sh`, `deploy-iis.ps1` |
| GitHub workflows | kebab-case `.yml`; reusable workflows prefixed `_` | `ci.yml`, `_reusable-dotnet-build.yml` |
| App settings | `appsettings.json`, `appsettings.{Environment}.json` | `appsettings.Development.json` |
| Resources | `{Marker}.resx`, `{Marker}.{culture}.resx` | `SampleErrorMessages.zh-Hans.resx` |
| EF migrations | PascalCase `{Verb}{What}`; the first one in a module is `Initial{Module}` | `InitialSample`, `AddApproverIdToLeaveRequests` |
| Load tests | kebab-case `.js` | `tests/load/submit-leave-request.js` |
| Docker | `Dockerfile`, `docker-compose.yml`, `.env.example` | — |

---

## 7. Names that leave the process

These are part of public contracts or operations. Changing one is a breaking change.

| Item | Convention | Example |
|---|---|---|
| Route segments | kebab-case, plural nouns; actions are verb sub-resources | `/api/v1/sample/leave-requests/{id}/approve` |
| Route and query parameters | camelCase | `{leaveRequestId}`, `?pageSize=20&sort=-createdAt` |
| Pagination (list endpoints) | Cursor only: `pageSize` (default 20, max 100), `cursor`, `sort` (comma-separated, `-` = descending, allow-listed fields), `includeTotalCount`. Response `{ items, pageSize, nextCursor, previousCursor, totalCount? }` | `GET /api/v1/sample/leave-requests?pageSize=20&sort=-createdAt` |
| JSON properties | camelCase | `"startDate"` |
| Enum values in JSON | PascalCase strings, never localized | `"Pending"` |
| HTTP headers | `Title-Case`; new custom headers have no `X-` prefix (RFC 6648). `X-Trace-Id` is the one established exception. | `Idempotency-Key`, `Idempotency-Replayed` |
| Error codes | `{module}.{snake_case}`; generic HTTP errors are `http.{status}` | `leave.not_pending`, `http.404` |
| Permission codes | `{module}.{resource}.{action}` | `leave.request.approve` |
| Notification type codes | `{module}.{snake_case}` | `auth.password_changed` |
| Config sections and keys | PascalCase; environment variables `Section__Key` | `Outbox:PollingInterval`, `Outbox__PollingInterval` |
| Feature flags | PascalCase | `NewLeaveFlow` |
| DB schema | lower-case module name | `sample`, `auth` |
| DB tables | PascalCase plural | `LeaveRequests` |
| DB columns | PascalCase; PK `Id`; FK `{Entity}Id`; booleans `Is…`/`Has…`; timestamps `{Event}At`; hashes `{Name}Hash`; encrypted `{Name}Encrypted` | `ApproverId`, `IsDeleted`, `TokenHash` |
| DB indexes | EF default `IX_{Table}_{Columns}` | `IX_LeaveRequests_EmployeeId_CreatedAt_Id` |
| Log message templates | PascalCase placeholders; message templates only, **never string interpolation** | `"Leave request {LeaveRequestId} submitted"` (CA2254) |
| `ActivitySource` / `Meter` names | `TemplateName.{Module}` | `TemplateName.Auth` |
| Metric instruments | lowercase, dot-separated | `templatename.auth.logins.failed` |
| Cache keys | lowercase `{app}:{tenant}:{module}:{entity}:{id}:v{n}` | `templatename:t1:sample:leave-request:…:v1` |
| Localization keys | identical to the error code | `leave.not_found` |

---

## 8. Enforcement summary

| Mechanism | What it checks | Where configured |
|---|---|---|
| `.editorconfig` naming rules (IDE1006) + code style (IDE0130, IDE0161, `var`) | Casing, `I`/`T` prefixes, `_camelCase` fields, `Async` suffix on `async` methods, namespace = folder, file-scoped namespaces | `.editorconfig` (root) and `tests/.editorconfig` (test-method overrides) |
| Build (`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` in Release) | Turns the above into build errors in CI | `Directory.Build.props` |
| Architecture tests | Role suffixes (Command, Query, Handler, Validator, DomainEvent, Repository, DbContext, Configuration), forbidden suffixes (G5), `Async` on Task-returning methods, `CancellationToken cancellationToken` as the last parameter, sealed/internal rules, module layering, translation completeness | `tests/TemplateName.ArchitectureTests/` |
| Commit-message hook + CI PR-title check | Conventional Commits format, allowed types and scopes, length | `.githooks/commit-msg`, `build/scripts/check-commit-msg.sh`, `.github/workflows/ci.yml` |
| Code review | Everything marked *review*: meaning, domain language, abbreviations | `.github/pull_request_template.md` |
