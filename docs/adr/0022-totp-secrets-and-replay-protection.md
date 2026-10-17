# 0022. Own RFC 6238 TOTP with a monotonic replay guard and 80-bit recovery codes

- Status: Accepted
- Date: 2026-10-17
- Deciders: Lee Jia Le

## Context
Plan 4 adds two-step verification (blueprint review Section 6, row 4: TOTP and recovery codes, the MFA challenge, step-up, trusted devices). Blueprint 9.9 sets the rules: TOTP per RFC 6238 with a 30-second step and ±1 step tolerance, code replay prevented within the window, the secret encrypted; 10 recovery codes of at least 10 characters, hashed and single use; every stored token or code a SHA-256 hash, compared in constant time. Blueprint 9.2 adds that Data Protection encrypts the TOTP secrets.

Three things have to be decided before the domain and persistence are built: whether a package computes the codes, which parameters the codes use and how a replay is refused, and how the secret and the recovery codes are stored.

## Options considered
1. **A package** (Otp.NET or a similar library) for TOTP and base32. It is well known and tested, but it is one more dependency to keep under the licence, fee and audit checks of Plan 1, for what is about 40 lines over `HMACSHA1`; its verification helpers keep their own replay window and do not compare in the shape we want (every candidate, no early exit), so we would wrap or bypass them anyway.
2. **Our own RFC 6238 code** over `System.Security.Cryptography` (`HMACSHA1.HashData`, `RandomNumberGenerator`, `CryptographicOperations.FixedTimeEquals`), with a hand-written RFC 4648 base32 encoder and decoder. The risk is a bug in our own code; the RFC 6238 and RFC 4648 test vectors pin the results.

## Decision
We chose option 2 (decisions D2-D5 of Plan 4). The primitives are `internal static` classes in `Domain/Mfa/` of the Auth module: `Totp`, `Base32`, `TotpUri` and `RecoveryCodeFormat`. No package is added; QR codes are drawn by the client from the `otpauth://` URI.

- **Parameters.** HMAC-SHA-1, 6 digits, a 30-second step counted from the Unix epoch (`floor(unixSeconds / 30)`), a 20-byte (160-bit) secret from `RandomNumberGenerator`: what every authenticator app supports. A code is accepted for the current step `t` and for `t - 1` and `t + 1`, never two steps away.
- **Constant-time check.** `Totp.FindMatchingStep` first refuses input that is not exactly 6 ASCII digits (the shape of the input is not secret), then computes the codes of all three candidate steps and compares each with `CryptographicOperations.FixedTimeEquals`; no result is looked at until all three comparisons are done, so the time taken does not tell which step matched, or how much of a code did.
- **Monotonic replay rule.** The method stores the step of the last accepted code (`LastUsedTimeStep`). A code is accepted only for a step **greater** than it, and among the matching candidates the highest one counts. The step is claimed with one conditional `UPDATE ... WHERE LastUsedTimeStep IS NULL OR LastUsedTimeStep < @step`, so of two simultaneous requests with one code exactly one wins, the same code cannot be used twice, and an older code is refused once a newer one was used.
- **The secret at rest.** The secret is stored encrypted by `IMfaSecretProtector` with its own Data Protection purpose, `TemplateName.Auth.MfaSecrets.v1`, on the same key ring (`auth.DataProtectionKeys`), and **not** with the shared `ISecretProtector` purpose `TemplateName.Secrets.v1`, which ADR 0020 made readable by the Notifications module on purpose: a TOTP secret must be readable by Auth only. In memory the secret stays in a byte array; it appears as text only in the setup response (base32 and inside the `otpauth://` URI).
- **Recovery codes.** 10 codes per user, each 16 characters from the 32-symbol alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (no `0`, `1`, `I`, `O`) chosen by `RandomNumberGenerator.GetItems`: 5 bits per character, **80 bits** per code. Shown as `XXXX-XXXX-XXXX-XXXX`; input is normalized (trimmed, ASCII letters upper-cased, hyphens and spaces removed) and must then be exactly 16 alphabet characters. Only the SHA-256 of the normalized code is stored, without salt. That is safe because of the entropy: guessing one of ten 80-bit codes offline takes about 2^76 hashes, out of reach, while the blueprint's minimum of 10 characters (50 bits) would fall to a GPU in about a day. Each code is single use (one conditional `UPDATE`).

## Consequences
- Positive: no new dependency or licence; the codes are pinned by the RFC 6238 SHA-1 vectors and the base32 encoding by the RFC 4648 vectors; replay protection is one rule (a monotonic step) that also holds across simultaneous requests and instances.
- **Negative / trade-offs accepted:**
  - **Key-ring dependency.** Every TOTP secret depends on the Data Protection key ring. Losing `auth.DataProtectionKeys` (a restore without it, a dropped table) makes every secret unreadable: those users sign in with a recovery code, or an administrator resets their MFA, and they enrol again. Back the ring up with the database.
  - **The ring is not encrypted at rest until Plan 6** (ADR 0017): anyone with a full copy of the database can read the keys and decrypt the TOTP secrets. Purpose isolation protects against another module, not against a database dump.
  - **One code per step.** A user who signs in (or steps up) twice within one 30-second step must wait for the next code, because the code just used, and every older one, is refused.
  - **Our own code.** A mistake in it is ours to find; the RFC vectors and the review focus on boundaries and replays are the mitigation.
- **Clock assumption.** The servers keep NTP time and read it through `TimeProvider.System`. The ±1 step absorbs up to 30 seconds of drift on the user's device (a code from the previous or next step); a device further off is refused until its clock is corrected.
- Follow-up actions: Plan 6 encrypts the key ring at rest (certificate or Key Vault protector); the runbook for a lost key ring comes with the end-to-end documentation of Plan 4.
