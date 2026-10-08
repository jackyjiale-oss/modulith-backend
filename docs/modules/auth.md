# Auth module

<!-- Copied from docs/modules/_template.md. Each Auth task updates the sections it changes; the module is built up over Plan 2. -->

## Purpose and boundaries

<!-- What the module owns, what it deliberately does not do, and which other modules it talks to (only through *.Contracts). -->

The sign-in and authorization module: registration with email confirmation, login, refresh-token rotation, logout, password reset and change, session management, and role-based permissions with administration of users, roles and the audit log. It is built up over Plan 2 (auth core); this page describes what has landed so far (the domain model, its persistence, the security services, the email sender, the access tokens with their JWKS, the permission checks with their cache and the seeding, registration with email confirmation, and login with lockout and the current user).

- Owns: users, roles, permissions, sessions, verification codes, the auth audit log, the Data Protection key ring and the `auth` schema.
- Does not: send localized emails (English only until Plan 3), check access tokens against session revocation on every request (ADR 0015), or support usernames, tenants or progressive lockout.
- Talks to: no other module. It has no `TemplateName.Modules.Auth.Contracts` project yet, because nothing outside the module needs its data; Plan 3 adds the integration events.
- Decisions: [ADR 0014](../adr/0014-own-identity-model-with-identity-password-hasher.md) (own identity model), [ADR 0015](../adr/0015-es256-jwt-with-configured-signing-keys.md) (ES256 tokens, configured keys), [ADR 0016](../adr/0016-server-side-permissions-with-per-user-cache.md) (permissions resolved server-side), [ADR 0017](../adr/0017-outbox-secrets-protected-with-data-protection.md) (outbox secrets protected with Data Protection).

Code: `src/Modules/Auth/TemplateName.Modules.Auth/`. The host calls `AddAuthModule(configuration)`, maps `MapAuthEndpoints()` on the `/api/v1` group and `MapAuthWellKnownEndpoints()` on the application root (for `/.well-known/jwks.json`). Today the module registers its persistence (context, outbox, repositories, audit writer, Data Protection key ring; see [Data](#data)), the security services (see [Security services](#security-services)), the access tokens and the JWT bearer handler (see [Access tokens](#access-tokens)), the permission checker, its cache and the seeder (see [Permission checks](#permission-checks)), its handlers (see [Registration](#registration) and [Login and current user](#login-and-current-user)), the sign-in settings, the counters of its meter (see [Observability](#observability)) and its error messages; `MapAuthEndpoints` maps the self-service `auth` group (`Endpoints/AuthEndpoints.cs`, see [Endpoints](#endpoints)). The host also calls `SeedAuthModuleAsync(cancellationToken)` after the migration step (see [Background processing](#background-processing)).

## Endpoints

<!-- Every endpoint, written as METHOD + full route (e.g. GET /api/v1/{module}/{resources}/{id:guid}), with its success status and error codes. -->

The module's routes are `/api/v1/auth/...` (self-service, tag `Auth`) and, later, `/api/v1/admin/auth/...` (administration). The JWKS lives at the root, outside `/api/v1`, because verifiers look for it at the well-known path.

Every route below except `GET /api/v1/auth/me` is anonymous: it says `.AllowAnonymous()` explicitly and has `.RequireRateLimiting(RateLimitPolicies.AuthStrict)` (10 requests per minute per client address by default, shared by all `auth-strict` routes, on top of the global limit; 429 `rate_limit.exceeded` with `Retry-After`). The bodies are JSON; a missing or malformed body, or one that fails the validator, is a 400 validation problem (`validation.failed`, with `errors` per field), which says nothing about accounts. `GET /api/v1/auth/me` says `.RequireAuthorization()` explicitly (Ruling R13), so the OpenAPI document shows its bearer requirement; it is limited by the global limiter only (per user).

| Method | Route | Purpose | Success | Errors |
|---|---|---|---|---|
| `POST` | `POST /api/v1/auth/register` | Creates an account (`{ email, password, displayName, locale? }`) and emails its confirmation link; for an address that already has an account, emails its owner a notice instead (see [Registration](#registration)). `locale` defaults to the request's language (`CultureInfo.CurrentUICulture`, chosen from `Accept-Language`). | 202, no body, the same for a new and an existing address | 400 `validation.failed`; 400 `auth.password_breached`; 429 |
| `POST` | `POST /api/v1/auth/email/confirm` | Confirms the address with the token from the link (`{ token }`). | 204 | 400 `validation.failed`; 400 `auth.invalid_token` (unknown, used, replaced, expired or another purpose's token, all alike); 429 |
| `POST` | `POST /api/v1/auth/email/resend-confirmation` | Sends a new confirmation link to an unconfirmed account (`{ email }`) and invalidates the pending ones. | 202, no body, whether or not anything was sent | 400 `validation.failed`; 429 |
| `POST` | `POST /api/v1/auth/login` | Signs in (`{ email, password, deviceName? }`; `deviceName` at most 200 characters, `Unknown` when absent) and starts a session (see [Login and current user](#login-and-current-user)). | 200 `{ accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt, sessionId }` | 400 `validation.failed`; 401 `auth.invalid_credentials` (unknown email, wrong password, no password set, locked account: all alike); 403 `auth.email_not_verified`, 403 `auth.account_inactive` (only after a correct password); 429 |
| `GET` | `GET /api/v1/auth/me` | The signed-in user: `{ id, email, emailConfirmed, displayName, locale, timeZone, roles, permissions }`. Never a hash, a stamp or a token. | 200 | 401 `http.401` (no valid token, or the token's user is unknown, soft-deleted or suspended) |
| `GET` | `/.well-known/jwks.json` | The public keys that verify access tokens (RFC 7517). Anonymous; `Cache-Control: public, max-age=300`. Left out of the OpenAPI document, like the health checks. | 200 `{"keys":[...]}`: one key per configured entry, each with only `kty` (`EC`), `crv` (`P-256`), `x`, `y`, `kid`, `alg` (`ES256`) and `use` (`sig`) | — |

## Domain model

<!-- Aggregates, their invariants and life cycle. Draw state machines as a Mermaid stateDiagram-v2. -->

`User` (`Domain/Users/`) is the first aggregate and `Role` (`Domain/Roles/`) the second; `UserSession` (`Domain/Sessions/`) the third and `VerificationCode` (`Domain/Verification/`) the fourth; the audit log entry `AuthAuditLog` (`Domain/Audit/`) is a plain entity. A user is identified by its email, kept trimmed (`Email`) and upper-case (`NormalizedEmail`, the form used for lookups and the unique index). It owns its password history (`PasswordHistoryEntry`) and its role assignments (`UserRole`), and it is audited and soft-deleted.

- **Registration:** `User.Register(email, displayName, locale, passwordHash, now)` creates an active, unconfirmed user with a new `SecurityStamp` and the time zone `UTC`. A user created by an administrator has no `PasswordHash` (valid; the history is then empty); a non-null hash is the first history entry.
- **Email confirmation:** `ConfirmEmail` is idempotent; only the first call raises `EmailConfirmedDomainEvent`. `EnsureCanSignIn` refuses a suspended user (`auth.account_inactive`) and an unconfirmed one (`auth.email_not_verified`).
- **Lockout:** `RecordFailedSignIn` counts failures; at the configured threshold it sets `LockoutEnd` to `now + duration` and raises `UserLockedOutDomainEvent`. The account is locked while `now < LockoutEnd`, so it is open again exactly at `LockoutEnd`. A failure after the lockout expired starts a new count; a failure while locked changes nothing. A successful sign-in, a password change and `Reinstate` clear the failures and the lockout.
- **Password:** `ChangePassword` rotates `SecurityStamp`, appends the new hash to the history and keeps at most `historyCount` entries including the current one (the oldest are dropped). The reuse check itself runs in the application layer against the history. `UpgradePasswordHash` stores a new hash of the **same** password (a rehash after a successful verification with weaker parameters): only `PasswordHash` changes, not the stamp, the history, `PasswordChangedAt` or the sign-in state, and no event is raised; a user without a password throws `InvalidOperationException`.
- **Suspension:** `Suspend` rotates `SecurityStamp` (so issued tokens stop matching) and is idempotent; `Reinstate` makes the account active again and is idempotent.
- **Registration attempts:** `NoteRegistrationAttempt` raises `RegistrationAttemptedDomainEvent` and changes no state, so the registration answer stays the same for known and unknown addresses.
- **Roles:** `AssignRole` and `RemoveRole` are idempotent. A user refers to a role by id (`UserRole`); the `Role` aggregate is described below.

`UserStatus` (`Domain/Users/`) is persisted by its numeric value, so a new state is appended and existing values are never renumbered.

```mermaid
stateDiagram-v2
    [*] --> Active: register
    Active --> Suspended: suspend
    Suspended --> Active: reinstate
```

`Role` (`Domain/Roles/`) is a named set of permissions, audited and soft-deleted, with a `RowVersion` for optimistic concurrency. `Permission` is a plain entity (`Code`, `Module`, `Name`, `Description`, `IsDeprecated`); `RolePermission(RoleId, PermissionId)` is a grant.

- **Names:** `Role.Create(name, description, now)` trims the name and stores `NormalizedName` (upper-case, the form for the unique index); a blank name is a programming error (`ArgumentException`), because the application validator rejects it first.
- **System roles:** `SystemRoles` names `SuperAdmin`, `Admin` and `User`. `Role.CreateSystem` builds them for the seeder. A system role cannot be renamed (`UpdateDetails`) or deleted (`MarkDeleted`): both answer `auth.system_role_protected`. `MarkDeleted` only checks the rule; the save interceptor performs the soft delete.
- **Permission set:** `SetPermissions` replaces the set, ignores duplicates and is idempotent; it is refused for `SuperAdmin` (`auth.system_role_protected`), whose set the seeder keeps equal to every declared permission through `SyncPermissions` (same replacement, no protection, internal use only).
- **Permissions:** a permission that is no longer declared is deprecated (`SetDeprecated`), never deleted, so existing grants stay readable.

### Permissions

Codes are `module.resource.action` in snake case. Each module declares its own through `IPermissionSource` (see [Application.Common](../building-blocks/application-common.md)), registered as a singleton; the Auth module declares these in `Application/AuthPermissions.cs` and `AuthPermissionSource` (module `auth`, English names), which `AddAuthModule` registers. The seeder syncs every registered source into `auth.Permissions` (see [Background processing](#background-processing)). Other modules declare theirs the same way without referencing this module: the Sample module's `sample.leave_request.view`, `.create` and `.approve` ([Sample module](sample.md#permissions)).

| Code | Allows |
|---|---|
| `auth.user.view` | List users and read their details. |
| `auth.user.create` | Create a user account on behalf of someone. |
| `auth.user.lock` | Suspend and reinstate user accounts. |
| `auth.user.reset_password` | Send a password reset to a user. |
| `auth.user.revoke_sessions` | End the sessions of a user. |
| `auth.user.assign_roles` | Add and remove the roles of a user. |
| `auth.role.view` | List roles and read their permissions. |
| `auth.role.manage` | Create, change and delete roles and set their permissions. |
| `auth.permission.view` | List the permissions that modules declare. |
| `auth.audit.view` | Read the authentication and administration audit log. |

The seeded system roles hold: `SuperAdmin` every permission that is not deprecated, kept equal to that set on each seeding run; `Admin` the defaults `auth.user.view`, `auth.user.create`, `auth.user.lock`, `auth.user.reset_password`, `auth.user.revoke_sessions`, `auth.role.view`, `auth.permission.view` and `auth.audit.view`, given only when the role is first created (later changes by administrators are kept); `User` nothing.

`UserSession` (`Domain/Sessions/`) is one sign-in on one device, and also the refresh-token family (decision D2: there is no `FamilyId`). Its id is the `sid` claim of the access tokens. It owns its `RefreshToken` chain; a token stores only the SHA-256 hash (`TokenHash`), never the token.

- **Start:** `UserSession.Start(userId, authMethods, deviceName, userAgent, ipAddress, securityStamp, firstTokenHash, slidingLifetime, absoluteLifetime, now)` creates the session and its first token. `SecurityStamp` is a **snapshot** of the user's stamp at sign-in; `ExpiresAt` is absolute (`now + absoluteLifetime`) and sliding refresh never moves it. Tokens expire at `min(now + slidingLifetime, session.ExpiresAt)`, so no token outlives the session. `IsActive(now)` is true while the session is not revoked and `now < ExpiresAt`. The device name, user agent and address are client-supplied, so `Start` cuts them to their columns (`UserSession.MaxDeviceNameLength` 200, `MaxUserAgentLength` 512, `MaxIpAddressLength` 45) instead of letting a long header fail the sign-in.
- **Rotate:** `Rotate(presentedTokenHash, newTokenHash, currentSecurityStamp, slidingLifetime, now)` is checked in this order. The caller saves the aggregate on every outcome, because some failures revoke the session.

  | Presented token | Result | Effect on the session |
  |---|---|---|
  | Hash matches no token, or the session is already revoked | `auth.invalid_refresh_token` | None. |
  | Current security stamp differs from the snapshot | `auth.invalid_refresh_token` | Revoked with `PasswordChanged`. |
  | Token already used or revoked | `auth.refresh_token_reused` | Revoked with `TokenReuse` (every token revoked), `RefreshTokenReuseDetectedDomainEvent` raised. |
  | Token or session expired | `auth.refresh_token_expired` | None. |
  | Valid | The new `RefreshToken` | Old token gets `UsedAt` and `ReplacedByTokenId`, `LastSeenAt` moves to `now`. |

  Hashes are compared with `CryptographicOperations.FixedTimeEquals` against every token of the session (no early exit), so timing does not tell which token matched. An unknown token, a token of a revoked session and a stamp mismatch all give `auth.invalid_refresh_token`.
- **Revoke:** `Revoke(reason, now)` ends the session and revokes every token that is not yet revoked. It is idempotent: a second call keeps the first time and reason.

`SessionRevokedReason` is persisted by its numeric value (`Logout`, `LogoutAll`, `PasswordChanged`, `AdminRevoked`, `TokenReuse`, `Expired`), so a new reason is appended and existing values are never renumbered.

```mermaid
stateDiagram-v2
    [*] --> Active: start
    Active --> Active: rotate
    Active --> Revoked: logout, logout all, password changed, admin revoked, token reuse
    Active --> Expired: absolute expiry reached
    Revoked --> [*]
    Expired --> [*]
```

`VerificationCode` (`Domain/Verification/`) is a single-use code behind an emailed link. `VerificationPurpose` (`EmailVerify = 1`, `PasswordReset = 2`) is persisted by its numeric value, so a new purpose is appended and existing values are never renumbered. Only the SHA-256 hash of the token is stored (`TokenHash`); the repository finds a code by that unique hash, so the domain compares no hashes. The token itself travels only inside the issued event, as `ProtectedToken`: the application encrypts it with Data Protection (ADR 0017) and the event handler decrypts it to build the link. It is never a property of the aggregate.

- **Issue:** `VerificationCode.Issue(userId, purpose, target, tokenHash, protectedToken, lifetime, createdIp, now)` sets `ExpiresAt = now + lifetime` (a non-positive lifetime is a programming error) and raises `VerificationCodeIssuedDomainEvent`. `Target` is the normalized email address. `createdIp` is cut to `VerificationCode.MaxCreatedIpLength` (45).
- **Consume:** `Consume(expected, now)` succeeds once. It fails with `auth.invalid_token` when the purpose differs, or the code is consumed, invalidated or expired. A code is valid up to, not including, `ExpiresAt`. Every cause gives the same error so the answer does not tell which one applied.
- **Invalidate:** `Invalidate(now)` makes a pending code unusable (a newer code replaces it, or the account state makes it useless). It is idempotent and leaves a consumed code unchanged. `IsPending(now)` is true while the code is neither consumed, invalidated nor expired.

```mermaid
stateDiagram-v2
    [*] --> Pending: issue
    Pending --> Consumed: consume
    Pending --> Invalidated: invalidate
    Pending --> Expired: ExpiresAt reached
    Consumed --> [*]
    Invalidated --> [*]
    Expired --> [*]
```

`AuthAuditLog` (`Domain/Audit/`) is one line of the append-only audit log. It is a plain entity with a `bigint` identity key, not an aggregate, and raises no domain events. `AuthAuditLog.Create(eventType, succeeded, now, userId, failureReason, attemptedIdentifier, sessionId, details)` leaves the client fields empty; the audit writer calls `SetClientInfo(ipAddress, userAgent, traceId)` from the request, which cuts the values to their columns (45, 512 and 64 characters). `AttemptedIdentifier` is cut to 256 characters. `Details` is JSON text that the domain neither validates nor bounds. `MaskIdentifier` makes an identifier safe to log: `alice@example.com` becomes `a****@example.com`, a value without `@` keeps its first character (`a****`), and null or blank gives null.

### Audit events

The event types are the constants of `AuthAuditEvents` (stored as text, never renamed):

- Registration and confirmation: `auth.registered`, `auth.register_duplicate`, `auth.email_confirmed`, `auth.email_confirm_failed`, `auth.confirmation_resent`.
- Sign-in and tokens: `auth.login_succeeded`, `auth.login_failed`, `auth.locked_out`, `auth.token_refreshed`, `auth.refresh_failed`, `auth.token_reuse_detected`.
- Sessions: `auth.logout`, `auth.logout_all`, `auth.session_revoked`.
- Passwords and profile: `auth.password_forgot_requested`, `auth.password_reset`, `auth.password_reset_failed`, `auth.password_changed`, `auth.password_change_failed`, `auth.profile_updated`.
- Administration: `auth.admin_user_created`, `auth.admin_user_locked`, `auth.admin_user_unlocked`, `auth.admin_password_reset_forced`, `auth.admin_sessions_revoked`, `auth.admin_roles_assigned`.
- Roles: `auth.role_created`, `auth.role_updated`, `auth.role_deleted`, `auth.role_permissions_changed`.

## Error codes

<!-- Every error code the module returns ({module}.snake_case), its HTTP status, its params and its English message. Messages live in Resources/{Module}ErrorMessages.resx with .ms.resx and .zh-Hans.resx (ADR 0009). -->

| Code | HTTP | `params` | Message (en) |
|---|---|---|---|
| `auth.invalid_credentials` | 401 | — | The email or password is incorrect. |
| `auth.email_not_verified` | 403 | — | The email address has not been verified. |
| `auth.account_inactive` | 403 | — | The account is not active. |
| `auth.user_not_found` | 404 | `id` | User '{id}' was not found. |
| `auth.password_reused` | 400 | — | The password was used recently. Choose a different one. |
| `auth.password_breached` | 400 | — | The password appears in a known data breach. Choose a different one. |
| `auth.current_password_incorrect` | 400 | — | The current password is incorrect. |
| `auth.role_not_found` | 404 | `id` | Role '{id}' was not found. |
| `auth.role_name_taken` | 409 | `name` | A role named '{name}' already exists. |
| `auth.system_role_protected` | 409 | — | This is a system role and cannot be changed this way. |
| `auth.permission_not_found` | 400 | — | One or more of the permissions do not exist. |
| `auth.invalid_refresh_token` | 401 | — | The refresh token is not valid. |
| `auth.refresh_token_expired` | 401 | — | The refresh token has expired. Sign in again. |
| `auth.refresh_token_reused` | 401 | — | The refresh token was already used. The session has been ended; sign in again. |
| `auth.session_not_found` | 404 | `id` | Session '{id}' was not found. |
| `auth.invalid_token` | 400 | — | The link is invalid or has expired. |

The user codes are declared in `Domain/Users/UserErrors.cs`, the role and permission codes in `Domain/Roles/RoleErrors.cs` and the session codes in `Domain/Sessions/SessionErrors.cs` and `auth.invalid_token` in `Domain/Verification/VerificationErrors.cs` (one answer for an unknown, used, replaced, expired or wrong-purpose token). Their messages are in `Resources/AuthErrorMessages.resx` (English) with `AuthErrorMessages.ms.resx` and `AuthErrorMessages.zh-Hans.resx` (drafts awaiting native review); every further code is added to all three files in the same change that introduces it. A locked account answers with `auth.invalid_credentials`, never a distinct code (it would reveal that the email exists).

## Events

<!-- Domain events (internal, dispatched through the module outbox) and integration events (published in *.Contracts), with their handlers. -->

| Event | Kind | Raised when | Handlers |
|---|---|---|---|
| `UserRegisteredDomainEvent(UserId, Email, Locale)` | Domain | A user registers | None yet (the confirmation email hangs off the code's event below) |
| `EmailConfirmedDomainEvent(UserId, Email)` | Domain | A user confirms their email address for the first time | None yet |
| `PasswordChangedDomainEvent(UserId, Email, Locale)` | Domain | A password is changed or reset | None yet |
| `UserLockedOutDomainEvent(UserId, LockoutEnd)` | Domain | A failed sign-in reaches the lockout threshold | None yet |
| `RegistrationAttemptedDomainEvent(UserId, Email, Locale)` | Domain | Someone registers an address that already has an account | `SendRegistrationAttemptedEmailDomainEventHandler` (`Application/Verification/`): emails the owner `AuthEmails.RegistrationAttempted` |
| `RefreshTokenReuseDetectedDomainEvent(UserId, SessionId)` | Domain | A used or revoked refresh token is presented again and the session is revoked | None yet |
| `VerificationCodeIssuedDomainEvent(CodeId, UserId, Purpose, Target, ProtectedToken)` | Domain | A verification code is issued; `ProtectedToken` is the encrypted token for the email link | `SendVerificationEmailDomainEventHandler` (`Application/Verification/`): decrypts the token and emails `AuthEmails.ConfirmEmail` (`EmailVerify`, link from `Auth:Links:ConfirmEmailUrl`) or `AuthEmails.ResetPassword` (`PasswordReset`, link from `Auth:Links:ResetPasswordUrl`) |

The events are declared in `Domain/Users/Events/`, `Domain/Sessions/Events/` and `Domain/Verification/Events/`. Domain events are written to `auth.OutboxMessages` in the same save as the aggregate and dispatched by the module outbox (ADR 0007), also when the aggregate itself did not change (`NoteRegistrationAttempt` raises an event on an unchanged user; `DomainEventsToOutboxInterceptor` reads every tracked aggregate, whatever its state, which `OutboxTests.Event_raised_by_an_unchanged_tracked_aggregate_is_written_to_the_outbox` pins). The module publishes no integration events yet.

Both email handlers send in English only (decision D8), to the user's current `Email`, loaded by id with its `DisplayName`. A user who no longer exists (or is soft-deleted) gets nothing: the handler logs it and returns without throwing, so the message is not retried. `SendVerificationEmailDomainEventHandler` also skips a code whose `Target` is no longer the user's normalized address. A failed send throws, so the outbox retries the message. They log at Information only the code or user id and the purpose; never the token, the link or the address.

## Registration

<!-- The registration, confirmation and resend flows, and what they guarantee against account enumeration. -->

The handlers live in `Application/Registration/{Register,ConfirmEmail,ResendConfirmation}/`, each with its command and validator. The validator runs in the validation decorator, **before** the handler, so nothing below happens for an invalid request.

**Validation** (`RegisterCommandValidator`; each rule stops at its first failure):

- `email`: required, at most 256 characters (`User.MaxEmailLength`), an email address. Surrounding white space and case do not matter for the account (`User.NormalizeEmail`: `Alice@Example.com ` and `alice@example.com` are one account, enforced by the unique index on `NormalizedEmail`).
- `password`: not empty and not only white space, `Auth:Password:MinLength` to `MaxLength` characters (12 to 128 by default, counted in UTF-16 characters as everywhere in the module; any Unicode is accepted, no composition rules), and not the email itself (ignoring case and surrounding white space). The length is checked before any breach lookup or hashing, so a 1 MB password is refused cheaply (the host also caps a request body at `Kestrel:Limits:MaxRequestBodySize`, 10 MB). The password is never trimmed.
- `displayName`: not blank, at most 200 characters; it is stored trimmed.
- `locale`: a predefined .NET culture name (`CultureInfo.GetCultureInfo(name, predefinedOnly: true)`) of at most 16 characters, stored in its canonical spelling (`ZH-hans` becomes `zh-Hans`). The endpoint fills it from the request's language when the body has none.
- Confirm: `token` not blank, at most 256 characters. Resend: `email` as for registration.

The custom messages (password equal to the email, unknown culture) are English; the built-in FluentValidation messages are translated (`ValidationMessageTranslations`).

**Register** (`RegisterCommandHandler`), in this order:

1. Breach check (`IBreachedPasswordChecker`, fail-open): a breached password is 400 `auth.password_breached`. The answer depends on the password only, never on the account.
2. Hash the password (`IPasswordHasher.Hash`) **before** looking the address up, so a known and a new address both pay the hashing cost.
3. Look the address up by `NormalizedEmail`.
4. **Known address:** `NoteRegistrationAttempt` (raises `RegistrationAttemptedDomainEvent`, changes nothing), audit `auth.register_duplicate` (not succeeded, the user id and the masked address), one save. The existing account is untouched: its password, name and locale stay as they are.
5. **New address:** `User.Register`, the `User` system role (looked up with `IRoleRepository.GetByNormalizedNameAsync`; if the seeder has not created it, the handler throws `InvalidOperationException` instead of creating a role-less user), an `EmailVerify` `VerificationCode` (token from `ISecureTokenService.Generate`, only its SHA-256 hash stored, the token protected with `ISecretProtector` into the issued event, lifetime `Auth:Verification:EmailLifetime`, the client address as `CreatedIp`), audit `auth.registered`, and **one** `SaveChangesAsync` that also writes the outbox rows of `UserRegisteredDomainEvent` and `VerificationCodeIssuedDomainEvent`.

**Confirm** (`ConfirmEmailCommandHandler`): hash the presented token, find the code by hash, `Consume(EmailVerify, now)`, load the user, `ConfirmEmail(now)`, audit `auth.email_confirmed`, save. Any failure (unknown token, used, invalidated by a resend, expired, another purpose such as a reset token, or a user that no longer exists) is 400 `auth.invalid_token` and is audited as `auth.email_confirm_failed` with the failure reason and the code's user id when known, never the token; a refused `Consume` changes nothing, only the audit entry is saved.

**Resend** (`ResendConfirmationCommandHandler`): an unknown or already confirmed address does nothing; so does a code issued less than `Auth:Verification:ResendCooldown` ago (`GetLastIssuedAtAsync` against `TimeProvider`, review 2.11: the per-account cooldown is enforced here because the limiter cannot see the body). Otherwise every pending `EmailVerify` code is invalidated, a new one is issued as at registration, audit `auth.confirmation_resent`, one save.

**Enumeration guarantees.**

- Register and resend answer 202 with no body and the same headers for a known and an unknown address (`RegistrationTests.Register_response_is_identical_for_new_and_existing_email`); every 400 depends only on the request itself.
- The only difference a caller could see is the email, which only the owner of the address receives: a confirmation link for a new account, a "sign-up attempt" notice (no link) for an existing one.
- Registration hashes on both paths. Resend does no hashing; its known-address path costs a few more queries than the unknown one.
- Known limitation: two registrations of the same new address at the same moment both pass the lookup; the unique index lets one insert win and the other fails with a 500. No duplicate account can exist.

**Secrets.** The token reaches the outbox only encrypted (ADR 0017); `auth.VerificationCodes` stores its hash; the commands and request bodies print only their type name, so a logged command never carries the password or the token.

## Login and current user

<!-- The login flow, its lockout and enumeration rules, and GET /me. -->

The handlers live in `Application/Authentication/Login/` (`LoginCommand`, `LoginCommandHandler`, `LoginCommandValidator`, `LoginResponse`) and `Application/Me/GetMe/` (`GetMeQuery`, `GetMeQueryHandler`, `MeResponse`); the settings in `Application/Authentication/` (`RefreshTokenOptions`, `LockoutOptions`).

**Validation** (`LoginCommandValidator`, in the validation decorator, before the handler): `email` required, at most 256 characters, an email address; `password` not empty and at most `Auth:Password:MaxLength` characters (UTF-16 characters, not trimmed, not normalized; Ruling R14), so an over-long password (a 1 MB string included) is refused with 400 before any lookup or hashing. There is no minimum at login: a password an older, shorter minimum accepted must still work, and only the hash can tell whether it is right. `deviceName` at most 200 characters (`UserSession.MaxDeviceNameLength`).

**Login** (`LoginCommandHandler`), in this order:

1. Normalize the email (`User.NormalizeEmail`) and load the user (`GetByNormalizedEmailAsync`; a soft-deleted user is not found).
2. **Always one verification.** A user with a hash: `IPasswordHasher.Verify`. An unknown email, or a user without a password (created by an administrator, Ruling R3): `SpendVerificationCost`, which costs the same, and the attempt counts as a wrong password.
3. **Wrong password or no such account:** for a user with a password, `IUserRepository.RecordFailedSignInAsync(userId, now, Auth:Lockout:MaxFailedAttempts, Auth:Lockout:Duration)` counts the failure in one atomic statement by the rules of `User.RecordFailedSignIn` (nothing changes while the account is already locked); when that failure locked the account, `User.NoteLockedOut(lockoutEnd)` raises `UserLockedOutDomainEvent` on the tracked user without changing it; audit `auth.login_failed` (failure reason `auth.invalid_credentials`, the user id when there is a user, the masked email), plus `auth.locked_out` (with `{"lockoutEnd":"..."}` in `Details`) when this failure locked the account; save (inserts only); 401 `auth.invalid_credentials`. An unknown email and an account without a password change no counter.
4. **Correct password, account locked** (`IsLockedOut(now)`): audit `auth.login_failed` with failure reason `locked`, save, 401 `auth.invalid_credentials`. The counters and `LockoutEnd` stay as they are: the lockout neither grows nor ends early. Decision D10: there is no distinct "locked" answer, because it would reveal that the email exists.
5. **Correct password, account cannot sign in** (`EnsureCanSignIn`): a suspended account is 403 `auth.account_inactive`, an unconfirmed email 403 `auth.email_not_verified` (suspension is checked first); audited as `auth.login_failed` with the code as the reason, saved. These answers come only after the right password, so they tell nothing to someone who does not know it.
6. **Success:** `RecordSuccessfulSignIn(now)` (failures and lockout cleared, `LastLoginAt` set). When the verifier answered `SuccessRehashNeeded`, the password is hashed again and stored with `User.UpgradePasswordHash`, which changes only the hash: not the security stamp, the history or `PasswordChangedAt`, and it raises no event (the password is the same). A refresh token from `ISecureTokenService.Generate` (43 characters; only its SHA-256 hash is stored); `UserSession.Start(userId, "pwd", deviceName ?? "Unknown", userAgent, ipAddress, user.SecurityStamp, refreshHash, SlidingLifetime, AbsoluteLifetime, now)` with the user agent and address from `IClientContext`; the access token from `IAccessTokenIssuer.Issue(new AccessTokenRequest(userId, sessionId, securityStamp, "pwd", now, user.Locale))`; audit `auth.login_succeeded` with the session id; one save. The response carries both tokens, their expiries (`refreshTokenExpiresAt` is the first token's, `now + SlidingLifetime`, never past the session's absolute expiry) and the session id, which is also the token's `sid`.

Every failure path saves, so the failure count survives the failure result (`LoginTests.Failed_login_counter_survives_the_failure_result`). The lockout ends exactly at `LockoutEnd`: one tick earlier the right password is still refused (`LoginTests.Lockout_ends_exactly_at_the_lockout_time`).

**Enumeration guarantees.**

- An unknown email, a wrong password, an account without a password and a locked account all answer 401 `auth.invalid_credentials` with the same headers and the same body (only `traceId` differs): `LoginTests.Unknown_email_and_wrong_password_return_identical_responses`, `User_without_password_hash_gets_the_same_response_as_unknown_email`.
- Each path does exactly one password verification, and each saves (an audit row at least), so the expensive work is the same. A known account's failure also runs the one-row counting statement before the save; that is a difference of one short round trip.
- **Concurrent attempts.** Wrong passwords that arrive together must each count, or parallel guessing would get far more than five tries per lockout. The failure is therefore counted by one `UPDATE auth.Users ... OUTPUT inserted.AccessFailedCount, inserted.LockoutEnd` (`UserRepository`, `WHERE Id = @id AND IsDeleted = 0` and the account not locked at `@now`): simultaneous failures queue on the row lock, and each sees the count the previous one left. The tracked user is not changed for a failure, so the save that follows only inserts (the audit rows and the lockout event's outbox row) and cannot conflict: every failed request keeps its `auth.login_failed` row, and exactly one request (the one that crossed the threshold) writes `auth.locked_out` and the event. With the default settings, ten wrong passwords at once leave the count at 5, `LockoutEnd` set, ten `auth.login_failed` rows and one `auth.locked_out` row; the other five found the account locked, which counts nothing (`LoginTests.Parallel_wrong_passwords_are_all_counted_and_lock_the_account`). The statement is a copy of the domain rule in SQL, so `AuthPersistenceTests.Atomic_failed_sign_in_count_matches_the_domain_rule` runs both over the same states (never failed, one failure, one below the threshold, at a lowered threshold, locked, locked until one tick from now, lock ended exactly now, lock ended long ago) and requires identical results; `User.RecordFailedSignIn` stays the specification. Only a **successful** login still changes the user through the change tracker (`LastLoginAt`, the reset, a rehash); two at once conflict on `RowVersion`, and the handler saves that path with `IUnitOfWork.TrySaveChangesAsync`, which answers false instead of throwing: the loser gets 401 `auth.invalid_credentials` (a 409 `concurrency.conflict` could only ever happen to an existing account) and simply signs in again.
- The audit log is internal and does tell the cases apart (user id, failure reason), for administrators.

**Current user** (`GetMeQueryHandler`, `GET /api/v1/auth/me`): one Dapper round trip reads the user (`IsDeleted = 0` and `Status = Active`) and the names of its roles (`IsDeleted = 0`), because Dapper bypasses the EF Core soft-delete filters (ADR 0006; `MeTests.Get_me_returns_roles_and_permissions_and_hides_soft_deleted_roles`). The permissions come from `IPermissionReader`, the same cached set (`perm:{userId}`) that every permission check uses (see [Permission checks](#permission-checks)), so the list shows exactly what the user may do. Roles and permissions are sorted ordinally. **Fail closed:** the access token stays valid until it expires (decision D9), but when its user no longer exists, is soft-deleted or is suspended, the handler fails and the endpoint answers the same body-less 401 a request without a valid token gets (`http.401`), not a 404.

**Secrets.** `LoginCommand`, `LoginRequest` and `LoginResponse` print only their type name (the response also its session id), so a logged command or response never carries the password or a token; no audit row, log line or metric tag holds the password, a token or an email address (the audit keeps the masked email only).

## Security services

<!-- The services that hash, generate, protect and check secrets. -->

The abstractions are `internal` in `Application/Abstractions/` (the application layer does not depend on the infrastructure); the implementations are `internal sealed` in `Infrastructure/Security/`. All are registered as singletons by `AddAuthModule`.

| Service | Implementation | What it does |
|---|---|---|
| `IPasswordHasher` | `Pbkdf2PasswordHasher` | `Hash` makes a PBKDF2 hash with a random salt through the Identity `PasswordHasher` (ADR 0014). `Verify` returns `PasswordVerification.Failed`, `Success` or `SuccessRehashNeeded` (the hash used a lower iteration count than today's, so the caller stores a new hash); a malformed hash is `Failed`, never an exception. `SpendVerificationCost` verifies against a dummy hash made once on first use, so the unknown-account path costs the same as a wrong password. An empty stored hash (a user created by an administrator, who has no password yet) and a stored value that is not base64 do the same dummy verification before answering `Failed`, so no stored state answers faster than a wrong password. |
| `ISecureTokenService` | `SecureTokenService` | `Generate` returns a `GeneratedToken(Value, Hash)`: 32 bytes from `RandomNumberGenerator`, base64url without padding (43 characters), and its hash. `Hash` is the SHA-256 of the token's UTF-8 bytes (32 bytes), the form stored in `varbinary(32)` columns and used for lookups. |
| `IBreachedPasswordChecker` | `HibpBreachedPasswordChecker` | `IsBreachedAsync` asks Have I Been Pwned with k-anonymity: it requests `https://api.pwnedpasswords.com/range/{first 5 hex characters of the uppercase SHA-1}` with `Add-Padding: true` and looks for the suffix itself; padding rows (count 0) are ignored. It uses the named client `hibp`. The whole lookup (request, headers and reading the body) has one budget of 2 seconds, also the client timeout (`HibpBreachedPasswordChecker.RequestBudget`), and the body is capped at 1 MB (a padded answer is some 40 KB). It is **fail-open**: a non-success status, the budget running out, a body over the cap or any transport error logs a warning (status code, exception type or the cap only, never the password, hash or prefix) and answers `false`. A cancelled caller token is not a failure and propagates. With `Auth:Password:CheckBreached` off it makes no request. |
| `ISecretProtector` | `DataProtectionSecretProtector` | `Protect` and `Unprotect` encrypt a secret that must leave the process, with the Data Protection purpose `TemplateName.Auth.Secrets.v1` (ADR 0017). The same value protected twice differs; a changed value throws `CryptographicException`. Changing the purpose makes existing values unreadable. |

### Access tokens

`IAccessTokenIssuer` (`Application/Abstractions/`) issues the access token: `Issue(AccessTokenRequest(UserId, SessionId, SecurityStamp, AuthMethods, AuthTime, Locale))` returns `AccessToken(Value, ExpiresAt)`. `AccessTokenIssuer` (`Infrastructure/Tokens/`) signs it with `JsonWebTokenHandler`, ES256 only (ADR 0015), with the active key's `kid` in the header and the default `typ` (`JWT`). The times come from `TimeProvider`, truncated to whole seconds: `iat` = `nbf` = now, `exp` = now + `Auth:Jwt:AccessTokenLifetime`, and `ExpiresAt` is exactly the `exp` instant.

| Claim | Value |
|---|---|
| `sub` | The user id. |
| `sid` | The session id (`UserSession`). |
| `sst` | The session's snapshot of the user's security stamp. |
| `amr` | The session's authentication methods as a JSON array (`AuthMethods` split on spaces, for example `["pwd"]`). |
| `auth_time` | When the user signed in (Unix seconds, a number), not when this token was issued. |
| `locale` | The user's saved language. |
| `jti` | A new random id per token. |
| `iat`, `nbf`, `exp`, `iss`, `aud` | Issue time, not-before, expiry, `Auth:Jwt:Issuer`, `Auth:Jwt:Audience`. |

There is no permission list, no email and no display name in the token (ADR 0016): permissions are resolved on the server.

**Validation.** `JwtValidation.CreateParameters(JwtOptions, ISigningKeyProvider, TimeProvider?)` is the one definition the bearer handler uses: the algorithm allow-list is exactly `ES256` (so `none`, `HS256` signed with the public key's bytes and every other algorithm fail), `iss` and `aud` must match, `exp` is required, `exp` and `nbf` are checked with `Auth:Jwt:ClockSkew`, and signed tokens are required. The signing key is looked up by the header's `kid` among the configured keys; an unknown or missing `kid` finds no key and fails, and other keys are never tried (`TryAllIssuerSigningKeys = false`). Lifetime is checked against the application's `TimeProvider` through a `LifetimeValidator` (IdentityModel's own clock setting is not public), so a test clock moves token expiry too. `AddAuthModule` registers the bearer handler as the default scheme (`AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer()`) with `MapInboundClaims = false`, so claims keep their names (`sub`, `sid`, ...); `sub` is the identity's name claim. Access tokens are not checked against session revocation per request (ADR 0015, D9).

**In the host.** `UseAuthentication` runs at pipeline slot 1a and `UseAuthorization` at 10, after the rate limiter at 9a ([API host](../services/api.md#pipeline)). A request without a valid token stays anonymous; every endpoint that does not say `.AllowAnonymous()` then answers 401 `http.401` (the fallback policy), whatever was wrong with the token: missing, malformed, `alg: none`, `HS256`, an unknown `kid`, a tampered payload, a wrong audience, expired. None of these reaches the exception handler or produces a 500. `ICurrentUser.UserId` and `ICurrentUser.SessionId` read the `sub` and `sid` claims, and the `locale` claim picks the response language ahead of `Accept-Language` (an unsupported value falls through to the header). Anonymous today: the health checks, OpenAPI, Scalar, `GET /.well-known/jwks.json` and the registration and login routes under [Endpoints](#endpoints).

**Keys.** `ISigningKeyProvider` (`Infrastructure/Tokens/`, `SigningKeyProvider`: a singleton, also registered as a hosted service that does nothing, so the host builds it when it starts) loads `Auth:Jwt:SigningKeys` once, at start, with `ECDsa.ImportFromPem`. Every key must be EC P-256. The first entry with a `PrivateKeyPem` signs (`Active`); every entry, active or retired, validates (`ValidationKeys`) and is published (`PublicKeySet`), both through public-only copies, so neither can carry a private part. Start-up validation (`ValidateOnStart`) refuses: a missing or duplicate `KeyId`; an entry with neither PEM; a `PrivateKeyPem` that is not a `PRIVATE KEY` or `EC PRIVATE KEY` block (an encrypted key, a public key, an RSA key, text that is not PEM); a `PublicKeyPem` that is not a `PUBLIC KEY` block; any other curve (P-384, P-521, brainpool); a `PublicKeyPem` that does not match the `PrivateKeyPem` next to it; and a list with no private key. Every message names the entry (`Auth:Jwt:SigningKeys:{index}:PrivateKeyPem ...`) and never quotes key material. With **no** key configured, Development and Testing generate one ephemeral P-256 key at start (kid `ephemeral-{random}`) and log a warning; tokens it signed stop validating when the process restarts, and other instances do not accept them. Every other environment refuses to start with a message that names `Auth:Jwt:SigningKeys`.

**Generating a key.** Once per environment, on a trusted machine, and straight into the secret store, never into a file in the repository:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt
```

The output (`-----BEGIN PRIVATE KEY-----` ...) is the `PrivateKeyPem` of `Auth:Jwt:SigningKeys:0`; choose a `KeyId` that says when it was made (for example `2026-10`). Locally: `dotnet user-secrets set "Auth:Jwt:SigningKeys:0:KeyId" "2026-10" --project src/Host/TemplateName.Api`, and the same for `Auth:Jwt:SigningKeys:0:PrivateKeyPem`. As environment variables: `Auth__Jwt__SigningKeys__0__KeyId` and `Auth__Jwt__SigningKeys__0__PrivateKeyPem`; the PEM may lose its line breaks on the way (a one-line PEM is accepted). The public part, for a retired entry, is `openssl pkey -pubout` of the private key.

**Rotating.** Put the new key first (index 0, with its `PrivateKeyPem`) and move the old one to index 1 with only its `PublicKeyPem`, so it no longer signs but still validates and stays in the JWKS. Keep it for at least `AccessTokenLifetime + ClockSkew` (10 minutes 30 seconds by default), then remove it. Every instance must have the new list before any of them signs with the new key. When other services verify tokens through the JWKS (which they may cache for 5 minutes), publish the new key first: add it at the end of the list with its `PrivateKeyPem` (published, not yet signing, because an earlier entry has a private key), wait at least 5 minutes, then do the swap above.

### Email sending

`IEmailSender` (`Application/Abstractions/`) is the seam every Auth email goes through: `SendAsync(EmailMessage, CancellationToken)`, where `EmailMessage(To, Subject, TextBody, HtmlBody?)` is a plain record. `SmtpEmailSender` (`Infrastructure/Email/`) implements it with MailKit's `SmtpClient`: a fresh connection per call (`SecureSocketOptions.StartTls` when `Auth:Email:UseTls` is on, otherwise none), authentication only when `Auth:Email:Username` is set, then send and disconnect. `Auth:Email:Timeout` bounds the connect and the send. It **throws** on any failure, so the outbox handler that called it fails and the event is retried; a failure while saying goodbye after the server accepted the message is ignored, so a retry cannot send the email twice. It logs one generic line (information when sent, warning with only the exception type when it failed): never the message and never the recipient, who is personal data.

`AuthEmails` (`Application/Verification/`) builds the three messages, in English only until Plan 3 (decision D8): `ConfirmEmail(to, displayName, link)`, `ResetPassword(to, displayName, link)` and `RegistrationAttempted(to, displayName)` (no link: it tells the owner of an existing account that someone tried to register with their address). Each has a plain-text and an HTML body. The subjects are fixed text, so user input never reaches a header; the display name and the link are HTML-encoded in the HTML body (the link also in the `href`); and a link that is not an absolute `http` or `https` address is refused with `ArgumentException`, so a bad configuration cannot produce a `javascript:` link. The caller passes the already built link, because `Application` may not depend on `Infrastructure`: `LinksOptions` (`Application/Verification/`) turns the `Auth:Links` templates into links with `ConfirmEmailLink(token)` and `ResetPasswordLink(token)`, replacing `{token}` with the URL-encoded token.

Locally, Mailpit from `docker-compose.yml` catches the mail: SMTP on `127.0.0.1:1025` and the inbox at <http://localhost:8025> (loopback only, no login).

### Permission checks

Permissions are resolved on the server for every request (ADR 0016); the access token carries none. An endpoint asks for one with `.RequirePermission(code)` ([Web.Common](../building-blocks/web-common.md)), whose handler calls `IPermissionChecker.HasPermissionAsync(userId, code, cancellationToken)`.

- **Checker.** `PermissionChecker` (`Infrastructure/Authorization/`, one singleton registered as both `IPermissionChecker` and `IPermissionCache`) loads a user's effective codes with one Dapper query over `Users`, `UserRoles`, `Roles`, `RolePermissions` and `Permissions`: the user must be `Active` and not soft-deleted, the role not soft-deleted, and the permission not deprecated. Dapper bypasses the EF Core filters, so the query writes `IsDeleted = 0` itself (ADR 0006). The result is the union over all the user's roles. An unknown, suspended or soft-deleted user gets the empty set; a deprecated permission is never granted, even through an old grant. Codes are compared exactly (ordinal), so `AUTH.USER.VIEW` is not `auth.user.view`.
- **Cache.** The set is a sorted `string[]` in `HybridCache` under `perm:{userId}`, with `Expiration` and `LocalCacheExpiration` both 30 seconds (`PermissionCache.Lifetime`); a check is a binary search in it. The empty set is cached too. No distributed cache is registered, so the cache is in memory per instance until Plan 5. `HybridCache` keeps its local entries in the shared `IMemoryCache`, whose clock `AddAuthModule` sets to the application's `TimeProvider` (`TimeProviderCacheClock`), so the 30 seconds follow the same clock as everything else (and a test clock moves them).
- **Reader.** The same singleton is `IPermissionReader`: `GetPermissionsAsync(userId, cancellationToken)` returns the cached set itself (sorted ordinally, read-only, empty for an unknown, suspended or soft-deleted user), so `GET /api/v1/auth/me` lists exactly what the checks allow and adds no query of its own.
- **Invalidation.** `IPermissionCache.InvalidateUsersAsync(userIds, cancellationToken)` removes each user's key (`HybridCache.RemoveAsync`), so the next check reloads. Every handler that changes a role's permissions or a user's role assignments must call it for the affected users after saving (`IRoleRepository.GetUserIdsInRoleAsync` lists a role's users). The seeder does not: it runs at start-up, before the instance serves requests, when its in-memory cache is empty.

**Security notes.**

- **Staleness across instances.** `RemoveAsync` reaches only the instance that runs it while the cache is in memory. Every other instance keeps using the set it loaded for up to 30 seconds: a removed role or permission can still be used there for that long, and a new one may not work there yet. Plan 5's distributed cache makes the removal reach every instance (ADR 0016). If 30 seconds is too long for a deployment, run one instance until then.
- **A load racing a change.** A check that started reading before a change was saved can store the old set just after the invalidation removed the key; that set then lives until its 30 seconds run out. The window is the length of one query.
- **Fail closed.** The handler grants nothing to an anonymous caller or a principal without a user id, and a database error while loading propagates (500) instead of granting. A deprecated permission, a soft-deleted role and a suspended or soft-deleted user grant nothing, whatever the grant rows say.
- **No permissions in the token.** A stolen access token is limited to what the user may do now, not what they could do when it was issued; a revoked session's token still works until it expires (ADR 0015, D9).

## Configuration

<!-- Configuration sections and keys the module reads, with defaults. -->

`AddAuthModule` receives the `IConfiguration`. `Auth:Password`, `Auth:Email`, `Auth:Links`, `Auth:Verification`, `Auth:RefreshToken`, `Auth:Lockout` and `Auth:Jwt` are validated at start-up, so an out-of-range value or a bad signing key stops the host. `Auth:Seed` is checked when the seeder runs.

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Database` | empty (user secrets or environment) | The database that holds the `auth` schema. |
| `Auth:DataProtection:ApplicationName` | `TemplateName` | The Data Protection application name. Every instance that must read the others' protected values (outbox tokens, ADR 0017) uses the same name and the same database. |
| `Auth:Password:MinLength` | `12` (8-128) | The shortest password accepted. There are no composition rules. |
| `Auth:Password:MaxLength` | `128` (8-128) | The longest password accepted, checked before hashing. May not be below `MinLength`. |
| `Auth:Password:HistoryCount` | `5` (1-24) | How many of the latest passwords may not be reused, the current one included. |
| `Auth:Password:CheckBreached` | `true` | Whether a new password is checked against Have I Been Pwned. The check fails open; `false` makes no request at all. |
| `Auth:Email:Host` | `localhost` | The SMTP host. `appsettings.Development.json` points at Mailpit (`localhost:1025`, no TLS). |
| `Auth:Email:Port` | `1025` (1-65535) | The SMTP port. |
| `Auth:Email:UseTls` | `false` | Upgrade the connection with STARTTLS. Turn it on for every real server. |
| `Auth:Email:Username` | empty (user secrets or environment) | The SMTP account. When empty the sender does not authenticate. Never set it in a file. |
| `Auth:Email:Password` | empty (user secrets or environment) | The SMTP password. Never set it in a file. |
| `Auth:Email:From` | `no-reply@localhost.test` | The sender address; it must be a valid email address. |
| `Auth:Email:FromName` | `TemplateName` | The display name shown next to the sender address (up to 100 characters). |
| `Auth:Email:Timeout` | `00:00:30` (1 second to 5 minutes) | How long connecting and sending may each take. |
| `Auth:Links:ConfirmEmailUrl` | `http://localhost:3000/confirm-email?token={token}` | The front-end page the confirmation link opens. Must be absolute `http(s)` and hold `{token}`, which is replaced by the URL-encoded token. |
| `Auth:Links:ResetPasswordUrl` | `http://localhost:3000/reset-password?token={token}` | The front-end page the reset link opens; same rules. |
| `Auth:Verification:EmailLifetime` | `01:00:00` (15 minutes to 1 hour) | How long an email-confirmation link works. |
| `Auth:Verification:PasswordResetLifetime` | `00:30:00` (15 minutes to 1 hour) | How long a password-reset link works. |
| `Auth:Verification:ResendCooldown` | `00:01:00` (0 to 1 hour) | The least time between two confirmation emails to one account, from the last code issued; a resend inside it sends nothing and still answers 202. `00:00:00` switches it off. |
| `Auth:RefreshToken:SlidingLifetime` | `14.00:00:00` (15 minutes to 365 days) | How long one refresh token works from its issue; each refresh issues the next. May not be greater than `AbsoluteLifetime`. |
| `Auth:RefreshToken:AbsoluteLifetime` | `90.00:00:00` (15 minutes to 365 days) | How long a session lasts at most from the login, however often it is refreshed. |
| `Auth:Lockout:MaxFailedAttempts` | `5` (1-100) | The wrong password that reaches this count locks the account. |
| `Auth:Lockout:Duration` | `00:15:00` (1 minute to 1 day) | How long the account stays locked (fixed, not progressive: decision D4). It opens again exactly at the end. |
| `Auth:Jwt:Issuer` | `templatename` | The `iss` claim written and required. Set it to the API's public address in a deployment. |
| `Auth:Jwt:Audience` | `templatename-api` | The `aud` claim written and required. |
| `Auth:Jwt:AccessTokenLifetime` | `00:10:00` (1 minute to 1 hour) | How long an access token is valid. |
| `Auth:Jwt:ClockSkew` | `00:00:30` (0 to 5 minutes) | The tolerance for clock differences on `exp` and `nbf`. |
| `Auth:Jwt:SigningKeys` | empty (user secrets, environment or a secret store) | The EC P-256 keys, each `{ KeyId, PrivateKeyPem?, PublicKeyPem? }`; the first with a private key signs, all validate and are published. Empty: an ephemeral key in Development and Testing, a start-up failure elsewhere. Never put a key in a file. See [Access tokens](#access-tokens). |
| `Auth:Seed:RunOnStartup` | `true` | Seed after the migration step when the host starts (see [Background processing](#background-processing)). The integration tests turn it off and seed from the test harness. |
| `Auth:Seed:AdminEmail` | empty (`admin@localhost.test` in `appsettings.Development.json`) | The email of the first administrator (`SuperAdmin`). Must be a plain address (no display name), up to 256 characters. |
| `Auth:Seed:AdminPassword` | empty (user secrets or environment) | That administrator's password, within `Auth:Password:MinLength` and `MaxLength`. Never set it in a file. The account is seeded only when both keys are set and no account, soft-deleted or not, has that email. Remove the key after the first start; while it is set and the account exists, every start logs a warning. Locally: `dotnet user-secrets set "Auth:Seed:AdminPassword" "…" --project src/Host/TemplateName.Api`. |

The permission cache lifetime is fixed at 30 seconds (`PermissionCache.Lifetime`, ADR 0016) and has no setting.

The PBKDF2 iteration count has no setting of the module: it is Identity's default (`PasswordHasherOptions.IterationCount`, ADR 0014). Only the integration test factory lowers it, through `Configure<PasswordHasherOptions>`.

## Data

<!-- The schema, its tables and indexes, and the migrations in order. -->

Schema `auth`, owned by `AuthDbContext` (`Infrastructure/Persistence/`), which is also the module's `IUnitOfWork` and the Data Protection key store (`IDataProtectionKeyContext`). Writes go through EF Core and the repositories in `Application/Abstractions/` (`IUserRepository`, `IRoleRepository`, `IPermissionRepository`, `ISessionRepository`, `IVerificationCodeRepository`); reads that bypass them use Dapper and filter `IsDeleted = 0` themselves (ADR 0006): today the permission query of `PermissionChecker` (see [Permission checks](#permission-checks)) and the user and role query of `GET /api/v1/auth/me` (see [Login and current user](#login-and-current-user)), later the list queries. Keys are `SequentialGuid`s set by the domain (`ValueGeneratedNever`), except the audit log's `bigint` identity. Every timestamp, `DateTime` or `DateTimeOffset`, is `datetime2(3)` holding UTC (`ApplyDefaultConventions`); a `DateTimeOffset` comes back with offset zero. Hashes are `varbinary(32)` (SHA-256); enums are `int`.

| Table | Purpose | Indexes |
|---|---|---|
| `auth.Users` | The `User` aggregate. `Email`/`NormalizedEmail nvarchar(256)`, `PasswordHash nvarchar(256)` (null for an administrator-created user), `SecurityStamp nvarchar(64)`, `DisplayName nvarchar(200)`, `Locale nvarchar(16)`, `TimeZone nvarchar(64)`, `Status int`, `RowVersion rowversion`, audit and soft-delete columns. | `PK_Users`, `IX_Users_NormalizedEmail` (unique, filtered `[IsDeleted] = 0`) |
| `auth.PasswordHistory` | The user's current and previous password hashes (`PasswordHistoryEntry`), loaded oldest first. | `PK_PasswordHistory`, `IX_PasswordHistory_UserId` (FK to `Users`, cascade) |
| `auth.UserRoles` | Role assignments (`UserRole`): `AssignedBy`, `AssignedAt`. | `PK_UserRoles` (`UserId`, `RoleId`), `IX_UserRoles_RoleId` (FK to `Roles`, restrict; FK to `Users`, cascade) |
| `auth.Roles` | The `Role` aggregate. `Name`/`NormalizedName nvarchar(100)`, `Description nvarchar(500)`, `IsSystem`, `RowVersion rowversion`, audit and soft-delete columns. | `PK_Roles`, `IX_Roles_NormalizedName` (unique, filtered `[IsDeleted] = 0`) |
| `auth.Permissions` | Declared permissions. `Code nvarchar(128)`, `Module nvarchar(64)`, `Name nvarchar(200)`, `Description nvarchar(500)`, `IsDeprecated`. | `PK_Permissions`, `IX_Permissions_Code` (unique) |
| `auth.RolePermissions` | Grants (`RolePermission`). | `PK_RolePermissions` (`RoleId`, `PermissionId`), `IX_RolePermissions_PermissionId` (FK to `Permissions`, restrict; FK to `Roles`, cascade) |
| `auth.UserSessions` | The `UserSession` aggregate. `AuthMethods nvarchar(64)`, `DeviceName nvarchar(200)`, `UserAgent nvarchar(512)`, `IpAddress nvarchar(45)`, `SecurityStamp nvarchar(64)`, `RevokedReason int`. | `PK_UserSessions`, `IX_UserSessions_UserId` (FK to `Users`, restrict) |
| `auth.RefreshTokens` | The session's token chain (`RefreshToken`): `TokenHash varbinary(32)`, `UsedAt`, `ReplacedByTokenId`, `RevokedAt`. | `PK_RefreshTokens`, `IX_RefreshTokens_TokenHash` (unique), `IX_RefreshTokens_SessionId` (FK to `UserSessions`, cascade) |
| `auth.VerificationCodes` | The `VerificationCode` aggregate. `Purpose int`, `Target nvarchar(256)`, `TokenHash varbinary(32)`, `CreatedIp nvarchar(45)`. | `PK_VerificationCodes`, `IX_VerificationCodes_TokenHash` (unique), `IX_VerificationCodes_UserId_Purpose_CreatedAt` (also serves the FK to `Users`, restrict) |
| `auth.AuthAuditLogs` | The audit log (`AuthAuditLog`). `Id bigint identity`, `EventType nvarchar(64)`, `FailureReason nvarchar(128)`, `AttemptedIdentifier nvarchar(256)`, `IpAddress nvarchar(45)`, `UserAgent nvarchar(512)`, `TraceId nvarchar(64)`, `Details nvarchar(max)`. No foreign keys, so entries outlive what they describe. | `PK_AuthAuditLogs`, `IX_AuthAuditLogs_UserId_OccurredAt`, `IX_AuthAuditLogs_EventType_OccurredAt`, `IX_AuthAuditLogs_IpAddress_OccurredAt` (each `OccurredAt DESC`) |
| `auth.DataProtectionKeys` | The Data Protection key ring (ADR 0017), shared by every instance; not encrypted at rest yet. | `PK_DataProtectionKeys` |
| `auth.OutboxMessages` | Domain events waiting for dispatch. | `PK_OutboxMessages`, `IX_OutboxMessages_OccurredAt` (filtered: `ProcessedAt IS NULL`) |
| `auth.OutboxMessageConsumers` | Which handler has processed which message. | `PK_OutboxMessageConsumers` (`OutboxMessageId`, `Name`) |
| `auth.__EFMigrationsHistory` | Applied migrations of this module. | — |

Rules the repositories and the context keep:

- **Whole aggregates.** A user is loaded with its password history (oldest first, which `ChangePassword` relies on to drop the oldest) and its roles; a role with its grants; a session with **every** refresh token, used or not, because `Rotate` and `Revoke` are only correct over the whole chain (Ruling R7). `GetByRefreshTokenHashAsync` uses the token only to pick the session; the include is never filtered.
- **Claiming a refresh token.** `TryClaimRefreshTokenAsync` is one `UPDATE ... SET UsedAt = @now WHERE Id = @id AND UsedAt IS NULL AND RevokedAt IS NULL` (`ExecuteUpdateAsync`), true only for the caller whose statement changed the row. It bypasses the change tracker, so the refresh handler loads the session, claims, then rotates the instance it already holds; it never reloads after the claim.
- **Soft delete keeps children.** Users and roles are soft-deleted by `SoftDeleteInterceptor`. The context cascades deletes at save time (`CascadeDeleteTiming = OnSaveChanges`), after the interceptor has turned the delete into an update, so a soft-deleted user keeps its history and assignments. Removing an item from a collection (`RemoveRole`, history trimming) still deletes that row.
- **Column lengths.** Every client-supplied value is cut before it reaches a bounded column, so a long `User-Agent` or an IPv6 address with a zone id never fails a save: `HttpClientContext` cuts to the `AuthAuditLog` limits, and `UserSession.Start`, `VerificationCode.Issue` and `AuthAuditLog.SetClientInfo` cut again to their own. The configurations take the lengths from these domain constants.
- **Seeded rows.** `auth.Roles` holds the three system roles (`IsSystem = 1`) and `auth.Permissions` one row per declared code, kept by the seeder (see [Background processing](#background-processing)). Permission rows are never deleted; a code no module declares any more has `IsDeprecated = 1`.
- **Counting failed sign-ins.** `IUserRepository.RecordFailedSignInAsync` is raw SQL on the context's connection (one `UPDATE ... OUTPUT`, retried by the execution strategy like any command), outside the change tracker. It implements `User.RecordFailedSignIn` (an equivalence test pins it), skips soft-deleted users and locked accounts, compares `LockoutEnd` with `@now` to the tick, and stamps `UpdatedAt` itself (`UpdatedBy` null: the caller is anonymous), because raw SQL skips the save interceptors; `RowVersion` changes as for any update. It commits on its own, before the handler's save: if that save then failed (a database error, so a 500), the count would stay and the audit row be lost, never the other way round.
- **Saving without a 409.** `IUnitOfWork.TrySaveChangesAsync` saves like `SaveChangesAsync` but answers false on an optimistic-concurrency conflict (`DbUpdateConcurrencyException`), after which the context drops its pending changes (`ChangeTracker.Clear`); nothing was saved. The successful login uses it, so a race on the user row cannot turn into a 409 that only an existing account can get.
- **Audit entries.** `IAuthAuditWriter.Record` fills `IpAddress`, `UserAgent` and `TraceId` from `IClientContext` (`HttpClientContext`: the address after the forwarded-headers middleware, the `User-Agent` header, and the trace id that `X-Trace-Id` also carries, each already cut to its audit column) and stages the entry; the handler's `SaveChangesAsync` saves it with the rest.

Migrations (`Infrastructure/Persistence/Migrations/`), in order:

1. `InitialAuth`: creates the schema and every table above.

Add one with:

```bash
dotnet ef migrations add {Verb}{What} \
  --project src/Modules/Auth/TemplateName.Modules.Auth \
  --startup-project src/Host/TemplateName.Api \
  --context AuthDbContext \
  --output-dir Infrastructure/Persistence/Migrations
```

## Background processing

<!-- Hosted services, outbox handlers and scheduled jobs the module runs. -->

**Seeding.** `SeedAuthModuleAsync(cancellationToken)` (an extension of `IServiceProvider` on `AuthModule`; `AuthSeeder` in `Infrastructure/Authorization/`) brings the database to the declared state. The host calls it once at start, after the migration step, when `Auth:Seed:RunOnStartup` is on (the default); the integration test harness calls it after migrating and again after every database reset. It is idempotent, and one run is one transaction under an exclusive SQL Server application lock (`sp_getapplock` on `auth.seed`, 60-second wait), so instances that start together seed one after the other and a failed run leaves nothing half done. It is retried as a whole on transient SQL errors. In order:

1. **Permissions.** `PermissionSynchronizer` validates every definition of every registered `IPermissionSource` and fails the run with an `InvalidOperationException` naming the code and the source when one is wrong: the code must match `^[a-z]+(\.[a-z_]+){2}\z`, the module must equal the code's first segment, the name must not be blank, code, name and description must fit their columns (128, 200, 500 characters), and no code may be declared twice, in one source or across sources. Then it inserts new codes, updates the name and description of known ones (and clears `IsDeprecated` if a code comes back), and deprecates every stored code that no source declares. It never deletes a permission.
2. **System roles.** `SuperAdmin`, `Admin` and `User` are created when missing (`Role.CreateSystem`). A non-system role that already has one of these names stops the run, because it would otherwise receive the system role's grants without its protection.
3. **Grants.** `SuperAdmin` is set to every non-deprecated permission on each run, so a newly declared permission reaches it at the next start and a deprecated one leaves it; this goes through `Role.SyncPermissions`, the seeder-only entry point (`SetPermissions` refuses `SuperAdmin`). `Admin` gets its defaults (see [Permissions](#permissions)) only in the run that creates the role; afterwards its set belongs to the administrators. `User` gets nothing.
4. **First administrator.** When `Auth:Seed:AdminEmail` and `Auth:Seed:AdminPassword` are both set (a blank value counts as unset; exactly one set logs an information line and seeds no account) and no user has that normalized email, it registers a user with display name `Administrator`, locale `en`, the hashed password, a confirmed email and the `SuperAdmin` role. The settings are checked before the transaction: an address that is not a plain email, or a password outside the `Auth:Password` length limits, fails the run with a message that names the setting and never the password. An existing account with that email, in any spelling and **soft-deleted or not**, is left exactly as it is and the run logs a warning instead (`IUserRepository.ExistsByNormalizedEmailIncludingDeletedAsync` ignores the soft-delete filter): a seed administrator that someone deleted is never re-created, even though the unique email index would allow a new row. The registration raises the usual `UserRegisteredDomainEvent` and `EmailConfirmedDomainEvent`. **Remove `Auth:Seed:AdminPassword` from the configuration after the first start**: the account exists from then on, and the password should not stay in the secret store or the environment.

**Outbox.** `SendVerificationEmailDomainEventHandler` and `SendRegistrationAttemptedEmailDomainEventHandler` (see [Events](#events)) run from the module outbox and send through `IEmailSender`, so a failed send throws, the outbox keeps the message and retries it, and the email goes out once SMTP is back (see Email sending). A queued link stops working when its code expires (60 minutes for a confirmation), whether or not the email has gone out.

## Observability

<!-- Log messages, ActivitySource and Meter names, and metrics the module emits. -->

- `SigningKeyProvider` logs one warning at start when it generates an ephemeral key (Development and Testing with no configured key): the configuration path, the generated `kid` and the environment name. No key material is ever logged, and a validation message about a key names only its configuration path.
- The JWT bearer handler logs failed authentications under ASP.NET Core's own `Microsoft.AspNetCore.Authentication` categories (Information, below the default `Warning` override for `Microsoft.AspNetCore`), with the failure reason and never the token. The 401 response's `WWW-Authenticate` header carries the standard `error="invalid_token"` and a short reason (for example that the token expired).
- The email event handlers log one Information line per message: `Sent the {Purpose} email of verification code {CodeId}`, `Sent the registration-attempt notice to user {UserId}`, or why they skipped it (the user no longer exists, or its address changed). Never the token, the link or the address.
- The breached-password checker and the email sender log the warnings described under [Security services](#security-services) and [Email sending](#email-sending).
- `AuthSeeder` logs at information level: `Seeded the Auth module: system roles and {PermissionCount} permissions` after each run, `Seeded the administrator account {UserId}` when it creates the administrator (the id only: never the email or the password), and a line when only one of the two administrator settings is set. It logs a **warning** when both settings are set but an account with that email already exists (soft-deleted or not), naming neither the email nor the password.

- The login and current-user handlers log nothing of their own; the logging decorator writes one line per request with the command name and, on failure, the error code (never the command, which prints only its type name).

**Metrics.** `AuthMetrics` (`Infrastructure/Observability/`, behind `IAuthMetrics`) creates the meter `TemplateName.Auth` through `IMeterFactory`; `AddAuthModule` adds it to OpenTelemetry with `ConfigureOpenTelemetryMeterProvider`, so it is exported whenever the host exports metrics (`OTEL_EXPORTER_OTLP_ENDPOINT`, see [Web.Common](../building-blocks/web-common.md#observability)). Tags carry fixed values only: never an email address, a user id or anything a caller typed.

| Counter | Tags | Counts |
|---|---|---|
| `auth.logins` | `result`: `succeeded`, `invalid_credentials`, `locked`, `unverified`, `inactive` | Every login that reached the handler (a 400 or 429 never does). `invalid_credentials` covers an unknown email, a wrong password (also while locked), an account without a password and a successful login that lost a save race; `locked` is a correct password on a locked account; `unverified` and `inactive` are the two 403s. |
| `auth.registrations` | none | Every accepted registration request, new and existing addresses alike, so the metric cannot tell them apart any more than the response can. A breached password or a 400 is not counted. |
| `auth.token_reuse_detected` | none | Refresh tokens presented again after use (defined now; the refresh handler counts them). |

## Testing

<!-- Where the module's unit and integration tests live and what they cover. -->

The module is covered by the architecture tests (`tests/TemplateName.ArchitectureTests`): layering, module boundaries, naming, documentation and translations all include `TemplateName.Modules.Auth`. Unit tests of the domain live under `tests/TemplateName.UnitTests/Auth/`. Integration tests live under `tests/TemplateName.IntegrationTests/Auth/`: `AuthPersistenceTests` covers aggregate round trips, the unique and filtered indexes, soft delete, UTC timestamps, the outbox row written with a new user, the concurrent refresh-token claim and the key ring in `auth.DataProtectionKeys`. The access tokens are covered by unit tests: `AccessTokenIssuerTests` (header, the exact claim set, expiry from `TimeProvider`, validation with only the published key), `JwtValidationTests` (Review Focus 4: `alg: none`, `HS256` signed with the public key's bytes, an unknown, missing or mislabelled `kid`, a tampered payload, a wrong audience or issuer, no `exp`, expiry and not-before inside and beyond the skew, a retired key) and `SigningKeyProviderTests` (other curves, malformed and mismatched PEMs, no echo of key material, missing keys outside Development and Testing, the ephemeral key and its warning, the JWKS members, the bearer registration); `JwksEndpointTests` (integration) reads the JWKS through the host. `PipelineTests` (integration) repeats Review Focus 4 against the running host, on the test-only `GET /test/protected` endpoint protected by the fallback policy alone: no token, garbage, `alg: none`, `HS256` with the public key's bytes, an unknown `kid`, a tampered payload, a wrong audience and an expired token (moved past `exp` + skew on `Factory.Time`) all get a 401 problem with `traceId` and `code` `http.401`, a valid token gets 200 with the token's `sub` and `sid` as `ICurrentUser`, and the `locale` claim wins over `Accept-Language`. `RateLimitOrderTests` proves the limiter runs before authorization: anonymous 401s and signed-in 403s (on the test-only `GET /test/permission`, which needs a permission nobody holds) are counted and end in 429. `TestAccessTokens` (`tests/TemplateName.IntegrationTests/Infrastructure/`) mints valid tokens with the host's own `IAccessTokenIssuer`. The email sender is covered by `AuthEmailsTests` and `EmailOptionsTests` (unit) and, against a real Mailpit container started by `MailpitFixture` (generic Testcontainers, image pinned to the tag in `docker-compose.yml`, which a test compares), by `SmtpEmailSenderTests`: text and HTML parts, sender and subject read back through Mailpit's REST API (`GET /api/v1/messages`, `GET /api/v1/message/{id}`), an unreachable server, a malformed address and a cancelled token. `RecordingEmailSender` (`tests/TemplateName.IntegrationTests/Infrastructure/`) is the test double the integration test factory registers in place of `IEmailSender` (exposed to tests as the internal `Factory.EmailSender` and emptied before every test): it keeps the messages in `Sent`, `Clear()` empties it and `LastLinkToken(to)` returns the decoded `token=` value from the last message to an address.

Permissions and seeding: `PermissionSynchronizerTests` (unit) covers every validation rule of a permission definition, including a trailing newline and a code declared by two sources. `PermissionCheckerTests` (integration) covers the union over roles, a soft-deleted role, a soft-deleted or suspended user, the cache (a second call within 30 seconds opens no connection, counted by `RecordingDbConnectionFactory`; `InvalidateUsersAsync` reloads only the named users; the entry expires at 30 seconds of `Factory.Time`, with no real waiting) and the registration. `AuthSeederTests` (integration) covers idempotency, `SuperAdmin` receiving new permissions, `Admin` defaults surviving manual changes, deprecation instead of deletion (and the way back), an invalid definition failing the run without changes, the administrator settings (both required, the normalized email, confirmation, the hash, no echo of a short password, a soft-deleted administrator never re-created) and seeding on host start. The test harness turns `Auth:Seed:RunOnStartup` off and seeds after migrating and after every reset; `TestPermissionSource` (`Factory.PermissionSource`) lets a test declare or drop permissions between runs and starts empty in every test.

Registration: unit tests cover the validator (`RegisterCommandValidatorTests`, Review Focus 5: 11, 12, 128 and 129 characters, a 1 MB string, only white space, Unicode, the password equal to the email, the email, display name and locale rules; `ConfirmEmailCommandValidatorTests`, `ResendConfirmationCommandValidatorTests`), the handlers with NSubstitute (`RegisterCommandHandlerTests`, including `Oversized_password_is_rejected_before_hashing` through the real `ValidationDecorator`, the hash-before-lookup order and the single save; `ConfirmEmailCommandHandlerTests`; `ResendConfirmationCommandHandlerTests`) and the email handlers (`SendVerificationEmailDomainEventHandlerTests`, both purposes; `SendRegistrationAttemptedEmailDomainEventHandlerTests`), each checking that nothing sensitive is logged. `RegistrationTests` (integration) drives the three endpoints, dispatches the Auth outbox after each call (`OutboxDispatcher<AuthDbContext>.ProcessBatchAsync`) and reads `Factory.EmailSender`: one confirmation email and 204; single use; expiry at 60 minutes on `Factory.Time`; a reset token (seeded through the repository) and a token replaced by a resend both rejected (Review Focus 3); a known address answering the same 202, headers and empty body, with the attempt notice; `Alice@Example.com ` and `alice@example.com` as one account (Review Focus 1); the resend cooldown, unknown and confirmed addresses; the `User` role and the locale (requested, or from `Accept-Language`); 129-character and 1 MB passwords refused with 400; `auth-strict` 429 with `Retry-After` on each route (a host with `RateLimiting:AuthStrictPermitLimit` 3); and no plaintext token in `auth.OutboxMessages`. The test factory sets `Auth:Password:CheckBreached` to `false`, so no test calls Have I Been Pwned.

Login and current user: `LoginCommandHandlerTests` (unit, NSubstitute) pins the handler order: an unknown email still spends the verification cost; an account without a password behaves exactly like an unknown email (Ruling R3); a wrong password counts, audits and saves; the fifth failure locks and audits `auth.locked_out`; a wrong password while locked changes no counter; a correct password while locked answers `auth.invalid_credentials` with the reason `locked` and leaves the counters; `auth.email_not_verified` and `auth.account_inactive` come only after a correct password; the success path (session, tokens, audit, metric, one save); the device name default; the rehash through `UpgradePasswordHash` without a new stamp; a lost save race on the success path answering `auth.invalid_credentials`; failures counted through `RecordFailedSignInAsync` (a fake that applies `User.RecordFailedSignIn`, the specification) with the lockout event raised once by the handler; and `Oversized_password_is_rejected_before_hashing` through the real `ValidationDecorator`. `LoginCommandValidatorTests` covers the rules (no minimum, 128 and 129 characters, a 1 MB string, the device name at 200 and 201), `AuthMetricsTests` the instruments and their tags through a `MeterListener`, and `UserTests` `UpgradePasswordHash` and `NoteLockedOut`. `LoginTests` (integration) signs users in through the endpoint: the tokens, the stored session (device name, address, user agent, only the refresh token's hash) and `GET /me` with the new access token; the token's `sub`, `sid`, `sst`, `amr`, `locale` and `auth_time`; the identical 401 for an unknown email, a wrong password and an account without a password (status, headers, every body member but `traceId`); the email in any case and with surrounding spaces (Review Focus 1); lockout after five failures and its end exactly at `LockoutEnd` on `Factory.Time`; ten wrong passwords at once from ten addresses, all counted (identical 401s, count 5 and locked, ten `auth.login_failed` rows, one `auth.locked_out` row, one lockout outbox message); the failure count read back from the row; both 403s; the audit rows (masked email, trace id equal to `X-Trace-Id`, client address, user agent, session id, no password anywhere); `auth-strict` per client address (a host with `RateLimiting:AuthStrictPermitLimit` 3: the fourth request from one address is 429, another address still gets 401); and 400 for a 129-character or 1 MB password and a 201-character device name. `MeTests` (integration) covers the response members, the roles and permissions with a soft-deleted role hidden (Dapper filter proof), 401 without a token, and the fail-closed 401 for a valid token whose user is unknown, suspended or soft-deleted. `PermissionCheckerTests` also covers `IPermissionReader` (the sorted set from the cache entry the checker uses), and `AuthPersistenceTests` `TrySaveChangesAsync` (false on a real `RowVersion` conflict between two successful sign-ins, nothing saved), the SQL count against the domain rule over the same states, a soft-deleted user left alone, and a count made in the database surviving the save of the tracked user (only the event and the audit row written).

The test server has no client address: `TestClientAddressStartupFilter` (`tests/TemplateName.IntegrationTests/Infrastructure/`, registered by the factory) sets the connection's remote address from the test-only `X-Test-Client-Address` header ahead of the whole pipeline, so the forwarded-headers middleware, the per-address limiter and `IClientContext` see a real address. `AuthTestHarness.CreateUserAsync` (`IntegrationTestBase.CreateUserAsync(email, password, confirmed, suspended, locale)`) writes a user who signs in through the endpoint: a known password hashed by the real hasher (or none, for an administrator-created account), the seeded `User` role, confirmed and active unless asked otherwise, no session.

Signing in from a test: `IntegrationTestBase.SignInAsync(params string[] permissions)` (and `SignInAsync(permissions, locale)`) uses `AuthTestHarness` (`tests/TemplateName.IntegrationTests/Infrastructure/`, through `InternalsVisibleTo`) to write a confirmed user with a known password (hashed by the real `IPasswordHasher`; the factory lowers `PasswordHasherOptions.IterationCount` to 1 000), a role of its own holding the named codes (a code no source declares is inserted into `auth.Permissions` first), and a session with one refresh token (`UserSession.Start`), then mints a real access token with the host's `IAccessTokenIssuer` on `Factory.Time` (saved locale `en` unless given) and sets it on `Client`. It returns `SignedInUser(UserId, Email, Password, AccessToken, SessionId)`; `SignOut()` removes the header, `RenewAccessTokenAsync()` issues a new token after a test moved the clock past its lifetime, and `CreateClientAsync(host)` gives a host derived with `WithWebHostBuilder` a client with a token minted by that host (each host has its own ephemeral signing key). The locale is the token's `locale` claim, which wins over `Accept-Language`.

## Changelog

<!-- Link to the changelog entries for this module's commit scope. -->

Changes to this module are the [`CHANGELOG.md`](../../CHANGELOG.md) entries with scope `auth` (release-please prints them as **auth:**). To list them from git: `git log --oneline -E --grep='^[a-z]+\(auth\)!?:'`.
