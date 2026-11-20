# 0038. ETag and If-Match on the update endpoints: a version of the editable state, optional, 412

- Status: Accepted
- Date: 2026-11-19
- Deciders: Lee Jia Le

## Context
Two clients that read the same record and update it minutes apart lose one update without an error: every update handler loads the record fresh and saves it, so the `rowversion` on `User`, `Role`, `UserNotificationProfile` and `LeaveRequest` only covers the milliseconds between that load and that save (409 `concurrency.conflict`, ADR 0006). The blueprint asks for `ETag` / `If-Match` for optimistic concurrency on updates where needed. The design is `docs/superpowers/specs/2026-11-19-if-match-concurrency-design.md` in the template repository.

## Options considered
**What the version is computed from.**
1. **The `rowversion`.** Setting a role's permissions or a user's roles writes only link tables, so the parent `rowversion` does not change; a user's `rowversion` changes on every sign-in; the notification preferences have none.
2. **A hash of the whole response.** The administered user's response carries the last sign-in and lockout state, so every sign-in would make a role assignment fail.
3. **A hash of the editable state**: exactly what the resource's `PUT` endpoints write.

**Whether `If-Match` is required.** Optional keeps every existing client working; required (428) prevents every lost update but breaks every client that does not send it.

**How a stale version is answered.** 412 Precondition Failed is the HTTP answer for a failed `If-Match`; 409 would hide which case happened.

## Decision
- **The version is a hash of the editable state** (option 3): one pure function per resource, called by the read endpoint with the response it returns and by the update handler with the state it loaded, so the two cannot drift. The canonical form is the format version `v1`, the resource kind and length-prefixed values (sets in ordinal order), hashed with SHA-256; the tag is the first 16 bytes in base64url, sent quoted as a strong `ETag`.
- **`If-Match` is optional.** Without it every endpoint behaves as before. `*` matches; a weak tag never matches (412); a malformed header is 400 `request.malformed`.
- **A stale `If-Match` is 412 `concurrency.precondition_failed`**, in three languages, from the new `ErrorType.PreconditionFailed`.
- **The precondition is checked in the command handler**, after loading and before any change, audit entry or outbox message. The `rowversion` still guards the save itself (409) where the update writes the row that carries it: a role's name and description, the caller's profile, the quiet hours, and a role's permissions, whose handler marks the role row updated (its `UpdatedAt`, not an audited column) because otherwise only link rows would change. The role-assignment update writes only link rows and cannot touch the user row (every sign-in updates it, so concurrent sign-ins would fail), so between the check and the save (milliseconds) a second request that carries the same `If-Match` is not refused and both sets are applied; the precondition protects against every change made before the check, as for the notification preferences. An endpoint filter was rejected: it would need its own read, and a write between that read and the handler's would slip through. Where a handler retries a save (the notification preferences), each attempt re-checks against its own fresh read.
- **Scope: the six `PUT` endpoints** (a role and its permissions, which share one version; a user's roles; the caller's profile; the notification preferences; the quiet hours) and their reads. State changes (POST, DELETE) are out of scope.
- **Every `PUT` under `/api/v1/` opts in or is excluded with a reason**, checked by `EntityTagCoverageTests`.
- **A strong validator over the editable state, not over the bytes.** Two responses that differ only in something the version does not cover (a last sign-in time) share an `ETag`. RFC 9110 expects a strong validator to change with the representation; this is acceptable because no read supports conditional requests (`If-None-Match`) or shared caching.

## Consequences
- Clients that send `If-Match` can no longer overwrite a change they did not see; clients that do not are unaffected.
- A version covers only what its update writes: the module pages list it per endpoint, so a client does not assume more protection than it has.
- Responses carry `ETag`; CORS exposes it, and the OpenAPI document shows the header and the optional `If-Match` parameter.
- Changing the canonical form means a new format version: old tags stop matching and clients read again (412), which fails safe.
- An update that ever gains `Idempotency-Key` needs the idempotency middleware to store and replay `ETag` and to hash `If-Match`.
- Requiring `If-Match` later is a breaking change under ADR 0031 (428, an `!` release).

## Follow-ups
- Decide when to require `If-Match`, once clients send it.
- Decide whether any state-change endpoint should take `If-Match`.
