# 0040. Phone numbers verified by our own SMS code, sent through Twilio

- Status: Accepted
- Date: 2026-11-24
- Deciders: Lee Jia Le

## Context
Phone-number sign-in (a later spec) needs a verified number to find an account by and a channel to send its code through. The blueprint lists SMS as a Notifications channel (Twilio, Vonage or a local gateway, rate limited and cost-tracked) and `PhoneNumber` columns on `auth.Users`. SMS costs money per message and is the target of SMS pumping: an attacker makes an application text premium-rate numbers it does not own and shares the revenue. A code is short, so its hash is weak protection if the database leaks. A security review of the design (2026-11-24) asked that the hash risk be accepted only with its controls written down, that SMS never be treated as guaranteed, and that the controls across accounts be required before production.

## Options considered
**Who makes the code.** (1) Auth issues and checks its own 6-digit code and sends it through Notifications; (2) Twilio Verify issues and checks it.

**Where the code lives.** (1) A new `PhoneVerificationCode` aggregate; (2) a new purpose of the link-based `VerificationCode`.

**Provider.** (1) Twilio only, the Messages REST API on a plain `HttpClient` behind an `ISmsTransport` seam; (2) Twilio's SDK; (3) several providers now.

**Abuse limits.** (1) Per-user limits (a cooldown, a daily cap, a try limit), the `auth-strict` per-address limit and a region allowlist, with Twilio's own controls across accounts; (2) the same plus an app-wide daily SMS cap.

**Delivery status.** (1) Store Twilio's message SID, receipts later; (2) status callbacks now; (3) nothing.

## Decision
- Auth owns the number and the code. `User.PhoneNumber` is the verified number in E.164 (parsed with `libphonenumber-csharp` without a default region, a mobile number, from `Auth:Phone:AllowedRegions` when set), unique among accounts that are not deleted; a number waiting for its code is not stored on the user.
- A new `PhoneVerificationCode` aggregate (`auth.PhoneVerificationCodes`), not a link purpose: a code is checked by comparison, not looked up by hash, and it counts tries.
- The code is 6 digits from `RandomNumberGenerator`, stored as `SHA-256(salt ‖ code)` with a 16-byte random salt, compared in constant time; it reaches the outbox only encrypted with `ISecretProtector` (ADR 0017), and Notifications decrypts it only to render the text.
- Wrong tries and the consumption are single conditional `UPDATE`s, so parallel guesses never exceed the limit and two confirmations never both win.
- The request route does not check the number against other accounts; only the person who receives the code learns, at confirmation, that it is taken (409), so the route reveals no account.
- Replacing or removing a verified number needs a fresh step-up (ADR 0023); adding the first does not.
- Notifications gains SMS as a channel and a `Code` kind of secret variable beside `Link`; type `auth.phone_verification` is critical and mandatory SMS.
- Twilio only, behind `ISmsTransport`, on the named client and pipeline `sms-provider`, which does not retry the `POST`. A configuration fault (credentials, sender) is transient, so deliveries wait for the fix.
- **The offline brute-force risk of a code hash is accepted**, only while these controls hold: a 16-byte random salt per code, a lifetime of at most 30 minutes (10 by default), at most 10 wrong tries (5 by default) counted atomically, a new code ending the previous one, the code encrypted in the outbox and never logged, and the daily cleanup deleting codes a day after they expire. Loosening any of them reopens this decision.
- **SMS is at least once and never guaranteed.** `Sent` means Twilio accepted the message. The delivery keeps Twilio's message SID (`Deliveries.ProviderMessageId`) for support and for the receipts that follow; a timed-out send that reached Twilio can be retried and text the same code twice, which is accepted over losing the code.
- **Pumping across accounts is stopped at Twilio, not by an app-wide cap** (which an attacker could use up to block every real user's codes): SMS Pumping Protection, geo permissions limited to the allowed regions, a spend limit and operator alerts are required before production (the production checklist).
- Both halves are off by default (`Auth:Phone:Enabled`, `Notifications:Sms:Enabled`) and turned on together.

## Consequences
- A database leak exposes at most the codes still alive (minutes) to an offline search of a million values each; the salt makes each one its own search.
- Each code request costs one text; the cooldown (1 minute), the daily cap (5) and `auth-strict` bound one account and one address, and Twilio's controls bound the rest.
- A user can hold one number. Phone-number sign-in will trust a verified number, which is why replacing it needs a step-up.
- With `--notifications false` the SMS channel goes with the module, and Auth's phone routes stay (off): a project without a module that texts the code keeps `Auth:Phone:Enabled` off.
- Twilio is the only provider; another is a new `ISmsTransport`.
- The `libphonenumber-csharp` metadata ages with the number ranges; Dependabot updates it like any package.

## Follow-ups
- SMS delivery receipts (Twilio status callbacks, keyed on the stored SID), deferred by the user on 2026-11-24.
- Phone-number OTP sign-in (spec 3 of the push and SMS roadmap row).
