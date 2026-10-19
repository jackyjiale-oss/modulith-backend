# 0024. The sign-in challenge is an opaque token over a hashed server-side row

- Status: Accepted
- Date: 2026-10-18
- Deciders: Lee Jia Le

## Context
Blueprint 9.5 describes the login of an account with two-step verification: a correct password answers 200 with `mfaRequired: true` and a challenge instead of tokens, and a second request with the challenge and a code gets the tokens. Blueprint 9.3 calls the challenge an "MFA challenge token: signed, single purpose, short-lived". The second request is anonymous (the caller has no access token yet), so the challenge is the only thing that ties it to the password step.

The challenge needs more than a signature can give: it must work **once** (a second verify with the same challenge must fail, also when both arrive together), it must count the wrong codes presented for it and stop at a cap, and it must die when the account changes between the two steps (a password change, an MFA change, a suspension). And the codes presented for it guess at a 6-digit secret, so the guess limits are part of the design, not an afterthought: one challenge per correct password must not mean five fresh guesses per password.

Three questions follow: what the challenge is, how wrong codes are limited, and what login answers.

## Options considered
1. **A signed JWT** (a separate audience and type, the user id, the stamp and a `jti`, valid 5 minutes). It needs no table to issue, but single use needs a list of used `jti`s, and the attempt cap needs a counter per `jti`: a table anyway, written on every verify. It is also one more JWT signed with our keys that a misconfigured verifier could take for an access token (our access tokens are not typed `at+jwt`, see the Auth module's known limits).
2. **An opaque token over a hashed row** (`auth.MfaChallenges`): 32 random bytes in base64url, shown once, stored only as SHA-256 like refresh tokens, with the user, a snapshot of the security stamp, the device name, the expiry, `ConsumedAt` and `FailedAttempts`. Single use and the cap are conditional `UPDATE`s on that row.
3. **Reuse `VerificationCodes`** with a new purpose. The table already has a hash, an expiry and single use, but not an attempt counter or a stamp snapshot, and its purposes are emailed links that the publishing handler turns into messages; mixing a sign-in state into it couples two unrelated life cycles and their cleanup.

## Decision
We chose option 2 (decisions D6, D7, D8 and D19 of Plan 4).

- **The challenge (D6).** `ISecureTokenService.Generate()` (32 random bytes, 43 base64url characters), `MfaChallenge.Issue(userId, hash, user.SecurityStamp, deviceName, Auth:Mfa:ChallengeLifetime, now)`; only the hash is stored. Only `POST /api/v1/auth/mfa/verify` looks it up. It is not a JWT, so the bearer handler refuses it, and it is not a refresh token (another table), so the refresh route refuses it. It is "signed" in substance: unguessable, single purpose, 5 minutes.
- **Usable means** `MfaChallenge.CanUse(user.SecurityStamp, now, Auth:Mfa:MaxChallengeAttempts)`: not consumed, before its expiry, fewer than 5 wrong codes, and the user's stamp still equal to the snapshot. A challenge that is not usable, and a user who is gone, suspended, locked or without a confirmed method, are one answer, 400 `auth.invalid_mfa_challenge`, and nothing is verified or counted.
- **Guess limits, three layers (D7).** The verify route has `auth-strict` (per client address). A wrong code counts on the challenge (`RecordFailedAttemptAsync`, one atomic `UPDATE`) **and** in the user's lockout counter (`IUserRepository.RecordFailedSignInAsync`, Plan 2's atomic statement); the password step of an MFA account does **not** reset that counter, only a verified second factor does (through the shared success path, `SessionStarter`). So "password, four wrong codes, password again" ends in a lockout after five wrong codes in total, from any number of addresses and challenges, and Plan 3 emails the owner.
- **Single use.** A right code is spent first (the TOTP time step or the recovery code is claimed by `MfaFactorVerifier`), then the challenge is consumed by one conditional `UPDATE` (`TryConsumeAsync`); only the request that wins both gets a session. A request that wins the code but loses the challenge answers `auth.invalid_mfa_challenge` and the code stays spent: the user signs in again with the next code.
- **Login answers 200 `LoginResult` (D8)** with `mfaRequired` as the discriminator: `false` with the Plan 2 token members, `true` with `challengeToken`, `challengeExpiresAt` and `methods` (`["totp","recovery_code"]`); the members of the other shape are omitted. Nothing two-step is read before the password is verified and the account is allowed to sign in, so an unknown email, a wrong password and a locked account answer exactly as in Plan 2, for MFA and non-MFA accounts alike. The challenge itself appears only after a correct password, which tells the caller the password was right: inherent to any second step, and the same disclosure the 403s of Plan 2 already make.
- **Wrong codes are 400 (D19):** `auth.invalid_mfa_code` (wrong, reused or unreadable code, wrong or used recovery code) and `auth.invalid_mfa_challenge` (every reason the challenge cannot be used, alike). A 401 would make clients refresh tokens instead of asking for a code.
- **`amr`.** The session the verify starts carries `pwd otp mfa` (TOTP) or `pwd rcvr mfa` (recovery code), with `auth_time` the verify's instant.

## Consequences
- Positive: single use and the cap are one statement each, race-free across requests and instances; the token is useless to any other route; the challenge dies with every stamp rotation without a revocation call anywhere.
- **One more table to clean up.** Expired and consumed challenges stay until the retention job of Plan 5 removes them (a few rows per sign-in of an MFA account).
- **The lockout coupling is a denial of service for whoever knows the password.** Someone with the email and the password can lock the owner out for `Auth:Lockout:Duration` (15 minutes) with five wrong codes, the same denial of service wrong passwords already allow (Plan 2); the lockout email tells the owner the password is known. Without the coupling the code would be guessable without limit.
- **A double tap.** Two simultaneous verifies with one code: one claims the code and the challenge; the other loses the code's claim, which counts as a wrong code (on the challenge and the user row) and may change the user row between the winner's read and save. The winner therefore retries its save once on the user read again (still bound to the challenge's stamp, allowed to sign in and not locked), so exactly one request gets the session; a second conflict is answered `auth.invalid_mfa_challenge`.
- **`rcvr` is not in RFC 8176.** Clients that read `amr` must accept it (as for step-up, ADR 0023).
- **The login schema changed.** The token members of the login answer are optional now (`LoginResult`); generated clients change. The template is unreleased.
- Follow-up actions: trusted devices skip the challenge for a remembered device and issue the device token at verify (`rememberDevice`; done in Plan 4, ADR 0025); the challenge cleanup in Plan 5.
