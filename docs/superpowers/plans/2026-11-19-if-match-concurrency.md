# ETag and If-Match Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The six PUT endpoints accept an optional `If-Match`, refuse a stale one with 412 `concurrency.precondition_failed`, and they and their reads return the resource's version as `ETag`.

**Architecture:** A version is a hash of a resource's editable state, computed by one pure function per resource that both the read endpoint (from the response it returns) and the update handler (from the state it loaded) call. The handler checks the precondition after loading and before any write; 204 updates return the new version from the handler, 200 updates let the endpoint compute it from the returned body. Shared types live in SharedKernel, Application.Common and Web.Common.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core and Dapper on SQL Server, xUnit v3 with Shouldly and NSubstitute, Microsoft.AspNetCore.OpenApi, Verify (snapshot).

**Spec:** `docs/superpowers/specs/2026-11-19-if-match-concurrency-design.md` (approved 2026-11-19). Read it first; its Sections 4 and 5 are the contract.

## Global Constraints

- Work on branch `feat/if-match` from `main` in a short-path worktree (`C:\w\ifm2` or similar); one commit per task, Conventional Commits, header at most 72 characters, never `--no-verify`. The pull request is squash-merged under the title `feat: add ETag and If-Match to the update endpoints`, which is the one changelog entry.
- Every update endpoint binds `[FromHeader(Name = HeaderNames.IfMatch)] string? ifMatch`, answers `IfMatchHeader.Malformed()` when `IfMatchHeader.TryParse` fails, passes the parsed `IfMatch` in its command, and is declared with `.ProducesEntityTag()` and `.ProducesProblem(StatusCodes.Status412PreconditionFailed)`; every matching read is declared with `.ProducesEntityTag()`.
- `If-Match` is optional everywhere: without it every endpoint behaves exactly as before (status, body, headers other than the new `ETag`).
- Error code `concurrency.precondition_failed`, status 412, English message exactly: `The resource was changed since you read it. Read it again and retry with its current ETag.`
- A malformed `If-Match` is 400 with code `request.malformed`; a weak tag never matches (412); `*` always matches here.
- ETag value: canonical text (Task 1) hashed with SHA-256, first 16 bytes, base64url without padding, written quoted. Format version `v1`.
- No response body and no schema changes. No migration.
- Docker is broken on the maintainer machine: unit and architecture tests run locally (`dotnet test tests/TemplateName.UnitTests`, `tests/TemplateName.ArchitectureTests`); integration tests are written and compiled locally (`dotnet build -c Release`) and run in CI on the pull request. Say so in each task report.
- `dotnet build -c Release` has warnings as errors; `dotnet format --verify-no-changes` must pass.
- After changing a route's documentation, a test file or a source file with a paired test: `git add` new files, then `bash build/scripts/project-index.sh`, and commit the result.

## Review Focus

- `If-Match` holding only weak tags (`W/"x"`) must answer 412, not succeed: the parser keeps it as an empty set of strong tags, not as "no precondition". Test in Task 2.
- `*` combined with a tag (`*, "x"`) is malformed (400), not "any". Test in Task 2.
- Preferences: a retry attempt after a save conflict must re-check against its fresh read; a change that lands between attempts answers 412. Test in Task 6.
- A role-permissions update that changes nothing, with a matching `If-Match`, answers 204 with the same `ETag`. Test in Task 3.
- A user linked to a soft-deleted role: the `ETag` of `GET` admin user equals the version the role assignment computes, so a client that read the user is not refused. Test in Task 4.

---

### Task 1: Version and precondition types

**Files:**
- Modify: `src/BuildingBlocks/TemplateName.SharedKernel/ErrorType.cs`, `src/BuildingBlocks/TemplateName.SharedKernel/Error.cs`
- Create: `src/BuildingBlocks/TemplateName.Application.Common/Concurrency/EntityTag.cs`, `EntityTagBuilder.cs`, `IfMatch.cs`, `ConcurrencyErrors.cs` (same folder)
- Modify: `src/BuildingBlocks/TemplateName.Web.Common/Resources/CommonErrorMessages.resx`, `.ms.resx`, `.zh-Hans.resx`
- Test: `tests/TemplateName.UnitTests/Application/Concurrency/EntityTagBuilderTests.cs`, `IfMatchTests.cs`

**Interfaces:**
- Produces: `ErrorType.PreconditionFailed`; `Error.PreconditionFailed(string code, string message)`.
- Produces: `public readonly record struct EntityTag(string Value)` with `public string ToHeaderValue()` returning `"` + Value + `"`.
- Produces: `public sealed class EntityTagBuilder(string kind)` with `Add(string? value)`, `Add(Guid value)`, `Add(bool value)`, `Add(TimeOnly? value)`, `AddSet(IEnumerable<string> values)`, `AddSet(IEnumerable<Guid> values)` (each returns the builder), `public string CanonicalText { get; }` and `public EntityTag Build()`.
- Produces: `public sealed class IfMatch` with `static IfMatch None`, `static IfMatch Any`, `static IfMatch Of(IReadOnlyCollection<EntityTag> strongTags)` and `bool IsSatisfiedBy(EntityTag current)`.
- Produces: `public static class ConcurrencyErrors` with `PreconditionFailed` (`concurrency.precondition_failed`, the message in Global Constraints) and `MalformedIfMatch` (`Error.Validation("request.malformed", "The If-Match header is malformed.")`).

The canonical text, which the tests pin: it starts with `v1;` then the kind as a string token. A string token is `s{length}:{text};` (length in UTF-16 code units of the stored string, no trimming or case change); null is `n;`; a Guid is its lower-case `D` string as a string token; a bool is `true` or `false` as a string token; a `TimeOnly` is `HH:mm:ss.fffffff` (invariant) as a string token; a set is `l{count}:` followed by its items' string tokens in `StringComparer.Ordinal` order (Guids sorted by their `D` string). `Build()` hashes the UTF-8 of `CanonicalText`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Canonical_text_has_the_format_version_kind_and_tokens()
    => new EntityTagBuilder("role").Add("ab").Add((string?)null).AddSet(["b", "a"]).CanonicalText
        .ShouldBe("v1;s4:role;s2:ab;n;l2:s1:a;s1:b;");

[Fact] public void Length_prefixes_keep_split_strings_apart()
    => new EntityTagBuilder("k").Add("ab").Add("c").Build().ShouldNotBe(new EntityTagBuilder("k").Add("a").Add("bc").Build());

[Fact] public void Null_and_empty_differ()
    => new EntityTagBuilder("k").Add((string?)null).Build().ShouldNotBe(new EntityTagBuilder("k").Add("").Build());

[Fact] public void Set_order_does_not_matter()
    => new EntityTagBuilder("k").AddSet([guidB, guidA]).Build().ShouldBe(new EntityTagBuilder("k").AddSet([guidA, guidB]).Build());

[Fact] public void Build_is_the_first_16_bytes_of_sha256_in_base64url()
{
    var builder = new EntityTagBuilder("k").Add("x");
    var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.CanonicalText))[..16]);
    builder.Build().Value.ShouldBe(expected);
    builder.Build().ToHeaderValue().ShouldBe($"\"{expected}\"");
}
// Also: Add(TimeOnly) writes 22:00:00.0000000; Add(true) writes s4:true;; a different kind gives a different tag.

// IfMatchTests
[Fact] public void None_and_any_are_satisfied_by_every_tag() { IfMatch.None.IsSatisfiedBy(tag).ShouldBeTrue(); IfMatch.Any.IsSatisfiedBy(tag).ShouldBeTrue(); }
[Fact] public void A_list_is_satisfied_by_any_of_its_tags() => IfMatch.Of([other, tag]).IsSatisfiedBy(tag).ShouldBeTrue();
[Fact] public void A_list_without_the_tag_is_not_satisfied() => IfMatch.Of([other]).IsSatisfiedBy(tag).ShouldBeFalse();
[Fact] public void An_empty_list_is_never_satisfied() => IfMatch.Of([]).IsSatisfiedBy(tag).ShouldBeFalse();
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~Concurrency"`
Expected: build errors (types not defined).

- [ ] **Step 3: Implement the types and add the message**

Add `concurrency.precondition_failed` to the three `CommonErrorMessages` files (English as above; `ms`: `Sumber ini telah diubah sejak anda membacanya. Baca semula dan cuba lagi dengan ETag semasanya.`; `zh-Hans`: `自您读取后，该资源已被更改。请重新读取，并使用其当前的 ETag 重试。`), the two translations with `<comment>Draft – needs native review</comment>` like their neighbours.

- [ ] **Step 4: Run unit and architecture tests**

Run: `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~Concurrency"` then `dotnet test tests/TemplateName.ArchitectureTests`
Expected: all pass; `TranslationTests` finds the new code in all three languages.

- [ ] **Step 5: Commit** — `feat(application): add entity tags and the If-Match precondition`

---

### Task 2: The HTTP side in Web.Common

**Files:**
- Create: `src/BuildingBlocks/TemplateName.Web.Common/Concurrency/IfMatchHeader.cs`, `EntityTagResult.cs`, `EntityTagEndpointExtensions.cs`, `EntityTagOpenApi.cs`
- Modify: `src/BuildingBlocks/TemplateName.Web.Common/Results/ResultExtensions.cs` (412), `src/BuildingBlocks/TemplateName.Web.Common/Security/HttpSecurityExtensions.cs:48` (expose `ETag`), `src/Host/TemplateName.Api/Program.cs:49` (register the transformer)
- Test: `tests/TemplateName.UnitTests/Web/IfMatchHeaderTests.cs`, `EntityTagResultTests.cs`, `tests/TemplateName.IntegrationTests/Host/HttpSecurityTests.cs:55`

**Interfaces:**
- Consumes: Task 1's `EntityTag`, `IfMatch`, `ConcurrencyErrors`.
- Produces: `public static bool IfMatchHeader.TryParse(string? header, out IfMatch ifMatch)` (null header: true and `IfMatch.None`); `public static IResult IfMatchHeader.Malformed()` (the 400 problem of `ConcurrencyErrors.MalformedIfMatch`).
- Produces: `public static IResult WithEntityTag(this IResult result, EntityTag tag)` (sets `ETag` to `tag.ToHeaderValue()`, then runs `result`).
- Produces: `public sealed class EntityTagMetadata` and `public static TBuilder ProducesEntityTag<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder`.
- Produces: `public static OpenApiOptions AddEntityTagHeaders(this OpenApiOptions options)`: on an operation with `EntityTagMetadata`, every 2xx response documents an `ETag` header (string). Register as `options.AddBearerSecurity().AddEntityTagHeaders()`.

Endpoints bind the header as `[FromHeader(Name = HeaderNames.IfMatch)] string? ifMatch`, which OpenAPI documents as an optional header parameter by itself.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("\"abc\"", "abc", true)]
[InlineData(" \"abc\" , \"def\" ", "def", true)]
[InlineData("\"abc\"", "xyz", false)]
[InlineData("W/\"abc\"", "abc", false)]            // weak never matches: an empty strong set, not None
public void Parses_tags(string header, string current, bool satisfied)
{
    IfMatchHeader.TryParse(header, out var ifMatch).ShouldBeTrue();
    ifMatch.IsSatisfiedBy(new EntityTag(current)).ShouldBe(satisfied);
}
[Fact] public void Absent_is_none() { IfMatchHeader.TryParse(null, out var m).ShouldBeTrue(); m.ShouldBeSameAs(IfMatch.None); }
[Fact] public void Star_is_any() { IfMatchHeader.TryParse(" * ", out var m).ShouldBeTrue(); m.ShouldBeSameAs(IfMatch.Any); }
[Theory]
[InlineData("")] [InlineData("   ")] [InlineData("abc")] [InlineData("\"abc")] [InlineData("*, \"abc\"")]
[InlineData("\"a\",,\"b\"")] [InlineData("\"a b\"")] [InlineData("w/\"abc\"")]
public void Malformed_is_refused(string header) => IfMatchHeader.TryParse(header, out _).ShouldBeFalse();

// EntityTagResultTests: WithEntityTag on TypedResults.NoContent() writes 204 and ETag: "\"q\""; on Ok(body) keeps the body.
// ResultExtensions: Result.Failure(ConcurrencyErrors.PreconditionFailed).ToProblem() has status 412 and code concurrency.precondition_failed.
// HttpSecurityTests line 55: the exposed list gains "ETag".
```

A tag value is RFC 9110 `etagc` characters (`!`, `#`–`~`, and `%x80`–`%xFF`) between quotes; `W/` is upper case; list elements are separated by commas with optional spaces; an empty element is malformed.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~IfMatchHeader|FullyQualifiedName~EntityTagResult|FullyQualifiedName~ResultExtensions"`
Expected: build errors.

- [ ] **Step 3: Implement** the four files, the 412 arm in `ToStatusCode`, `ETag` in `WithExposedHeaders`, the transformer registration, and the `HttpSecurityTests` expectation.

- [ ] **Step 4: Run the unit tests and build everything**

Run: `dotnet test tests/TemplateName.UnitTests` and `dotnet build -c Release`
Expected: pass; build clean (the integration change compiles; it runs in CI).

- [ ] **Step 5: Commit** — `feat(web): parse If-Match and write ETag responses`

---

### Task 3: Role and role permissions

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth/Application/Admin/Roles/RoleVersion.cs`
- Modify: `.../Application/Admin/Roles/Update/UpdateRoleCommand.cs` and `UpdateRoleCommandHandler.cs`, `.../Roles/SetPermissions/SetRolePermissionsCommand.cs` and `SetRolePermissionsCommandHandler.cs`, `.../Endpoints/AdminActor.cs`, `.../Endpoints/AdminRoleEndpoints.cs` (Get, Update, SetPermissions and their route declarations)
- Test: `tests/TemplateName.UnitTests/Auth/RoleVersionTests.cs`, the existing unit tests of the two handlers, `tests/TemplateName.IntegrationTests/Auth/RoleEntityTagTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: `internal static class RoleVersion { public static EntityTag Of(string name, string description, IEnumerable<Guid> permissionIds); }` (kind `role`).
- Produces: `UpdateRoleCommand(Guid ActorId, Guid RoleId, string Name, string Description, IfMatch IfMatch) : ICommand<EntityTag>`; `SetRolePermissionsCommand(Guid ActorId, Guid RoleId, IReadOnlyCollection<Guid> PermissionIds, IfMatch IfMatch) : ICommand<EntityTag>`.
- Produces: `AdminActor.RunAsync(ICurrentUser currentUser, Func<Guid, Task<Result<EntityTag>>> send)`: 204 with the `ETag` on success.

Read path: `GET` role answers `Ok(role).WithEntityTag(RoleVersion.Of(role.Name, role.Description, role.Permissions.Select(p => p.Id)))`. Write path: after the 404 check and before any other rule, `RoleVersion.Of(role.Name, role.Description, role.Permissions.Select(p => p.PermissionId))`; when `!command.IfMatch.IsSatisfiedBy(current)` return `ConcurrencyErrors.PreconditionFailed` before recording an audit entry or staging a change; on success return the version of the saved state (the unchanged one on the no-op path). The three routes get `.ProducesEntityTag()`; the two updates also get `.ProducesProblem(StatusCodes.Status412PreconditionFailed)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// RoleVersionTests
[Fact] public void Covers_name_description_and_permissions()  // each change gives a different tag
[Fact] public void Permission_order_does_not_matter()

// RoleEntityTagTests (AdminTestBase)
[Fact] public async Task Get_returns_an_etag_that_a_matching_update_accepts_and_replaces()  // GET ETag e1; PUT name with If-Match e1 → 204, ETag e2 != e1; GET → e2
[Fact] public async Task A_stale_etag_is_412_and_changes_nothing()        // PUT with e1 after another PUT; 412, code concurrency.precondition_failed; GET name unchanged
[Fact] public async Task Without_if_match_the_update_behaves_as_before()  // 204, ETag present
[Fact] public async Task Star_matches_weak_is_412_and_malformed_is_400()
[Fact] public async Task A_permissions_change_changes_the_role_etag()     // PUT permissions with If-Match e1 → ETag e2; PUT name with e1 → 412
[Fact] public async Task A_permissions_update_that_changes_nothing_keeps_the_etag()  // Review Focus: 204, ETag == e1
```

- [ ] **Step 2: Run the unit tests to see them fail; build the integration tests**

Run: `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~Role"`
Expected: build errors, then (after Step 3) the existing handler tests need the new command shape.

- [ ] **Step 3: Implement** `RoleVersion`, the command and handler changes, the `AdminActor` overload and the endpoint changes; update the existing unit tests of the two handlers for the new signatures (pass `IfMatch.None`) and add one test each: a non-matching `IfMatch` returns `PreconditionFailed` and saves nothing (`unitOfWork` received no save, no audit recorded).

- [ ] **Step 4: Run unit tests and build**

Run: `dotnet test tests/TemplateName.UnitTests` and `dotnet build -c Release`
Expected: pass; the integration tests compile.

- [ ] **Step 5: Commit** — `feat(auth): check If-Match on the role updates`

---

### Task 4: A user's roles

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth/Application/Admin/Users/AdministeredUserVersion.cs`
- Modify: `.../Application/Admin/Users/AssignRoles/AssignRolesCommand.cs` and `AssignRolesCommandHandler.cs`, `.../Endpoints/AdminUserEndpoints.cs` (Get, AssignRoles, route declarations)
- Test: `tests/TemplateName.UnitTests/Auth/AdministeredUserVersionTests.cs`, the existing `AssignRolesCommandHandler` unit tests, `tests/TemplateName.IntegrationTests/Auth/UserRolesEntityTagTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 (`AdminActor.RunAsync` overload).
- Produces: `internal static class AdministeredUserVersion { public static EntityTag Of(IEnumerable<Guid> roleIds); }` (kind `user-roles`).
- Produces: `AssignRolesCommand(Guid ActorId, Guid UserId, IReadOnlyCollection<Guid> RoleIds, IfMatch IfMatch) : ICommand<EntityTag>`.

Read path: from `UserResponse.Roles` ids (roles that are not deleted). Write path: the current version comes from the roles that exist among the user's links, loaded as `GetUserQueryHandler` loads them (`roles.GetByIdsAsync(user.Roles.Select(a => a.RoleId))`), so links to a soft-deleted role count on neither side; the new version is `Of(command.RoleIds)` after a successful save.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Get_user_etag_is_accepted_by_assign_roles_and_replaced()
[Fact] public async Task A_stale_etag_is_412_and_changes_nothing()
[Fact] public async Task Signing_in_does_not_change_the_users_etag()             // spec decision 1
[Fact] public async Task A_link_to_a_deleted_role_does_not_break_the_precondition() // Review Focus
```

- [ ] **Step 2: Run to see the unit tests fail.** `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~AssignRoles|FullyQualifiedName~AdministeredUser"` — build errors.

- [ ] **Step 3: Implement**, update the existing handler unit tests (`IfMatch.None`), add a `PreconditionFailed` unit test that asserts no save and no audit entry, and mark the routes as in Task 3.

- [ ] **Step 4: Run** `dotnet test tests/TemplateName.UnitTests` and `dotnet build -c Release` — pass.

- [ ] **Step 5: Commit** — `feat(auth): check If-Match on a user's role assignment`

---

### Task 5: The caller's profile

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth/Application/Me/ProfileVersion.cs`
- Modify: `.../Application/Me/Update/UpdateProfileCommand.cs` and `UpdateProfileCommandHandler.cs`, `.../Endpoints/AuthEndpoints.cs` (GetMe, UpdateProfile, route declarations)
- Test: `tests/TemplateName.UnitTests/Auth/ProfileVersionTests.cs`, `tests/TemplateName.UnitTests/Auth/UpdateProfileCommandHandlerTests.cs`, `tests/TemplateName.IntegrationTests/Auth/ProfileEntityTagTests.cs`

**Interfaces:**
- Produces: `internal static class ProfileVersion { public static EntityTag Of(string displayName, string locale, string timeZone); }` (kind `profile`).
- Produces: `UpdateProfileCommand(string DisplayName, string Locale, string TimeZone, IfMatch IfMatch) : ICommand<MeResponse>` (return type unchanged).

The handler checks the precondition after loading the user, from the user's stored display name, locale and time zone. Both endpoints compute the `ETag` from the `MeResponse` they return. A 412 is answered as a problem (only `NotFound` keeps its body-less 401).

- [ ] **Step 1: Write the failing tests** — the four integration cases of Task 3 for `/me` (matching, stale with read-back, without header, `*`/weak/malformed), plus: the `ETag` of `GET /me` does not change when the user's roles change (not covered).
- [ ] **Step 2: Run** the unit tests to see them fail.
- [ ] **Step 3: Implement**; update `UpdateProfileCommandHandlerTests` (`IfMatch.None`) and add a `PreconditionFailed` case that asserts no save.
- [ ] **Step 4: Run** `dotnet test tests/TemplateName.UnitTests` and `dotnet build -c Release` — pass.
- [ ] **Step 5: Commit** — `feat(auth): check If-Match on the profile update`

---

### Task 6: Notification preferences and quiet hours

**Files:**
- Create: `src/Modules/Notifications/TemplateName.Modules.Notifications/Application/Preferences/PreferencesView.cs`, `PreferencesVersion.cs`, `QuietHoursVersion.cs`
- Modify: `.../Application/Preferences/ConflictRetry.cs`, `Get/GetPreferencesQueryHandler.cs` (use `PreferencesView`), `Update/UpdatePreferencesCommand.cs` and its handler, `UpdateQuietHours/UpdateQuietHoursCommand.cs` and its handler, `.../Endpoints/NotificationEndpoints.cs` (the four routes)
- Test: `tests/TemplateName.UnitTests/Notifications/PreferencesVersionTests.cs`, `QuietHoursVersionTests.cs`, `UpdatePreferencesCommandHandlerTests.cs` (new or existing), `tests/TemplateName.IntegrationTests/Notifications/PreferenceEntityTagTests.cs`

**Interfaces:**
- Produces: `internal static class PreferencesView { public static PreferencesResponse Build(NotificationCatalog catalog, IEnumerable<UserPreference> stored); }`, moved out of `GetPreferencesQueryHandler` unchanged in behaviour.
- Produces: `internal static class PreferencesVersion { public static EntityTag Of(PreferencesResponse view); }` (kind `preferences`; every `(TypeCode, Channel, Enabled)` of the view as a set of `"{code}|{channel}|{enabled}"` strings).
- Produces: `internal static class QuietHoursVersion { public static EntityTag Of(TimeOnly? start, TimeOnly? end); }` (kind `quiet-hours`).
- Produces: `ConflictRetry.SaveAsync(IUnitOfWork unitOfWork, Func<CancellationToken, Task<Result>> stageChangesAsync, CancellationToken cancellationToken) : Task<Result>`: a failed stage returns its failure at once, without saving.
- Produces: `UpdatePreferencesCommand(IReadOnlyList<ChannelPreferenceUpdate> Items, IfMatch IfMatch)`; `UpdateQuietHoursCommand(TimeOnly? Start, TimeOnly? End, IfMatch IfMatch)`.

Each attempt of the retry loop reads, builds the version from that read (preferences: `PreferencesView.Build(catalog, rows)`; quiet hours: the profile's window start and end, null when there is no profile or no window), and returns `ConcurrencyErrors.PreconditionFailed` before staging when it does not match. Both endpoints compute the `ETag` from the response they return.

- [ ] **Step 1: Write the failing tests**

```csharp
// UpdatePreferencesCommandHandlerTests (NSubstitute repository and unit of work)
[Fact] public async Task A_change_between_attempts_is_412_on_the_retry()   // Review Focus: ListAsync returns rows A then rows B;
    // SaveChangesUnlessConflictAsync returns false the first time; If-Match = version of A; result PreconditionFailed, one save attempt only
[Fact] public async Task Without_if_match_the_retry_still_saves()
// Integration: matching / stale (read back) / without header / */weak/malformed, for both routes;
// quiet hours for a user with no settings row: GET has an ETag and a PUT with it succeeds.
```

- [ ] **Step 2: Run** the unit tests to see them fail.
- [ ] **Step 3: Implement**; keep `GetPreferencesQueryHandler`'s output identical (its existing integration tests are the check).
- [ ] **Step 4: Run** `dotnet test tests/TemplateName.UnitTests` and `dotnet build -c Release` — pass.
- [ ] **Step 5: Commit** — `feat(notifications): check If-Match on preferences and quiet hours`

---

### Task 7: Coverage, documentation and the CI snapshot

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Documentation/EntityTagCoverageTests.cs`, `docs/adr/0038-etag-over-editable-state.md`
- Modify: `.github/workflows/ci.yml` (upload Verify's `*.received.*` files when the tests fail), `tests/TemplateName.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json`, `docs/modules/auth.md`, `docs/modules/notifications.md`, `docs/services/api.md`, `docs/building-blocks/web-common.md`, `docs/building-blocks/application-common.md`, `docs/building-blocks/shared-kernel.md`, `docs/building-blocks/infrastructure-common.md` (the idempotency note of spec Section 8), `docs/api-compatibility.md`, `docs/coding-conventions.md`, `docs/blueprint-review.md` (a row), `docs/README.md` (the ADR), the spec's Status line, the project index

**Interfaces:**
- Consumes: `EntityTagMetadata` (Task 2).

- [ ] **Step 1: Write the coverage test**: every `RouteEndpoint` under `/api/v1/` whose HTTP methods include `PUT` carries `EntityTagMetadata`, except those on an explicit `ExcludedPuts` list (empty, with a comment saying a new entry needs a reason).
- [ ] **Step 2: Add the CI step** to the test job of `.github/workflows/ci.yml`: `if: failure()`, upload `**/*.received.*` with the `actions/upload-artifact` version this repository already pins elsewhere (find it with `git grep upload-artifact .github`); no `${{ }}` inside a `run:` step (`static-rules.test.sh`).
- [ ] **Step 3: Write the documentation**: per endpoint the `ETag` and `If-Match` headers, the 412, and what each version covers (spec Section 4 table); `api-compatibility.md` rows: adding an optional request header is compatible, making `If-Match` required is breaking (`!`); `coding-conventions.md`: how a new PUT opts in (a version function, `IfMatch` on the command, `.ProducesEntityTag()`, the 412) or joins `ExcludedPuts`; ADR 0038 records decisions 1–4 and the strong-validator note (spec 5.1).
- [ ] **Step 4: Verify locally**: `dotnet build -c Release`, `dotnet format --verify-no-changes`, `dotnet test tests/TemplateName.UnitTests`, `dotnet test tests/TemplateName.ArchitectureTests`, `bash build/scripts/tests/static-rules.test.sh`, `git add -A && bash build/scripts/project-index.sh`, then `bash build/scripts/template-smoke.sh --generate-only` and the same with `--notifications false` (the Notifications docs and tests leave with the module).
- [ ] **Step 5: Commit, push, open the pull request.** Commit `docs: document ETag and If-Match`; open the pull request titled `feat: add ETag and If-Match to the update endpoints`. In CI the snapshot test fails as expected: download the artifact (`gh run download <run> -n <artifact>`), check that the `.received.json` differs from the verified one only by the `If-Match` header parameters, the `ETag` response headers and the 412 responses, replace the verified file with it, and push. Every integration test must then pass in CI; report the CI result per task's test file.

## Execution notes

- Tasks 3–6 depend on Tasks 1 and 2 only through their Interfaces blocks; 4 reuses 3's `AdminActor` overload.
- The integration tests are the main proof but run only in CI: read their logs on the pull request, fix, push again.
