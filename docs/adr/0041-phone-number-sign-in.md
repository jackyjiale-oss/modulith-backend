# 0041. Phone-number sign-in with a texted code as a first factor

- Status: Accepted
- Date: 2026-11-24
- Deciders: Lee Jia Le

## Context
ADR 0040 gave an account one verified, unique mobile number and a way to text a code to it. The roadmap's next 1.0 item is signing in with such a code. A number that signs in is a credential, but a weak one: whoever controls the SIM (a SIM swap, a ported number, a lost phone) receives the code, and nothing on our side changes when that happens. Until now a number was only something to text, so adding one needed no step-up; once it signs in, a stolen access token could bind an attacker's number and keep access after the token expires.

## Options considered
**Who may sign in.** (1) Existing accounts with a verified number; (2) also sign-up by number, creating accounts without an email; (3) sign-up by number with a required email.

**Two-step verification.** (1) The texted code is a first factor and the TOTP challenge still applies; (2) SMS sign-in is refused for accounts with TOTP; (3) the texted code alone signs a TOTP account in.

**Step-up after an SMS sign-in.** (1) An SMS session never satisfies a step-up; (2) it counts like any fresh sign-in; (3) a texted code is also a step-up method.

**Where the code lives.** (1) The existing `PhoneVerificationCode` with a purpose and one SMS budget per user; (2) a separate aggregate and table; (3) a ticket flow like the MFA challenge.

## Decision
- **Existing accounts only.** No account is created from a number, so every account keeps its email and everything that relies on it.
- **A first factor; the challenge still applies.** `POST /api/v1/auth/phone/sign-in/send` texts a code to an account's verified number and `POST .../verify` trades it for a session with `amr` `sms`, or for the usual challenge when the account has TOTP (`sms otp mfa` after it). A trusted device never stands in for it.
- **An SMS session never satisfies a step-up**, whatever its age: a sensitive action needs a password, a TOTP or recovery code, or a provider's proof. `sms otp mfa` proved a code and does satisfy it.
- **The number is guarded like a sign-in method.** Adding one needs a fresh step-up, as replacing and removing already did; replacing or removing one rotates the security stamp and ends the caller's other sessions (`PhoneNumberChanged`); every change publishes an event the owner is emailed about, with the last three digits only.
- **Privileged roles** (`Auth:Phone:SignInDeniedRoles`, `SuperAdmin` and `Admin` by default) cannot sign in by SMS without TOTP.
- **One code aggregate, one budget.** `PhoneVerificationCode` gains `Purpose` (`Verify`, `SignIn`); the cooldown and the daily cap count both purposes.
- **Nothing tells whether a number has an account.** Send answers 202 for every well-formed number, a lost race included; verify answers one 400 `auth.invalid_code` for every refusal, and 403 `auth.account_inactive` only to a suspended account that presented the right code.
- Everything is off by default behind `Auth:Phone:SignInEnabled`, which needs `Auth:Phone:Enabled`.

## Consequences
- Phone-only accounts do not exist; a user who signs in only by SMS and has no password steps up after setting one through forgot-password.
- **Accepted:** about 25 guesses a day per account (6 digits, 5 tries per code, 5 codes a day, each wrong code counting toward the 15-minute lockout), while ADR 0040's controls hold.
- **Accepted:** sends for a victim's number can use up that victim's SMS budget for the day, and five wrong codes can lock the account for 15 minutes; knowing an email already allows the second with passwords.
- **Accepted:** a known number's send costs one save that an unknown number's does not (a timing difference bounded by `auth-strict`).
- **Accepted:** whoever controls the number can sign in to an account without TOTP. They cannot pass a step-up, so they cannot change the number, the password or two-step verification, or link a login; but a SIM swap changes nothing on our side, so no email warns the owner.
- Breaking for clients of ADR 0040's routes (no client depended on them yet): adding a first number can answer 403 `auth.step_up_required`, and replacing or removing a number ends the caller's other sessions.

## Follow-ups
- Delivery receipts (the follow-up of ADR 0040) would let a failed text be told apart from a guess.
- A per-user opt-in to SMS sign-in, if projects want numbers that only receive notices.
