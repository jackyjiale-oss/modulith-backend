# Social login — design

- Status: Approved 2026-10-30. The design was approved section by section in the design session (flow, linking rule, MFA, sign-up, verification approach, configuration, security rules and scope); the user then asked for the open points to be settled by research and the plan to proceed. Section 12 records what the research found and the three amendments it caused (4, 5.2, 11).
- Date: 2026-10-30
- Roadmap row: new, after 5c. In `docs/blueprint-review.md` Section 6 it is part of the row "Social login and phone-number OTP login"; adding its own row and leaving phone OTP behind SMS is part of this plan.
- Builds on: Plans 1–5c, all merged (main `8f442ec`); ADR 0014 (own identity model), ADR 0015 (ES256 tokens), ADR 0023 (step-up), ADR 0025 (trusted devices), ADR 0029 (HTTP resilience), ADR 0031 (compatibility policy)
- Source: the user's roadmap decision of 2026-10-20 (Google, Apple, Microsoft, Facebook and a generic OpenID Connect entry; every provider off by default behind `Enabled`) and `BACKEND_TEMPLATE_BLUEPRINT.md` 9.7 (whose redirect-based `challenge`/`callback` routes this design replaces; the review wins)

## 1. Intent

**Outcome.** A person signs in or signs up with an account they already have at Google, Apple, Microsoft, Facebook or another OpenID Connect provider, from a web SPA or a Flutter app, and ends up in the same session as a password login: the same access and refresh tokens, `GET /me`, sessions, permissions, notifications and two-step verification. A team that starts a project from the template switches each provider on or off in configuration.

**Success looks like.**

1. `POST /api/v1/auth/external/{provider}/login` takes the provider's proof and answers the existing `LoginResult`: tokens, or the existing MFA challenge.
2. Each provider is off by default and is switched by `Auth:External:Providers:{name}:Enabled`; a switched-off provider is invisible and refused.
3. A provider identity is linked to a user once and found by `(Provider, Subject)` ever after, never by email.
4. An existing account is linked automatically only under the strict rule of section 5.3; otherwise the user links from settings after a step-up.
5. A password-less account can still step up and unlink: it proves itself with a fresh provider proof.
6. No client secret is stored for Google, Apple, Microsoft or generic OIDC; the one secret (Facebook's app secret) comes from user-secrets or the environment.
7. CI never calls a real provider; every verification path is tested against keys and metadata the tests generate.
8. Nothing here breaks an existing route, response, table or column (ADR 0031): every route is new, the one new request member is optional, and the database change expands only.

**Said by you:** the five providers, per-provider on/off by configuration, same sessions and step-up rule, a separate plan from push, SMS and phone OTP; in this session: the client runs the provider step and the backend verifies it; link automatically only under strict rules; the MFA challenge still applies; sign-up behind one switch; one OIDC verifier with presets plus a Facebook adapter.

**Assumed by me:** the clients are a web SPA and a Flutter app that can obtain a provider's ID token (or Facebook access token) themselves; the template stays a JSON API with tokens and no cookies; Auth owns the feature and references no other module; the provider's metadata is reachable from the server.

## 2. Non-goals

- **No backend redirect flow** (`challenge` and `callback`). The client runs the provider step. A project that needs it adds routes later; the verification code is reusable.
- **No provider tokens kept and no provider API calls** after sign-in (no calendar, no profile photo, no avatar sync). The proof is checked once and discarded.
- **No admin view or admin unlink** of a user's logins. A user manages their own.
- **No phone OTP, SMS or push**, no GitHub, no tenant SSO, no account merging (two existing accounts are never combined), no provider-initiated or back-channel logout.
- **No new package.** `Microsoft.IdentityModel.Protocols.OpenIdConnect` is already in the Auth project's graph through the JWT bearer package; the plan makes the reference explicit through Central Package Management if the build needs it.

## 3. Decisions

| # | Decision | Why | Rejected, and cost if you disagree |
|---|---|---|---|
| D1 | **The client posts the provider's proof; the backend verifies it.** An ID token with its nonce, or Facebook's access token. | One route serves web and mobile, native SDKs work, and the backend keeps no redirect state or cookie. | **Backend redirect flow:** server-side state, a front-end handoff page, no native SDK. **Both:** doubles routes, state and tests. |
| D2 | **One generic OIDC verifier with presets (Google, Microsoft, Apple) plus a small Facebook adapter.** The four named providers and any `Type: "oidc"` entry use the same code. | They all issue OIDC ID tokens, so the four need only an issuer, audiences and an email rule; no client secret exists to leak or rotate (Apple's rotating signed-JWT secret is avoided). Facebook issues an ID token only in iOS Limited Login; on web and Android it issues an access token, so it needs its own verifier. | **A class per provider:** four near-copies of one verifier. **ASP.NET Core's `AddGoogle`/`AddFacebook` handlers:** cookie and redirect based, no first-party Apple handler, a cookie scheme the template does not have. |
| D3 | **A linked identity is `(Provider, Subject)`; email is never used to find a user after the link.** | Email is mutable at the provider and can be reassigned; `sub` is stable. | **Match by email each time:** an account takeover when a provider's email changes hands. |
| D4 | **Strict linking** (section 5.3): automatic only when the provider's email is verified by that provider's rule, the provider is trusted for email in configuration, and the local email is confirmed. | A password account registered by an attacker with the victim's address must not become the victim's account after the victim signs in with Google. | **Never link by email:** safest, but a password user who taps "Sign in with Google" is told to sign in the old way first. **Always link:** takeover by an unverified provider email. |
| D5 | **The MFA challenge applies to a social sign-in.** `auth.MfaChallenges` gains `FirstFactor` (`pwd` default, or `fed`) so the verify step records `fed otp mfa`. Trusted devices never apply. | We cannot know whether the provider enforced its own second factor, and an account that turned ours on keeps it. ADR 0025: a trusted device replaces the code, never the first factor. | **Trust the provider:** whoever enters the user's Google account bypasses the authenticator. **A per-provider switch:** a policy each project can get wrong. |
| D6 | **Sign-up on first social sign-in, behind `Auth:External:AllowSignUp` (default on).** | Open password registration is the template's default; an invite-only project turns it off without a code change. | **Never create:** "Sign in with Google" cannot onboard anyone. **No switch:** an invite-only project patches code. |
| D7 | **The backend issues every token the client uses.** The provider's proof is discarded after verification. | One session model: refresh rotation, reuse detection, revocation, permissions and `amr` stay ours; other services trust only our JWKS. | **Passing provider tokens through:** two token systems and no way to revoke. |
| D8 | **A password-less, MFA-less account steps up with a fresh proof of one of its own linked identities** (`amr` `fed`). A password account keeps the password; an MFA account keeps the code. | Without it a social-only user cannot link a second provider or unlink one. | **Require a password first:** a forced detour through forgot-password for a sensitive action. |
| D9 | **Replay is bounded by the token's age and a single-use nonce.** `iat` must be younger than `Auth:External:MaxTokenAge` (5 minutes) and each nonce is accepted once in that window (best effort: shared with Redis, per instance without). | A stolen ID token and its nonce could otherwise be replayed against us for as long as the provider's token lives. | **A server-issued nonce:** an extra route and server state, against D1. |
| D10 | **Microsoft's `email` is never a verified email; only `xms_edov = true` is.** Its subject is `tid` plus `oid`. | Microsoft documents `email` as "not guaranteed to be correct, and mutable"; its `sub` is pairwise per app registration. | **Trust Microsoft's `email`:** the known nOAuth takeover. |
| D11 | **Unlinking rotates the security stamp and ends the caller's other sessions; linking only sends a notice.** | Unlinking may be the response to a compromised provider account. | **Rotate on link too:** signs out the owner's other devices for an addition they made. |
| D12 | **Providers are a dictionary of configuration names; `Provider` in the table is that name as a string.** | Adding or renaming a provider never needs a migration, and a project can configure several generic OIDC entries. | **An enum column:** a migration per provider. |

## 4. Surface

All routes are new, under `/api/v1/auth/external`, tag `Auth`.

| Method | Route | Auth | Purpose and answers |
|---|---|---|---|
| `GET` | `/providers` | anonymous, global limiter | The enabled providers: `[{ name, displayName, type }]` (`type` is `oidc` or `facebook`). Never client IDs or secrets. 200. |
| `POST` | `/{provider}/login` | anonymous, `auth-strict`, `no-store` | Signs in or signs up (section 5). Body `{ idToken?, accessToken?, nonce?, displayName?, locale?, deviceName? }`. 200 `LoginResult` (tokens, or `mfaRequired` with a challenge). |
| `POST` | `/{provider}/link` | signed in, fresh step-up | Attaches the proven identity to the caller. Body `{ idToken?, accessToken?, nonce? }`. 200 with the new login item; the same identity already linked to the caller is 200 unchanged. |
| `GET` | `/logins` | signed in | `{ hasPassword, logins: [{ id, provider, email, linkedAt, lastUsedAt }] }`, at most one login per provider, so not paged. 200. |
| `DELETE` | `/logins/{id:guid}` | signed in, fresh step-up | Unlinks (section 5.5). 204. |

**Body rules (validators).** Exactly one of `idToken` and `accessToken`; `accessToken` only for `facebook` (which also accepts an iOS Limited Login `idToken`); `nonce` required with `idToken` (16 to 256 characters); `idToken` and `accessToken` at most 8192 characters; `displayName` at most 200; `locale` a predefined culture name (default: the request's language, as in register); `deviceName` at most 200 (`Unknown` when absent). The step-up route (`POST /api/v1/auth/step-up`) gains the same optional proof members plus `provider`, still "exactly one proof" (D8).

**Errors** (all in `en`, `ms` and `zh-Hans`):

| Code | Status | When |
|---|---|---|
| `auth.external_provider_not_found` | 404 | the provider is unknown or disabled, alike |
| `auth.invalid_external_token` | 401 | the proof fails any check of section 5.2 |
| `auth.external_provider_unavailable` | 503 | the provider's metadata or Graph call cannot be reached; never reported as a bad token |
| `auth.external_email_unverified` | 403 | an unlinked identity without a usable email |
| `auth.external_link_required` | 409 | an account with that email exists and cannot be linked automatically; sent only to a caller who proved a usable email |
| `auth.external_sign_up_disabled` | 403 | no account, and `AllowSignUp` is off |
| `auth.external_identity_taken` | 409 | link: the identity belongs to another user |
| `auth.external_provider_already_linked` | 409 | link: the caller already has a login for that provider with a different identity |
| `auth.external_login_not_found` | 404 | unlink: an unknown id and another user's alike |
| `auth.last_login_method` | 409 | unlink would leave neither a password nor another login |
| `auth.step_up_identity_mismatch` | 400 | step-up by proof: the identity is not one of the caller's |

Existing codes keep their meaning: `auth.account_inactive` 403 (suspended, after a valid proof), `auth.invalid_credentials` 401 (locked), `auth.step_up_required` 403, `auth.step_up_method_not_allowed` 400.

## 5. Behaviour

### 5.1 Login, in order

1. Resolve the provider from configuration; disabled or unknown is `auth.external_provider_not_found`.
2. Verify the proof (5.2) into a `VerifiedExternalIdentity(Provider, Subject, Email?, HasUsableEmail, Name?)`.
3. **Linked identity:** load the user by `(Provider, Subject)`. Go to 5.4.
4. **Not linked:** without a usable email, `auth.external_email_unverified`. With one, look the user up by `NormalizedEmail`:
   - **found, email confirmed:** link the identity to it (5.3), then 5.4;
   - **found, email unconfirmed:** `auth.external_link_required`;
   - **not found, `AllowSignUp` on:** register (5.6), then 5.4;
   - **not found, `AllowSignUp` off:** `auth.external_sign_up_disabled`.
5. Concurrency: the unique indexes on `(Provider, Subject)` and `NormalizedEmail` decide a race. The loser re-reads and continues on the linked or link-required path, never a 500 (the `Register` pattern, `SaveChangesUnlessDuplicateAsync`).

### 5.2 Verifying a proof

**OIDC (`OidcIdTokenVerifier`).** The provider's signing keys come from its discovery document (`ConfigurationManager`, cached, refreshed on an unknown `kid`, through the `oidc-discovery` resilience pipeline of ADR 0029). Checks: algorithm from an allow-list (`RS256`, `ES256`; `none` and `HS*` fail); signature; `iss` exactly the provider's issuer (Microsoft: `https://login.microsoftonline.com/{tid}/v2.0` for the token's own `tid`, and with a specific `TenantId` configured only that tenant is accepted); `aud` one of `ClientIds`; `exp` and `nbf` with `Auth:Jwt:ClockSkew`; `iat` younger than `Auth:External:MaxTokenAge`; `nonce` equal to the posted nonce or to the lower-case hex SHA-256 of it (Apple's native SDK sends it hashed) and not yet used (D9). A missing `sub` (Microsoft: `tid` or `oid`) is a failure. Subject is `sub`, except Microsoft's `{tid}:{oid}` (D10).

**Usable email.** `Email` is the token's `email`; `HasUsableEmail` is true only when the provider's rule holds **and** its `TrustEmail` is on: Google and Apple, `email_verified` true (a boolean or the string `"true"`); Microsoft, `xms_edov` true; generic OIDC, `email_verified` true; Facebook never. `TrustEmail` defaults to on for Google, Apple and Microsoft, off for Facebook and generic OIDC. Microsoft's claim exists only when the project adds the optional claim `xms_edov` to its app registration; the setup guide says so, and without it Microsoft sign-up refuses (`auth.external_email_unverified`).

**Facebook (`FacebookAccessTokenVerifier`).** `GET debug_token` with the app access token (`AppId|AppSecret`): `is_valid`, `app_id` equals `AppId`, not expired; then `GET /me?fields=id,name,email` with `appsecret_proof`, through the `facebook-graph` pipeline. Subject is the Facebook user id. The `facebook` provider also accepts an iOS Limited Login `idToken`, which goes through the OIDC verifier with the Facebook preset (issuer `https://www.facebook.com`, audience `AppId`, nonce required, no secret needed); Meta's token debugger cannot inspect such a token, so the Graph path is never used for it, and its subject is the token's `sub`. A Facebook access token is a bearer with no nonce; replay beyond the checks above is a documented limit.

**Presets** (each read from the provider's live discovery document on 2026-10-30, section 12): Google, issuer `https://accounts.google.com` (the bare `accounts.google.com` is also accepted, as Google documents), keys at `https://www.googleapis.com/oauth2/v3/certs`; Apple, `https://appleid.apple.com`, keys at `https://appleid.apple.com/auth/keys`; Microsoft, `https://login.microsoftonline.com/{tenantid}/v2.0`, keys at `https://login.microsoftonline.com/common/discovery/v2.0/keys`; Facebook, `https://www.facebook.com`, keys at `https://www.facebook.com/.well-known/oauth/openid/jwks/`. All four sign with `RS256`; the allow-list stays `RS256` and `ES256` for generic providers. Microsoft's `oid` is issued only with the `profile` scope, so the client requests `openid profile email`; a token without `oid` or `tid` is refused. A generic entry sets `Authority`.

### 5.3 Linking (strict)

An existing account is linked by a social sign-in only when all hold: the identity has a usable email (5.2), that email equals the account's `NormalizedEmail`, and the account's email is confirmed. Otherwise `auth.external_link_required`: the user signs in the usual way and links from settings (`POST /{provider}/link`, step-up). A user has at most one login per provider (unique index on `(UserId, Provider)`). A link, automatic or by request, raises `ExternalLoginLinked` (5.7) and an audit entry.

### 5.4 Starting the session

After the user is resolved: a suspended account is 403 `auth.account_inactive`; a locked account is 401 `auth.invalid_credentials`, so a lockout closes every door until it ends; neither touches the failure count. Then, as password login does: an account with a confirmed MFA method gets a challenge (`MfaChallenge.Issue` with `FirstFactor = fed`, answered by `POST /api/v1/auth/mfa/verify` unchanged), otherwise `SessionStarter` starts the session with first factor `fed` (`UserSession.AuthMethods` `fed`), sets `LastLoginAt` and `LastUsedAt` on the login, and audits `auth.external_login_succeeded`. `trustedDeviceToken` is not read. Wrong MFA codes in the challenge count toward `Auth:Lockout` exactly as today.

### 5.5 Unlinking

Needs a fresh step-up through `StepUpPolicy` (an MFA account proves a code). The login must be the caller's (`auth.external_login_not_found` otherwise). It is refused with `auth.last_login_method` unless the user has a password or another login. On success: the row is deleted, the security stamp rotates, every other session of the caller ends (a new, appended `SessionRevokedReason`, never renumbered), the current one stays, and `ExternalLoginUnlinked` is raised. Unlinking works for a switched-off provider.

### 5.6 Registering

`User.RegisterExternal(email, displayName, locale, now)`: an active user with no password and a confirmed email (sign-up needs a usable email), role `User`, time zone `UTC`, a new security stamp. The display name is the body's `displayName`, else the token's `name`, else the part of the email before `@` (Apple sends the name to the client only). It raises the domain events a confirmed registration raises today, so no consumer needs a new case; the plan pins which. Audit `auth.external_registered`. A user who later wants a password uses the existing forgot and reset flow.

### 5.7 Events, audit, observability

- **Integration events** (`Auth.Contracts`): `ExternalLoginLinkedIntegrationEvent` and `ExternalLoginUnlinkedIntegrationEvent` (user id, provider display name, time). Notifications adds two security-notice types with email and in-app templates in `en`, `ms` and `zh-Hans`; `docs/modules/notifications.md` is read first, as `CLAUDE.md` requires.
- **Audit:** `UserExternalLogin` is `[Audited]` (key `ValueGeneratedNever()`; nothing secret to ignore). The Auth audit log gains `auth.external_login_succeeded`, `auth.external_login_failed` (with a reason), `auth.external_linked`, `auth.external_unlinked` and `auth.external_registered`.
- **Logging and metrics:** commands and requests print only their type name; a proof, a nonce or a token never reaches a log, an audit row or a tag. The `auth.external_logins` counter is tagged with the provider only when it is a configured one, and the outcome.

## 6. Data and configuration

### 6.1 Schema (one migration, expand-only, ADR 0031)

`auth.UserExternalLogins`: `Id` (`SequentialGuid`), `UserId`, `Provider` (`nvarchar(64)`), `Subject` (`nvarchar(256)`), `Email` (`nvarchar(256)`, null; display only), `LinkedAt`, `LastUsedAt`. Unique index `(Provider, Subject)`; unique index `(UserId, Provider)`; foreign key to `Users`. It is a child of `User` in `Domain/Users/`. `auth.MfaChallenges.FirstFactor` (`nvarchar(16)`, not null, default `pwd`). No drop or rename, so no `[ContractMigration]`; a migration test confirms it.

### 6.2 Configuration (`Auth:External`, validated at start-up)

```json
"External": {
  "AllowSignUp": true,
  "MaxTokenAge": "00:05:00",
  "Providers": {
    "Google":    { "Enabled": false, "ClientIds": [] },
    "Microsoft": { "Enabled": false, "ClientIds": [], "TenantId": "common" },
    "Apple":     { "Enabled": false, "ClientIds": [] },
    "Facebook":  { "Enabled": false, "AppId": "", "AppSecret": "" },
    "Okta":      { "Enabled": false, "Type": "oidc", "Authority": "https://…", "ClientIds": [] }
  }
}
```

Common keys: `Enabled` (off), `DisplayName`, `TrustEmail` (5.2). A name other than the four must say `Type: "oidc"` and give `Authority`. `MaxTokenAge` is 1 minute to 1 hour. An **enabled** provider without `ClientIds` (Facebook: `AppId` and `AppSecret`), with an `Authority` that is not absolute `https`, or an unknown name without `Type` stops the host; the message names the key, never a value. Disabled providers are not validated and not exposed. `AppSecret` is never in a file (user-secrets or an environment variable). Switching a provider off removes it from `/providers`, makes its `login` and `link` answer 404, keeps its rows and lets users unlink; nobody is locked out, because forgot-password still gives them a password.

## 7. Where it lands

| Area | Change |
|---|---|
| `Auth` Domain | `UserExternalLogin`, `User.RegisterExternal`, `User` child collection, the appended revoke reason, `MfaChallenge.FirstFactor` |
| `Auth` Application | `Application/ExternalLogins/{Login,Link,Unlink,List,Providers}/` (message, handler, validator each); `IExternalIdentityVerifier`, `IExternalProviderCatalog`, `IExternalProofReplayGuard`; the step-up command and handler gain the proof; `SessionStarter` and the verify handler read `FirstFactor` |
| `Auth` Infrastructure | `Infrastructure/External/`: `OidcIdTokenVerifier`, `FacebookAccessTokenVerifier`, the catalog, options and their validators; configuration; the migration; repository members |
| `Auth` Endpoints | `Endpoints/ExternalLoginEndpoints.cs`; `login` carries `auth-strict` (the route-list test changes to match) |
| `Auth.Contracts` | the two integration events |
| `Notifications` | two types and their templates in three languages |
| Resilience | `oidc-discovery` and `facebook-graph` pipelines (`Resilience:Pipelines:*`) |
| `Resources` | the error codes of section 4 in all three `.resx` files |
| Docs | `docs/modules/auth.md` (External login section: endpoints, configuration, data, known limits), ADR 0032 (D1, D2, D4, D5, D7, D8), a per-provider setup guide (consoles, Apple Services ID and bundle ID, Microsoft's `xms_edov`, Facebook app review and Limited Login), `notifications.md`, `services/api.md`, the version register (still `v1`), `blueprint-review.md` row, the OpenAPI snapshot |

The template-smoke script must still pass: nothing here names a concrete project, and every file is code, a resource or under `docs/`.

## 8. Tests, first

- **Verifier (unit, no network):** keys and a fake discovery handler generated in the test; wrong issuer, audience, nonce, expired, too old, `alg: none`, `HS256`, unknown `kid`, missing `sub`; Microsoft `tid` handling and `xms_edov`; Apple's string `"true"` and hashed nonce; Facebook valid, wrong app, expired, `appsecret_proof` sent.
- **Domain and handlers (unit):** the linking matrix (linked, found and confirmed, found and unconfirmed, unusable email, sign-up on and off), suspended and locked, last-login-method, one login per provider, nonce replay, step-up by proof (allowed only without password and MFA, identity must be the caller's, wrong proof not counted).
- **Integration (CI, Docker):** every route against a fake provider; the MFA challenge with `fed` and the resulting `amr`; concurrent first sign-ins creating one account; unlink ending other sessions; a switched-off provider.
- **Existing suites:** the OpenAPI snapshot is updated and the compatibility gate must classify the change as compatible; the `auth-strict` route-list test and the Auth documentation tests pass; a migration test confirms the expand-only migration; no architecture rule is relaxed.
- **Real providers:** not called in CI. The setup guide carries a manual checklist per provider, including one personal and one work or school Microsoft account (D10).

## 9. Schedule

1. **Social login, this design** (spec, plan, implementation).
2. **Push and SMS channels** (a Notifications plan).
3. **Phone OTP login**, after SMS.
4. **Plan 6 delivery** stays where the blueprint puts it.

Order, not dates: each plan starts when the one before is merged.

## 10. Risks

- **A preset drifts from the provider's published metadata.** The plan verifies each preset against the live discovery document once; a generic entry is the escape hatch.
- **Microsoft's identifiers differ for a personal and a work account.** The checklist tests both; a token without `tid` or `oid` is refused rather than guessed.
- **Replay inside `MaxTokenAge` on another instance without Redis.** Documented known limit, like the permission cache's 30 seconds; the single-use nonce and the age bound make it narrow.
- **A project turns `TrustEmail` on for Facebook or a generic provider that does not verify email.** The page states the takeover this opens; the default is off.
- **Provider outages.** Discovery and Graph run on resilience pipelines; an outage is 503, not a login failure, and password login is unaffected.

## 11. Open questions

None. The three points flagged for review are settled: `fed` stays as the `amr` value (RFC 8176 registers no federated value, so a custom one is unavoidable, like `rcvr`; clients that read `amr` must accept it); Facebook's `TrustEmail` stays off (the user's choice); D8's step-up by provider proof stays.

## 12. Research notes (2026-10-30)

- **Presets.** Issuer, key address and signing algorithm of Google, Apple, Microsoft and Facebook were read from their discovery documents: `https://accounts.google.com/.well-known/openid-configuration`, `https://appleid.apple.com/.well-known/openid-configuration`, `https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration` and `https://www.facebook.com/.well-known/openid-configuration/`. All four use `RS256`. Microsoft's issuer carries a `{tenantid}` placeholder, which is why D10 and 5.2 check it against the token's `tid`.
- **Microsoft.** [Optional claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference): `email` "isn't guaranteed to be correct, and is mutable"; `xms_edov` is the domain-verified signal and needs the `email` claim. [ID token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference): `sub` is pairwise per application; `oid` is the same across applications and needs the `profile` scope; "`oid` and `tid` is best" when data is shared across services; personal accounts have `tid` `9188040d-6c67-4c5b-b112-36a304b66dad`. This confirms D10's subject `{tid}:{oid}`.
- **Apple.** [`id_token` reference](https://developer.apple.com/documentation/signinwithapplejs/authorizationi/id_token.md): `email_verified` may be a string or a boolean, and `is_private_email` may be absent, so neither is relied on beyond `email_verified`; when `nonce_supported` is true the nonce is mandatory, which the required nonce satisfies; the native flow commonly sends the nonce hashed, which 5.2 accepts alongside the raw value.
- **Facebook.** [Validating a Limited Login token](https://developers.facebook.com/documentation/facebook-login/ios/limited-login/token/validating): signature through the JWKS, `exp`, `iss`, `aud` equal to the app ID and `nonce`. Hence the Facebook preset in 5.2.
- **`amr`.** RFC 8176 registers 20 values (`pwd`, `otp`, `mfa`, `sms`, `wia` and others) and none for a federated login.
- **Still manual.** A real personal and a real work or school Microsoft account, an Apple Services ID and bundle ID, and Facebook app review cannot be checked from here; the setup guide carries them as a checklist (section 8).
