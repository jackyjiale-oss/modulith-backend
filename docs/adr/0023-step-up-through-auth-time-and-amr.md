# 0023. Step-up through a fresh auth_time and amr in the access token

- Status: Accepted
- Date: 2026-10-17
- Deciders: Lee Jia Le

## Context
Blueprint 9.1 asks for step-up authentication "always", and 9.3 names a "step-up token / claim" (`auth_time` plus `amr`): before a sensitive action (turning two-step verification on or off, regenerating recovery codes, changing the password of an MFA account) the user proves their identity again, even inside a long-lived session. Blueprint 9.9 lists those actions, and 9.10 the setting `Auth:StepUp`.

Plan 2 decided (D9, ADR 0015) that access tokens are stateless: a request is never checked against the session in the database, so an access token keeps working until it expires (10 minutes by default) after a logout or a revocation. The access token already carries `auth_time` and `amr`, so whatever step-up adds should keep that property and not put a database read on every request.

Three questions follow: where the proof of a recent step-up lives, how a protected handler checks it, and which factor a step-up must use.

## Options considered
1. **A separate signed step-up token**, issued by the step-up route and sent next to the access token (a second header) to the protected routes. It keeps the access token unchanged, but clients carry and refresh two tokens, every protected route reads a second header, and the step-up token needs its own audience, lifetime and binding to the session (otherwise it can be replayed with another session's access token).
2. **A server-side `StepUpAt`** on the session, checked on each protected request. It can be revoked at once, but it puts a session read on those requests, which D9 avoids everywhere else, and a refresh would not need to carry anything, so the claims in the token and the stored state could disagree.
3. **`auth_time` and `amr` freshness.** The step-up route verifies a factor, records the moment and the methods on the session (`UserSession.AuthenticatedAt` and `AuthMethods`) and returns a new access token of the same session whose `auth_time` is now and whose `amr` is what was proved. Refreshed tokens take both claims from the session, so they stay. A protected handler reads the claims of the token it already has.

## Decision
We chose option 3 (decisions D9 to D12 of Plan 4).

- **The proof is the access token.** `POST /api/v1/auth/step-up` (authenticated, `auth-strict`, `no-store`) verifies one factor, calls `UserSession.StepUp(methods, now)`, which **replaces** the session's `AuthMethods` with what the step-up proved (`pwd`, `otp` or `rcvr`) and sets `AuthenticatedAt`, and issues a new access token of the same session with those values. The refresh token is unchanged. The refresh handler issues `auth_time` from `AuthenticatedAt` (no longer from `CreatedAt`), so a refresh after a step-up keeps it and a refresh never renews it. `amr` describes the authentication at `auth_time`, as OpenID Connect defines it, which is why a step-up replaces it rather than adding to it.
- **The rule.** `StepUpPolicy.Check(hasConfirmedMfa)` succeeds while `now - auth_time < Auth:StepUp:MaxAge` (10 minutes, 1 minute to 1 hour; at exactly `MaxAge` it has expired) and, for an account with a confirmed MFA method, `amr` contains `otp` or `rcvr`. Otherwise it is 403 `auth.step_up_required`, and the client steps up and retries. It reads only `ICurrentUser.AuthTime` and `AuthMethods` and the injected `TimeProvider`. A handler calls it (not an endpoint filter), because only the handler knows whether the account has MFA, and change password applies it to MFA accounts only (D11): `ChangePasswordCommandHandler` calls it after loading the caller and before verifying the current password, when the account has a confirmed method, and a password-only account is unchanged.
- **The factor.** An account with two-step verification steps up with a TOTP code or a recovery code; its password is refused with 400 `auth.step_up_method_not_allowed` (not counted). An account without it steps up with its password. A session thief with a phished password therefore cannot switch MFA off. Every wrong password or code counts in the lockout counter through the same atomic statement as a wrong password at login (D7, D12); a locked account is refused without verifying. A TOTP code claims its time step and a recovery code its row, so a code works once.
- **Fail closed.** The route checks what the token cannot: an unknown, deleted or suspended user, or a token whose `sid` names a session that is revoked, expired, someone else's or holding an outdated security stamp, gets the 401 of a request without a valid token. A step-up never extends a dead session.

## Consequences
- Positive: no new token type, header or per-request read; the check costs a comparison of two claims; clients keep one access token and one refresh token. The claims are standard (`auth_time`, `amr`), so a resource server that verifies our tokens can apply the same rule.
- **Protected handlers call `StepUpPolicy`.** Each sensitive handler must call it at the right place; forgetting it is not caught by the type system. Today's callers are TOTP setup and confirm, removing a confirmed method, regenerating recovery codes and changing the password of an MFA account. Endpoints built later that need a fresh identity (email change, account deletion, API keys, unlinking a login: D1, Part D) reuse the same policy and add their own tests.
- **Clients replace the access token after a step-up.** The token they hold keeps its old `auth_time`; until it is replaced, a protected action still answers `auth.step_up_required`. The response gives `stepUpExpiresAt` (the new token's `auth_time` second plus `MaxAge`) so a client can tell when to ask again.
- **A stolen fresh token is usable for `MaxAge`.** Someone who obtains an access token minted by a step-up can perform protected actions until its `auth_time` ages out (and the token itself expires), the same exposure the stateless access token already has; a logout or revocation does not stop it within that window (D9). Keep `MaxAge` short.
- **A trusted device does not satisfy an MFA step-up.** Its sign-ins carry `pwd` only, so an MFA account always steps up with a code.
- **Lockout coupling.** A session thief can lock the owner out with five wrong codes (they could already sign them out); the lockout email tells the owner. A successful step-up does not reset the failure count; only a successful sign-in does.
- **`rcvr` is not in RFC 8176.** Clients that read `amr` must accept it.
- Follow-up actions: none open. The callers above and the endpoints of Plan 4 Part D are implemented ([Step-up](../modules/auth.md#step-up)); a step-up on administrator actions stays deferred ([Known limits](../modules/auth.md#known-limits)).
