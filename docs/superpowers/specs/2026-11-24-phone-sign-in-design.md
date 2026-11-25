# Phone-number sign-in — design

- Status: design approved section by section by the user (2026-11-24); written spec waiting for the user's review.
- Date: 2026-11-24
- Roadmap row: "Phone-number OTP login" in `docs/blueprint-review.md` (row 128, Section 9.1). Spec 3 of the push and SMS 1.0 gate; it builds on spec 2 ("Phone number and SMS channel", PR #65, ADR 0040). All gate items block 1.0.0 (decided 2026-11-20).
- Builds on: the verified, unique `User.PhoneNumber` and the `PhoneVerificationCode` aggregate (ADR 0040); the SMS channel of the Notifications module; the password login, its lockout and enumeration rules; the MFA challenge (ADR 0024) and step-up (ADR 0023); the social login's precedent of a non-password first factor (ADR 0032); ADR 0035 (switch markers).
- Blueprint: Section 9.1 (`/otp/send` always 202, `/otp/verify` answers tokens or an MFA challenge), the `auth.login_otp` notification type, the OTP row of the security table (random, 6 digits, hashed, short-lived, limited tries, single use, resend cooldown, a new code ends the previous one), and "SMS not allowed as sole MFA for admin roles".

## 1. Intent

**Outcome.** A user whose account has a verified mobile number can sign in with a 6-digit code texted to that number, and ends up in the same session a password sign-in gives: the same access and refresh tokens, `GET /me`, sessions, permissions, notices and two-step verification. Because a verified number becomes a way into the account, the number itself gets the guard any sign-in method has: a step-up to add it and an email whenever it changes.

**Success.**

- With `Auth:Phone:SignInEnabled` off (the default), the two new routes answer 404 and sign-in behaves as today. The spec-2 changes to `me/phone` (step-up on a first number, sessions ended on replace and remove, the change email) apply whether the switch is on or off.
- With it on (and `Auth:Phone:Enabled` and `Notifications:Sms:Enabled` on, Twilio configured), `POST /api/v1/auth/phone/sign-in/send` texts a code within about two polling intervals, and `POST /api/v1/auth/phone/sign-in/verify` with it answers tokens with `amr` `sms`, or an MFA challenge for an account with TOTP.
- The send route never reveals whether a number has an account; the verify route answers every refusal with one body.
- No test needs a Twilio account (the spec-2 fakes).

## 2. Non-goals

- **Sign-up by phone.** No account is ever created from a number; every account keeps its email (decision 1).
- **SMS as a second factor or a step-up method.** A texted code is a first factor only; `POST /step-up` gains no SMS method.
- **A per-user opt-in.** A verified number signs in as soon as the switch is on (decision 3).
- **Trusted devices for an SMS sign-in.** A device replaces the code of a challenge, never the first factor, so, as for `fed`, `rememberDevice` is ignored after an `sms` first factor and the send and verify routes read no `trustedDeviceToken`.
- **Email codes and magic links** (post-v1 backlog), voice and WhatsApp, Twilio Verify, SMS delivery receipts (their own roadmap row).
- **A template switch.** The feature is configuration-switched inside Auth; `--notifications false` removes the module that texts the code, and the switch then stays off (as in spec 2).

## 3. Decisions (the user, 2026-11-24)

1. **Existing accounts only.** Only a user who already verified a number on the account can sign in with it.
2. **The SMS code is a first factor; the TOTP challenge still applies.** An account with a confirmed TOTP method gets the usual challenge after the code, answered at `POST /api/v1/auth/mfa/verify`; `amr` becomes `sms otp mfa` or `sms rcvr mfa`.
3. **Every verified number signs in** once `Auth:Phone:SignInEnabled` is on; no per-user opt-in.
4. **Adding a number always needs a fresh step-up**, as replacing and removing already do, whether or not sign-in is on; and every add, replace and remove sends a security email (spec 2's deferred minor 4).
5. **An SMS sign-in never satisfies a step-up.** A session whose `amr` contains `sms` but neither `otp` nor `rcvr` is refused by `StepUpPolicy` whatever its age; the user steps up with a password, a TOTP or recovery code, or a provider's proof.
6. **Privileged roles are denied SMS sign-in without TOTP**, by a configurable list `Auth:Phone:SignInDeniedRoles` (default `SuperAdmin`, `Admin`).
7. **The code reuses `PhoneVerificationCode`** with a new `Purpose` (`Verify`, `SignIn`), and both purposes share one SMS budget per user (the cooldown and the daily cap); approach 1 of three (the others: a new `PhoneSignInCode` aggregate, a ticket flow like the MFA challenge).

## 4. Surface

### 4.1 Auth routes

| Method | Route | Caller | Body | Success | Refusals |
|---|---|---|---|---|---|
| `POST` | `/api/v1/auth/phone/sign-in/send` | anonymous, `auth-strict` | `{ phoneNumber }` | 202, no body, for every well-formed request | 400 `validation.failed` (missing, over 32 characters); 400 `auth.invalid_phone_number` (not a valid mobile E.164 number of an allowed region); 404 while `Auth:Phone:SignInEnabled` is off |
| `POST` | `/api/v1/auth/phone/sign-in/verify` | anonymous, `auth-strict`, `no-store` | `{ phoneNumber, code, deviceName? }` | 200 `LoginResult` (tokens, or `mfaRequired: true` with a challenge) | 400 `validation.failed` (`code` not 6 ASCII digits, `deviceName` over 200 characters); 400 `auth.invalid_code` (every refusal of 5.3); 403 `auth.account_inactive` (suspended, only after the right code); 404 while off |

The 404 while switched off matches spec 2's `me/phone` routes: the routes exist (in the OpenAPI document; the limiter and the validator run first, so a malformed body is still 400 `validation.failed`), and a well-formed request gets 404 before its number is parsed. `validation.failed` and `auth.invalid_phone_number` depend on the request alone, never on an account.

Changed (spec 2's routes, decision 4):

| Route | Before | After |
|---|---|---|
| `POST /api/v1/auth/me/phone` | step-up only to replace a verified number | step-up always (403 `auth.step_up_required`) |
| `POST /api/v1/auth/me/phone/confirm` | sets the number | also: replacing a verified number rotates the stamp and ends the caller's other sessions; publishes `PhoneNumberChangedIntegrationEvent` (`Added` or `Replaced`) |
| `DELETE /api/v1/auth/me/phone` | removes the number | also: rotates the stamp, ends the caller's other sessions, publishes the event (`Removed`); nothing of this when there was no number |

### 4.2 Contracts and constants

- `PhoneSignInCodeRequestedIntegrationEvent(UserId, PhoneNumber, ProtectedCode, ExpiresAt)` in `TemplateName.Modules.Auth.Contracts`, shaped like `PhoneVerificationRequestedIntegrationEvent`: the number in E.164, the code encrypted with `ISecretProtector`.
- `PhoneNumberChangedIntegrationEvent(UserId, Kind, LastDigits, ChangedAt)` with `Kind` `Added`, `Replaced` or `Removed` and `LastDigits` the last 3 digits of the new number (of the removed one for `Removed`), never the full number.
- `amr` value `sms` (`AuthenticationMethods.Sms`), registered in RFC 8176.
- `SessionRevokedReason.PhoneNumberChanged`.

### 4.3 Configuration

| Key | Default | What it does |
|---|---|---|
| `Auth:Phone:SignInEnabled` | `false` | Turns the two sign-in routes on. Startup validation refuses it unless `Auth:Phone:Enabled` is also on. |
| `Auth:Phone:SignInCodeLifetime` | `00:05:00` (1 to 10 minutes) | How long a sign-in code is valid. |
| `Auth:Phone:SignInDeniedRoles` | `["SuperAdmin", "Admin"]` | Roles whose members cannot sign in by SMS without a confirmed TOTP method. Each entry must be non-blank; entries are compared with the roles' normalized names, and a name no role has denies nobody (a role can be created later). An empty list denies nobody. |

`Auth:Phone:ResendCooldown`, `MaxCodesPerDay`, `MaxAttempts` and `AllowedRegions` apply to sign-in codes too.

### 4.4 Notifications (with the Notifications module)

| Type | From | Channel | Category, priority | Variables |
|---|---|---|---|---|
| `auth.phone_sign_in` | `PhoneSignInCodeRequestedIntegrationEvent` | SMS, mandatory | Security, Critical | `expires_in_minutes`; secret `code` (a `Code`) |
| `auth.phone_number_changed` | `PhoneNumberChangedIntegrationEvent` | Email, mandatory | Security, Critical | `kind`, `last_digits`, the time in the user's time zone |

Templates in `en`, `ms` and `zh-Hans`. The SMS part fits one segment in each culture (160 GSM-7 characters in `en` and `ms`, 70 in `zh-Hans`), pinned by the render test spec 2 added. The email says, per kind, that a number ending in the three digits was added to, changed on, or removed from the account at the local time, and tells the reader to reset the password and contact the administrator if it was not them.

## 5. Behaviour

### 5.1 Data

One migration, `AddPhoneCodePurpose`: `auth.PhoneVerificationCodes` gains `Purpose tinyint NOT NULL` with the default `1` (`Verify`), so every existing row stays a verification code; `2` is `SignIn`. Additive; `Down` drops the column. The table, the aggregate, the indexes and `auth.Users` are unchanged (`IX_PhoneVerificationCodes_UserId_CreatedAt` serves the lookup by user; a user has a handful of rows).

`PhoneVerificationCode` gains `Purpose` and an issue factory per purpose; its rules stay (salted SHA-256, tries counted and the code ended by conditional `UPDATE`s, consumed by a conditional `UPDATE`, compared in constant time). Issuing a code ends the user's pending codes **of the same purpose**; a number change (confirm, replace, remove) ends the pending codes of **both** purposes. The cooldown and the daily cap count codes of **both** purposes (one SMS budget per user), serialized as in spec 2 by setting `User.PhoneCodeRequestedAt` in the issuing save.

### 5.2 Sending a sign-in code

`SendPhoneSignInCodeCommandHandler`, one save at most, in this order:

1. **The switch.** Off: 404 (after the validator).
2. **The number** parsed exactly as `me/phone` parses it (libphonenumber, no default region, mobile, `AllowedRegions`); otherwise 400 `auth.invalid_phone_number`.
3. **The user** by verified number (`GetByPhoneNumberAsync`, soft-deleted users not found). None: 202, nothing saved or audited.
4. **Silent refusals**, each 202 with nothing texted, audited `auth.phone_sign_in_requested` (not succeeded) with the user and the reason, saved: suspended (`inactive`), locked (`locked`), a member of a denied role without a confirmed TOTP method (`role_denied`), the cooldown (`cooldown`) or the daily cap (`daily_cap`) reached.
5. **Issue.** Every pending `SignIn` code of the user ends, `PhoneCodeRequestedAt` is set, a `SignIn` code is issued for the user's verified number with `SignInCodeLifetime`, the code goes to the outbox only encrypted in `PhoneSignInCodeIssuedDomainEvent`, `auth.phone_sign_in_requested` (succeeded) is audited; one save. A save that loses the user row's `RowVersion` to a simultaneous send saves nothing and answers 202 (never 409, which would reveal the account).
6. 202.

Publishing (`PublishPhoneSignInCodeIssuedDomainEventHandler`) reloads the code as spec 2's publisher does: a code no longer pending, or whose user is gone, is not published.

The answer is the same 202 (status, headers, empty body) on every path from step 3 on. The paths differ in cost by one save (an unknown number saves nothing); see 8.

### 5.3 Verifying

`VerifyPhoneSignInCommandHandler`, in this order:

1. **The switch** (404) and **the number** (as in 5.2, but an invalid number here is the generic 400 `auth.invalid_code`, not `auth.invalid_phone_number`, so verify never says more than send).
2. **The user** by verified number and the user's newest pending `SignIn` code whose number equals the user's current number. None: hash the presented code with a dummy salt (level cost), audit `auth.phone_sign_in_failed` (with the user when one exists), save, 400 `auth.invalid_code`. Nothing is counted: there was nothing to guess.
3. **Locked** (`IsLockedOut(now)`): 400 `auth.invalid_code`, audited with `locked`, nothing counted or spent (as a locked account's right password is not verified further).
4. **The code.** Wrong: one try on the code (the conditional `UPDATE`, ending it at `MaxAttempts`) **and** one failure on the account (`RecordFailedSignInAsync`, the login's atomic statement; the failure that locks raises `UserLockedOutDomainEvent` and writes `auth.locked_out`), audit `auth.phone_sign_in_failed`, save (no cancellation token), 400 `auth.invalid_code`. Right: consume it (conditional `UPDATE`, no cancellation token); a lost consume is 400 `auth.invalid_code` with the code spent.
5. **The account.** Suspended: 403 `auth.account_inactive` (audited). A member of a denied role without a confirmed TOTP method: 400 `auth.invalid_code`, audited `role_denied`, the code spent. `User.EnsureCanSignIn`'s unconfirmed-email refusal does **not** apply: the number was verified, which proves the account's holder as a provider's proof does.
6. **A confirmed TOTP method:** `MfaChallenge.Issue(..., firstFactor: "sms")`, audit `auth.mfa_challenge_issued`, save, 200 `LoginResult.Challenge`. The failure count is not reset until the challenge is answered (as after a password). `/mfa/verify` joins `sms` with `otp` or `rcvr` and `mfa`.
7. **Otherwise the session:** `SessionStarter.StartAsync(user, "sms", deviceName ?? "Unknown", viaTrustedDevice: false, now)` (failures cleared, `LastLoginAt`, the new-device notice), audit `auth.login_succeeded` with the session id and `{"method":"sms"}`, `TrySaveChangesAsync` (no cancellation token; a lost race is 400 `auth.invalid_code` with the code spent), 200 `LoginResult.Signed`.

Every 400 of this route has the same body except `traceId` (`ProblemResponseComparison`).

### 5.4 Step-up

`StepUpPolicy.Check` gains one rule before the age check: when the caller's `amr` contains `sms` and neither `otp` nor `rcvr`, it answers 403 `auth.step_up_required`. The rest is unchanged. So a TOTP account's `sms otp mfa` session satisfies it within `MaxAge`, and a password, social-only or other account steps up with its usual factor (`POST /step-up` with a password, or a provider's proof); a step-up replaces `amr`, so `sms` is gone afterwards. An account with neither a password, a social login nor TOTP (an administrator-created account that verified a number) has no step-up factor: a code sent to `POST /step-up` is `auth.step_up_method_not_allowed`, and a password sent there is a counted wrong password, `auth.current_password_incorrect` (the handler spends the cost of a verification, as for an unknown account), until it sets a password through forgot-password. The route's class documentation holds this order.

### 5.5 Changing the number

- **Add** (`POST me/phone` with no verified number): `StepUpPolicy.Check(hasConfirmedMfa)` first, as replace already does.
- **Confirm a first number:** as in spec 2, plus `PhoneNumberChangedDomainEvent(Added)`; no stamp change, no session ended (adding a way in tells the owner, as linking a social login does, D11).
- **Confirm a replacement** and **remove:** the user's security stamp rotates, every other active session of the caller is revoked with `PhoneNumberChanged`, the current session adopts the new stamp, the pending codes of both purposes end, and `PhoneNumberChangedDomainEvent(Replaced or Removed)` is raised; `auth.phone_number_verified` / `auth.phone_number_removed` gain `{"revokedSessionCount":n}`. One save, as for an unlink; a simultaneous change of the user loses on `RowVersion` (409 `concurrency.conflict`). The stamp rotation also ends pending MFA challenges and trusted devices, as every rotation does.

### 5.6 Switches

`Auth:Phone:SignInEnabled` off: the two routes answer 404 after the validator and the limiter; nothing else of 5.2 or 5.3 runs. 5.4 and 5.5 do not depend on it. With `--notifications false` the event types stay in Auth's contracts with no consumer, and the documentation says to keep both phone switches off (spec 2's rule).

## 6. Where it lands

| Area | Files |
|---|---|
| Auth domain | `PhoneVerificationCode` (`Purpose`, factories), `PhoneCodePurpose`, `User` (number change effects, `PhoneNumberChangedDomainEvent`), `SessionRevokedReason.PhoneNumberChanged`, `AuthenticationMethods.Sms` |
| Auth application | `Application/PhoneSignIn/{Send,Verify}/` (command, handler, validator), `StepUpPolicy`, the `me/phone` handlers (step-up on add, change effects), the two publishers, `PhoneOptions` (+ validation) |
| Auth infrastructure | the repository queries by purpose, the configuration of `Purpose`, migration `AddPhoneCodePurpose` |
| Auth endpoints | `PhoneSignInEndpoints` (or two routes in `AuthEndpoints`), the request records |
| Auth contracts | the two integration events |
| Notifications | two type registrations, the integration-event handlers, templates (SMS `text` x3, email x3), resource strings |
| Docs | `docs/modules/auth.md` (new "Phone-number sign-in" section; Endpoints, Step-up, Phone number, Audit events, Configuration, Data, Known limits, Testing), `docs/modules/notifications.md` (Auth's types), ADR 0041, `docs/deployment/production-checklist.md`, `docs/blueprint-review.md` (row 128) |
| Generated | OpenAPI snapshot, `build/scripts/project-index.sh` output |

## 7. Tests, first

- **Unit.** `PhoneVerificationCode` by purpose (ends only its own purpose; a number change ends both). `StepUpPolicy`: `sms` alone refused even when fresh, `sms otp mfa` and `sms rcvr mfa` accepted within `MaxAge`, a step-up after `sms` accepted. The send handler: every path of 5.2 (unknown, inactive, locked, role denied, cooldown, cap, lost `RowVersion`) answers 202, publishes nothing and audits the right reason. The verify handler: every path of 5.3. `PhoneOptions` validation (sign-in without `Enabled`, a blank role, lifetime bounds).
- **Integration** (`PhoneSignInTests`, the real host, the code read from the published event as spec 2's tests read it): send and verify give a session with `amr` `sms`; a known and an unknown number get identical 202s; every verify refusal is one body; a TOTP account gets a challenge and `/mfa/verify` gives `sms otp mfa`; an `sms` session gets 403 on TOTP setup and passes after a password step-up; adding a first number needs a step-up; replace and remove end the other sessions and publish the change event; an `Admin` without TOTP is denied and with TOTP challenged; five wrong codes lock the account (and the right password then gets the generic 401); two verifies with one code give one session; two sends at once give one code; a number change kills the pending code; `no-store`; the `auth-strict` limit; 404 while off; a scan of every text column and every log line for the number and the code.
- **Notifications.** The render tests pin one SMS segment per culture for `auth.phone_sign_in` and the three kinds of `auth.phone_number_changed`; the handler tests map each event.
- **Template.** The smoke with `--notifications false`, `--includeSample false` and `--caching memory`.

## 8. Risks and accepted limits

- **Guessing.** 6 digits, 5 tries per code, at most 5 codes a day across both purposes, and every wrong code counting toward the 15-minute lockout: about 25 guesses a day per account (about 1 in 40,000). Accepted while ADR 0040's controls hold (salt, short life, encrypted outbox, cleanup).
- **SMS spend.** Only verified numbers are ever texted, so an attacker's spend is bounded by the number of accounts times the daily cap; Twilio's SMS Pumping Protection, geo permissions, spend limit and alerts stay required before production (the checklist gains `SignInEnabled`).
- **Budget exhaustion.** Sends for a victim's number use up the victim's SMS budget for the day: SMS sign-in and number verification are blocked until it resets; password and social sign-in still work. Bounded per client address by `auth-strict`.
- **Lockout by a known number.** Someone who knows the number can request a code and guess wrong five times, locking the account for 15 minutes; knowing the email already allows the same with passwords.
- **Timing.** A known number's send saves (code, outbox, audit); an unknown number's does not. Same class of limit as "resend confirmation"; bounded by `auth-strict`.
- **SIM swap.** Whoever controls the number can sign in to an account without TOTP. They cannot pass a step-up (5.4), so they cannot change the number, the password or the MFA, or link a login; admins are denied without TOTP; but a swap changes nothing on our side, so no email warns the owner. Projects that care turn TOTP on for their users.
- **A concurrent profile save.** An anonymous send writes `PhoneCodeRequestedAt` on the user row, so a save of that user at the same instant can lose on `RowVersion` (409; a retry succeeds).

## 9. Compatibility and release

No request or response shape changes; the OpenAPI document gains two operations. Two behaviour changes for clients of spec 2's routes (shipped in 1.0.0-rc.6): adding a first number can answer 403 `auth.step_up_required`, and replacing or removing a number ends the caller's other sessions. Under `docs/api-compatibility.md` these are tightened behaviour that no client depends on yet (rule 3: change in place, mark breaking), so the commit carries a `BREAKING CHANGE:` footer naming both. Before merging the release PR that follows, check that release-please proposes `1.0.0-rc.7` and not `2.0.0`; if it proposes a major, fix the version with `release-as` as was done for rc.1 (`docs/releasing.md`).

## 10. Open questions

None from the design. The implementation plan settles file-level choices (one endpoints class or two, the handler names).

## 11. Review focus

1. The silent send: every path after the number is parsed answers the identical 202, and the lost `RowVersion` race is a 202, not a 409.
2. The one-body verify: an unknown number, a locked account and a denied role cannot be told apart from a wrong code.
3. `StepUpPolicy`'s `sms` rule, and that a `sms otp mfa` session still passes.
4. A code bound to the number: a change between send and verify kills it.
5. Wrong codes count on the code and on the account, both atomically, and parallel guesses never exceed either limit.
6. The change effects on replace and remove (stamp, sessions, codes, event) in one save.
