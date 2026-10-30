# Social login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users sign in, sign up, link and unlink Google, Apple, Microsoft, Facebook and generic OpenID Connect accounts through client-posted proofs, ending in the same session, MFA challenge and step-up rules as a password login.

**Architecture:** Auth gains `UserExternalLogin` (a child of `User`), a provider catalog built from `Auth:External`, one OIDC verifier plus a Facebook Graph verifier behind `IExternalIdentityVerifier`, and five routes under `/api/v1/auth/external`. The MFA challenge remembers its first factor (`pwd` or `fed`); step-up accepts a fresh provider proof for password-less accounts; Notifications gets two security notices.

**Tech Stack:** ASP.NET Core 10, EF Core (SQL Server), `Microsoft.IdentityModel.Protocols.OpenIdConnect` and `JsonWebTokens` (already in Auth's graph), HybridCache, xunit v3 on Microsoft Testing Platform, Shouldly, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-10-30-social-login-design.md` (approved 2026-10-30). Read it with this plan; section numbers below refer to it.

## Global Constraints

- Work only in a short-path worktree (`C:\w\sl`); `Directory.Build.targets` TN0001 rejects long paths. `TemplateName` is the placeholder; never hard-code a project name.
- Tests first and green. Run one class with `dotnet test --project tests/TemplateName.UnitTests -c Release --filter-class "*ClassName"` (Microsoft Testing Platform, `global.json`). Docker cannot start on this machine: integration tests are verified in CI only; unit and architecture tests run locally.
- Every route is new under `/api/v1/auth/external`; the only changed request shape is `POST /api/v1/auth/step-up`, which gains optional members (ADR 0031: compatible). The migration only adds (no drop, no rename, no `[ContractMigration]`).
- Every new error code exists in `AuthErrorMessages.resx`, `.ms.resx` and `.zh-Hans.resx`, and as `` `code` `` in `docs/modules/auth.md` (Error codes).
- No new package; no secret in a file (`Auth:External:Providers:Facebook:AppSecret` only from user-secrets or the environment); no `DateTime.Now`/`UtcNow` (inject `TimeProvider`); never `--no-verify`.
- Every provider is `Enabled: false` by default. `Provider` is stored lower-case; `Provider` at most 64, `Subject` at most 256, `Email` at most 256 characters; `idToken` and `accessToken` at most 8192; `nonce` 16 to 256; `displayName` at most 200; `MaxTokenAge` 5 minutes (1 minute to 1 hour); `amr` value `fed`.
- Proofs, nonces and tokens never reach a log, an audit row, a metric tag or a command's `ToString` (print the type name only; a record's generated `ToString` must be overridden).
- Commit headers are Conventional Commits of at most 72 characters with scopes from CONTRIBUTING.md 2.3 (`auth`, `notifications`, `adr`, `web`, `persistence`). Branch from `docs/social-login-spec`; run `bash build/scripts/template-smoke.sh` before the PR.
- Names used by several tasks: `AuthenticationMethods.Federated`, `ExternalLoginErrors`, `ExternalProvider`, `ExternalProof`, `VerifiedExternalIdentity`, `IExternalProviderCatalog`, `IExternalIdentityVerifier`, `IExternalProofReplayGuard`, `UserExternalLogin`, `ExternalLoginResponse`.

## Review Focus

- A provider email with other case or surrounding white space (`Alice@Example.com `) links to the account `alice@example.com` and never creates a second one; an empty email with `email_verified` true is no usable email. (Task 8)
- A malformed, truncated, oversized, future-dated or non-JWT proof answers 401 `auth.invalid_external_token`, never a 500 and never a burnt nonce. (Task 5)
- A `Subject` or provider email longer than its column is refused (401 / ignored for display) instead of truncated into a collision or a database error. (Tasks 3, 5)
- A missing or blank name falls back to the email's local part, and a name or fallback over 200 characters is cut to 200; locale defaults to the request language. (Task 8)
- The provider in the route is case-insensitive (`/Google/login`), a disabled provider with an existing link still lists in `GET /logins` and can be unlinked, and a federated challenge never creates a trusted device. (Tasks 7, 8, 9)

---

### Task 1: Foundations: constants, errors, 503 type, audit events

**Files:**
- Modify: `src/BuildingBlocks/TemplateName.SharedKernel/ErrorType.cs`, `Error.cs`; `ResultExtensions.ToProblem` in Web.Common; `Auth/Domain/Sessions/AuthenticationMethods.cs`, `SessionRevokedReason.cs`; `Auth/Domain/Audit/AuthAuditEvents.cs`
- Create: `Auth/Domain/Users/ExternalLoginErrors.cs` (beside `UserErrors`)
- Modify: `Auth/Resources/AuthErrorMessages.resx`, `.ms.resx`, `.zh-Hans.resx`; `docs/modules/auth.md` (Error codes, line ~260); `tests/TemplateName.UnitTests/Auth/AuthAuditLogTests.cs` (count 42 to 47)
- Test: `tests/TemplateName.UnitTests/Web/ResultExtensionsTests.cs` (or the existing file for `ToProblem`)

**Interfaces:**
- Produces: `ErrorType.Unavailable` mapped to 503; `Error.Unavailable(string code, string message)`; `AuthenticationMethods.Federated == "fed"`; `SessionRevokedReason.ExternalLoginChanged = 7` (appended); `ExternalLoginErrors` with `ProviderNotFound` (NotFound), `InvalidToken` (Unauthorized), `ProviderUnavailable` (Unavailable), `EmailUnverified` (Forbidden), `LinkRequired` (Conflict), `SignUpDisabled` (Forbidden), `IdentityTaken` (Conflict), `ProviderAlreadyLinked` (Conflict), `LoginNotFound` (NotFound), `LastLoginMethod` (Conflict), `StepUpIdentityMismatch` (Validation), codes exactly as spec section 4; `AuthAuditEvents.ExternalLoginSucceeded`, `ExternalLoginFailed`, `ExternalLinked`, `ExternalUnlinked`, `ExternalRegistered` = `"auth.external_login_succeeded"`, `"auth.external_login_failed"`, `"auth.external_linked"`, `"auth.external_unlinked"`, `"auth.external_registered"`.

- [ ] **Step 1: Write the failing tests:** `ToProblem_maps_Unavailable_to_503`; `Federated_is_fed`; `ExternalLoginChanged_is_7_and_the_others_keep_their_values` (Logout 0 … MfaChanged 6); `AuthAuditLogTests` count 47; the existing translation and documentation architecture tests will fail on missing keys.
- [ ] **Step 2: Run** `dotnet test --project tests/TemplateName.UnitTests -c Release --filter-class "*ResultExtensionsTests"` and the architecture project. Expected: FAIL (missing members, keys, docs).
- [ ] **Step 3: Implement** the members above; messages in all three `.resx` (natural translations, no placeholders); one sentence per code in `auth.md` Error codes.
- [ ] **Step 4: Run** both test projects. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): add external login errors and constants`.

### Task 2: Domain: external logins, federated registration, challenge first factor

**Files:**
- Create: `Auth/Domain/Users/UserExternalLogin.cs`, `ExternalLoginLinkedDomainEvent.cs`, `ExternalLoginUnlinkedDomainEvent.cs`
- Modify: `Auth/Domain/Users/User.cs`; `Auth/Domain/Mfa/MfaChallenge.cs`
- Test: `tests/TemplateName.UnitTests/Auth/UserExternalLoginTests.cs`, `MfaChallengeTests.cs`

**Interfaces:**
- Produces: `[Audited] internal sealed class UserExternalLogin` (`Guid Id, Guid UserId, string Provider, string Subject, string? Email, DateTimeOffset LinkedAt, DateTimeOffset? LastUsedAt`; `const int ProviderMaxLength = 64, SubjectMaxLength = 256, EmailMaxLength = 256`; `internal static UserExternalLogin Create(Guid userId, string provider, string subject, string? email, DateTimeOffset now)`; `void NoteUsed(DateTimeOffset now)`).
- `User`: `IReadOnlyCollection<UserExternalLogin> ExternalLogins`; `bool HasPassword`; `static Result<User> RegisterExternal(string email, string displayName, string locale, string provider, string subject, string? providerEmail, DateTimeOffset now)` (active, no password, email confirmed, raises `UserRegisteredDomainEvent` and `EmailConfirmedDomainEvent`, adds the login, no linked event); `Result<UserExternalLogin> LinkExternalLogin(string provider, string subject, string? email, DateTimeOffset now)`; `Result UnlinkExternalLogin(Guid loginId, DateTimeOffset now)`.
- `ExternalLoginLinkedDomainEvent(Guid UserId, string Provider)`, `ExternalLoginUnlinkedDomainEvent(Guid UserId, string Provider)`.
- `MfaChallenge.Issue(Guid userId, byte[] tokenHash, string securityStamp, string deviceName, string firstFactor, TimeSpan lifetime, DateTimeOffset now)` and property `string FirstFactor` (`pwd` or `fed`, anything else throws `ArgumentException`).

- [ ] **Step 1: Write the failing tests:** `Link_adds_a_login_and_raises_linked`; `Link_of_the_same_identity_is_idempotent_and_raises_nothing`; `Link_of_another_subject_for_the_same_provider_is_ProviderAlreadyLinked`; `Unlink_removes_rotates_the_stamp_and_raises_unlinked`; `Unlink_of_the_last_method_without_a_password_is_LastLoginMethod`; `Unlink_with_a_password_or_another_login_succeeds`; `Unlink_of_an_unknown_id_is_LoginNotFound`; `RegisterExternal_creates_a_confirmed_passwordless_user_with_the_login`; `Provider_is_stored_lower_case_and_over_long_subject_is_refused`; `MfaChallenge_Issue_stores_the_first_factor`; `MfaChallenge_Issue_rejects_an_unknown_first_factor`.
- [ ] **Step 2: Run** `--filter-class "*UserExternalLoginTests"` and `"*MfaChallengeTests"`. Expected: FAIL (not defined).
- [ ] **Step 3: Implement** per the signatures. `LinkExternalLogin` and `UnlinkExternalLogin` raise the events; unlink rotates the `SecurityStamp` the way `ChangeMfa` does; `RegisterExternal` reuses `Register(…, passwordHash: null, …)` then `ConfirmEmail`. The first factor constants are `AuthenticationMethods.Password` and `Federated`.
- [ ] **Step 4: Run** the two classes; fix the existing `MfaChallenge.Issue` callers in tests to pass `AuthenticationMethods.Password`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): add external login domain and challenge first factor`.

### Task 3: Persistence and migration

**Files:**
- Create: `Auth/Infrastructure/Persistence/UserExternalLoginConfiguration.cs`; migration `AddExternalLogins` (with `.Designer.cs`) and updated `AuthDbContextModelSnapshot.cs`
- Modify: `UserConfiguration.cs` (navigation `_externalLogins`, cascade), `MfaChallengeConfiguration.cs`, `Application/Abstractions/UniqueIndexNames.cs`, `IUserRepository.cs`, `UserRepository.cs` (`Users()` includes `ExternalLogins`), `IUnitOfWork.cs`, `AuthDbContext.cs`
- Modify (docs/tests): `tests/TemplateName.ArchitectureTests/AuditingTests.cs` (add `UserExternalLogin` to the hard-coded set), `docs/modules/audit.md:49`, `docs/modules/auth.md` (Audited entities, Data)
- Test: `tests/TemplateName.IntegrationTests/Auth/ExternalLoginPersistenceTests.cs`

**Interfaces:**
- Produces: `UniqueIndexNames.ExternalLoginIdentity` (`(Provider, Subject)`), `UniqueIndexNames.ExternalLoginProviderPerUser` (`(UserId, Provider)`); `IUserRepository.GetByExternalLoginAsync(string provider, string subject, CancellationToken cancellationToken) : Task<User?>`; `IUnitOfWork.SaveChangesUnlessDuplicateAsync(IReadOnlyCollection<string> uniqueIndexNames, CancellationToken cancellationToken) : Task<bool>` (the single-name overload keeps working); table `auth.UserExternalLogins` as spec 6.1; `MfaChallenges.FirstFactor nvarchar(16) NOT NULL DEFAULT 'pwd'`.

- [ ] **Step 1: Write the failing tests** (integration, CI): `Duplicate_provider_and_subject_is_refused_by_the_index`; `A_user_cannot_hold_two_logins_for_one_provider`; `GetByExternalLoginAsync_loads_the_user_with_roles_and_logins`; `Deleting_a_user_row_cascades_to_its_logins`; `Overlong_subject_is_rejected_by_the_column`; `SaveChangesUnlessDuplicateAsync_with_several_names_returns_false_for_either_index`; `Existing_challenges_read_as_pwd`. Architecture: `AuditingTests` set now lists the entity (fails); `MigrationTests.Every_migration_in_the_repository_is_additive_or_marked` must still pass.
- [ ] **Step 2: Run** the architecture project locally (FAIL on `AuditingTests`); integration tests wait for CI.
- [ ] **Step 3: Implement** the configuration (`Id` `ValueGeneratedNever()`, lengths from the constants, both unique indexes named by the constants, FK to `Users`) and `dotnet ef migrations add AddExternalLogins` with the command of `CLAUDE.md`; read the generated `Up` and confirm it only creates and adds. The multi-name overload matches each name in the SQL error (2601, 2627) like the single one.
- [ ] **Step 4: Run** the architecture and unit projects. Expected: PASS. Push the branch and read the CI integration result.
- [ ] **Step 5: Commit** `feat(persistence): add the external logins table`.

### Task 4: Provider options and catalog

**Files:**
- Create: `Auth/Application/Abstractions/IExternalProviderCatalog.cs`, `ExternalProvider.cs`; `Auth/Infrastructure/External/ExternalLoginOptions.cs`, `ExternalProviderOptions.cs`, `ExternalProviderPresets.cs`, `ExternalProviderCatalog.cs`, `ExternalHttpClients.cs`
- Modify: `AuthModule.cs` (`AddExternalLogin`, called from `AddAuthModule`), `src/Host/TemplateName.Api/appsettings.json` (`Auth:External`, every provider `Enabled: false`), `docs/modules/auth.md` (Configuration), `docs/services/api.md` (Auth sections, resilience pipelines), `docs/building-blocks/infrastructure-common.md` (custom pipeline names)
- Test: `tests/TemplateName.UnitTests/Auth/External/ExternalLoginOptionsTests.cs`, `ExternalProviderCatalogTests.cs`

**Interfaces:**
- Produces: `enum ExternalProviderKind { Oidc, Facebook }`; `enum ExternalEmailRule { EmailVerifiedClaim, MicrosoftDomainVerified, Never }`; `sealed record ExternalProvider(string Name, string DisplayName, ExternalProviderKind Kind, string? Authority, IReadOnlyList<string> Issuers, IReadOnlyList<string> ClientIds, ExternalEmailRule EmailRule, bool TrustEmail, string? TenantId, string? AppId, string? AppSecret)` with `ToString()` returning `Name` only; `IExternalProviderCatalog { ExternalProvider? Find(string name); IReadOnlyList<ExternalProvider> Enabled { get; } string DisplayNameOf(string name); }` (`Find` is case-insensitive and returns only enabled providers; `DisplayNameOf` answers for any configured name, else the name itself); `ExternalLoginOptions` (`SectionName = "Auth:External"`, `bool AllowSignUp = true`, `TimeSpan MaxTokenAge`, `Dictionary<string, ExternalProviderOptions> Providers` with `StringComparer.OrdinalIgnoreCase`); `ExternalHttpClients.OidcDiscovery = "oidc-discovery"`, `FacebookGraph = "facebook-graph"` (client and pipeline names, registered with `AddExternalHttpClient`).

- [ ] **Step 1: Write the failing tests:** disabled providers are never validated or listed; enabled without `ClientIds` fails naming `Auth:External:Providers:Google:ClientIds`; Facebook without `AppId` or `AppSecret` fails; a name other than the four without `Type: "oidc"` fails; `oidc` with a non-`https` or relative `Authority` fails; `MaxTokenAge` of 30 seconds and 2 hours fail, 5 minutes default; failure messages never contain a configured `AppSecret` or client ID value; presets: Google issuers `https://accounts.google.com` and `accounts.google.com`, Apple `https://appleid.apple.com`, Microsoft `https://login.microsoftonline.com/{tenantid}/v2.0` with rule `MicrosoftDomainVerified`, Facebook `https://www.facebook.com` and rule `Never`; `TrustEmail` defaults on for Google, Apple, Microsoft and off for Facebook and generic; `Find("GOOGLE")` equals `Find("google")` and `Name` is lower-case; `ToString()` of a provider with a secret does not contain it.
- [ ] **Step 2: Run** `--filter-class "*ExternalLoginOptionsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** with `ValidateDataAnnotations` plus an `IValidateOptions<ExternalLoginOptions>` and `ValidateOnStart()`; register both named clients through `AddExternalHttpClient(name, name)` (a custom pipeline name needs no other code); document the keys, the two pipeline names and the Microsoft `xms_edov` requirement.
- [ ] **Step 4: Run** the unit project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): add external provider configuration`.

### Task 5: OIDC verifier, replay guard and the fake provider

**Files:**
- Create: `Auth/Application/Abstractions/IExternalIdentityVerifier.cs` (with `ExternalProof`, `VerifiedExternalIdentity`), `IExternalProofReplayGuard.cs`; `Auth/Infrastructure/External/OidcIdTokenVerifier.cs`, `ExternalIdentityVerifier.cs`, `HybridCacheExternalProofReplayGuard.cs`
- Create (tests): `tests/TemplateName.UnitTests/Auth/External/FakeIdentityProvider.cs` (linked into the integration project like `OpenApiCompatibility` is, in its csproj), `OidcIdTokenVerifierTests.cs`, `ExternalProofReplayGuardTests.cs`
- Modify: `AuthModule.cs`

**Interfaces:**
- Produces: `sealed record ExternalProof(string? IdToken, string? AccessToken, string? Nonce)`; `sealed record VerifiedExternalIdentity(string Provider, string Subject, string? Email, bool HasUsableEmail, string? Name)`; `IExternalIdentityVerifier.VerifyAsync(ExternalProvider provider, ExternalProof proof, CancellationToken cancellationToken) : Task<Result<VerifiedExternalIdentity>>` (failures `ExternalLoginErrors.InvalidToken`, `ProviderUnavailable`); `IExternalProofReplayGuard.TryClaimAsync(string provider, string nonce, CancellationToken cancellationToken) : Task<bool>`; `FakeIdentityProvider` (an RSA key, `Issuer`, `ExternalProvider ToProvider(string name = "google", …)`, `string IdToken(Action<IDictionary<string, object>>? customize = null, string? signWith = null)`, an `HttpMessageHandler Handler` that serves discovery and the key set and can be told to fail).
- `ExternalIdentityVerifier` dispatches: `IdToken` to `OidcIdTokenVerifier`, `AccessToken` to the Facebook verifier (Task 6), and on success claims the nonce through `IExternalProofReplayGuard`.

- [ ] **Step 1: Write the failing tests:** a valid token yields the identity (`Subject` is `sub`; Microsoft `{tid}:{oid}`); each of these is `InvalidToken` and burns no nonce: wrong issuer, wrong audience, expired, `nbf` after skew, `iat` older than `MaxTokenAge`, `iat` in the future beyond skew, nonce missing or different, `alg: none`, HS256 signed with the public key bytes, unknown `kid`, tampered payload, missing `sub`, `"x"`, `"a.b"`, non-base64 segments, a 100 kB string, `Subject` over 256 characters; the nonce may equal the raw value or its lower-case hex SHA-256; Google's bare `accounts.google.com` issuer is accepted; Apple `email_verified` as boolean and as `"true"` is usable, `"false"` and missing are not; Microsoft: usable only with `xms_edov` true, a specific `TenantId` rejects another `tid`, a token without `oid` or `tid` is invalid; `TrustEmail = false` gives `HasUsableEmail` false; an empty `email` is never usable and an `email` over 256 characters gives `Email` null and `HasUsableEmail` false; discovery answering 500 or timing out is `ProviderUnavailable`, never `InvalidToken`; an unknown `kid` triggers one key refresh then `InvalidToken`; the same nonce twice for one provider is refused the second time (`TryClaimAsync` false), a different provider or nonce is fine, an expired window frees it (`FakeTimeProvider`).
- [ ] **Step 2: Run** `--filter-class "*OidcIdTokenVerifierTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `OidcIdTokenVerifier`: one `ConfigurationManager<OpenIdConnectConfiguration>` per provider (cached in a singleton dictionary) with `HttpDocumentRetriever` over the `oidc-discovery` client and metadata at `{Authority}/.well-known/openid-configuration`; validate with `JsonWebTokenHandler.ValidateTokenAsync` (`ValidAlgorithms` `RS256` and `ES256`, signed tokens required, the issuer chosen as in 5.2, `ValidAudiences` from `ClientIds`, the lifetime validator against `TimeProvider` with `Auth:Jwt:ClockSkew`, as `JwtValidation` does); `iat` and nonce checked in code. The replay guard uses `HybridCache.GetOrCreateAsync` with a factory flag, key `auth:ext-nonce:{sha256(provider|nonce)}`, expiry `MaxTokenAge` (best effort, spec D9).
- [ ] **Step 4: Run** the unit project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): verify OpenID Connect ID tokens`.

### Task 6: Facebook verifier

**Files:**
- Create: `Auth/Infrastructure/External/FacebookAccessTokenVerifier.cs`, `tests/TemplateName.UnitTests/Auth/External/FacebookAccessTokenVerifierTests.cs`
- Modify: `ExternalIdentityVerifier.cs`, `AuthModule.cs`

**Interfaces:**
- Consumes: `ExternalProvider` (Kind `Facebook`), `ScriptedHttpMessageHandler` (existing unit-test helper), the `facebook-graph` client.
- Produces: `FacebookAccessTokenVerifier.VerifyAsync(ExternalProvider provider, string accessToken, CancellationToken cancellationToken) : Task<Result<VerifiedExternalIdentity>>`; the Facebook provider also accepts an `IdToken` through `OidcIdTokenVerifier` with issuer `https://www.facebook.com` and audience `AppId`.

- [ ] **Step 1: Write the failing tests:** valid `debug_token` plus `/me` gives `Subject` = the Facebook id, `Name`, `Email`, `HasUsableEmail` false; `is_valid` false, `app_id` different, expired `expires_at`, and `/me` id different from `debug_token`'s `user_id` are each `InvalidToken`; Graph 5xx or timeout is `ProviderUnavailable`; the `appsecret_proof` equals the lower-case hex HMAC-SHA256 of the token keyed by `AppSecret`; `debug_token` is called with the app access token `AppId|AppSecret`; the access token and app secret appear in no captured log message; a Limited Login `idToken` is verified by the OIDC path (audience `AppId`) and never calls Graph.
- [ ] **Step 2: Run** `--filter-class "*FacebookAccessTokenVerifierTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the two Graph calls (`/debug_token`, `/me?fields=id,name,email`) against `https://graph.facebook.com`; keep the app access token and the user token out of logs (a fake logger asserts it).
- [ ] **Step 4: Run** the unit project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): verify Facebook access tokens`.

### Task 7: MFA challenge carries the first factor

**Files:**
- Modify: `Auth/Application/Authentication/Login/LoginCommandHandler.cs` (`ChallengeAsync`), `Auth/Application/Mfa/VerifyMfa/VerifyMfaCommandHandler.cs` (line ~92); `docs/modules/auth.md` (Challenge and verify)
- Test: `tests/TemplateName.UnitTests/Auth/VerifyMfaCommandHandlerTests.cs`, `LoginCommandHandlerTests.cs`; `tests/TemplateName.IntegrationTests/Auth/MfaLoginTests.cs`

**Interfaces:**
- Consumes: `MfaChallenge.Issue(…, firstFactor, …)`, `AuthenticationMethods.Join`.
- Produces: password login issues challenges with `FirstFactor = pwd`; the verify handler builds `amr` as `Join(challenge.FirstFactor, otp|rcvr, mfa)`; `rememberDevice` is ignored (no trusted device is created) when the factor is `fed`.

- [ ] **Step 1: Write the failing tests:** `Verify_of_a_pwd_challenge_still_gives_pwd_otp_mfa`; `Verify_of_a_fed_challenge_gives_fed_otp_mfa`; `Verify_of_a_fed_challenge_with_a_recovery_code_gives_fed_rcvr_mfa`; `Remember_device_is_ignored_for_a_fed_challenge`; `Password_login_issues_a_pwd_challenge`.
- [ ] **Step 2: Run** `--filter-class "*VerifyMfaCommandHandlerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the two call-site changes only.
- [ ] **Step 4: Run** the unit project. Expected: PASS (integration in CI).
- [ ] **Step 5: Commit** `feat(auth): record the first factor on the MFA challenge`.

### Task 8: External login and the provider list

**Files:**
- Create: `Auth/Application/ExternalLogins/Login/ExternalLoginCommand.cs`, `ExternalLoginCommandHandler.cs`, `ExternalLoginCommandValidator.cs`; `Auth/Application/ExternalLogins/Providers/ListExternalProvidersQuery.cs`, `ListExternalProvidersQueryHandler.cs`, `ExternalProviderResponse.cs`; `Auth/Endpoints/ExternalLoginEndpoints.cs`, `ExternalLoginRequest.cs`
- Modify: `AuthModule.cs` (map the `auth/external` group), the module's meter class (counter `auth.external_logins`, tags `provider` for a configured provider only and `outcome`), `IntegrationTestWebAppFactory.cs` (a fake provider on the two named clients), `tests/TemplateName.IntegrationTests/Auth/AuthStrictRoutesTests.cs` (eleven routes, add `POST /api/v1/auth/external/{provider}/login`; rename the test), `docs/modules/auth.md` (endpoints table, "The ten `auth-strict` routes" to eleven, a Login section), `docs/services/api.md` (~167), the OpenAPI snapshot `OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json`
- Test: `tests/TemplateName.UnitTests/Auth/ExternalLoginCommandHandlerTests.cs`, `ExternalLoginCommandValidatorTests.cs`; `tests/TemplateName.IntegrationTests/Auth/ExternalLoginTests.cs`

**Interfaces:**
- Consumes: `IExternalProviderCatalog`, `IExternalIdentityVerifier`, `IUserRepository.GetByExternalLoginAsync` and `GetByNormalizedEmailAsync`, `User.RegisterExternal`, `User.LinkExternalLogin`, `MfaChallenge.Issue`, `SessionStarter.StartAsync`, `IMfaMethodRepository.HasConfirmedAsync`, `IUnitOfWork.SaveChangesUnlessDuplicateAsync(IReadOnlyCollection<string>, …)`, the `User` system role lookup of `RegisterCommandHandler`.
- Produces: `sealed record ExternalLoginCommand(string Provider, string? IdToken, string? AccessToken, string? Nonce, string? DisplayName, string? Locale, string? DeviceName) : ICommand<LoginResult>`; `sealed record ExternalProviderResponse(string Name, string DisplayName, string Type)` (`oidc` or `facebook`); `ListExternalProvidersQuery : IQuery<IReadOnlyList<ExternalProviderResponse>>`; routes `GET /api/v1/auth/external/providers` (anonymous) and `POST /api/v1/auth/external/{provider}/login` (anonymous, `auth-strict`, `no-store`, answers `LoginResult`).

- [ ] **Step 1: Write the failing tests.** Validator: exactly one of `idToken` and `accessToken`; `accessToken` only for Facebook; nonce required with `idToken` and 16 to 256 characters; lengths 8192, 200, 200; `locale` a predefined culture. Handler (unit, fake verifier): linked identity signs in and notes use; unknown provider and disabled provider alike `ProviderNotFound`; no usable email and no link `EmailUnverified`; email of a confirmed account links and signs in (event and audit `auth.external_linked`); email of an unconfirmed account `LinkRequired`; no account with sign-up on registers (role `User`, no password, confirmed, audit `auth.external_registered`); sign-up off `SignUpDisabled`; suspended `account_inactive` and locked `InvalidCredentials` with no failure counted; a confirmed MFA method issues a `fed` challenge and reads no trusted-device token; otherwise `SessionStarter` runs with `fed`; a lost duplicate race re-reads and ends on the link or link-required path; `Alice@Example.com ` links to `alice@example.com`; a missing name falls back to the email's local part cut to 200; an absent `locale` takes the request's language (`CultureInfo.CurrentUICulture`). Integration (fake provider): `POST /Google/login` with a mixed-case provider; the whole flow to `GET /api/v1/auth/me`; challenge then `POST /api/v1/auth/mfa/verify` gives `amr` `fed otp mfa`; two simultaneous first sign-ins create one account; `GET /providers` lists only enabled ones; a disabled provider answers 404.
- [ ] **Step 2: Run** `--filter-class "*ExternalLoginCommandHandlerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** handler order per spec 5.1 and 5.4. Failures are audited `auth.external_login_failed` with the error code as reason; a success audits `auth.external_login_succeeded` with the session id and `{provider}`. `ToString()` on command and request prints the type name only.
- [ ] **Step 4: Run** unit and architecture projects, regenerate the OpenAPI snapshot from `.received.json`, and read the CI integration result. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): add external login and provider list`.

### Task 9: Link, list and unlink

**Files:**
- Create: `Auth/Application/ExternalLogins/{Link,Unlink,List}/` (command, handler, validator or query, handler, response each); `Endpoints/ExternalLoginEndpoints.cs` (extend)
- Modify: `docs/modules/auth.md` (endpoints table, Passwords and step-up note), OpenAPI snapshot
- Test: `tests/TemplateName.UnitTests/Auth/LinkExternalLoginCommandHandlerTests.cs`, `UnlinkExternalLoginCommandHandlerTests.cs`; `tests/TemplateName.IntegrationTests/Auth/ExternalLoginManagementTests.cs`

**Interfaces:**
- Consumes: `CallerSession.ResolveAsync`, `StepUpPolicy.Check`, `ISessionRepository.GetActiveByUserAsync`, `UserSession.Revoke`, `AdoptSecurityStamp`, Task 8's verifier path.
- Produces: `LinkExternalLoginCommand(string Provider, string? IdToken, string? AccessToken, string? Nonce) : ICommand<ExternalLoginResponse>`; `sealed record ExternalLoginResponse(Guid Id, string Provider, string? Email, DateTimeOffset LinkedAt, DateTimeOffset? LastUsedAt)`; `ListExternalLoginsQuery : IQuery<ExternalLoginsResponse>`; `sealed record ExternalLoginsResponse(bool HasPassword, IReadOnlyList<ExternalLoginResponse> Logins)` (Dapper read, `IsDeleted = 0` written by hand per ADR 0006; provider shown through `DisplayNameOf`); `UnlinkExternalLoginCommand(Guid Id) : ICommand`; routes `POST /api/v1/auth/external/{provider}/link`, `GET /api/v1/auth/external/logins`, `DELETE /api/v1/auth/external/logins/{id:guid}`, all `.RequireAuthorization()`.

- [ ] **Step 1: Write the failing tests:** link without a fresh step-up `step_up_required`; an MFA account needs a session with `otp` or `rcvr`; a disabled provider `ProviderNotFound`; an identity of another user `IdentityTaken`; a second identity for one provider `ProviderAlreadyLinked`; the same identity again is 200 unchanged; success audits `auth.external_linked` and raises the event; unlink of another user's or an unknown id `LoginNotFound`; unlink of the last method `LastLoginMethod`; unlink works for a disabled provider; unlink rotates the stamp, revokes the caller's other sessions with `ExternalLoginChanged`, keeps the current one, audits `auth.external_unlinked` with the revoked count; `GET /logins` shows `hasPassword` and a disabled provider's link by its key.
- [ ] **Step 2: Run** `--filter-class "*UnlinkExternalLoginCommandHandlerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** after the model of `RemoveMfaMethodCommandHandler` (`CallerSession`, `StepUpPolicy`, revoke, one save); link saves with `SaveChangesUnlessDuplicateAsync(UniqueIndexNames.ExternalLoginIdentity)` and answers `IdentityTaken` when it loses.
- [ ] **Step 4: Run** unit and architecture projects, update the OpenAPI snapshot, read the CI result. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): link, list and unlink external logins`.

### Task 10: Step-up by provider proof

**Files:**
- Modify: `Auth/Application/StepUp/StepUpCommand.cs`, `StepUpCommandValidator.cs`, `StepUpCommandHandler.cs`; `Auth/Endpoints/StepUpRequest.cs`; `docs/modules/auth.md` (Step-up, endpoints row for `POST /api/v1/auth/step-up`); OpenAPI snapshot
- Test: `tests/TemplateName.UnitTests/Auth/StepUpCommandHandlerTests.cs`, `StepUpCommandValidatorTests.cs`, `StepUpPolicyTests.cs`; `tests/TemplateName.IntegrationTests/Auth/StepUpTests.cs`

**Interfaces:**
- Produces: `StepUpCommand(string? Password, string? Code, string? RecoveryCode, string? Provider = null, string? IdToken = null, string? AccessToken = null, string? Nonce = null)`; the provider proof counts as one of the "exactly one proof" (blank is absent; `Provider` required with it; nonce required with `idToken`); allowed only for an account with no password and no confirmed MFA method, otherwise `auth.step_up_method_not_allowed`; the proof's `(Provider, Subject)` must be one of the caller's logins else `ExternalLoginErrors.StepUpIdentityMismatch`; success replaces `amr` with `fed`; a wrong proof is not counted toward `Auth:Lockout`; a locked account is refused as today. `StepUpPolicy` needs no change.

- [ ] **Step 1: Write the failing tests:** a password-less account steps up by proof and gets `amr` `fed` and a fresh `auth_time`; a proof of another user's identity `StepUpIdentityMismatch`; a password account sending a proof `StepUpMethodNotAllowed`; an MFA account sending a proof `StepUpMethodNotAllowed`; a proof together with a password fails validation; an invalid proof is `InvalidToken` and the failure count is unchanged; `StepUpPolicy.Check(false)` passes for a fresh `fed` session and fails once `Auth:StepUp:MaxAge` has passed; the existing password and code step-ups are unchanged.
- [ ] **Step 2: Run** `--filter-class "*StepUpCommandHandlerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the new branch in `VerifyAsync` before the password branch; resolve the provider from the catalog (a disabled provider is `ProviderNotFound`) and verify through `IExternalIdentityVerifier`.
- [ ] **Step 4: Run** unit and architecture projects, update the OpenAPI snapshot, read the CI result. Expected: PASS.
- [ ] **Step 5: Commit** `feat(auth): step up with a provider proof`.

### Task 11: Notifications for link and unlink

**Files:**
- Create: `Auth.Contracts/IntegrationEvents/ExternalLoginLinkedIntegrationEvent.cs`, `ExternalLoginUnlinkedIntegrationEvent.cs`; `Auth/Application/IntegrationEvents/PublishExternalLoginLinkedDomainEventHandler.cs`, `PublishExternalLoginUnlinkedDomainEventHandler.cs`; in Notifications: `Application/AuthEvents/ExternalLoginLinkedIntegrationEventHandler.cs`, `ExternalLoginUnlinkedIntegrationEventHandler.cs`; templates `Templates/Email/auth.external_login_{linked,unlinked}.{en,ms,zh-Hans}.{subject,text,html}.scriban` and `Templates/InApp/…{title,body}.scriban`
- Modify: `Application/Catalog/AuthNotificationTypes.cs`, `AuthNotificationTypeSource.cs`; `IntegrationTestWebAppFactory.cs` (recording handlers for both events); tests `AuthNotificationTypesTests` (ten to twelve), `TemplateCompletenessTests` (the hard-coded paragraph list), `AuthEventHandlerTests`, `AuthNotificationRenderTests`, `AuthEventConsumptionTests`, `ContractsTests`; docs `docs/modules/notifications.md` (Auth's types, counts, Events), `docs/modules/auth.md` (integration events table)
- Test: `tests/TemplateName.UnitTests/Notifications/ExternalLoginEventHandlerTests.cs`

**Interfaces:**
- Consumes: `ExternalLoginLinkedDomainEvent`, `ExternalLoginUnlinkedDomainEvent`, `IExternalProviderCatalog.DisplayNameOf`, `AuthEventRequests.Notice` and `LocalTime`.
- Produces: `public sealed record ExternalLoginLinkedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string Provider, string ProviderDisplayName) : IIntegrationEvent` and the same shape for `…Unlinked…`; notification type codes `auth.external_login_linked` and `auth.external_login_unlinked` (Security, Critical, email mandatory, in-app as `auth.password_changed`), variables `provider` and `occurred_at`.

- [ ] **Step 1: Write the failing tests:** each domain event publishes the integration event with the display name (the key when the provider is no longer configured); the Notifications handler schedules the right type with `provider` and local `occurred_at`; every template exists for `en`, `ms`, `zh-Hans` and renders without a missing variable; `ContractsTests` accepts both events; the consumption integration test sends the email (CI).
- [ ] **Step 2: Run** `--filter-class "*ExternalLoginEventHandlerTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** after `MfaChanged` end to end (domain handler, contract, consumer); read `docs/modules/notifications.md` "Adding a type's templates" first.
- [ ] **Step 4: Run** the unit and architecture projects. Expected: PASS; read the CI result for the integration tests.
- [ ] **Step 5: Commit** `feat(notifications): add external login security notices`.

### Task 12: ADR, guides, roadmap and release check

**Files:**
- Create: `docs/adr/0032-external-login-by-client-posted-proof.md` (D1, D2, D4, D5, D7, D8; consequences: `fed` is not an RFC 8176 value; the verified-email rule; Microsoft's `xms_edov`); `docs/social-login-setup.md` (per provider: console settings, client IDs per platform, Apple Services ID and bundle ID and the hashed nonce, Microsoft `openid profile email` scope and the `xms_edov` optional claim, Facebook app ID and secret and Limited Login, a manual checklist including one personal and one work or school Microsoft account, the generic OIDC entry)
- Modify: `docs/README.md` and `docs/architecture/overview.md` (list the ADR as `adr/0032-….md)`), `docs/modules/auth.md` (Purpose, an External login section, Known limits: replace the "planned as their own plans" sentence at ~1193, add the replay and Facebook limits), `docs/blueprint-review.md` Section 6 (a row for social login, done; phone OTP stays after SMS), ADR 0023 and 0025 follow-up lines, the spec status and the memory note
- Test: the documentation architecture tests

**Interfaces:**
- Consumes: everything above.
- Produces: a documented, releasable feature.

- [ ] **Step 1: Write the failing check:** run the architecture project: `Every_adr_is_listed_in_the_docs_index_and_the_architecture_overview` fails until the ADR is linked.
- [ ] **Step 2: Run** `dotnet test --project tests/TemplateName.ArchitectureTests -c Release`. Expected: FAIL.
- [ ] **Step 3: Write** the documents above; link the setup guide from `docs/README.md` and the auth page.
- [ ] **Step 4: Verify** in order: `dotnet build -c Release` (0 warnings), `dotnet format --verify-no-changes`, unit and architecture projects, `bash build/scripts/template-smoke.sh`; push and read every CI job (`build-test`, `template-smoke`, `licenses`) including `OpenApi_changes_are_compatible_with_the_baseline` (it must run, not skip, and classify the change as compatible). Expected: all green.
- [ ] **Step 5: Commit** `docs(adr): add the external login decision and setup guide`; open the PR as `feat(auth): add social login`.
