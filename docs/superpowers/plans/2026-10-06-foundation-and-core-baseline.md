# Modulith: Foundation & Core Baseline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A buildable, tested, `dotnet new`-installable ASP.NET Core 10 modular-monolith skeleton with every cross-cutting concern from the blueprint's core baseline, proven end-to-end by a small Sample (leave request) module.

**Architecture:** Building-block libraries (SharedKernel, Application.Common, Infrastructure.Common, Web.Common) plus one host (`TemplateName.Api`). Each module is **two projects**: `Modules.X` (folders `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`, `internal` by default) and `Modules.X.Contracts` (created only when a module first exposes something; not needed in this plan). Handlers are injected directly into Minimal API endpoints and wrapped by Scrutor decorators. Each module has its own `DbContext`, schema and outbox.

**Tech Stack:** .NET 10 / C# latest, ASP.NET Core Minimal APIs, EF Core 10 (SQL Server), Dapper, FluentValidation, Scrutor, Serilog, OpenTelemetry, Microsoft.AspNetCore.OpenApi + Scalar, Microsoft.Extensions.Localization (`.resx`), xUnit v3, Shouldly, NSubstitute, NetArchTest.Rules, Testcontainers.MsSql, Respawn, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `BACKEND_TEMPLATE_BLUEPRINT.md` as amended by `docs/blueprint-review.md`. The review wins where they disagree. This plan covers row 1 of the review's roadmap (Section 6).

## Global Constraints

- **Repository:** <https://github.com/jackyjiale-oss/modulith-backend> (public). Its settings and branch-protection phases are in `docs/repository-management.md`. The repo is already initialised locally with remote `origin` and a repo-local noreply commit identity; the bootstrap commit with the docs is on `main`.
- **Workflow per task:** branch `feature/task-NN-<kebab-title>` from an up-to-date `main` → implement → push → open a PR whose title is the task's commit header (`gh pr create`). Squash-merge when CI is green (once CI exists, from Task 14). Never push directly to `main`. If `gh` isn't available, push the branch and give the user the compare URL `https://github.com/jackyjiale-oss/modulith-backend/compare/<branch>?expand=1`.
- **Prerequisites:** .NET SDK `10.0.401` (installed). **Docker Desktop must be installed and running before Task 8.** It isn't installed on the dev machine yet, and Testcontainers needs it.
- `global.json`: `{ "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }`.
- Every project targets `net10.0` with `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion` latest and `TreatWarningsAsErrors` in Release, all set in `Directory.Build.props` and never in a csproj.
- Central Package Management: every version lives in `Directory.Packages.props`. Use the latest **stable** version at implementation time. Prerelease is allowed only for OpenTelemetry EF Core/SqlClient instrumentation, and must be noted in the ADR.
- **Forbidden packages (licensing):** MediatR, AutoMapper, FluentAssertions, MassTransit ≥ 9, Moq.
- Restore uses lock files (`RestorePackagesWithLockFile`), and NuGet Audit fails the build on high/critical advisories (`NU1903`, `NU1904` as errors).
- Code style: file-scoped namespaces; classes `sealed` unless designed for inheritance; `record` for commands, queries, DTOs and events; module types `internal` unless the host must see them.
- Every async method takes a `CancellationToken`. Time comes only from an injected `TimeProvider`: `DateTime.Now`, `DateTime.UtcNow` and `DateTimeOffset.UtcNow` are banned outside `TimeProvider.System`.
- Connection strings and options are read lazily through `IOptions<T>` / the service provider, never eagerly from `builder.Configuration` in `Program.cs`, so `WebApplicationFactory` overrides apply.
- Options use `.ValidateDataAnnotations().ValidateOnStart()`. No secrets in `appsettings*.json`: `ConnectionStrings:Database` is empty there and supplied by user-secrets or environment.
- **Naming, files, variables and classes follow [`docs/coding-conventions.md`](../../coding-conventions.md); branches, commits and PRs follow [`CONTRIBUTING.md`](../../../CONTRIBUTING.md).** Both already exist and ship with the template. Highlights: US spelling in identifiers; `Async` suffix on every Task-returning method (handlers are `HandleAsync`); `CancellationToken cancellationToken` as the last parameter; config classes are `{Section}Options` (never `*Settings`); no `Manager`, `Helper`, `Util`, `Dto` or `Impl` suffixes; one type per file named after it.
- Error codes are `module.snake_case` (e.g. `leave.not_pending`). Every commit header is a Conventional Commit of at most 72 characters.
- **Docs are part of every task's definition of done** (review Section 11, CONTRIBUTING Section 4). A task that adds or changes an endpoint, error code, event, table, configuration key or pipeline step updates the matching file under `docs/` in the same commit. Once Task 17 creates the doc set, later tasks keep it current. `CHANGELOG.md` is never edited by hand; release-please generates it from commit messages.
- Licence: the template repo is MIT (`LICENSE`, "Modulith contributors", already written). Generated projects don't get it. Dependencies must be MIT, Apache-2.0, BSD-2-Clause or BSD-3-Clause unless an ADR allows otherwise.
- HTTP errors are RFC 9457 ProblemDetails, and **every** one carries `traceId` (32-hex W3C trace id, equal to the `X-Trace-Id` response header) and `code`. Status map: Validation 400 (with `errors`), Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Failure 500.
- Routes: `/api/v1/{module}/{plural-kebab-resource}`, e.g. `/api/v1/sample/leave-requests/{id}/approve`. JSON enums are serialized as strings.
- **List endpoints use cursor (keyset) pagination only** (review Section 9.3, ADR 0010). Query parameters are `pageSize` (default 20, max 100), `cursor`, `sort` and `includeTotalCount`. The response is `{ items, pageSize, nextCursor, previousCursor, totalCount? }`. No page numbers or offset pagination. Data API builder isn't used.
- Database: one schema per module (lower-case module name), tables PascalCase plural, migrations history table `__EFMigrationsHistory` inside the module schema, `DateTime` stored as `datetime2(3)` UTC and read back with `DateTimeKind.Utc`, timestamp columns suffixed `At`, hash columns `varbinary(32)`.
- **i18n** (review Section 8):
  - Supported UI cultures are `en` (neutral resource / default), `ms` and `zh-Hans`. The formatting culture is always `en`.
  - `code`, `params` and enum values are never localized; only `detail` follows `Accept-Language`.
  - Every user-facing error message is a key in its assembly's `*ErrorMessages.resx`, plus `.ms.resx` and `.zh-Hans.resx`. Templates use named placeholders (`{id}`) fed from `Error.Parameters`.
  - `Error.Parameters` are returned to clients, so never put secrets or personal data in them.
- Tests: Shouldly assertions; async test calls pass `TestContext.Current.CancellationToken`; integration tests share one container and run **non-parallel** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`).
- Git: Conventional Commits; one commit per task minimum.

## Review Focus

1. **Malformed JSON body** (truncated JSON, `"employeeId": "not-a-guid"`) must return `400` ProblemDetails with `code = request.malformed` and `traceId`, not a 500 or an empty body. Owned by Task 11 (`Malformed_json_returns_400_problem_details`).
2. **Concurrent duplicate requests with the same `Idempotency-Key`** must execute the handler exactly once. Owned by Task 12 (`Concurrent_requests_with_same_key_execute_once`).
3. **Two app instances running the outbox dispatcher** must dispatch each message to each handler exactly once. Owned by Task 9 (`Concurrent_dispatchers_process_each_message_once`).
4. **Spoofed `X-Forwarded-For`** from an untrusted client must not change the rate-limit partition (a client could otherwise dodge limits). Owned by Task 7 (`Spoofed_forwarded_for_does_not_bypass_rate_limit`).
5. **`DateTime` read back from SQL Server** must have `Kind == Utc` and serialize with a trailing `Z`; otherwise clients shift times by the server offset. Owned by Task 8 (`DateTime_values_round_trip_as_utc`).
6. **Unsupported or malformed `Accept-Language`** (`fr-FR`, `!!!;q=abc`) must fall back to English, not 500. Errors raised inside the exception handler (500, malformed JSON) must still come back in the caller's language. Owned by Task 15 (`Malformed_accept_language_falls_back_to_english`, `Exception_handler_responses_are_localized`).
7. **Paging through rows that change or tie** must visit every row exactly once. Cases: rows inserted between page requests, many rows sharing the same sort value, and `DateTime` cursor values (millisecond precision, UTC). Owned by Task 16 (`Items_inserted_after_first_page_do_not_cause_duplicates`, `Ties_on_sort_value_are_broken_by_id`).

---

## File Structure

```
/                                   (repo root = template root)
├─ .template.config/template.json            Task 14
├─ .config/dotnet-tools.json                 Task 1  (dotnet-ef)
├─ .githooks/commit-msg                      Task 1  (calls build/scripts/check-commit-msg.sh)
├─ build/scripts/check-commit-msg.sh         Task 1  (commit/PR-title rule; also used by CI)
├─ CONTRIBUTING.md, docs/coding-conventions.md, LICENSE   already written (conventions spec; MIT licence)
├─ CLAUDE.md                                 Task 1  (template-maintainer guide; imports build/template-content/CLAUDE.md)
├─ CHANGELOG.md                              Task 1  (release-please output; never hand-edited)
├─ build/template-content/{README.md, CHANGELOG.md, CLAUDE.md, .release-please-manifest.json}   Tasks 1,17 (generated-project versions)
├─ release-please-config.json, .release-please-manifest.json   Task 17
├─ build/licenses/allowed-licenses.json      Task 17
├─ docs/README.md, docs/architecture/overview.md, docs/services/api.md, docs/building-blocks/*.md   Task 17
├─ docs/modules/_template.md, docs/modules/sample.md   Task 11
├─ tests/.editorconfig                       Task 1  (relaxes naming rules for test methods)
├─ .github/workflows/ci.yml, dependabot.yml, pull_request_template.md   Task 14
├─ build/scripts/template-smoke.sh           Task 14
├─ docs/adr/0001…0010-*.md                   Tasks 1,2,3,5,8,9,15,16
├─ src/
│  ├─ Directory.Build.props                  Task 1  (InternalsVisibleTo for test projects)
│  ├─ BuildingBlocks/
│  │  ├─ TemplateName.SharedKernel/          Tasks 1–2  Result, Error, Entity, AggregateRoot, IDomainEvent, IAuditable, ISoftDeletable, SequentialGuid
│  │  ├─ TemplateName.Application.Common/    Tasks 3,15  Messaging/ (ICommand…, handlers, decorators), Data/IDbConnectionFactory, Identity/ICurrentUser, Localization/, Pagination/
│  │  ├─ TemplateName.Infrastructure.Common/ Tasks 8,9,12,15  Persistence/, Outbox/, Idempotency/, Resources/
│  │  └─ TemplateName.Web.Common/            Tasks 4,6,7,15   Results/, Errors/, Middleware/, Identity/, Observability/, Security/, Localization/, Resources/
│  ├─ Modules/Sample/TemplateName.Modules.Sample/   Tasks 10–11
│  │  ├─ Domain/LeaveRequests/               LeaveRequest, LeaveRequestStatus, LeaveRequestErrors, Events/
│  │  ├─ Application/Abstractions/           ILeaveRequestRepository, IUnitOfWork
│  │  ├─ Application/LeaveRequests/{Submit,Approve,GetById,List}/
│  │  ├─ Infrastructure/Persistence/         SampleDbContext, LeaveRequestConfiguration, LeaveRequestRepository, Migrations/
│  │  ├─ Endpoints/LeaveRequestEndpoints.cs
│  │  ├─ Resources/SampleErrorMessages(.ms|.zh-Hans).resx   Task 15
│  │  └─ SampleModule.cs                     AddSampleModule / MapSampleEndpoints
│  └─ Host/TemplateName.Api/                 Task 5 (+ wiring in 6,7,8,11,12)
├─ tests/
│  ├─ Directory.Build.props                  Task 1  (test packages, OutputType Exe for xUnit v3)
│  ├─ TemplateName.UnitTests/                Tasks 1–4,6,8–10
│  ├─ TemplateName.IntegrationTests/         Tasks 5,7–9,11,12
│  └─ TemplateName.ArchitectureTests/        Task 13
├─ Directory.Build.props, Directory.Packages.props, global.json, .editorconfig, .gitattributes, .gitignore   Task 1
├─ coverage.runsettings                      Task 14
├─ docker-compose.yml, .env.example          Task 14
├─ TemplateName.slnx                         Task 1
└─ README.md                                 Task 14
```

---

### Task 1: Repository foundation + Result/Error

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `src/Directory.Build.props`, `tests/Directory.Build.props`, `.editorconfig`, `tests/.editorconfig`, `.gitattributes`, `.gitignore`, `.config/dotnet-tools.json`, `TemplateName.slnx`, `.githooks/commit-msg`, `build/scripts/check-commit-msg.sh`, `CLAUDE.md`
- Create: `CHANGELOG.md` (header + `## [Unreleased]`, with a note that release-please maintains it), `build/template-content/CLAUDE.md`
- Keep: `CONTRIBUTING.md`, `docs/coding-conventions.md` (already written; the rules every task follows), `LICENSE` (MIT, already written)
- Create: `src/BuildingBlocks/TemplateName.SharedKernel/{ErrorType.cs, Error.cs, ValidationError.cs, Result.cs, ResultOfT.cs}`
- Create: `docs/adr/0001-modular-monolith-two-projects-per-module.md`, `0002-result-pattern.md`, `0003-sql-server-only-v1.md` (use the blueprint's Section 22 ADR template)
- Test: `tests/TemplateName.UnitTests/SharedKernel/ResultTests.cs`

**Interfaces:**
- Produces (namespace `TemplateName.SharedKernel`):
  - `public enum ErrorType { Validation, NotFound, Conflict, Unauthorized, Forbidden, Failure }`
  - `public record Error(string Code, string Message, ErrorType Type)` with `public static readonly Error None` (`""`, `""`, `Failure`) and factories `Error.Validation/NotFound/Conflict/Unauthorized/Forbidden/Failure(string code, string message)`
  - `public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors) : Error("validation.failed", "One or more validation errors occurred.", ErrorType.Validation)`
  - `public class Result`: `bool IsSuccess`, `bool IsFailure`, `Error Error`, `static Result Success()`, `static Result Failure(Error error)`, `static Result<T> Success<T>(T value)`, `static Result<T> Failure<T>(Error error)`. Constructor is protected; success with an error, or failure with `Error.None`, throws `ArgumentException`.
  - `public sealed class Result<T> : Result`: `T Value` (throws `InvalidOperationException` on failure), `implicit operator Result<T>(T value)`, `implicit operator Result<T>(Error error)`

- [ ] **Step 1: Initialise the repo and tooling**

The repository already exists (bootstrap commit on `main`). Start the task branch `feature/task-01-repository-foundation`, then create `global.json` (value in Global Constraints) and `.gitignore` (`dotnet new gitignore`, plus `.env`, `TestResults/`, `coverage/`).
`.gitattributes`: `* text=auto eol=lf`, `*.sh text eol=lf`, `*.ps1 text eol=crlf`.
`.editorconfig` (`root = true`):
- Formatting: `end_of_line = lf`, `insert_final_newline = true`, 4-space indent for `*.cs`, 2-space for `*.json`, `*.yml`, `*.xml`, `*.props` and `*.resx`.
- Style:
  - `csharp_style_namespace_declarations = file_scoped:warning`
  - `dotnet_style_namespace_match_folder = true` with `dotnet_diagnostic.IDE0130.severity = warning`
  - `dotnet_style_qualification_for_field|property|method|event = false:warning`
  - `csharp_style_var_for_built_in_types = true:suggestion`, `csharp_style_var_when_type_is_apparent = true:warning`, `csharp_style_var_elsewhere = false:suggestion`
  - `dotnet_diagnostic.IDE1006.severity = warning`
- **Naming rules (conventions doc Section 2).** Order matters: the most specific rule comes first.

```ini
[*.cs]
dotnet_naming_style.pascal_case.capitalization = pascal_case
dotnet_naming_style.camel_case.capitalization = camel_case
dotnet_naming_style.underscore_camel_case.capitalization = camel_case
dotnet_naming_style.underscore_camel_case.required_prefix = _
dotnet_naming_style.i_prefix.capitalization = pascal_case
dotnet_naming_style.i_prefix.required_prefix = I
dotnet_naming_style.t_prefix.capitalization = pascal_case
dotnet_naming_style.t_prefix.required_prefix = T
dotnet_naming_style.async_suffix.capitalization = pascal_case
dotnet_naming_style.async_suffix.required_suffix = Async

dotnet_naming_symbols.interfaces.applicable_kinds = interface
dotnet_naming_symbols.type_parameters.applicable_kinds = type_parameter
dotnet_naming_symbols.types.applicable_kinds = class, struct, enum, delegate
dotnet_naming_symbols.async_methods.applicable_kinds = method, local_function
dotnet_naming_symbols.async_methods.required_modifiers = async
dotnet_naming_symbols.constants.applicable_kinds = field, local
dotnet_naming_symbols.constants.required_modifiers = const
dotnet_naming_symbols.static_readonly_fields.applicable_kinds = field
dotnet_naming_symbols.static_readonly_fields.required_modifiers = static, readonly
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private, private_protected
dotnet_naming_symbols.members.applicable_kinds = method, local_function, property, event
dotnet_naming_symbols.locals_and_parameters.applicable_kinds = parameter, local

dotnet_naming_rule.interfaces_begin_with_i.symbols = interfaces
dotnet_naming_rule.interfaces_begin_with_i.style = i_prefix
dotnet_naming_rule.interfaces_begin_with_i.severity = warning
dotnet_naming_rule.type_parameters_begin_with_t.symbols = type_parameters
dotnet_naming_rule.type_parameters_begin_with_t.style = t_prefix
dotnet_naming_rule.type_parameters_begin_with_t.severity = warning
dotnet_naming_rule.types_are_pascal_case.symbols = types
dotnet_naming_rule.types_are_pascal_case.style = pascal_case
dotnet_naming_rule.types_are_pascal_case.severity = warning
dotnet_naming_rule.async_methods_end_with_async.symbols = async_methods
dotnet_naming_rule.async_methods_end_with_async.style = async_suffix
dotnet_naming_rule.async_methods_end_with_async.severity = warning
dotnet_naming_rule.constants_are_pascal_case.symbols = constants
dotnet_naming_rule.constants_are_pascal_case.style = pascal_case
dotnet_naming_rule.constants_are_pascal_case.severity = warning
dotnet_naming_rule.static_readonly_fields_are_pascal_case.symbols = static_readonly_fields
dotnet_naming_rule.static_readonly_fields_are_pascal_case.style = pascal_case
dotnet_naming_rule.static_readonly_fields_are_pascal_case.severity = warning
dotnet_naming_rule.private_fields_are_underscore_camel_case.symbols = private_fields
dotnet_naming_rule.private_fields_are_underscore_camel_case.style = underscore_camel_case
dotnet_naming_rule.private_fields_are_underscore_camel_case.severity = warning
dotnet_naming_rule.members_are_pascal_case.symbols = members
dotnet_naming_rule.members_are_pascal_case.style = pascal_case
dotnet_naming_rule.members_are_pascal_case.severity = warning
dotnet_naming_rule.locals_and_parameters_are_camel_case.symbols = locals_and_parameters
dotnet_naming_rule.locals_and_parameters_are_camel_case.style = camel_case
dotnet_naming_rule.locals_and_parameters_are_camel_case.severity = warning
```

`tests/.editorconfig` (no `root`): under `[*.cs]` sets `dotnet_naming_rule.async_methods_end_with_async.severity = none` and `dotnet_naming_rule.members_are_pascal_case.severity = none`, so `async Task Approve_twice_fails_with_not_pending()` compiles.
`.config/dotnet-tools.json`: `dotnet new tool-manifest` then `dotnet tool install dotnet-ef` (version matching EF Core 10).

- [ ] **Step 1b: Commit-message enforcement and agent guide**

`build/scripts/check-commit-msg.sh` (`set -euo pipefail`, LF line endings, executable via `git update-index --chmod=+x`):
- Input: a file path (`$1`, as git passes it to `commit-msg`) or `--message "<text>"` (CI). Only the first non-comment line is checked.
- `ALLOWED_TYPES="feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert"`
- `ALLOWED_SCOPES="shared-kernel|application|infrastructure|web|host|persistence|outbox|idempotency|observability|security|i18n|architecture|sample|auth|notifications|audit|ci|deps|template|docker|adr|release"`
- Passes headers starting `Merge `, `fixup! ` or `squash! ` only when `--allow-autosquash` is given (the hook gives it, CI doesn't).
- Rejects if the header is longer than 72 characters, or doesn't match `^(${ALLOWED_TYPES})(\((${ALLOWED_SCOPES})\))?!?: [^A-Z[:space:]].*[^.[:space:]]$`.
- On failure, prints the header, the reason and a pointer to `CONTRIBUTING.md` Section 2, and exits 1.

`.githooks/commit-msg`: `exec bash build/scripts/check-commit-msg.sh --allow-autosquash "$1"` (executable). Then run `git config core.hooksPath .githooks`.
`build/template-content/CLAUDE.md` is the agent guide that generated projects receive. Keep it under ~80 lines and **link** to long docs rather than `@import` them. Sections, in order:
1. **Overview**: one paragraph (`TemplateName` modular monolith on ASP.NET Core 10, SQL Server, two projects per module).
2. **Commands**: build (`dotnet build -c Release`), test (`dotnet test`), run, format check (`dotnet format --verify-no-changes`), add a migration (the `dotnet ef migrations add {Verb}{What} --project … --startup-project … --context {Module}DbContext` form).
3. **Where things go**: module folder layout and the use-case folder per command/query; cross-module calls only through `*.Contracts`.
4. **Definition of done**: tests first and green; the matching `docs/` page updated; new error codes in all three `.resx` languages; ADR for significant decisions; Conventional Commit message (it becomes the changelog).
5. **Hard rules**: no secrets in files; no `DateTime.Now`/`UtcNow` (use `TimeProvider`); no access to another module's tables; no new package without CPM and an allowed licence; never `--no-verify`; never edit an applied migration.
6. **Read before editing**: links to `docs/coding-conventions.md`, `CONTRIBUTING.md`, `docs/architecture/overview.md`, `docs/adr/`.

Root `CLAUDE.md` (template repo only; excluded from output in Task 17) is `@build/template-content/CLAUDE.md` followed by a short **"Maintaining the template"** section:
- `TemplateName` is the placeholder; never hard-code a concrete project name.
- Files under `build/template-content/` replace their root counterparts in generated output.
- Run `bash build/scripts/template-smoke.sh` after changing anything that ships.
- `BACKEND_TEMPLATE_BLUEPRINT.md` + `docs/blueprint-review.md` are the spec; the review wins.

Verify (each exit code as stated):
```bash
bash build/scripts/check-commit-msg.sh --message "feat(sample): add leave request cancellation"   # 0
bash build/scripts/check-commit-msg.sh --message "feat(auth)!: require MFA for admin roles"       # 0
bash build/scripts/check-commit-msg.sh --message "Added stuff."                                   # 1
bash build/scripts/check-commit-msg.sh --message "feat(unknown): add thing"                       # 1 (scope)
bash build/scripts/check-commit-msg.sh --message "fix: Fix the bug."                              # 1 (case, period)
bash build/scripts/check-commit-msg.sh --message "fixup! feat(sample): x"                         # 1 (no --allow-autosquash)
```

- [ ] **Step 2: Write the shared MSBuild files**

`Directory.Build.props` (root):

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnalysisLevel>latest</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors Condition="'$(Configuration)' == 'Release'">true</TreatWarningsAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <NuGetAuditMode>all</NuGetAuditMode>
    <WarningsAsErrors>$(WarningsAsErrors);NU1903;NU1904</WarningsAsErrors>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`: `ManagePackageVersionsCentrally=true`, `CentralPackageTransitivePinningEnabled=true`. Add each package when the first task needs it.
`src/Directory.Build.props`: imports the root props (`<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`) and adds `InternalsVisibleTo` for `TemplateName.UnitTests`, `TemplateName.IntegrationTests`, `TemplateName.ArchitectureTests` and `DynamicProxyGenAssembly2`.
`tests/Directory.Build.props`: imports the root, sets `IsPackable=false` and `OutputType=Exe` (xUnit v3), and references `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Shouldly`, `NSubstitute`, `coverlet.collector`, with a global `using Xunit; using Shouldly;`.

- [ ] **Step 3: Create the solution and first two projects**

```bash
dotnet new sln -n TemplateName            # SDK 10 produces TemplateName.slnx
dotnet new classlib -o src/BuildingBlocks/TemplateName.SharedKernel
dotnet new classlib -o tests/TemplateName.UnitTests
dotnet sln add src/BuildingBlocks/TemplateName.SharedKernel tests/TemplateName.UnitTests
dotnet add tests/TemplateName.UnitTests reference src/BuildingBlocks/TemplateName.SharedKernel
```

Delete `Class1.cs` files, and strip `TargetFramework`, `Nullable` and `ImplicitUsings` from the generated csproj files.

- [ ] **Step 4: Write the failing tests** in `ResultTests.cs`

```csharp
[Fact] public void Success_has_no_error()
{ var r = Result.Success(); r.IsSuccess.ShouldBeTrue(); r.Error.ShouldBe(Error.None); }

[Fact] public void Failure_carries_error()
{ var e = Error.NotFound("leave.not_found", "x"); var r = Result.Failure(e); r.IsFailure.ShouldBeTrue(); r.Error.ShouldBe(e); }

[Fact] public void Failure_with_none_throws()
    => Should.Throw<ArgumentException>(() => Result.Failure(Error.None));

[Fact] public void Value_of_failed_result_throws()
    => Should.Throw<InvalidOperationException>(() => _ = Result.Failure<int>(Error.Conflict("a.b", "c")).Value);

[Fact] public void Implicit_conversions_create_success_and_failure()
{ Result<int> ok = 5; ok.Value.ShouldBe(5); Result<int> bad = Error.Failure("a.b", "c"); bad.IsFailure.ShouldBeTrue(); }

[Fact] public void ValidationError_has_validation_type_and_code()
{ var v = new ValidationError(new Dictionary<string, string[]> { ["reason"] = ["required"] });
  v.Type.ShouldBe(ErrorType.Validation); v.Code.ShouldBe("validation.failed"); v.Errors["reason"].ShouldBe(new[] { "required" }); }
```

- [ ] **Step 5: Run to verify failure**

Run: `dotnet test tests/TemplateName.UnitTests`
Expected: build FAILS (types `Result`/`Error` not defined).

- [ ] **Step 6: Implement the types listed under Interfaces**

- [ ] **Step 7: Run tests and a Release build**

Run: `dotnet test tests/TemplateName.UnitTests` → 6 passed. Then `dotnet build -c Release` → 0 warnings.

- [ ] **Step 8: Write ADRs 0001–0003** (content from `docs/blueprint-review.md` D1, Section 2.13 for `Error`, and D2)

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "chore: set up repository foundation and Result/Error types"
```

---

### Task 2: Domain primitives and SQL Server-ordered IDs

**Files:**
- Create: `src/BuildingBlocks/TemplateName.SharedKernel/{IDomainEvent.cs, Entity.cs, AggregateRoot.cs, IHasDomainEvents.cs, IAuditable.cs, ISoftDeletable.cs, SequentialGuid.cs}`
- Create: `docs/adr/0004-sql-server-ordered-sequential-guids.md`
- Test: `tests/TemplateName.UnitTests/SharedKernel/{SequentialGuidTests.cs, AggregateRootTests.cs}`

**Interfaces:**
- Produces (namespace `TemplateName.SharedKernel`):
  - `public interface IDomainEvent;`
  - `public abstract class Entity<TId> where TId : notnull { public TId Id { get; protected set; } }`
  - `public interface IHasDomainEvents { IReadOnlyList<IDomainEvent> DomainEvents { get; } void ClearDomainEvents(); }`
  - `public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents` with `protected void Raise(IDomainEvent domainEvent)`
  - `public interface IAuditable { DateTime CreatedAt { get; } Guid? CreatedBy { get; } DateTime? UpdatedAt { get; } Guid? UpdatedBy { get; } }`
  - `public interface ISoftDeletable { bool IsDeleted { get; } DateTime? DeletedAt { get; } Guid? DeletedBy { get; } }`. Properties are get-only on the interface; interceptors set them through EF property entries.
  - `public static class SequentialGuid { public static Guid Create(DateTimeOffset timestamp); }`

- [ ] **Step 1: Write the failing tests**

`SequentialGuidTests`:
```csharp
[Fact] public void Later_timestamp_sorts_after_earlier_in_sql_server_order()
{
    var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    new SqlGuid(SequentialGuid.Create(t.AddMilliseconds(1)))
        .CompareTo(new SqlGuid(SequentialGuid.Create(t))).ShouldBeGreaterThan(0);
}

[Fact] public void Ids_created_over_increasing_time_are_sorted_in_sql_server_order()
{
    var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var ids = Enumerable.Range(0, 1000).Select(i => new SqlGuid(SequentialGuid.Create(t.AddMilliseconds(i)))).ToList();
    ids.ShouldBe(ids.OrderBy(x => x).ToList());
}

[Fact] public void Same_timestamp_produces_distinct_ids()
{
    var t = DateTimeOffset.UnixEpoch;
    Enumerable.Range(0, 1000).Select(_ => SequentialGuid.Create(t)).Distinct().Count().ShouldBe(1000);
}
```
`AggregateRootTests` (uses a private nested `TestAggregate : AggregateRoot<Guid>` with a public `DoSomething()` that raises `TestEvent : IDomainEvent`): `Raise_adds_event` and `ClearDomainEvents_empties_list`.

- [ ] **Step 2: Run to verify failure** → `dotnet test tests/TemplateName.UnitTests`, which fails to compile.

- [ ] **Step 3: Implement.** `SequentialGuid.Create` algorithm: allocate 16 bytes; fill bytes `[0..10)` with `RandomNumberGenerator.Fill`; write `timestamp.ToUnixTimeMilliseconds()` as a **48-bit big-endian** integer into bytes `[10..16)`; return `new Guid(bytes)`. SQL Server compares `uniqueidentifier` bytes 10–15 first, most significant first.

- [ ] **Step 4: Run tests** → all pass.

- [ ] **Step 5: Write ADR 0004** (from review Section 2.4)

- [ ] **Step 6: Commit** → `git commit -m "feat(shared-kernel): add domain primitives and sequential GUIDs"`

---

### Task 3: Messaging contracts and decorators

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Messaging/{ICommand.cs, IQuery.cs, ICommandHandler.cs, IQueryHandler.cs, IDomainEventHandler.cs, ValidationDecorator.cs, LoggingDecorator.cs, MessagingServiceCollectionExtensions.cs}`
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Identity/ICurrentUser.cs`, `Data/IDbConnectionFactory.cs`
- Create: `docs/adr/0005-injected-handlers-with-scrutor-decorators.md`
- Test: `tests/TemplateName.UnitTests/Application/{ValidationDecoratorTests.cs, HandlerRegistrationTests.cs}`

**Interfaces:**
- Consumes: `Result`, `Result<T>`, `ValidationError`, `IDomainEvent` (Task 1–2)
- Produces (namespace `TemplateName.Application.Common.Messaging` unless noted):
  - `public interface IBaseCommand;` `public interface ICommand : IBaseCommand;` `public interface ICommand<TResponse> : IBaseCommand;` `public interface IQuery<TResponse>;`
  - `ICommandHandler<in TCommand>`: `Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken)`
  - `ICommandHandler<in TCommand, TResponse>`: `Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken)`
  - `IQueryHandler<in TQuery, TResponse>`: `Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken)`
  - `IDomainEventHandler<in TEvent> where TEvent : IDomainEvent`: `Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken)`
  - `public static IServiceCollection AddApplicationHandlers(this IServiceCollection services, Assembly assembly)`: Scrutor scan with `publicOnly: false` for all four handler interfaces (scoped), plus `AddValidatorsFromAssembly(assembly, includeInternalTypes: true)`
  - `public static IServiceCollection AddApplicationDecorators(this IServiceCollection services)`: called **once, last**, from the host. Uses `TryDecorate`, registering **Validation first, then Logging**, so Logging is outermost.
  - Decorator classes: `ValidationDecorator.CommandHandler<TCommand>`, `ValidationDecorator.CommandHandler<TCommand,TResponse>`, `ValidationDecorator.QueryHandler<TQuery,TResponse>` (constructor: inner handler, `IEnumerable<IValidator<T>>`), and the same three under `LoggingDecorator` (constructor: inner handler, `ILogger<...>`)
  - `TemplateName.Application.Common.Identity.ICurrentUser { Guid? UserId { get; } bool IsAuthenticated { get; } }`
  - `TemplateName.Application.Common.Data.IDbConnectionFactory { Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken); }`

- [ ] **Step 1: Write the failing tests**

Test fixtures in the test file: `internal sealed record PingCommand(string Reason) : ICommand<string>;`, a validator requiring `Reason` non-empty, and `internal sealed class PingHandler : ICommandHandler<PingCommand, string>` returning `"pong"`.

```csharp
[Fact] public async Task Invalid_command_returns_validation_error_without_calling_inner()
{
    var inner = Substitute.For<ICommandHandler<PingCommand, string>>();
    var sut = new ValidationDecorator.CommandHandler<PingCommand, string>(inner, [new PingValidator()]);
    var result = await sut.HandleAsync(new PingCommand(""), TestContext.Current.CancellationToken);
    result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("reason");   // camelCase key
    await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
}

[Fact] public async Task Valid_command_calls_inner() { /* same setup, "ok" → result.Value == "pong" from inner */ }

[Fact] public void Registration_resolves_internal_handler_wrapped_with_logging_outermost()
{
    var sp = new ServiceCollection().AddLogging()
        .AddApplicationHandlers(typeof(PingHandler).Assembly).AddApplicationDecorators()
        .BuildServiceProvider();
    sp.CreateScope().ServiceProvider.GetRequiredService<ICommandHandler<PingCommand, string>>()
        .ShouldBeOfType<LoggingDecorator.CommandHandler<PingCommand, string>>();
}
```

- [ ] **Step 2: Run to verify failure** → compile errors.

- [ ] **Step 3: Implement.** Validation decorator: run all validators, group failures by `JsonNamingPolicy.CamelCase.ConvertName(PropertyName)` into `ValidationError`, and return `Result.Failure`/`Result.Failure<TResponse>`. Logging decorator: log `Processing {RequestName}`, completion with `{ElapsedMs}`, and failures at **Warning** with `{ErrorCode}`. Let exceptions propagate unlogged (the exception handler logs them). Use `[LoggerMessage]` source-generated methods.

- [ ] **Step 4: Run tests** → pass.

- [ ] **Step 5: Write ADR 0005** (review Section 4, decorators row, plus 2.5)

- [ ] **Step 6: Commit** → `git commit -m "feat(application): add handler contracts and decorators"`

---

### Task 4: Web common: ProblemDetails, exception handling, headers, current user

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Results/ResultExtensions.cs`
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Errors/{GlobalExceptionHandler.cs, ProblemDetailsSetup.cs}`
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Middleware/{TraceIdHeaderMiddleware.cs, SecurityHeadersMiddleware.cs}`
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Identity/HttpContextCurrentUser.cs`
- Create: `src/BuildingBlocks/TemplateName.Web.Common/WebCommonServiceCollectionExtensions.cs`
- Test: `tests/TemplateName.UnitTests/Web/{ResultExtensionsTests.cs, GlobalExceptionHandlerTests.cs, MiddlewareTests.cs}`

**Interfaces:**
- Consumes: `Result`, `Error`, `ErrorType`, `ValidationError`, `ICurrentUser`
- Produces:
  - `public static IResult ToProblem(this Result result)` (namespace `TemplateName.Web.Common.Results`): throws `InvalidOperationException` on success. Uses `TypedResults.Problem(statusCode, detail: error.Message, extensions: { ["code"] = error.Code })`, or `TypedResults.ValidationProblem(errors, detail, extensions: code)` for `ValidationError`.
  - `public static IServiceCollection AddWebCommon(this IServiceCollection services)`: `AddHttpContextAccessor`, `ICurrentUser → HttpContextCurrentUser` (scoped; reads `sub`, falling back to `ClaimTypes.NameIdentifier`), `AddProblemDetails` whose `CustomizeProblemDetails` sets `traceId = Activity.Current?.TraceId.ToHexString() ?? HttpContext.TraceIdentifier` and sets `code = "http.{status}"` when no `code` exists, `AddExceptionHandler<GlobalExceptionHandler>()`, and `Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true)`
  - `GlobalExceptionHandler : IExceptionHandler`: `BadHttpRequestException` → its `StatusCode` (normally 400) with `code = request.malformed`; anything else → 500 with `code = server.unexpected_error`, logged at Error. `detail` = `exception.ToString()` **only** when `IHostEnvironment.IsDevelopment()`, otherwise `"An unexpected error occurred."`.
  - `app.UseTraceIdHeader()`: sets `X-Trace-Id` **before** calling `next`.
  - `app.UseSecurityHeaders()`: before `next`, sets `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(ErrorType.NotFound, 404)] [InlineData(ErrorType.Conflict, 409)]
[InlineData(ErrorType.Unauthorized, 401)] [InlineData(ErrorType.Forbidden, 403)] [InlineData(ErrorType.Failure, 500)]
public void Maps_error_type_to_status_and_code(ErrorType type, int status)
{
    var problem = Result.Failure(new Error("x.y", "msg", type)).ToProblem().ShouldBeOfType<ProblemHttpResult>();
    problem.StatusCode.ShouldBe(status);
    problem.ProblemDetails.Extensions["code"].ShouldBe("x.y");
}

[Fact] public void Validation_error_maps_to_400_with_errors()
{ /* ToProblem() is ValidationProblem; StatusCode 400; ProblemDetails.Errors["reason"] == ["required"] */ }

[Fact] public void ToProblem_on_success_throws() => Should.Throw<InvalidOperationException>(() => Result.Success().ToProblem());
```
`GlobalExceptionHandlerTests` (use `DefaultHttpContext` with `RequestServices` from `new ServiceCollection().AddLogging().AddProblemDetails()` and a `MemoryStream` body): `Development_includes_exception_detail`, `Production_hides_exception_detail` (body doesn't contain the exception message or `"   at "`), and `BadHttpRequestException_maps_to_400_request_malformed`.
`MiddlewareTests`: `Security_headers_are_set` (all five headers above) and `Trace_id_header_matches_current_activity` (start an `Activity`, invoke, compare with `activity.TraceId.ToHexString()`).

- [ ] **Step 2: Run to verify failure** → compile errors.

- [ ] **Step 3: Implement the Interfaces above**

- [ ] **Step 4: Run tests** → pass.

- [ ] **Step 5: Commit** → `git commit -m "feat(web): map results to problem details and add security headers"`

---

### Task 5: API host + integration test harness

**Files:**
- Create: `src/Host/TemplateName.Api/{Program.cs, appsettings.json, appsettings.Development.json, Properties/launchSettings.json}`
- Create: `docs/adr/0008-literal-v1-routes.md`
- Create: `tests/TemplateName.IntegrationTests/Infrastructure/{IntegrationTestWebAppFactory.cs, IntegrationTestBase.cs, AssemblyInfo.cs}`
- Test: `tests/TemplateName.IntegrationTests/Host/HostTests.cs`

**Interfaces:**
- Consumes: `AddWebCommon`, `UseTraceIdHeader`, `UseSecurityHeaders` (Task 4); `AddApplicationDecorators` (Task 3)
- Produces:
  - `public partial class Program;` (for `WebApplicationFactory<Program>`)
  - `IntegrationTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime`: environment `Testing`; settings `RateLimiting:GlobalPermitLimit = 100000`, `Outbox:Enabled = false`. Registered as an xUnit v3 assembly fixture: `[assembly: AssemblyFixture(typeof(IntegrationTestWebAppFactory))]`, plus `DisableTestParallelization = true`.
  - `abstract class IntegrationTestBase(IntegrationTestWebAppFactory factory) : IAsyncLifetime`: exposes `IntegrationTestWebAppFactory Factory`, `HttpClient Client` and `static CancellationToken Ct => TestContext.Current.CancellationToken` (Task 8 adds per-test reset in `InitializeAsync`)
  - Final `Program.cs` pipeline order. Later tasks insert at their marked slots; keep this order:
    1. `UseForwardedHeaders` *(Task 7)*
    1a. `UseApiLocalization` *(Task 15; must come before `UseExceptionHandler`)*
    2. `UseExceptionHandler`
    3. `UseStatusCodePages`
    4. `UseTraceIdHeader`
    5. `UseSecurityHeaders`
    6. non-Development: `UseHsts`, `UseHttpsRedirection` *(Task 7)*
    7. `UseRequestLogging` *(Task 6)*
    8. `UseRouting`
    9. `UseCors` *(Task 7)*
    10. `UseRateLimiter` *(Task 7)*
    11. `UseIdempotency` *(Task 12)*
    12. endpoints: `/health/live` (no checks), `/health/ready` (checks tagged `ready`), both `.DisableRateLimiting()` *(after Task 7)*; `MapOpenApi()` + `MapScalarApiReference()` when **not** Production; `var api = app.MapGroup("/api/v1");` for module endpoints
  - Service order in `Program.cs`: `AddObservability` *(Task 6)* → `AddInfrastructureCommon` *(Task 8; must come before `AddWebCommon` so its exception handler runs first)* → `AddWebCommon` → `AddApiLocalization` *(Task 15)* → `ConfigureHttpJsonOptions` (add `JsonStringEnumConverter`) → `AddOpenApi("v1")` → `AddHealthChecks()` → `AddHttpSecurity` *(Task 7)* → module registrations *(Task 11)* → `AddApplicationDecorators()` last

- [ ] **Step 1: Create the projects**

`dotnet new web -o src/Host/TemplateName.Api`; create `tests/TemplateName.IntegrationTests` as a classlib referencing the Api and `Microsoft.AspNetCore.Mvc.Testing`; add both to the slnx. Add packages `Microsoft.AspNetCore.OpenApi` and `Scalar.AspNetCore`. `appsettings.json` includes `"Kestrel": { "Limits": { "MaxRequestBodySize": 10485760 } }`.

- [ ] **Step 2: Write the failing tests** (`HostTests : IntegrationTestBase`)

```csharp
[Fact] public async Task Live_returns_200() => (await Client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
[Fact] public async Task Ready_returns_200() => (await Client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

[Fact] public async Task Unknown_route_returns_404_problem_details_with_trace_id()
{
    var response = await Client.GetAsync("/api/v1/does-not-exist", Ct);
    response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
    body.GetProperty("code").GetString().ShouldBe("http.404");
}

[Fact] public async Task OpenApi_document_is_served_outside_production()
    => (await Client.GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

[Fact] public async Task Scalar_is_not_mapped_in_production()
{ /* Factory.WithWebHostBuilder(b => b.UseEnvironment("Production")).CreateClient(); GET /scalar/v1 → 404 */ }

[Fact] public async Task Security_headers_are_present() { /* GET /health/live has X-Content-Type-Options: nosniff and X-Frame-Options: DENY */ }
```

- [ ] **Step 3: Run to verify failure** → `dotnet test tests/TemplateName.IntegrationTests` fails.

- [ ] **Step 4: Implement `Program.cs`** with the order above (slots for later tasks left out until those tasks).

- [ ] **Step 5: Run tests** → pass. Manually run `dotnet run --project src/Host/TemplateName.Api` and open `/scalar/v1` to confirm the UI loads.

- [ ] **Step 6: Write ADR 0008** (review Section 4, versioning row)

- [ ] **Step 7: Commit** → `git commit -m "feat(host): add API host with health checks and OpenAPI"`

---

### Task 6: Observability

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Observability/{ObservabilityExtensions.cs, SensitiveDataDestructuringPolicy.cs}`
- Modify: `src/Host/TemplateName.Api/Program.cs` (slot 7 and services), `appsettings.json` (`Serilog` section), `appsettings.Development.json`
- Test: `tests/TemplateName.UnitTests/Web/SensitiveDataDestructuringPolicyTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser`
- Produces:
  - `public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)`: Serilog via `ReadFrom.Configuration` + `Enrich.FromLogContext` + properties `Application` (= `Environment.ApplicationName`) and `Environment`. Console sink uses plain text in Development and `RenderedCompactJsonFormatter` otherwise. `Destructure.With<SensitiveDataDestructuringPolicy>()`. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set: Serilog OpenTelemetry sink + OpenTelemetry tracing (AspNetCore, HttpClient, SqlClient/EF Core) + metrics (AspNetCore, HttpClient, Runtime) with the OTLP exporter. Serilog ≥ 4 records `TraceId`/`SpanId` natively, so no extra enricher is needed.
  - `public static WebApplication UseRequestLogging(this WebApplication app)`: `UseSerilogRequestLogging`, with `GetLevel` returning `Verbose` for `/health/*` and `EnrichDiagnosticContext` adding `UserId` from `ICurrentUser`
  - `public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy`: replaces with `"***"` the value of any property whose name contains, case-insensitively, `password`, `token`, `secret`, `otp` or `apikey`, recursively through nested objects
- `appsettings.Development.json`: `"OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317"` (the Aspire dashboard from Task 14)

- [ ] **Step 1: Write the failing tests** (an in-test `CollectingSink : ILogEventSink` captures events)

```csharp
[Fact] public void Masks_sensitive_properties_and_keeps_others()
{
    var sink = new CollectingSink();
    using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();
    log.Information("{@Req}", new { Email = "a@b.c", Password = "p", RefreshToken = "t" });
    var req = (StructureValue)sink.Events.Single().Properties["Req"];
    req.Properties.Single(p => p.Name == "Password").Value.ToString().ShouldBe("\"***\"");
    req.Properties.Single(p => p.Name == "RefreshToken").Value.ToString().ShouldBe("\"***\"");
    req.Properties.Single(p => p.Name == "Email").Value.ToString().ShouldBe("\"a@b.c\"");
}

[Fact] public void Masks_nested_properties() { /* new { User = new { ApiKey = "k" } } → nested ApiKey is "***" */ }
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement and wire into `Program.cs`** (`builder.AddObservability()` first; `app.UseRequestLogging()` at slot 7).
- [ ] **Step 4: Run all tests** → unit and integration pass (the app boots without an OTLP endpoint).
- [ ] **Step 5: Commit** → `git commit -m "feat(observability): add Serilog masking and OpenTelemetry"`

---

### Task 7: HTTP security: CORS, rate limiting, forwarded headers, HSTS

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Security/{HttpSecurityExtensions.cs, ApiCorsOptions.cs, RateLimitingOptions.cs, ApiForwardedHeadersOptions.cs}`
- Modify: `src/Host/TemplateName.Api/Program.cs` (slots 1, 6, 9, 10, 12), `appsettings.json`
- Test: `tests/TemplateName.IntegrationTests/Host/HttpSecurityTests.cs`

**Interfaces:**
- Produces:
  - `public static IServiceCollection AddHttpSecurity(this IServiceCollection services, IConfiguration configuration)`
  - `ApiCorsOptions` (section `Cors`): `string[] AllowedOrigins = []`. Validation rejects any `"*"` entry. One default policy: listed origins, any header, any method, credentials allowed.
  - `RateLimitingOptions` (section `RateLimiting`): `int GlobalPermitLimit = 300` (`[Range(1, int.MaxValue)]`), `TimeSpan GlobalWindow = 00:01:00`. A global fixed-window limiter partitioned by `user:{sub}` when authenticated, else `ip:{RemoteIpAddress ?? "unknown"}`. On rejection: 429 ProblemDetails with `code = rate_limit.exceeded` and a `Retry-After` header (seconds) when the lease provides it.
  - `ApiForwardedHeadersOptions` (section `ForwardedHeaders`): `string[] KnownProxies = []`, `string[] KnownNetworks = []` (CIDR). Configures `XForwardedFor | XForwardedProto` using the **non-obsolete** .NET 10 property for networks (`KnownIPNetworks`), and clears the default loopback entries only when the lists are non-empty.
  - Non-Development: `UseHsts()` + `UseHttpsRedirection()`

- [ ] **Step 1: Write the failing tests** (each uses `Factory.WithWebHostBuilder(b => b.UseSetting(...)).CreateClient()` where settings differ)

```csharp
[Fact] public async Task Allowed_origin_gets_cors_headers()
{ /* Cors:AllowedOrigins:0 = https://app.example.com; OPTIONS /api/v1/x with Origin + Access-Control-Request-Method: GET
     → Access-Control-Allow-Origin == https://app.example.com */ }

[Fact] public async Task Unknown_origin_gets_no_cors_headers() { /* Origin https://evil.example → no Access-Control-Allow-Origin */ }

[Fact] public async Task Exceeding_global_limit_returns_429_problem_details()
{ /* GlobalPermitLimit = 2; three GET /api/v1/does-not-exist → third is 429, code rate_limit.exceeded, has traceId */ }

[Fact] public async Task Health_endpoints_are_not_rate_limited() { /* GlobalPermitLimit = 1; five GET /health/live → all 200 */ }

[Fact] public async Task Spoofed_forwarded_for_does_not_bypass_rate_limit()
{ /* GlobalPermitLimit = 2, no KnownProxies; three requests each with a different X-Forwarded-For → third is 429 */ }
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement and wire the slots.**
- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Commit** → `git commit -m "feat(security): add CORS, rate limiting and forwarded headers"`

---

### Task 8: Persistence foundation

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Persistence/{ModuleDbContextExtensions.cs, ModelConventions.cs, UtcDateTimeConverter.cs, AuditableEntityInterceptor.cs, SoftDeleteInterceptor.cs, SqlConnectionFactory.cs, ConnectionStringsOptions.cs, DatabaseOptions.cs, ModuleDbContextRegistration.cs, MigrationExtensions.cs, ConcurrencyExceptionHandler.cs}`
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/InfrastructureServiceCollectionExtensions.cs`
- Create: `docs/adr/0006-ef-core-writes-dapper-reads.md`
- Modify: `Program.cs` (services + `MigrateModuleDatabasesAsync` when `Database:ApplyMigrationsOnStartup`), `appsettings.json` (`"ConnectionStrings": { "Database": "" }`, `"Database": { "ApplyMigrationsOnStartup": false }`), `appsettings.Development.json` (`ApplyMigrationsOnStartup: true`)
- Modify: `IntegrationTestWebAppFactory` (SQL Server container, `FakeTimeProvider`, `TestCurrentUser`, Respawn), `IntegrationTestBase`
- Create: `tests/TemplateName.IntegrationTests/Persistence/{TestDbContext.cs, TestAggregate.cs}`, `tests/TemplateName.IntegrationTests/Infrastructure/TestCurrentUser.cs`
- Test: `tests/TemplateName.IntegrationTests/Persistence/PersistenceTests.cs`, `tests/TemplateName.UnitTests/Infrastructure/ConcurrencyExceptionHandlerTests.cs`

**Interfaces:**
- Consumes: `ICurrentUser`, `IDbConnectionFactory`, `IAuditable`, `ISoftDeletable`, `TimeProvider`
- Produces (namespace `TemplateName.Infrastructure.Common.Persistence` unless noted):
  - `public static IServiceCollection AddInfrastructureCommon(this IServiceCollection services, IConfiguration configuration)` (namespace `TemplateName.Infrastructure.Common`): `TryAddSingleton(TimeProvider.System)`; `ConnectionStringsOptions` (section `ConnectionStrings`, `[Required] string Database`) validated on start; `DatabaseOptions` (section `Database`, `bool ApplyMigrationsOnStartup`); `IDbConnectionFactory → SqlConnectionFactory` (singleton); interceptors (scoped); `AddExceptionHandler<ConcurrencyExceptionHandler>()`
  - `public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema, string connectionStringName = "Database", bool includeInMigrations = true) where TContext : DbContext`: `UseSqlServer(conn, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", schema).EnableRetryOnFailure())`, with the connection string resolved lazily via `IConfiguration.GetConnectionString(connectionStringName)` inside the options callback; adds the interceptors from DI; adds `AddDbContextCheck<TContext>(name: schema, tags: ["ready"])`; when `includeInMigrations`, registers `ModuleDbContextRegistration(typeof(TContext))`
  - `public static void ApplyDefaultConventions(this ModelConfigurationBuilder builder)`: every `DateTime`/`DateTime?` uses `UtcDateTimeConverter` with precision 3
  - `public static void ApplySoftDeleteQueryFilters(this ModelBuilder modelBuilder)`: an EF Core 10 **named** filter `"SoftDelete"` (`e => !e.IsDeleted`) on every `ISoftDeletable` entity
  - `AuditableEntityInterceptor(ICurrentUser, TimeProvider) : SaveChangesInterceptor`: Added → `CreatedAt`/`CreatedBy`; Modified → `UpdatedAt`/`UpdatedBy` (set via `entry.Property(name).CurrentValue`)
  - `SoftDeleteInterceptor(ICurrentUser, TimeProvider) : SaveChangesInterceptor`: Deleted `ISoftDeletable` → Modified with `IsDeleted = true`, `DeletedAt`, `DeletedBy`
  - `public static Task MigrateModuleDatabasesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)`: migrates every registered context in registration order
  - `ConcurrencyExceptionHandler : IExceptionHandler`: `DbUpdateConcurrencyException` → 409 with `code = concurrency.conflict`; otherwise returns `false`
- Test harness (`IntegrationTestWebAppFactory`):
  - `InitializeAsync` starts `MsSqlContainer` (`mcr.microsoft.com/mssql/server:2022-latest`) and runs `CREATE DATABASE` for `TemplateName_Tests` and `TemplateName_PersistenceTests` on the master connection.
  - Settings: `ConnectionStrings:Database` points at `TemplateName_Tests`, and `ConnectionStrings:PersistenceTests` at `TemplateName_PersistenceTests`.
  - `ConfigureTestServices` replaces `TimeProvider` with a singleton `FakeTimeProvider` starting at `2026-01-01T00:00:00Z` and `ICurrentUser` with a singleton `TestCurrentUser` (settable `Guid? UserId`), and calls `AddModuleDbContext<TestDbContext>("test", "PersistenceTests", includeInMigrations: false)`.
  - After the host starts it runs `MigrateModuleDatabasesAsync` and `TestDbContext.Database.EnsureCreatedAsync`.
  - It exposes `FakeTimeProvider Time`, `TestCurrentUser CurrentUser` and `Task ResetDatabasesAsync()`. The reset uses one Respawner per database (`__EFMigrationsHistory` excluded), created lazily and skipping a database that has no tables yet.
- `IntegrationTestBase.InitializeAsync` calls `Factory.ResetDatabasesAsync()`, resets `Factory.Time` to the start instant and sets `Factory.CurrentUser.UserId = null`.
- `TestAggregate : AggregateRoot<Guid>, IAuditable, ISoftDeletable` with `Name` (`nvarchar(100)`), `static TestAggregate Create(string name, DateTimeOffset now)` and `Rename(string name)`

- [ ] **Step 1: Write the failing tests** (`PersistenceTests : IntegrationTestBase`; `NewTestDbContext()` creates a scope and resolves `TestDbContext`)

```csharp
[Fact] public async Task Added_entity_gets_created_audit_fields()
{
    Factory.CurrentUser.UserId = UserA;
    var e = TestAggregate.Create("a", Factory.Time.GetUtcNow());
    await using (var db = NewTestDbContext()) { db.Add(e); await db.SaveChangesAsync(Ct); }
    await using var read = NewTestDbContext();
    var saved = await read.Set<TestAggregate>().SingleAsync(x => x.Id == e.Id, Ct);
    saved.CreatedAt.ShouldBe(Factory.Time.GetUtcNow().UtcDateTime);
    saved.CreatedBy.ShouldBe(UserA);
}

[Fact] public async Task Modified_entity_gets_updated_audit_fields() { /* advance clock 1 min, Rename, save → UpdatedAt == new now, UpdatedBy == UserA */ }

[Fact] public async Task Deleted_entity_is_soft_deleted_and_hidden()
{ /* Remove + save → query returns nothing; IgnoreQueryFilters(["SoftDelete"]) returns it with IsDeleted true and DeletedAt set */ }

[Fact] public async Task DateTime_values_round_trip_as_utc()
{ /* read back → CreatedAt.Kind == DateTimeKind.Utc; JsonSerializer.Serialize(saved.CreatedAt) ends with "Z\"" */ }

[Fact] public async Task Ready_includes_database_checks()
{ /* GET /health/ready → 200 (now includes the test schema check) */ }

[Fact] public void Missing_connection_string_fails_startup()
{ /* Factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Database", "")).CreateClient()
     throws; an OptionsValidationException appears in the exception chain */ }
```
Unit: `ConcurrencyExceptionHandlerTests` → `Concurrency_exception_maps_to_409` and `Other_exceptions_are_not_handled`.

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement the Interfaces above and update the harness.**
- [ ] **Step 4: Run tests** (Docker running) → pass.
- [ ] **Step 5: Write ADR 0006** (blueprint 7.6 + review 2.6, Dapper bypasses query filters)
- [ ] **Step 6: Commit** → `git commit -m "feat(persistence): add module DbContext conventions and interceptors"`

---

### Task 9: Per-module outbox

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Outbox/{OutboxMessage.cs, OutboxMessageConsumer.cs, OutboxModelBuilderExtensions.cs, DomainEventsToOutboxInterceptor.cs, OutboxOptions.cs, OutboxRetryPolicy.cs, OutboxDispatcher.cs, OutboxBackgroundService.cs, OutboxServiceCollectionExtensions.cs}`
- Modify: `ModuleDbContextExtensions.cs` (add `DomainEventsToOutboxInterceptor`), `appsettings.json` (`Outbox` section)
- Modify: `TestDbContext` (call `ApplyOutbox()`), `TestAggregate` (raise `TestAggregateCreatedDomainEvent(Guid Id)` in `Create`), factory (`AddApplicationHandlers(typeof(IntegrationTestWebAppFactory).Assembly)`, `AddOutbox<TestDbContext>(...)`, singletons `EventRecorder` and `FlakySwitch`), `IntegrationTestBase` (clears `EventRecorder`, resets `FlakySwitch`)
- Create: `tests/TemplateName.IntegrationTests/Outbox/{EventRecorder.cs, FlakySwitch.cs, RecordingTestEventHandler.cs, FlakyTestEventHandler.cs}`
- Create: `docs/adr/0007-per-module-outbox.md`
- Test: `tests/TemplateName.UnitTests/Infrastructure/OutboxRetryPolicyTests.cs`, `tests/TemplateName.IntegrationTests/Outbox/OutboxTests.cs`

**Interfaces:**
- Consumes: `IHasDomainEvents`, `IDomainEventHandler<T>`, `AddModuleDbContext`, `AddApplicationHandlers`, `SequentialGuid`, `TimeProvider`
- Produces (namespace `TemplateName.Infrastructure.Common.Outbox`):
  - `OutboxMessage`: `Guid Id`, `string Type` (event `FullName`, max 500), `string Content` (JSON), `DateTime OccurredAt`, `DateTime? ProcessedAt`, `int AttemptCount`, `DateTime? NextAttemptAt`, `DateTime? LockedUntil`, `string? Error` (max 2000). Table `OutboxMessages`, filtered index on `(OccurredAt)` where `ProcessedAt IS NULL`.
  - `OutboxMessageConsumer`: `Guid OutboxMessageId`, `string Name` (handler type `FullName`, max 500), `DateTime ProcessedAt`. PK `(OutboxMessageId, Name)`, table `OutboxMessageConsumers`.
  - `public static ModelBuilder ApplyOutbox(this ModelBuilder modelBuilder)`: maps both entities into the context's default schema
  - `DomainEventsToOutboxInterceptor(TimeProvider) : SaveChangesInterceptor`: on saving, converts every `IHasDomainEvents` entry's events into `OutboxMessage` rows (Id = `SequentialGuid.Create(now)`, `Content = JsonSerializer.Serialize(evt, evt.GetType())`), then clears the events
  - `OutboxOptions` (section `Outbox`): `bool Enabled = true`, `TimeSpan PollingInterval = 00:00:05`, `int BatchSize = 20` (`[Range(1, 500)]`), `int MaxAttempts = 5`, `TimeSpan LeaseDuration = 00:01:00`
  - `public static class OutboxRetryPolicy { public static DateTime? NextAttemptAt(int attemptCount, DateTime utcNow, int maxAttempts); }`: delays after attempt 1–4 are 5 s, 30 s, 2 min, 10 min; after attempt 5, 1 h; returns `null` when `attemptCount >= maxAttempts`
  - `public sealed class OutboxDispatcher<TContext> where TContext : DbContext { public Task<int> ProcessBatchAsync(CancellationToken cancellationToken); }`: returns the number of messages claimed
  - `public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services, Assembly domainEventsAssembly) where TContext : DbContext`: registers the dispatcher (singleton, creates a scope per message), a type map from `FullName` to every `IDomainEvent` type in the assembly, and `OutboxBackgroundService<TContext>`, which loops `ProcessBatchAsync` every `PollingInterval` only when `Enabled`
- Test helpers: `EventRecorder` (thread-safe list of received events); `FlakySwitch` (`bool FailNext`, `bool FailAlways`); `FlakyTestEventHandler` throws when either flag is set, then clears `FailNext`

- [ ] **Step 1: Write the failing tests**

Unit (`OutboxRetryPolicyTests`):
```csharp
[Theory]
[InlineData(1, 5)] [InlineData(2, 30)] [InlineData(3, 120)] [InlineData(4, 600)]
public void Delay_grows_per_attempt(int attempt, int seconds)
    => OutboxRetryPolicy.NextAttemptAt(attempt, T0, maxAttempts: 6).ShouldBe(T0.AddSeconds(seconds));

[Fact] public void Returns_null_when_attempts_exhausted() => OutboxRetryPolicy.NextAttemptAt(5, T0, 5).ShouldBeNull();
```
Integration (`OutboxTests : IntegrationTestBase`):
```csharp
[Fact] public async Task Saving_aggregate_writes_event_to_outbox_in_same_save()
{ /* save TestAggregate → one OutboxMessage with Type == typeof(TestAggregateCreatedDomainEvent).FullName, ProcessedAt null; aggregate.DomainEvents empty */ }

[Fact] public async Task ProcessBatch_dispatches_and_marks_processed()
{ /* save; ProcessBatchAsync == 1; recorder contains aggregate id; message ProcessedAt == fake now */ }

[Fact] public async Task Failing_handler_is_retried_and_successful_handler_is_not_rerun()
{ /* FlakySwitch.FailNext = true; process → AttemptCount 1, Error not null, NextAttemptAt == now + 5s, recorder count 1;
     process again without advancing time → returns 0; advance 5s; process → ProcessedAt set; recorder count still 1 */ }

[Fact] public async Task Message_is_abandoned_after_max_attempts()
{ /* FlakySwitch.FailAlways = true; process + advance 1h five times → AttemptCount 5; next ProcessBatchAsync returns 0 */ }

[Fact] public async Task Concurrent_dispatchers_process_each_message_once()
{ /* save 50 aggregates; two dispatcher instances (ActivatorUtilities.CreateInstance) loop ProcessBatchAsync concurrently until both return 0;
     recorder count == 50 and distinct ids == 50 */ }
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.** The claim query is the one part the signatures don't determine. Use a lease so no transaction is held while handlers run:

```sql
WITH batch AS (
    SELECT TOP (@BatchSize) *
    FROM [{schema}].[OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE ProcessedAt IS NULL AND AttemptCount < @MaxAttempts
      AND (NextAttemptAt IS NULL OR NextAttemptAt <= @Now)
      AND (LockedUntil IS NULL OR LockedUntil < @Now)
    ORDER BY OccurredAt)
UPDATE batch SET LockedUntil = @LeaseUntil
OUTPUT inserted.Id, inserted.Type, inserted.Content, inserted.AttemptCount;
```

For each claimed message, in its own scope: resolve the event type from the map (unknown type counts as a failure); for each `IDomainEventHandler<T>`, skip it if an `OutboxMessageConsumer` row exists for `(Id, handler FullName)`, otherwise call `HandleAsync` and insert the consumer row. If every handler succeeds, set `ProcessedAt = now` and `LockedUntil = null`. If any handler throws, increment `AttemptCount` and set `Error` (truncated to 2000), `NextAttemptAt = OutboxRetryPolicy.NextAttemptAt(...)` and `LockedUntil = null`, and log at Error.

- [ ] **Step 4: Run tests** → pass.
- [ ] **Step 5: Write ADR 0007** (review 2.1–2.2; delivery is at-least-once per handler)
- [ ] **Step 6: Commit** → `git commit -m "feat(outbox): add per-module outbox with leased dispatch"`

---

### Task 10: Sample module: domain and application

**Files:**
- Create: `src/Modules/Sample/TemplateName.Modules.Sample/Domain/LeaveRequests/{LeaveRequest.cs, LeaveRequestStatus.cs, LeaveRequestErrors.cs, Events/LeaveRequestSubmittedDomainEvent.cs, Events/LeaveRequestApprovedDomainEvent.cs}`
- Create: `.../Application/Abstractions/{ILeaveRequestRepository.cs, IUnitOfWork.cs}`
- Create: `.../Application/LeaveRequests/Submit/{SubmitLeaveRequestCommand.cs, SubmitLeaveRequestCommandValidator.cs, SubmitLeaveRequestCommandHandler.cs, LeaveRequestSubmittedDomainEventHandler.cs}`
- Create: `.../Application/LeaveRequests/Approve/{ApproveLeaveRequestCommand.cs, ApproveLeaveRequestCommandHandler.cs}`
- Create: `.../Application/LeaveRequests/GetById/{GetLeaveRequestByIdQuery.cs, LeaveRequestResponse.cs}` (handler in Task 11)
- Test: `tests/TemplateName.UnitTests/Sample/{LeaveRequestTests.cs, SubmitLeaveRequestCommandValidatorTests.cs, SubmitLeaveRequestCommandHandlerTests.cs, ApproveLeaveRequestCommandHandlerTests.cs}`

**Interfaces:**
- Consumes: `AggregateRoot<Guid>`, `IAuditable`, `ISoftDeletable`, `SequentialGuid`, `Result`, `Error`, `ICommand<T>`, `ICommandHandler<…>`, `IDomainEventHandler<T>`
- Produces (all `internal`, namespace root `TemplateName.Modules.Sample`):
  - `enum LeaveRequestStatus : byte { Pending = 1, Approved = 2, Rejected = 3, Canceled = 4 }`
  - `sealed class LeaveRequest : AggregateRoot<Guid>, IAuditable, ISoftDeletable`: `Guid EmployeeId`, `DateOnly StartDate`, `DateOnly EndDate`, `string Reason`, `LeaveRequestStatus Status`, `Guid? ApproverId`, `byte[] RowVersion`, plus the audit/soft-delete properties (private setters)
    - `static Result<LeaveRequest> Submit(Guid employeeId, DateOnly startDate, DateOnly endDate, string reason, DateTimeOffset now)`: `endDate < startDate` → `LeaveRequestErrors.InvalidDateRange`; otherwise Pending, Id = `SequentialGuid.Create(now)`, raises `LeaveRequestSubmittedDomainEvent`
    - `Result Approve(Guid approverId)`: not Pending → `LeaveRequestErrors.NotPending`; otherwise Approved, raises `LeaveRequestApprovedDomainEvent`
  - `static class LeaveRequestErrors`: `InvalidDateRange` = Validation `leave.invalid_date_range`; `NotPending` = Conflict `leave.not_pending`; `static Error NotFound(Guid id)` = NotFound `leave.not_found`
  - `sealed record LeaveRequestSubmittedDomainEvent(Guid LeaveRequestId, Guid EmployeeId) : IDomainEvent`; `sealed record LeaveRequestApprovedDomainEvent(Guid LeaveRequestId, Guid ApproverId) : IDomainEvent`
  - `interface ILeaveRequestRepository { Task<LeaveRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken); void Add(LeaveRequest leaveRequest); }`
  - `interface IUnitOfWork { Task<int> SaveChangesAsync(CancellationToken cancellationToken); }` (module-local; each module defines its own)
  - `sealed record SubmitLeaveRequestCommand(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string Reason) : ICommand<Guid>`, with a validator: `EmployeeId` not empty; `Reason` not empty, max 500; `EndDate >= StartDate`
  - `sealed record ApproveLeaveRequestCommand(Guid LeaveRequestId, Guid ApproverId) : ICommand`. The approver comes from the body until the Auth plan replaces it with `ICurrentUser`.
  - `sealed record GetLeaveRequestByIdQuery(Guid LeaveRequestId) : IQuery<LeaveRequestResponse>`; `sealed record LeaveRequestResponse(Guid Id, Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string Reason, LeaveRequestStatus Status, Guid? ApproverId, DateTime CreatedAt)`
  - `LeaveRequestSubmittedDomainEventHandler`: logs `Leave request {LeaveRequestId} submitted` at Information (a placeholder for the Notifications plan)

- [ ] **Step 1: Create the project** `dotnet new classlib -o src/Modules/Sample/TemplateName.Modules.Sample` referencing SharedKernel, Application.Common, Infrastructure.Common and Web.Common (`<FrameworkReference Include="Microsoft.AspNetCore.App" />`); add it to the slnx and to UnitTests.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void Submit_valid_creates_pending_request_and_raises_event()
{
    var r = LeaveRequest.Submit(Emp, new(2026, 3, 2), new(2026, 3, 4), "Trip", Now);
    r.Value.Status.ShouldBe(LeaveRequestStatus.Pending);
    r.Value.DomainEvents.Single().ShouldBe(new LeaveRequestSubmittedDomainEvent(r.Value.Id, Emp));
}
[Fact] public void Submit_end_before_start_fails() => LeaveRequest.Submit(Emp, new(2026, 3, 4), new(2026, 3, 2), "x", Now).Error.ShouldBe(LeaveRequestErrors.InvalidDateRange);
[Fact] public void Approve_pending_sets_approved_and_raises_event() { /* Status Approved, ApproverId set, last event is LeaveRequestApprovedDomainEvent */ }
[Fact] public void Approve_twice_fails_with_not_pending() { /* second Approve → Error == LeaveRequestErrors.NotPending */ }
```
Validator: one test per rule (`EmployeeId` empty, `Reason` empty, `Reason` 501 chars, `EndDate < StartDate`) using `TestValidate(...).ShouldHaveValidationErrorFor(...)`, plus `Valid_command_passes`.
Submit handler (NSubstitute repo + uow, `FakeTimeProvider`): `Returns_new_id_adds_and_saves_once`, and `Invalid_range_returns_error_and_does_not_save`.
Approve handler: `Unknown_id_returns_not_found` (code `leave.not_found`), `Pending_request_is_approved_and_saved`, and `Already_approved_returns_not_pending_and_does_not_save`.

- [ ] **Step 3: Run to verify failure.**
- [ ] **Step 4: Implement the Interfaces above.**
- [ ] **Step 5: Run tests** → pass.
- [ ] **Step 6: Commit** → `git commit -m "feat(sample): add leave request domain and handlers"`

---

### Task 11: Sample module: persistence, endpoints, end-to-end

**Files:**
- Create: `src/Modules/Sample/TemplateName.Modules.Sample/Infrastructure/Persistence/{SampleDbContext.cs, LeaveRequestConfiguration.cs, LeaveRequestRepository.cs, Migrations/*}`
- Create: `.../Application/LeaveRequests/GetById/GetLeaveRequestByIdQueryHandler.cs` (Dapper)
- Create: `.../Endpoints/LeaveRequestEndpoints.cs`, `.../SampleModule.cs`
- Modify: `Program.cs` (`AddSampleModule`, `api.MapSampleEndpoints()`), `IntegrationTestWebAppFactory` (register `RecordingLeaveSubmittedHandler`)
- Create: `tests/TemplateName.IntegrationTests/Sample/RecordingLeaveSubmittedHandler.cs` (`IDomainEventHandler<LeaveRequestSubmittedDomainEvent>` writing to `EventRecorder`)
- Test: `tests/TemplateName.IntegrationTests/Sample/LeaveRequestEndpointTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3, 4, 8, 9 and 10
- Produces:
  - `public static class SampleModule`: `public static IServiceCollection AddSampleModule(this IServiceCollection services)` (calls `AddModuleDbContext<SampleDbContext>("sample")`, `AddOutbox<SampleDbContext>(assembly)`, `AddApplicationHandlers(assembly)`, repository and `IUnitOfWork` → `SampleDbContext`) and `public static IEndpointRouteBuilder MapSampleEndpoints(this IEndpointRouteBuilder app)`
  - `internal sealed class SampleDbContext : DbContext, IUnitOfWork`: default schema `sample`, `ApplyOutbox()`, `ApplySoftDeleteQueryFilters()`, `ApplyDefaultConventions()`
  - Table `sample.LeaveRequests`: `Reason nvarchar(500)`, `Status tinyint`, `StartDate`/`EndDate date`, `RowVersion rowversion` (concurrency token), index on `(EmployeeId)`
  - Endpoints (group `sample/leave-requests`, tag `Sample`):
    - `POST /` body `SubmitLeaveRequestRequest(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string Reason)` → `201 Created`, `Location: /api/v1/sample/leave-requests/{id}`, body `{ "id": "<guid>" }`
    - `GET /{id:guid}` → `200 LeaveRequestResponse`
    - `POST /{id:guid}/approve` body `ApproveLeaveRequestRequest(Guid ApproverId)` → `204`
    - Failures → `result.ToProblem()`; every endpoint declares `.ProducesProblem(...)` for OpenAPI
  - The Dapper query **must** include `WHERE Id = @Id AND IsDeleted = 0` (review 2.6). If the Dapper version can't map `DateOnly`, register a `SqlMapper.TypeHandler<DateOnly>` in Infrastructure.Common.

- [ ] **Step 1: Write the failing tests** (`LeaveRequestEndpointTests : IntegrationTestBase`; `Valid()` builds a valid request body)

```csharp
[Fact] public async Task Submit_returns_201_with_location_and_id() { /* Location == $"/api/v1/sample/leave-requests/{id}" */ }
[Fact] public async Task Get_returns_submitted_request() { /* dates equal input; "status" == "Pending"; "createdAt" ends with "Z" */ }
[Fact] public async Task Get_unknown_returns_404_leave_not_found() { /* code leave.not_found; traceId == X-Trace-Id */ }
[Fact] public async Task Submit_invalid_returns_400_with_camel_case_errors() { /* Reason "" → errors.reason present; code validation.failed */ }
[Fact] public async Task Approve_twice_returns_409_leave_not_pending() { /* first 204, second 409 code leave.not_pending */ }
[Fact] public async Task Malformed_json_returns_400_problem_details()
{ /* POST raw "{\"employeeId\": \"not-a-guid\"" with application/json → 400, application/problem+json, code request.malformed, traceId */ }
[Fact] public async Task Unhandled_exception_returns_500_without_stack_trace()
{ /* WithWebHostBuilder → ConfigureTestServices: RemoveAll<IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse>>, add a throwing one;
     GET → 500, code server.unexpected_error, body does not contain "   at " */ }
[Fact] public async Task Soft_deleted_request_is_not_returned()
{ /* submit; resolve SampleDbContext, Remove + SaveChanges; GET → 404 */ }
[Fact] public async Task Submitted_event_is_dispatched_through_outbox()
{ /* submit; resolve OutboxDispatcher<SampleDbContext>; ProcessBatchAsync ≥ 1; recorder holds LeaveRequestSubmittedDomainEvent with the id */ }
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement the DbContext, configuration, repository, query handler, endpoints and module registration.**
- [ ] **Step 4: Generate the migration**

Run: `dotnet ef migrations add InitialSample --project src/Modules/Sample/TemplateName.Modules.Sample --startup-project src/Host/TemplateName.Api --context SampleDbContext --output-dir Infrastructure/Persistence/Migrations`
Expected: the migration creates `sample.LeaveRequests`, `sample.OutboxMessages`, `sample.OutboxMessageConsumers` and `sample.__EFMigrationsHistory`. Review it for indexes and column types against Global Constraints.

- [ ] **Step 5: Run all tests** → pass.
- [ ] **Step 5b: Module documentation.** Create `docs/modules/_template.md`: the required headings (`## Purpose and boundaries`, `## Endpoints`, `## Domain model`, `## Error codes`, `## Events`, `## Configuration`, `## Data`, `## Background processing`, `## Observability`, `## Testing`), each with a one-line instruction comment and a table skeleton where relevant. The Endpoints table has `Method | Route | Purpose | Success | Errors`; the Error codes table has `Code | HTTP | Message (en)`. Copy it to `docs/modules/sample.md` and fill it in:
  - the three endpoints written as `POST /api/v1/sample/leave-requests` etc.
  - the `LeaveRequest` state diagram (Mermaid `stateDiagram-v2`: Pending → Approved)
  - error codes `leave.invalid_date_range`, `leave.not_pending`, `leave.not_found`
  - domain events
  - schema `sample`, its tables and migration `InitialSample`
  - the outbox handler
  - test locations
  - a link to the changelog filtered by scope `sample`

  Later tasks touching Sample (12, 15, 16) update this file.
- [ ] **Step 6: Commit** → `git commit -m "feat(sample): add leave request persistence and endpoints"`

---

### Task 12: Idempotency

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Infrastructure.Common/Idempotency/{PlatformDbContext.cs, IdempotencyRecord.cs, IdempotencyOptions.cs, IdempotencyMiddleware.cs, IdempotencyEndpointExtensions.cs, Migrations/*}`
- Modify: `InfrastructureServiceCollectionExtensions.cs` (`AddModuleDbContext<PlatformDbContext>("platform")`, options), `Program.cs` (slot 11), `LeaveRequestEndpoints.cs` (Submit `.WithIdempotency()`)
- Test: `tests/TemplateName.IntegrationTests/Idempotency/IdempotencyTests.cs`

**Interfaces:**
- Consumes: `AddModuleDbContext`, `ICurrentUser`, `TimeProvider`
- Produces (namespace `TemplateName.Infrastructure.Common.Idempotency`):
  - `IdempotencyRecord`: `string Scope` (max 100; `UserId` or `"anonymous"`), `string Key` (max 100), `byte[] RequestHash` (32), `int? StatusCode` (null = in progress), `string? ContentType`, `byte[]? ResponseBody`, `string? Location`, `DateTime CreatedAt`, `DateTime ExpiresAt`. PK `(Scope, Key)`, table `platform.IdempotencyKeys`.
  - `IdempotencyOptions` (section `Idempotency`): `TimeSpan TimeToLive = 1.00:00:00`
  - `public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder`: adds `IdempotentEndpointMetadata`
  - `public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)`
  - Middleware behaviour, only for endpoints with the metadata and an `Idempotency-Key` header:
    1. Key length outside 1–100 → 400 `idempotency.invalid_key`
    2. Hash = SHA-256 of method + path + raw body (enable buffering)
    3. If an unexpired record exists: same hash and completed → replay the stored status, content type, `Location` and body with header `Idempotency-Replayed: true`; same hash and in progress → 409 `idempotency.in_progress`; different hash → 422 `idempotency.key_reused`. An expired record is deleted and treated as absent.
    4. Otherwise insert an in-progress record; a unique-key violation on insert → 409 `idempotency.in_progress`
    5. Run the pipeline with the response body captured. Status < 500 → store the response; status ≥ 500 or an exception → delete the record (the client may retry).

- [ ] **Step 1: Write the failing tests** (POST the Sample submit endpoint)

```csharp
[Fact] public async Task Same_key_same_body_replays_response()
{ /* two posts, same key → both 201, same Location, second has Idempotency-Replayed: true; one LeaveRequests row */ }
[Fact] public async Task Same_key_different_body_returns_422() { /* code idempotency.key_reused */ }
[Fact] public async Task Requests_without_key_execute_each_time() { /* two rows */ }
[Fact] public async Task Invalid_key_returns_400() { /* 101-char key → code idempotency.invalid_key */ }
[Fact] public async Task Expired_key_executes_again() { /* advance Factory.Time 24h + 1s → new 201 with a different Location */ }
[Fact] public async Task Concurrent_requests_with_same_key_execute_once()
{ /* Task.WhenAll of 5 posts → exactly one LeaveRequests row; every status ∈ {201, 409} */ }
[Fact] public async Task Server_error_is_not_stored()
{ /* host A (WithWebHostBuilder, throwing ICommandHandler<SubmitLeaveRequestCommand, Guid>): post key K → 500; default host: post K → 201 */ }
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement**, then generate the migration: `dotnet ef migrations add InitialPlatform --project src/BuildingBlocks/TemplateName.Infrastructure.Common --startup-project src/Host/TemplateName.Api --context PlatformDbContext --output-dir Idempotency/Migrations`
- [ ] **Step 4: Run all tests** → pass (Respawn picks up the `platform` tables automatically).
- [ ] **Step 5: Commit** → `git commit -m "feat(idempotency): add Idempotency-Key handling"`

---

### Task 13: Architecture tests

**Files:**
- Create: `tests/TemplateName.ArchitectureTests/{Assemblies.cs, LayeringTests.cs, ModuleBoundaryTests.cs, ConventionTests.cs}`

**Interfaces:**
- Consumes: the assemblies of every `src` project. `Assemblies.Modules` is a static list (`typeof(SampleModule).Assembly`); adding a module means adding one line.

- [ ] **Step 1: Create the project** (classlib + `NetArchTest.Rules`, referencing all src projects and the Api) and add it to the slnx.

- [ ] **Step 2: Write the tests.** Each asserts `result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []))`:
  - `SharedKernel_has_no_framework_or_project_dependencies`: no dependency on `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `TemplateName.Application`, `TemplateName.Infrastructure`, `TemplateName.Web`
  - `Application_common_does_not_depend_on_infrastructure_or_web`
  - `Module_domain_does_not_depend_on_other_layers_or_frameworks` (per module: namespace `*.Domain` must not depend on `*.Application`, `*.Infrastructure`, `*.Endpoints`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`)
  - `Module_application_does_not_depend_on_infrastructure_or_endpoints` (Dapper allowed)
  - `Modules_do_not_reference_other_modules_except_contracts` (assembly references of each module matching `TemplateName.Modules.*` must end in `.Contracts`)
  - `Handlers_are_sealed_and_not_public` (types implementing the four handler interfaces)
  - `Domain_events_are_sealed_and_named_DomainEvent`
  - `Module_types_are_internal_except_the_module_entry_point` (public types in a module assembly: only `*Module` classes and EF `Migration`/`ModelSnapshot` types)
  - **Naming (`NamingConventionTests.cs`, conventions doc Sections 1–4):**
    - `Commands_end_with_Command`, `Queries_end_with_Query`, `Validators_end_with_Validator` (`AbstractValidator<>` subclasses)
    - `Handlers_end_with_Handler` (all four handler interfaces)
    - `Domain_events_end_with_DomainEvent` (already covered above; keep a single test)
    - `Repositories_end_with_Repository`, `DbContexts_end_with_DbContext`, `Entity_configurations_end_with_Configuration` (`IEntityTypeConfiguration<>` implementers)
    - `No_type_uses_a_forbidden_suffix` (`Manager`, `Helper`, `Helpers`, `Util`, `Utils`, `Utility`, `Impl`, `Dto`, `Settings`, `Info`), over all `TemplateName.*` src assemblies
    - `Task_returning_methods_end_with_Async`: declared, non-compiler-generated methods in src assemblies returning `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` or `IAsyncEnumerable<T>` must end in `Async`. Exclude overrides and interface implementations of non-`TemplateName` types, and `Program`'s entry point.
    - `Cancellation_token_parameter_is_last_and_named_cancellationToken` (src assemblies; skip compiler-generated methods)

- [ ] **Step 3: Run** `dotnet test tests/TemplateName.ArchitectureTests` → all pass. Confirm each rule can fail by temporarily making `LeaveRequestSubmittedDomainEventHandler` public (expect a failure), then revert.
- [ ] **Step 4: Commit** → `git commit -m "test(architecture): enforce layering, boundaries and naming"`

---

### Task 14: Dev environment, CI and template smoke test

**Files:**
- Create: `docker-compose.yml`, `.env.example`, `README.md`, `coverage.runsettings`
- Create: `.github/workflows/ci.yml`, `.github/dependabot.yml`, `.github/pull_request_template.md`
- Create: `.template.config/template.json`, `build/scripts/template-smoke.sh`

**Interfaces:** none (delivery and configuration only).

- [ ] **Step 1: Local environment.** `docker-compose.yml` services:
  - `sqlserver`: `mcr.microsoft.com/mssql/server:2022-latest`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD=${SQL_SA_PASSWORD:?set in .env}`, port 1433, named volume
  - `aspire-dashboard`: `mcr.microsoft.com/dotnet/aspire-dashboard:latest`, `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`, ports `18888:18888`, `4317:18889`

  `.env.example` contains `SQL_SA_PASSWORD=ChangeMe_LocalOnly_1`.
  The README quick start: copy `.env.example` to `.env` → `docker compose up -d` → `dotnet user-secrets set "ConnectionStrings:Database" "Server=localhost,1433;Database=TemplateName;User Id=sa;Password=<from .env>;TrustServerCertificate=True" --project src/Host/TemplateName.Api` → `dotnet run --project src/Host/TemplateName.Api` → Scalar at `/scalar/v1`, dashboard at `http://localhost:18888`. Also document prerequisites, the GitHub branch-protection settings from blueprint 6 (PR required, CI green, one approval, no force push), and links to the blueprint and review.
  Verify: `docker compose config` exits 0, then `docker compose up -d`, run the API, and `curl -f http://localhost:<port>/health/ready` returns 200.

- [ ] **Step 2: Coverage settings.** `coverage.runsettings` (coverlet collector, `Format=cobertura`) with `Include = [TemplateName.SharedKernel]*,[TemplateName.Application.Common]*,[TemplateName.Modules.*]TemplateName.Modules.*.Domain.*,[TemplateName.Modules.*]TemplateName.Modules.*.Application.*`.

- [ ] **Step 3: Template definition.** `.template.config/template.json`: `identity` `Modulith.Backend.Templates`, `name` `Modulith Backend`, `shortName` `modulith-backend`, `author` = the owning team or organization (no personal names), `classifications` `["Web", "API", "Modular Monolith", "Clean Architecture"]`, `sourceName` `TemplateName`, `preferNameDirectory` true, `tags` `{ language: C#, type: solution }`, **no switches yet**. Exclude `**/bin/**`, `**/obj/**`, `.git/**`, `.vs/**`, `**/*.user`, `**/TestResults/**`, `coverage/**`, `.env`, `docs/superpowers/**`, `BACKEND_TEMPLATE_BLUEPRINT.md`, `docs/blueprint-review.md`.

- [ ] **Step 4: Smoke script.** `build/scripts/template-smoke.sh` (`set -euo pipefail`) does the following in order:
  1. `dotnet new install . --force`
  2. generates `dotnet new modulith-backend -n Acme.Smoke -o "$tmp/Acme.Smoke"`
  3. fails if `grep -rI "TemplateName" "$tmp/Acme.Smoke" --exclude-dir={bin,obj}` finds anything
  4. `dotnet build "$tmp/Acme.Smoke" -c Release`
  5. `dotnet test` on the generated unit and architecture projects
  6. `dotnet new uninstall .` in a `trap`

  Run: `bash build/scripts/template-smoke.sh` → exits 0.

- [ ] **Step 5: CI.** `.github/workflows/ci.yml`: triggers `pull_request` and `push` on `main` plus `workflow_dispatch`; `concurrency` cancels in-progress runs; `permissions: contents: read, pull-requests: write, checks: write`.
  - Job `build-test` (ubuntu-latest, 30 min): checkout → setup-dotnet (`global-json-file`, NuGet cache on `**/packages.lock.json`) → `dotnet restore --locked-mode` (NuGet Audit fails here) → `dotnet format --verify-no-changes --no-restore` → `dotnet build --no-restore -c Release` → `dotnet test --no-build -c Release --settings coverage.runsettings --logger trx --results-directory TestResults` (unit, architecture, integration; Docker is available on the runner) → ReportGenerator → coverage gate **80 % line** → sticky PR comment → `dorny/test-reporter` (if `always()`).
  - Job `template-smoke` runs `bash build/scripts/template-smoke.sh`.
  - Job `commit-lint` (only on `pull_request`; trigger types `opened`, `edited`, `synchronize`) runs `bash build/scripts/check-commit-msg.sh --message "$PR_TITLE"` with `env: PR_TITLE: ${{ github.event.pull_request.title }}`. **Pass the title through `env`, never inline `${{ }}` in `run:`**, because an inline expression lets a crafted PR title inject shell commands.
  - Use the latest major tag of each action (Dependabot updates them). Add a comment noting that production forks should pin full SHAs.

  `dependabot.yml`: `nuget` (minor/patch grouped) and `github-actions`, weekly. `pull_request_template.md`: the checklist from blueprint 13.4 plus "Names, files and commits follow `docs/coding-conventions.md` / `CONTRIBUTING.md`". The README links both documents and lists `git config core.hooksPath .githooks` in the quick start.

- [ ] **Step 6: Final verification**

Run: `dotnet format --verify-no-changes`, then `dotnet build -c Release`, then `dotnet test -c Release --settings coverage.runsettings`.
Expected: 0 warnings, all tests pass, and coverage on the included namespaces is ≥ 80 %. Then `bash build/scripts/template-smoke.sh` exits 0.

- [ ] **Step 7: Commit** → `git commit -m "chore: add local environment, CI and template smoke test"`
- [ ] **Step 8: Tighten branch protection (phase 1)** after this task's PR is merged and CI has run once on `main`: with the user's approval, run `bash build/scripts/configure-github-repo.sh --checks build-test,template-smoke,commit-lint`. Confirm in the PR settings that the three checks show as required.

---

### Task 15: Internationalization (i18n)

Implements review Section 8 (I1–I10). It comes last because it changes the shared error contract after every code path that produces errors exists, and its tests then cover all of them.

**Files:**
- Modify: `src/BuildingBlocks/TemplateName.SharedKernel/Error.cs` (add `Parameters`)
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Localization/{IErrorMessageLocalizer.cs, ErrorMessageLocalizer.cs, ErrorMessageSource.cs, LocalizationServiceCollectionExtensions.cs}`
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Localization/{ApiLocalizationOptions.cs, ApiLocalizationExtensions.cs, ValidationLanguageManager.cs}`
- Create: resources (marker class + neutral/`ms`/`zh-Hans` `.resx`, same folder and base name so the SDK's `DependentUpon` convention names the manifest resource after the class):
  - `src/BuildingBlocks/TemplateName.Web.Common/Resources/CommonErrorMessages{.cs,.resx,.ms.resx,.zh-Hans.resx}`
  - `src/BuildingBlocks/TemplateName.Infrastructure.Common/Resources/InfrastructureErrorMessages{.cs,.resx,.ms.resx,.zh-Hans.resx}`
  - `src/Modules/Sample/TemplateName.Modules.Sample/Resources/SampleErrorMessages{.cs,.resx,.ms.resx,.zh-Hans.resx}`
- Modify: `Web.Common/Results/ResultExtensions.cs` (`params` extension), `WebCommonServiceCollectionExtensions.cs` (localize `detail`, register common source), `Errors/GlobalExceptionHandler.cs` (dev exception text moves to `exceptionDetails`), `InfrastructureServiceCollectionExtensions.cs`, `SampleModule.cs`, `Domain/LeaveRequests/LeaveRequestErrors.cs` (`NotFound` carries `id`), `Program.cs` (slot 1a + services), `appsettings.json` (`Localization` section)
- Create: `docs/adr/0009-internationalization.md`
- Modify: `tests/TemplateName.ArchitectureTests/Assemblies.cs` (add `ErrorMessageResources`)
- Test: `tests/TemplateName.UnitTests/Localization/{ErrorMessageLocalizerTests.cs, ApiLocalizationOptionsTests.cs}`, update `tests/TemplateName.UnitTests/Web/GlobalExceptionHandlerTests.cs`, `tests/TemplateName.ArchitectureTests/TranslationTests.cs`, `tests/TemplateName.IntegrationTests/Localization/LocalizationTests.cs`

**Interfaces:**
- Consumes: `Error`, `ToProblem`, `AddWebCommon`'s `CustomizeProblemDetails`, `GlobalExceptionHandler`, `ValidationDecorator`, `Assemblies` (Task 13)
- Produces:
  - `Error`: add `public IReadOnlyDictionary<string, object?>? Parameters { get; init; }` (default `null`). Because records compare dictionaries by reference, tests compare errors that carry parameters by `Code`.
  - `LeaveRequestErrors.NotFound(Guid id)` → `Error.NotFound("leave.not_found", $"Leave request '{id}' was not found.") with { Parameters = new Dictionary<string, object?> { ["id"] = id } }`
  - `ToProblem()`: adds extension `params` when `Parameters` is non-empty
  - `TemplateName.Application.Common.Localization.IErrorMessageLocalizer { string Localize(string code, IReadOnlyDictionary<string, object?>? parameters, string fallback); }`: uses `CultureInfo.CurrentUICulture`. It asks each registered source in registration order and the first with `ResourceNotFound == false` wins. It replaces `{name}` placeholders with `Convert.ToString(value, CultureInfo.CurrentCulture)` and leaves unknown placeholders untouched. If no source has the key, it returns `fallback`.
  - `public static IServiceCollection AddErrorMessages<TResource>(this IServiceCollection services)`: `AddLocalization()` (no `ResourcesPath`), registers `ErrorMessageSource(typeof(TResource))`, and does `TryAddSingleton<IErrorMessageLocalizer, ErrorMessageLocalizer>()`. Called by `AddWebCommon` (`CommonErrorMessages`), `AddInfrastructureCommon` (`InfrastructureErrorMessages`) and `AddSampleModule` (`SampleErrorMessages`).
  - `CustomizeProblemDetails` (Task 4) additionally sets `detail = localizer.Localize(code, params, fallback: detail ?? title ?? "")`.
  - `GlobalExceptionHandler`: `detail` is always the localized `server.unexpected_error` / `request.malformed` message; in Development the exception text goes in extension `exceptionDetails`.
  - `TemplateName.Web.Common.Localization.ApiLocalizationOptions` (section `Localization`): `string DefaultCulture = "en"`, `string[] SupportedUICultures = ["en", "ms", "zh-Hans"]`. Validation fails if `DefaultCulture` isn't in the list, or if any name throws from `CultureInfo.GetCultureInfo(name, predefinedOnly: true)`.
  - `public static RequestLocalizationOptions CreateRequestLocalizationOptions(ApiLocalizationOptions settings)`: `DefaultRequestCulture = new(DefaultCulture)`, `SupportedCultures = [DefaultCulture]`, `SupportedUICultures = SupportedUICultures`, `RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()]`, `FallBackToParentUICultures = true`, `ApplyCurrentCultureToResponseHeaders = true`
  - `public static IServiceCollection AddApiLocalization(this IServiceCollection services, IConfiguration configuration)`: settings with `ValidateOnStart`; configures `RequestLocalizationOptions` from the factory above; sets `ValidatorOptions.Global.LanguageManager = new ValidationLanguageManager()` (one-time global FluentValidation config, documented in the ADR)
  - `public static WebApplication UseApiLocalization(this WebApplication app)`: sets `CultureInfo.DefaultThreadCurrentCulture` and `DefaultThreadCurrentUICulture` to `DefaultCulture` (I7), then `UseRequestLocalization()`
  - `ValidationLanguageManager : LanguageManager`: adds `ms` (and `zh-Hans` if missing) translations for every FluentValidation validator the solution uses (at minimum `NotEmptyValidator`, `MaximumLengthValidator`, `GreaterThanOrEqualValidator`), keeping FluentValidation's `{PropertyName}`-style placeholders
  - Neutral resource keys (English text):
    - `CommonErrorMessages`: `validation.failed`, `request.malformed`, `server.unexpected_error`, `rate_limit.exceeded`, `http.400`, `http.401`, `http.403`, `http.404`, `http.405`, `http.415`
    - `InfrastructureErrorMessages`: `concurrency.conflict`, `idempotency.invalid_key`, `idempotency.in_progress`, `idempotency.key_reused`
    - `SampleErrorMessages`: `leave.invalid_date_range`, `leave.not_pending`, `leave.not_found` (`"Leave request '{id}' was not found."`)
  - Each `.ms.resx` / `.zh-Hans.resx` entry gets a `<comment>Draft – needs native review</comment>` until reviewed.

- [ ] **Step 1: Write the failing unit tests**

`ErrorMessageLocalizerTests` (build a provider with `AddLogging().AddErrorMessages<SampleErrorMessages>()`; set `CultureInfo.CurrentUICulture` inside each test):
```csharp
[Fact] public void Returns_translation_with_parameters_substituted()
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ms");
    var id = Guid.NewGuid();
    var text = Localizer.Localize("leave.not_found", new Dictionary<string, object?> { ["id"] = id }, "fallback");
    text.ShouldContain(id.ToString());
    text.ShouldNotBe($"Leave request '{id}' was not found.");   // not the English text
}
[Fact] public void Chinese_region_falls_back_to_zh_Hans() { /* zh-CN result == zh-Hans result for leave.not_pending */ }
[Fact] public void Unknown_code_returns_fallback() => Localizer.Localize("nope.missing", null, "fb").ShouldBe("fb");
[Fact] public void Unknown_placeholder_is_left_untouched() { /* en leave.not_found with no parameters contains "{id}" */ }
```
`ApiLocalizationOptionsTests`:
```csharp
[Fact] public void Only_ui_culture_varies()
{
    var o = ApiLocalizationExtensions.CreateRequestLocalizationOptions(new ApiLocalizationOptions());
    o.SupportedCultures!.Select(c => c.Name).ShouldBe(new[] { "en" });
    o.SupportedUICultures!.Select(c => c.Name).ShouldBe(new[] { "en", "ms", "zh-Hans" });
    o.RequestCultureProviders.Single().ShouldBeOfType<AcceptLanguageHeaderRequestCultureProvider>();
}
[Fact] public void Default_culture_outside_supported_list_fails_validation() { /* DefaultCulture = "fr" → DataAnnotations/IValidateOptions failure */ }
```
Update `GlobalExceptionHandlerTests.Development_includes_exception_detail` to assert the `exceptionDetails` extension instead of `detail`.

- [ ] **Step 2: Write the failing architecture tests** (`TranslationTests`; `Assemblies.ErrorMessageResources = [typeof(CommonErrorMessages), typeof(InfrastructureErrorMessages), typeof(SampleErrorMessages)]`; read with `new ResourceManager(type.FullName!, type.Assembly).GetResourceSet(culture, createIfNotExists: true, tryParents: false)`)
  - `Every_translation_has_the_same_keys_as_the_neutral_resource`: for `ms` and `zh-Hans`, the key set equals the neutral key set; the failure message lists missing and extra keys per resource
  - `Translations_use_the_same_placeholders`: the `{name}` set of every translated value equals the neutral value's set
  - `Every_module_error_code_has_a_neutral_message`: for each module assembly, collect `Error` codes from static fields and properties of classes named `*Errors`, plus static methods returning `Error` (invoked with `default` arguments); each code must be a key in that assembly's `*ErrorMessages` neutral resource

- [ ] **Step 3: Write the failing integration tests** (`LocalizationTests : IntegrationTestBase`; `Get(url, lang)` sends `Accept-Language`)
```csharp
[Fact] public async Task Malay_request_gets_malay_detail_and_unchanged_code()
{ /* GET unknown leave id with "ms" → 404; code == "leave.not_found"; params.id == id; detail != English text and contains id;
     Content-Language header == "ms" */ }
[Fact] public async Task Chinese_region_gets_simplified_chinese() { /* "zh-CN,zh;q=0.9" → Content-Language "zh-Hans" */ }
[Fact] public async Task Unsupported_language_falls_back_to_english() { /* "fr-FR" → English detail, Content-Language "en" */ }
[Fact] public async Task Malformed_accept_language_falls_back_to_english() { /* "!!!;q=abc" → 404 (not 500), English detail */ }
[Fact] public async Task Exception_handler_responses_are_localized() { /* malformed JSON POST with "ms" → 400 request.malformed, detail is the ms text */ }
[Fact] public async Task Validation_messages_are_localized() { /* submit with Reason "" and "ms" → errors.reason[0] differs from the "en" response's errors.reason[0] */ }
[Fact] public async Task Enum_values_are_not_localized() { /* GET submitted request with "ms" → status == "Pending" */ }
[Fact] public void Background_default_culture_is_english()
{ /* after host start: CultureInfo.DefaultThreadCurrentUICulture!.Name == "en" and DefaultThreadCurrentCulture!.Name == "en" */ }
```

- [ ] **Step 4: Run to verify failure** → unit, architecture and integration projects fail to compile or fail their assertions.

- [ ] **Step 5: Implement the Interfaces above and write the three resource sets** (English neutral text, then `ms` and `zh-Hans` drafts with the review comment).

- [ ] **Step 6: Run all tests** → pass. Also confirm `dotnet build -c Release` emits satellite assemblies `ms/` and `zh-Hans/` next to the Api output.

- [ ] **Step 7: Write ADR 0009** (review Section 8). Add a README section, "Adding a language / adding an error message": add the resx key in all languages, and the architecture tests enforce it. Add a release-checklist line saying `ms`/`zh-Hans` strings need native review. Re-run `bash build/scripts/template-smoke.sh` → exits 0 (satellite resources survive renaming).

- [ ] **Step 8: Commit** → `git commit -m "feat(i18n): localize problem details in en, ms and zh-Hans"`

---

### Task 16: Cursor pagination

Implements review Section 9.3 (P1–P8) and adds a paged list endpoint to the Sample module.

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Pagination/{CursorPageRequest.cs, CursorPage.cs, CursorDirection.cs, SortField.cs, SortSpecification.cs, Cursor.cs, CursorCodec.cs, KeysetQuery.cs, KeysetSqlBuilder.cs, CursorPageBuilder.cs, PaginationErrors.cs}`
- Create: `src/Modules/Sample/TemplateName.Modules.Sample/Application/LeaveRequests/List/{ListLeaveRequestsQuery.cs, ListLeaveRequestsQueryValidator.cs, ListLeaveRequestsQueryHandler.cs, LeaveRequestSortFields.cs, LeaveRequestListItemResponse.cs}`
- Modify: `.../Endpoints/LeaveRequestEndpoints.cs` (GET `/`), `.../Infrastructure/Persistence/LeaveRequestConfiguration.cs` (indexes) + new migration `AddLeaveRequestListIndexes`
- Modify: `src/BuildingBlocks/TemplateName.Web.Common/Resources/CommonErrorMessages*.resx` (`pagination.*` keys in en/ms/zh-Hans)
- Create: `docs/adr/0010-cursor-pagination.md`
- Test: `tests/TemplateName.UnitTests/Pagination/{SortSpecificationTests.cs, CursorCodecTests.cs, KeysetSqlBuilderTests.cs}`, `tests/TemplateName.IntegrationTests/Sample/ListLeaveRequestsTests.cs`

**Interfaces:**
- Consumes: `Result<T>`, `Error` (with `Parameters`), `IQuery<T>`/`IQueryHandler<,>`, `IDbConnectionFactory`, `ToProblem`, i18n resources (Task 15)
- Produces (namespace `TemplateName.Application.Common.Pagination`):
  - `public sealed record CursorPageRequest(int PageSize = CursorPageRequest.DefaultPageSize, string? Cursor = null, string? Sort = null, bool IncludeTotalCount = false)` with `public const int DefaultPageSize = 20, MaxPageSize = 100`
  - `public sealed record CursorPage<T>(IReadOnlyList<T> Items, int PageSize, string? NextCursor, string? PreviousCursor, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalCount)`
  - `public enum CursorDirection { Next = 1, Previous = 2 }`
  - `public sealed record SortField(string Name, string Column, Type ValueType)`: `Name` is the camelCase API name; `Column` is a bracketed SQL column defined in code (e.g. `"[CreatedAt]"`); the column must be non-nullable
  - `public sealed class SortSpecification`: `static Result<SortSpecification> Parse(string? sort, IReadOnlyList<SortField> allowedFields, SortField idField, string defaultSort)`. Terms are comma-separated, with `-` meaning descending. An unknown or duplicate field returns `PaginationErrors.InvalidSort(allowedNames)`. It always appends `idField` with the direction of the last term. It exposes `IReadOnlyList<(SortField Field, bool Descending)> Terms` and `string Signature` (canonical, e.g. `"-createdAt,-id"`).
  - `public static class CursorCodec`: `string Encode(Cursor cursor)` (base64url of JSON `{ "v":1, "d":…, "s":signature, "f":filterHash, "k":[values] }`) and `Result<Cursor> Decode(string token, SortSpecification sort, string filterHash)`. A malformed token, one longer than 1024 characters, a wrong version, or values that don't convert to each field's `ValueType` returns `PaginationErrors.InvalidCursor`. A signature or filter-hash mismatch returns `PaginationErrors.CursorMismatch`. `DateTime` values are written in round-trip format (`"O"`) and read back with `DateTimeKind.Utc`.
  - `public sealed record Cursor(CursorDirection Direction, string SortSignature, string FilterHash, IReadOnlyList<object> KeyValues)`
  - `public static string ComputeFilterHash(string canonicalFilter)`: the first 16 bytes of SHA-256, hex
  - `public sealed record KeysetQuery(string WhereClause, string OrderByClause, DynamicParameters Parameters, int Take, bool IsBackward)`
  - `public static class KeysetSqlBuilder { public static KeysetQuery Build(SortSpecification sort, Cursor? cursor, int pageSize); }`: `Take = pageSize + 1`. `WhereClause` is `"1 = 1"` with no cursor, otherwise the expanded row comparison `((c1 ⋚ @k0) OR (c1 = @k0 AND c2 ⋚ @k1) …)`, where `⋚` is `<` for descending and `>` for ascending, flipped when `IsBackward`. `OrderByClause` is flipped when backward. Column text comes **only** from `SortField.Column`.
  - `public static class CursorPageBuilder { public static CursorPage<T> Build<T>(IReadOnlyList<T> fetchedRows, KeysetQuery query, Cursor? incomingCursor, SortSpecification sort, string filterHash, Func<T, IReadOnlyList<object>> keySelector, long? totalCount); }`: trims the extra row and re-reverses backward pages. Forward: `NextCursor` is set when an extra row existed; `PreviousCursor` is set when a cursor was supplied. Backward: `PreviousCursor` is set when an extra row existed; `NextCursor` is always set.
  - `public static class PaginationErrors`: `InvalidCursor` (Validation, `pagination.invalid_cursor`), `CursorMismatch` (Validation, `pagination.cursor_mismatch`), `static Error InvalidSort(IEnumerable<string> allowed)` (Validation, `pagination.invalid_sort`, parameter `allowed` = comma-joined names)
- Sample:
  - `sealed record ListLeaveRequestsQuery(Guid? EmployeeId, LeaveRequestStatus? Status, CursorPageRequest Page) : IQuery<CursorPage<LeaveRequestListItemResponse>>`. The validator checks `Page.PageSize` is in 1–100.
  - `sealed record LeaveRequestListItemResponse(Guid Id, Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, LeaveRequestStatus Status, DateTime CreatedAt)`
  - `static class LeaveRequestSortFields`: `CreatedAt` (`"createdAt"`, `"[CreatedAt]"`, `DateTime`), `StartDate` (`"startDate"`, `"[StartDate]"`, `DateOnly`), `Id` (`"id"`, `"[Id]"`, `Guid`); `Allowed = [CreatedAt, StartDate]`; `Default = "-createdAt"`
  - Handler SQL: `SELECT TOP (@Take) … FROM [sample].[LeaveRequests] WHERE IsDeleted = 0 [AND EmployeeId = @EmployeeId] [AND Status = @Status] AND {WhereClause} ORDER BY {OrderByClause}`. When `IncludeTotalCount`, it also runs `SELECT COUNT_BIG(*)` with the same filters and no keyset. The canonical filter string is `employeeId={…};status={…}`.
  - Endpoint: `GET /api/v1/sample/leave-requests` with `[AsParameters]` `ListLeaveRequestsRequest(Guid? EmployeeId, LeaveRequestStatus? Status, int? PageSize, string? Cursor, string? Sort, bool? IncludeTotalCount)` → `200 CursorPage<LeaveRequestListItemResponse>`
  - Indexes: replace `IX_LeaveRequests_EmployeeId` with `(EmployeeId, CreatedAt, Id)`, and add `(CreatedAt, Id)` and `(StartDate, Id)`

- [ ] **Step 1: Write the failing unit tests**

```csharp
// SortSpecificationTests
[Fact] public void Appends_id_tiebreaker_with_last_direction()
    => SortSpecification.Parse("-createdAt", Allowed, IdField, "-createdAt").Value.Signature.ShouldBe("-createdAt,-id");
[Fact] public void Empty_sort_uses_default() { /* Parse(null, …) → Signature "-createdAt,-id" */ }
[Fact] public void Unknown_field_is_rejected() { /* "reason" → Error.Code "pagination.invalid_sort", Parameters["allowed"] == "createdAt,startDate" */ }
[Fact] public void Duplicate_field_is_rejected() { /* "createdAt,-createdAt" → pagination.invalid_sort */ }

// CursorCodecTests
[Fact] public void Round_trips_utc_datetime_with_millisecond_precision_and_guid()
{ /* encode KeyValues [new DateTime(2026,1,1,8,30,0,123, DateTimeKind.Utc), guid] → decode → equal values, Kind == Utc */ }
[Fact] public void Garbage_token_is_invalid() { /* "abc", base64url("{}"), a 2000-char token → pagination.invalid_cursor */ }
[Fact] public void Different_sort_signature_is_a_mismatch() { /* encoded with "-createdAt,-id", decoded with "startDate,id" → pagination.cursor_mismatch */ }
[Fact] public void Different_filter_hash_is_a_mismatch() { /* → pagination.cursor_mismatch */ }

// KeysetSqlBuilderTests
[Fact] public void No_cursor_produces_true_predicate_and_requested_order()
{ /* WhereClause "1 = 1"; OrderByClause "[CreatedAt] DESC, [Id] DESC"; Take == pageSize + 1 */ }
[Fact] public void Forward_descending_cursor_uses_less_than()
{ /* WhereClause == "(([CreatedAt] < @k0) OR ([CreatedAt] = @k0 AND [Id] < @k1))"; parameters k0, k1 set */ }
[Fact] public void Backward_cursor_flips_comparison_and_order() { /* ">" comparisons; "[CreatedAt] ASC, [Id] ASC"; IsBackward */ }
```

- [ ] **Step 2: Write the failing integration tests** (`ListLeaveRequestsTests : IntegrationTestBase`; `SeedAsync(n)` submits `n` requests, advancing `Factory.Time` 1 s between each unless told otherwise)

```csharp
[Fact] public async Task First_page_returns_items_and_next_cursor() { /* 25 seeded, pageSize 10 → 10 items newest first; nextCursor not null; previousCursor null; no totalCount property */ }
[Fact] public async Task Walking_forward_visits_every_item_exactly_once() { /* follow nextCursor until null → 3 pages, 25 distinct ids, strictly descending createdAt */ }
[Fact] public async Task Walking_backward_returns_the_previous_page() { /* page2.previousCursor → items equal page1 items in the same order */ }
[Fact] public async Task Items_inserted_after_first_page_do_not_cause_duplicates() { /* get page1; submit a new (newer) request; page2 shares no id with page1 */ }
[Fact] public async Task Ties_on_sort_value_are_broken_by_id() { /* seed 10 with the clock frozen (same CreatedAt); pageSize 3 walk → 10 distinct ids */ }
[Fact] public async Task Filters_and_sort_apply() { /* employeeId filter + sort=startDate → only that employee, ascending startDate */ }
[Fact] public async Task Cursor_reused_with_different_sort_returns_400_cursor_mismatch() { /* code pagination.cursor_mismatch */ }
[Fact] public async Task Tampered_cursor_returns_400_invalid_cursor() { /* cursor=abc → pagination.invalid_cursor */ }
[Fact] public async Task Unknown_sort_returns_400_invalid_sort_with_allowed_fields() { /* sort=reason → params.allowed == "createdAt,startDate" */ }
[Theory, InlineData(0), InlineData(101)] public async Task Out_of_range_page_size_returns_400(int pageSize) { /* code validation.failed, errors has "page.pageSize" or "pageSize" */ }
[Fact] public async Task Total_count_is_returned_only_when_requested() { /* includeTotalCount=true → totalCount == 25 */ }
[Fact] public async Task Soft_deleted_items_are_excluded() { /* soft-delete one via SampleDbContext → never appears while walking */ }
```

- [ ] **Step 3: Run to verify failure.**
- [ ] **Step 4: Implement the building blocks, then the Sample query, endpoint and indexes.** Generate the migration: `dotnet ef migrations add AddLeaveRequestListIndexes --project src/Modules/Sample/TemplateName.Modules.Sample --startup-project src/Host/TemplateName.Api --context SampleDbContext --output-dir Infrastructure/Persistence/Migrations`. Add the three `pagination.*` keys to `CommonErrorMessages` in all three languages (the Task 15 translation tests must stay green).
- [ ] **Step 5: Run all tests** → pass. In Scalar, confirm the list endpoint documents `pageSize`, `cursor`, `sort` and `includeTotalCount`.
- [ ] **Step 6: Write ADR 0010** (review Section 9: why not offset, why not DAB, the optional DAB reporting sidecar, index rule P7). Update the README with an "Adding a paged list endpoint" recipe: allow-list fields → indexes → handler using `KeysetSqlBuilder` and `CursorPageBuilder`.
- [ ] **Step 7: Commit** → `git commit -m "feat(sample): add cursor-paginated leave request list"`

---

### Task 17: Documentation set, changelog automation, licence compliance and generated-project files

Implements review Section 11. It comes last so the documents describe the finished baseline, and its tests stop them drifting from then on.

**Files:**
- Create: `docs/README.md`, `docs/architecture/overview.md`, `docs/services/api.md`, `docs/building-blocks/{shared-kernel.md, application-common.md, infrastructure-common.md, web-common.md}`
- Create: `build/template-content/{README.md, CHANGELOG.md, .release-please-manifest.json}` (`CLAUDE.md` exists from Task 1)
- Create: `release-please-config.json`, `.release-please-manifest.json`, `.github/workflows/release.yml`, `build/licenses/allowed-licenses.json`
- Modify: `README.md` (template repo version, final), `Directory.Build.props` (`<Version>0.1.0</Version> <!-- x-release-please-version -->`), `.template.config/template.json` (exclusions + content overrides), `.github/workflows/ci.yml` (licence job), `build/scripts/template-smoke.sh` (extra assertions)
- Test: `tests/TemplateName.ArchitectureTests/DocumentationTests.cs`, `tests/TemplateName.IntegrationTests/Documentation/EndpointDocumentationTests.cs`

**Interfaces:**
- Consumes: module assemblies and error-code reflection helper (Tasks 13, 15); `EndpointDataSource` from the running host (Task 5)
- Produces:
  - `static class RepositoryPaths { public static string Root { get; } }` (ArchitectureTests; IntegrationTests links the same file): walks up from `AppContext.BaseDirectory` to the directory containing `*.slnx`
  - **Template repo `README.md`** sections:
    - badges: CI (`https://github.com/jackyjiale-oss/modulith-backend/actions/workflows/ci.yml/badge.svg`) and MIT licence
    - what Modulith is: one paragraph and a feature list, linking review Sections 6–11
    - clone URL `https://github.com/jackyjiale-oss/modulith-backend.git`, plus links to `docs/repository-management.md` and `SECURITY.md`
    - prerequisites
    - quick start: `dotnet new install Modulith.Backend.Templates`, `dotnet new modulith-backend -n Acme.Hr`, then the generated README takes over
    - template options (none yet)
    - repository layout
    - docs index
    - contributing (CONTRIBUTING.md, commit hook)
    - licence (MIT badge + link)
  - **Generated `build/template-content/README.md`** (uses `TemplateName`) sections:
    - service overview placeholder (`> Describe what TemplateName does.`)
    - prerequisites
    - quick start (`.env`, `docker compose up -d`, user-secrets, `git config core.hooksPath .githooks`, `dotnet run`)
    - URLs (Scalar, Aspire dashboard)
    - modules table linking `docs/modules/*.md`
    - docs index, testing, conventions
    - "Generated from [Modulith Backend](https://github.com/jackyjiale-oss/modulith-backend) `<version>`" line
    - licence ("Add a LICENSE file for this project.")
  - **`build/template-content/CHANGELOG.md`**: header + `## [Unreleased]`. **`build/template-content/.release-please-manifest.json`**: `{ ".": "0.1.0" }`.
  - **`template.json` additions**:
    - exclude `LICENSE`, `CHANGELOG.md`, `README.md`, `CLAUDE.md`, `.release-please-manifest.json`, `build/template-content/**`, `SECURITY.md`, `docs/repository-management.md` and `build/scripts/configure-github-repo.sh` from the main source (these point at the Modulith repo)
    - add a second source `{ "source": "./build/template-content/", "target": "./" }` so the overrides land at the generated root
  - **`release-please-config.json`**:
    - `"release-type": "simple"`, `"include-component-in-tag": false`
    - `"pull-request-title-pattern": "chore(release): release${component} ${version}"` (passes commit-lint via the `release` scope)
    - `extra-files`: `[{ "type": "xml", "path": "Directory.Build.props", "xpath": "//Project/PropertyGroup/Version" }]`
    - `changelog-sections`: `feat`→`Added`, `fix`→`Fixed`, `perf`→`Changed`, `revert`→`Reverted`, `deps`→`Dependencies`; `refactor`, `test`, `ci`, `build`, `chore`, `style` and `docs` hidden
  - **`.github/workflows/release.yml`**: on push to `main`, `googleapis/release-please-action` (latest major) with `permissions: contents: write, pull-requests: write`. When a release is created, it runs `dotnet pack` for the template package (Plan 7 publishes it) and generates `THIRD-PARTY-NOTICES.md` as a release asset with the licence tool.
  - **Licence check**: CI job `licenses` runs the licence tool (install the `nuget-license` .NET tool via the tool manifest) over the solution with `--allowed-license-types build/licenses/allowed-licenses.json` (`["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause"]`). Packages that declare only a licence URL go in a mapping file next to it, each with a comment naming the licence verified.
  - **Docs content:**
    - `docs/architecture/overview.md`: a Mermaid flowchart of Host → modules → building blocks, the request lifecycle following the pipeline order (Task 5), data ownership per schema, and links to ADRs 0001–0010
    - `docs/services/api.md`: the pipeline order; **every configuration section with keys, defaults and validation** (`ConnectionStrings`, `Database`, `Outbox`, `Idempotency`, `Cors`, `RateLimiting`, `ForwardedHeaders`, `Localization`, `Serilog`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `Kestrel:Limits`); health endpoints; environments; migrations on startup
    - building-block docs: purpose, public types and extension methods, configuration, and a "how to use from a module" snippet

- [ ] **Step 1: Write the failing documentation tests**

`DocumentationTests` (ArchitectureTests):
```csharp
[Fact] public void Every_module_has_a_document()
{ /* for each Assemblies.Modules: File.Exists(Root/docs/modules/{moduleName.ToLowerInvariant()}.md), where moduleName = assembly name after "TemplateName.Modules." */ }

[Fact] public void Module_documents_have_required_sections_in_order()
{ /* headings "## Purpose and boundaries" … "## Testing" all present and in order; docs/modules/_template.md also passes */ }

[Fact] public void Module_documents_list_every_error_code()
{ /* error codes collected as in Task 15's Every_module_error_code_has_a_neutral_message; each appears in the module doc */ }

[Fact] public void Every_building_block_has_a_document()
{ /* shared-kernel, application-common, infrastructure-common, web-common exist under docs/building-blocks */ }

[Fact] public void Generated_project_files_exist()
{ /* build/template-content/{README.md, CHANGELOG.md, CLAUDE.md, .release-please-manifest.json} exist; README and CLAUDE.md contain "TemplateName" */ }
```
`EndpointDocumentationTests` (IntegrationTests):
```csharp
[Fact] public void Every_module_endpoint_is_documented()
{ /* Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
     where RoutePattern.RawText starts with "/api/v1/{module}/" → module doc contains "{HTTP METHOD} {raw route}" */ }
```

- [ ] **Step 2: Run to verify failure** → missing files.
- [ ] **Step 3: Write the documents, generated-project overrides, release-please files and licence allow-list listed above.**
- [ ] **Step 4: Extend the template smoke test.** The generated project must:
  - contain `README.md`, `CHANGELOG.md` (with `## [Unreleased]`), `CLAUDE.md` and `.release-please-manifest.json` with `0.1.0`
  - **not** contain `LICENSE`, `BACKEND_TEMPLATE_BLUEPRINT.md`, `docs/blueprint-review.md`, `docs/superpowers/` or `build/template-content/`
  - have no `CLAUDE.md` line containing "Maintaining the template"
- [ ] **Step 5: Run everything**: `dotnet test`, `bash build/scripts/template-smoke.sh` (exits 0), the licence tool locally (exits 0), and `npx --yes @action-validator/cli .github/workflows/release.yml` if Node is available (optional).
- [ ] **Step 6: Commit** → `git commit -m "docs: add service, module and architecture docs with release automation"`
- [ ] **Step 7: Branch protection phase 2** after merge and the first CI run: with the user's approval, run `bash build/scripts/configure-github-repo.sh --checks build-test,template-smoke,commit-lint,licenses`.

---

## Out of scope for this plan (next plans, per `docs/blueprint-review.md` Section 6)

Auth (Plan 2) replaces `ApproverId`-in-body with `ICurrentUser`, adds the fallback authenticated policy, and adds the saved-`locale` culture provider ahead of `Accept-Language`. Notifications (Plan 3) render templates in the recipient's language (review Section 8.2). Redis/HybridCache, Hangfire, resilience and the audit trail come in Plan 5. Dockerfile, CD and `migrate` mode come in Plan 6. Template switches and the `modulith-module` item template come in Plan 7.
