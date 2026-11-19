# ETag and If-Match on the update endpoints — design

- Status: approved 2026-11-19; implementation plan `docs/superpowers/plans/2026-11-19-if-match-concurrency.md`.
- Date: 2026-11-19
- Roadmap row: new. `BACKEND_TEMPLATE_BLUEPRINT.md` line 308 (API conventions) asks for "`ETag` / `If-Match` for optimistic concurrency on updates where needed"; `docs/blueprint-review.md` never took it up, and no ADR declined it. Adding its own row is part of this plan.
- Builds on: the core baseline's `DbUpdateConcurrencyException` → 409 `concurrency.conflict` (ADR 0006), ADR 0009 (error messages in three languages), ADR 0031 (API compatibility policy).
- Source: the user's question of 2026-11-19 ("how does the project handle concurrent update"), the finding that two clients editing the same record minutes apart lose one edit without an error, and the four decisions in Section 3.

## 1. Intent

**Outcome.** A client that read a record and later sends an update is told when someone else changed that record in between, instead of silently overwriting the other change. A client opts in by sending back the version it read; a client that does not opt in behaves exactly as today.

**The gap today.** `User`, `Role`, `UserNotificationProfile` and `LeaveRequest` carry a `rowversion`, and a stale one becomes 409 `concurrency.conflict`. But no client ever sends a version: every update handler loads the record fresh and saves it, so the `rowversion` only covers the milliseconds between that load and that save. Two administrators who open the same role at 10:00 and save at 10:01 and 10:02 end with the second save overwriting the first, and no one is told. For audited records the audit trail shows both changes afterwards; nothing prevents the loss.

**Success.**
- Each of the six update endpoints in Section 4 accepts `If-Match`; a stale value is refused with 412 and nothing is written.
- Each matching read returns the current version as `ETag`, and each successful update returns the new one, so a client can chain edits without reading again.
- A client that sends no `If-Match` sees no change in behaviour.
- A version changes when, and only when, something the endpoint writes changes: a role's permissions changing changes the role's version; a user signing in does not change the version an administrator's role assignment checks.

## 2. Non-goals

- **Conditional reads** (`If-None-Match`, 304). The reads keep their current caching headers (`/auth/me` stays `no-store`); the ETag is for updates only.
- **Required preconditions** (428 Precondition Required). Optional now; requiring it later is a breaking change with its own release (Section 9).
- **State-change endpoints** (POST and DELETE such as lock and unlock a user, delete a role, approve a leave request, revoke a session). They already check state (approving twice fails) or act on records without a lasting representation; adding `If-Match` to them is a later, separate decision.
- **Idempotency on the PUT endpoints.** None of them uses `Idempotency-Key` today; Section 8 records what would have to change if one ever does.
- **Schema changes.** The version is computed, not stored (Section 3, decision 1).

## 3. Decisions (the user, 2026-11-19)

| # | Question | Decision | Why |
|---|---|---|---|
| 1 | What the ETag is computed from | A hash of the resource's **editable state**: exactly what its PUT endpoints write | Setting a role's permissions or a user's roles writes only link tables, so the parent `rowversion` does not change; a user's `rowversion` changes on every sign-in (`LastLoginAt`) and failed sign-in; preferences have no `rowversion` at all. A hash of the editable state has none of these problems and needs no migration. A hash of the whole response was rejected: the admin user view carries `LastLoginAt`, `IsLockedOut` and `LockoutEnd`, so every sign-in would make an administrator's role assignment fail. |
| 2 | Is `If-Match` required | **Optional** | Non-breaking (ADR 0031): existing clients keep working. Clients that send it are protected. |
| 3 | Which endpoints | **The six PUT endpoints** (Section 4) | They replace state wholesale, which is where lost updates happen. |
| 4 | How a stale `If-Match` is answered | **412 Precondition Failed**, code `concurrency.precondition_failed` | The HTTP answer for a failed `If-Match` (RFC 9110, 13.1.1); it lets a client tell "your copy is stale" (412) apart from the instant race the `rowversion` still catches (409 `concurrency.conflict`). |

## 4. Surface

| Resource | Read (returns `ETag`) | Update (accepts `If-Match`, returns the new `ETag`) | Editable state the version covers |
|---|---|---|---|
| Role | `GET /api/v1/admin/auth/roles/{id}` | `PUT /api/v1/admin/auth/roles/{id}` (204) and `PUT /api/v1/admin/auth/roles/{id}/permissions` (204) | name, description, the granted permission ids |
| User, as administered | `GET /api/v1/admin/auth/users/{id}` | `PUT /api/v1/admin/auth/users/{id}/roles` (204) | the assigned role ids |
| The caller's profile | `GET /api/v1/auth/me` | `PUT /api/v1/auth/me` (200, `MeResponse`) | display name, locale, time zone |
| Notification preferences | `GET /api/v1/notifications/preferences` | `PUT /api/v1/notifications/preferences` (200) | the stored choices: each (type code, channel, enabled) row the user has |
| Quiet hours | `GET /api/v1/notifications/preferences/quiet-hours` | `PUT /api/v1/notifications/preferences/quiet-hours` (200) | start and end (none when the user has no settings row) |

The two role endpoints share one version: a permissions change makes a stale name update fail, and the other way round. That is deliberate; a client that edits both reads once and chains the returned `ETag`.

What a version does **not** cover, on purpose: the user's sign-in data, lockout and status (changed by sign-ins and by the POST state changes, none of which conflicts with assigning roles); `MeResponse`'s roles and permissions (not written by `PUT /me`); `QuietHoursResponse.TimeZone` (owned by Auth, not written here); the notification type catalog (code, changes only with a deployment).

No response body changes. The version travels in headers only.

## 5. Behaviour

### 5.1 The version value

- One pure function per resource takes the editable values as plain values (not the entity), so the read path (Dapper row or EF entity) and the write path (the loaded aggregate) call the same function and cannot drift. A test proves, per resource, that the `ETag` a read returns equals the one the update computes for the same state.
- The function writes a canonical form and hashes it: the resource kind and a format version (`v1`), then the values in a fixed order, lists sorted by an invariant ordinal comparison, strings as they are stored, nulls written distinctly from empty strings, every value length-prefixed so that no two different states produce the same text. The hash is SHA-256, the first 16 bytes in base64url without padding, quoted: `ETag: "q3Y0b7lX2m9k1Rr0cVn8Aw"`.
- It is a **strong** ETag, because `If-Match` uses strong comparison (RFC 9110, 13.1.1: a weak tag never matches). Strictly, a strong validator should change whenever the representation's bytes change; ours changes when the editable state changes, so two responses that differ only in, say, `LastLoginAt` share an ETag. That is acceptable because no read supports conditional requests or shared caching (Section 2), and the ADR records it.
- Changing the canonical form later bumps the format version. Old ETags then stop matching, which fails safe: the client gets 412, reads again and retries.

### 5.2 Reading `If-Match`

A building block in Web.Common parses the header once per request:

| Header | Meaning |
|---|---|
| absent | No precondition: today's behaviour. |
| `"tag"`, or a comma-separated list of tags | Matches when the current version equals any of them. |
| `*` | Matches when the resource exists. Every resource in Section 4 exists for an authorised caller (preferences and quiet hours exist with their defaults), so `*` always matches. |
| `W/"tag"` | Weak; never matches (strong comparison), so 412. |
| anything else (no quotes, an empty value, a stray character) | 400 `request.malformed`: a client bug, not a stale copy. |

### 5.3 Order of checks in an update

1. Authentication and authorisation (401, 403), as today.
2. Request validation (400 `validation.failed`) and a malformed `If-Match` (400 `request.malformed`).
3. Load the resource (404 where the endpoint answers it today).
4. **The precondition**: compute the current version from what was loaded; when `If-Match` is present and does not match, answer 412 `concurrency.precondition_failed` and write nothing.
5. The endpoint's own rules (409 `auth.role_name_taken` and the like), as today.
6. Save. The `rowversion` still guards the save where the record has one, so a write that lands between step 4 and the save still becomes 409 `concurrency.conflict`.
7. Answer as today, adding `ETag` with the version of the new state. A permissions update that changes nothing saves nothing (as today) and returns the unchanged `ETag`.

The precondition is checked in the command handler, against the state the handler loaded, not in an endpoint filter: a filter would need its own read, and a write between that read and the handler's would slip through. The command carries the parsed precondition; the handler returns the new version with its result.

### 5.4 Notification preferences: the retry loop

Both preferences handlers run `ConflictRetry.SaveAsync`, which retries up to three times on a concurrency or duplicate conflict and re-stages from a fresh read, so that "the last one wins" (the handlers' own documentation). That stays the behaviour without `If-Match`. With `If-Match`, each attempt re-checks the precondition against its fresh read: if another request changed the preferences, the next attempt sees a different version and answers 412 instead of overwriting. Preferences have no `rowversion`, so between the check and the save the existing unique keys and the retry loop are the guard, as today.

### 5.5 The error

- New `ErrorType.PreconditionFailed` in the SharedKernel, mapped to 412 by `ResultExtensions.ToProblem`.
- New error `concurrency.precondition_failed`, declared in an `*Errors` class so `ErrorCatalog` and the translation tests find it, with messages in `en`, `ms` and `zh-Hans` (the latter two marked as drafts, as the others are). English: "The resource was changed since you read it. Read it again and retry with its current ETag."
- 412 is added to each update endpoint's documented problems (`ProducesProblem(412)`).

### 5.6 Headers across the platform

- **CORS.** `ETag` joins `WithExposedHeaders` in Web.Common, or a browser client cannot read it; `HttpSecurityTests` pins the list and gains it. `If-Match` is already allowed (`AllowAnyHeader`).
- **OpenAPI.** An operation transformer documents the optional `If-Match` request header on the six updates and the `ETag` response header on their success responses and on the matching reads, driven by endpoint metadata that one extension sets (for example `.WithEntityTag()`). The OpenAPI snapshot changes; under ADR 0031 an optional header parameter and a new response header are compatible.
- **Error responses** carry no `ETag`.

## 6. Where it lands

- **SharedKernel:** `ErrorType.PreconditionFailed`.
- **Application.Common or SharedKernel:** the canonical-form and hash helper (`EntityTag`), and the precondition type a command carries (`IfMatch`: none, any, or a set of tags), with its `Matches(EntityTag)` rule.
- **Web.Common:** `If-Match` parsing into that type, the `ETag` response helper, the 412 mapping, the CORS exposed header, the OpenAPI transformer and the endpoint metadata extension.
- **Auth module:** version functions for role, administered user and profile; the five Auth read and update paths (two role updates, user roles, `/me` read and update) pass the precondition in and the version out. The `/me` read is Dapper: its query already selects display name, locale and time zone.
- **Notifications module:** version functions for preferences and quiet hours; both handlers and their retry loop.
- **Documentation:** `docs/modules/auth.md` and `docs/modules/notifications.md` (each endpoint's headers and the 412), `docs/services/api.md`, the Web.Common building-block page, `docs/api-compatibility.md` (rows for an optional request header, and for making a precondition required), `docs/coding-conventions.md` (how a new PUT opts in), a new ADR (ETag over editable state, optional `If-Match`, 412, the strong-validator note), a row in `docs/blueprint-review.md`, and the project index regenerated.
- **Template:** everything ships to generated projects. The Notifications parts live in that module and leave with it under `--notifications false`; no new switch markers are needed outside it. The module item template is unchanged; the coding conventions tell a new module how to opt in.
- **Release:** a `feat` change, so the next release candidate.

## 7. Tests, first

- **Unit, the building block:** the canonical form (field order, list order independence, null against empty, length prefixes keep `["ab","c"]` and `["a","bc"]` apart, the format version in the input); the `If-Match` parser (one tag, a list, `*`, weak, malformed forms, surrounding spaces); `Matches` for each.
- **Unit, per resource:** changing any covered field changes the version; changing an uncovered one (`LastLoginAt`, a role's `CreatedAt`, the time zone of quiet hours) does not.
- **Integration, per endpoint:** the read returns an `ETag`; an update with that `ETag` succeeds and returns a new one; an update with a stale `ETag` answers 412 and changes nothing (read back); an update without `If-Match` behaves as before; `*` succeeds; a weak tag answers 412; a malformed header answers 400; the read's `ETag` equals the one the update computed.
- **Integration, the cases that motivated decision 1:** a role's `ETag` changes after its permissions change; an administered user's `ETag` does not change when that user signs in; quiet hours for a user with no settings row have an `ETag` and accept it.
- **Integration, preferences:** a concurrent change between the read and an update with `If-Match` answers 412 (the retry loop does not overwrite).
- **Platform:** `ETag` in the CORS exposed headers; the OpenAPI snapshot shows the header parameter and response headers; the 412 message exists in all three languages (the existing translation tests).
- **Architecture:** every `MapPut` endpoint in a module either carries the entity-tag metadata or is on an explicit, commented exclusion list, so a new PUT cannot forget the decision silently.

## 8. Risks

- **The version and the response can disagree in what they cover.** A client could think the `ETag` protects fields it does not (a role's `CreatedAt`, the user's status). The module documentation lists what each version covers (Section 4).
- **The precondition races the save for preferences.** There is no `rowversion` there; between the check and the save, a parallel update can still land and the retry loop then decides as today. The window is milliseconds, as for the instant race elsewhere, and the 412 path catches every change made before the check.
- **Idempotency.** If an update ever gains `.WithIdempotency()`, the idempotency middleware must store and replay the `ETag` header (it stores only status, content type, `Location` and body today, so a migration of the `platform` schema) and include `If-Match` in the request hash (today the hash is method, path and body, so the same key with another `If-Match` would replay instead of failing). `docs/building-blocks/infrastructure-common.md` records this.
- **Generated projects that add PUT endpoints** without a version function lose the protection; the architecture test makes the choice explicit.
- **Hash collisions** are not a practical concern at 128 bits for this purpose; a collision would only let a stale update through, as today.

## 9. Open questions

- **When to require `If-Match`.** Requiring it later (428 Precondition Required, an `!` release under ADR 0031) is the user's call once clients send it.
- **State-change endpoints** (Section 2): whether any of them should take `If-Match` later.

## 10. Review focus

- The parser and `Matches`: `*`, lists, weak tags and malformed values, since a wrong match silently drops the protection.
- That the read path and the write path call the same version function with the same inputs (the equality test in Section 7).
- The preferences retry loop with `If-Match`: an attempt after a conflict must re-check, not reuse the first check.
- The order in 5.3: the precondition must come after loading and before any write, and a 412 must leave the audit trail and the outbox untouched.
