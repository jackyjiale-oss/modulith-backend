# Phone number and SMS channel — design

- Status: draft, waiting for the user's review (2026-11-23).
- Date: 2026-11-23
- Roadmap row: "Phone number and SMS channel" in `docs/blueprint-review.md` (split from push on 2026-11-23). Spec 2 of the push and SMS 1.0 gate; phone-number OTP login is spec 3 and builds on it. All three gate 1.0.0 (decided 2026-11-20).
- Builds on: the Auth module (users, the `/me` routes, step-up, ADR 0023; verification links, ADR 0017; the outbox), the Notifications module and its push channel (ADR 0039: per-channel registration while enabled, secret variables, the delivery worker), the external HTTP clients and resilience pipelines (`AddExternalHttpClient`), ADR 0035 (switch markers).
- Blueprint: Section 10.1 (SMS: Twilio, Vonage, a local gateway; rate limited, cost-tracked), 8.4 (the `sms-provider` pipeline), the `PhoneNumber` columns of `auth.Users` (Section 11), the `/phone/send-code` and `/phone/confirm` routes (Section 9.1).

## 1. Intent

**Outcome.** A user can add a mobile number to their account and prove they own it with a code sent by SMS; the Notifications module gains SMS as a channel any notification type can declare. Phone-number OTP login (spec 3) then has a verified, unique number to find an account by and a channel to send its code through.

**Success.**

- With `Auth:Phone:Enabled` and `Notifications:Sms:Enabled` off (both default), nothing changes: the phone routes answer 404, no SMS delivery is created, every existing test passes.
- With both on and Twilio credentials set, `POST /api/v1/auth/me/phone` texts a 6-digit code to the number within about two polling intervals (~10 s), and `POST /api/v1/auth/me/phone/confirm` with that code makes it the account's verified number.
- No test needs a Twilio account: Twilio is replaced by a fake HTTP handler and a recording `ISmsTransport`.

## 2. Non-goals

- Phone-number OTP login (spec 3) and an SMS second factor for MFA.
- SMS for any other notification type (the catalog supports adding it per type later).
- Vonage, a local or in-country gateway, Twilio Verify; delivery receipts, status callbacks, inbound STOP handling beyond the `twilio_21610` reason code.
- A global daily SMS cap (the per-user limits and the region allowlist are the cost guards).
- A template switch: both halves are configuration-switched; `--notifications false` removes the SMS channel with the module.

## 3. Decisions (the user, 2026-11-23)

1. **Twilio only**, the Messages REST API over a plain `HttpClient` on the `sms-provider` pipeline, behind an `ISmsTransport` seam.
2. **Our own code**: Auth issues a 6-digit code and publishes an integration event; Notifications sends it by SMS (type `auth.phone_verification`, mandatory SMS), through the same outbox, inbox and retries as email.
3. **One number per user, unique when verified**, in E.164, parsed and checked with `libphonenumber-csharp`; a new number stays pending until verified; replacing a number keeps the old one until the new one is verified.
4. **Only the verification code goes by SMS.**
5. **Abuse limits and a region allowlist**: signed-in users only, the `auth-strict` per-address limit, a 60 s resend cooldown, at most 5 codes per user per day, 5 wrong tries end a code, and `Auth:Phone:AllowedRegions`.
6. **A new `PhoneVerificationCode` aggregate**, not an extension of the link-based `VerificationCode`.

## 4. Surface

### 4.1 Auth routes

All signed in (`.RequireAuthorization()`), under `/api/v1/auth`; while `Auth:Phone:Enabled` is off each answers 404 `http.404`.

| Method | Route | Purpose | Success | Errors |
|---|---|---|---|---|
| `POST` | `/api/v1/auth/me/phone` | Sends a code to `{ phoneNumber }` (5.2). | 202 `{ expiresAt }` | 400 `validation.failed` (blank or longer than 32); 400 `auth.invalid_phone_number` (not a valid mobile number in E.164, or a region not allowed); 401 `http.401`; 403 `auth.step_up_required` (the caller already has a verified number and has no fresh step-up); 429 `rate_limit.exceeded` (the cooldown, the daily cap or `auth-strict`; with `Retry-After`) |
| `POST` | `/api/v1/auth/me/phone/confirm` | Verifies `{ code }` (5.3). | 204 | 400 `validation.failed` (not 6 digits); 400 `auth.invalid_code` (no pending code, expired, used, ended, or wrong); 401 `http.401`; 409 `auth.phone_number_taken` |
| `DELETE` | `/api/v1/auth/me/phone` | Removes the verified number. | 204, also when there is none | 401 `http.401`; 403 `auth.step_up_required` |

`GET /api/v1/auth/me` adds `phoneNumber` (the verified number or null) and `phoneNumberVerifiedAt`.

New error codes (in `en`, `ms`, `zh-Hans`): `auth.invalid_phone_number`, `auth.invalid_code`, `auth.phone_number_taken`.

### 4.2 Contracts

- `UserContact` gains `PhoneNumber` (the verified number or null), so a type with an SMS channel can reach the user.
- New `PhoneVerificationRequestedIntegrationEvent(Id, OccurredAt, UserId, PhoneNumber, ProtectedCode, ExpiresAt)`: `PhoneNumber` is the pending number the code is for, `ProtectedCode` the code encrypted with `ISecretProtector` (ADR 0017).

### 4.3 Configuration

| Key | Default | Meaning |
|---|---|---|
| `Auth:Phone:Enabled` | `false` | Turns the phone routes on; turn it on together with `Notifications:Sms:Enabled`. |
| `Auth:Phone:AllowedRegions` | empty | ISO 3166 region codes (`MY`, `SG`, ...) whose numbers are accepted; empty means every region. An unknown code stops the host. |
| `Auth:Phone:CodeLifetime` | `00:10:00` (1 to 30 minutes) | How long a code is valid. |
| `Auth:Phone:ResendCooldown` | `00:01:00` (0 to 10 minutes) | The least time between two codes for one user. |
| `Auth:Phone:MaxCodesPerDay` | `5` (1 to 50) | The most codes one user is sent in 24 hours. |
| `Auth:Phone:MaxAttempts` | `5` (1 to 10) | Wrong tries that end a code. |
| `Auth:Retention:PhoneVerificationCodes` | `1.00:00:00` | How long a code is kept after it expires before the daily cleanup deletes it. |
| `Notifications:Sms:Enabled` | `false` | Turns the SMS channel on. |
| `Notifications:Sms:Provider` | `twilio` | The only accepted value. |
| `Notifications:Sms:Twilio:AccountSid` | empty | Required while SMS is enabled. |
| `Notifications:Sms:Twilio:AuthToken` | empty (user secrets or environment) | Required while SMS is enabled; never in a file. |
| `Notifications:Sms:Twilio:From` / `MessagingServiceSid` | empty | Exactly one is required while SMS is enabled (a sender number in E.164, or a Messaging Service). |

A new resilience pipeline `sms-provider` (suggested attempt timeout 10 s, total 30 s), which does not retry the `POST` (no `RetryUnsafeMethods`), as for push.

## 5. Behaviour

### 5.1 Data

- `auth.Users` gains `PhoneNumber nvarchar(32) NULL` and `PhoneNumberVerifiedAt datetime2(3) NULL`, with a filtered unique index `IX_Users_PhoneNumber` (`WHERE [PhoneNumber] IS NOT NULL`). `User.PhoneNumber` carries `[AuditMask]`.
- New `auth.PhoneVerificationCodes` (`PhoneVerificationCode`): `Id`, `UserId`, `PhoneNumber nvarchar(32)` (the pending number), `CodeHash varbinary(32)`, `Salt varbinary(16)`, `CreatedAt`, `ExpiresAt`, `AttemptCount int`, `ConsumedAt`, `InvalidatedAt`. Indexes: `IX_PhoneVerificationCodes_UserId_CreatedAt` (the pending code, the cooldown and the daily count), `IX_PhoneVerificationCodes_ExpiresAt` (the cleanup).
- Migration: Auth `AddPhoneNumbers`.

### 5.2 Requesting a code

In one save, for the caller (`ICurrentUser`):

1. Parse `phoneNumber` with `libphonenumber-csharp` without a default region (a number must start with `+`); it must be valid, of type mobile or "fixed line or mobile", and its region in `AllowedRegions` when that is set. Else 400 `auth.invalid_phone_number`. The number is stored formatted as E.164.
2. If the caller already has a verified number, a fresh step-up is required (ADR 0023), else 403 `auth.step_up_required`: phone-OTP login will trust that number.
3. The caller's latest code is less than `ResendCooldown` old, or `MaxCodesPerDay` codes were issued in the last 24 hours: 429 `rate_limit.exceeded` with `Retry-After` (the time until the next code is allowed).
4. Every pending code of the caller is invalidated; a new `PhoneVerificationCode` is issued: a code from `RandomNumberGenerator.GetInt32(0, 1_000_000)` formatted as 6 digits, a 16-byte random salt, `CodeHash = SHA-256(salt + code)`, `ExpiresAt = now + CodeLifetime`.
5. `PhoneVerificationRequestedIntegrationEvent` is staged in the outbox with the code encrypted; the plaintext never leaves the handler's memory. Answer 202 `{ expiresAt }`.

The number is not checked against other users here: a request for a number another account has verified is accepted and texted, and only a person who receives the code learns it is taken (5.3), so the route does not reveal which numbers have accounts.

### 5.3 Confirming

1. The caller's pending code (latest, not consumed, not invalidated, not expired) is loaded; none: 400 `auth.invalid_code`.
2. A wrong code counts one try with one conditional `UPDATE ... SET AttemptCount = AttemptCount + 1 WHERE Id = @id AND AttemptCount < @max AND ConsumedAt IS NULL AND InvalidatedAt IS NULL AND ExpiresAt > @now`; the try that reaches `MaxAttempts` also invalidates it. Parallel guesses cannot exceed the limit. Answer 400 `auth.invalid_code` (the same answer for every cause).
3. The right code (compared with `CryptographicOperations.FixedTimeEquals`) is consumed with one conditional `UPDATE` (of two concurrent confirmations exactly one wins); the user's `PhoneNumber` becomes the code's number and `PhoneNumberVerifiedAt = now`; an audit entry `auth.phone_number_verified` is written; one save.
4. A unique-index violation on `IX_Users_PhoneNumber` (another account verified the number first): 409 `auth.phone_number_taken`; the code stays consumed.

Removing (`DELETE`) clears both columns, writes `auth.phone_number_removed`, and invalidates any pending code.

### 5.4 The SMS channel

- `NotificationChannel.Sms = 4`. While `Notifications:Sms:Enabled` is off no SMS delivery is created and no SMS channel is registered (a pending one dead-letters `channel_unavailable`), as for push.
- **Type** `auth.phone_verification` (Auth's catalog source): category `Security`, priority `Critical` (never deferred), default and mandatory channels `[Sms]`, variable `expires_in_minutes`, secret variable `code` of kind `Code`. Its consumer schedules it with the event's `PhoneNumber` as the destination (`SmsOverride`, as `EmailOverride` is for an email link) and `ExpiresAt` from the event, so a text that waited past the code's life is expired, never sent.
- **Secret kinds.** `NotificationTypeDefinition` declares each secret variable's kind: `Link` (today's rule: an absolute `http(s)` URL with a host, no user info, at most 2048 characters) or `Code` (4 to 10 ASCII digits). The scheduler checks each by its kind; the worker decrypts secrets for email and SMS; in-app and push never receive one (the catalog refuses a secret variable on a type whose default channels are only in-app or push).
- **Destination.** `SmsDestination`: an E.164 number (`+` and 8 to 15 digits, the first not 0); the scheduler skips an SMS delivery without one (Warning naming only the user id); the destination is the request's `SmsOverride` or else the contact's verified `PhoneNumber`.
- **Templates.** One part, `text`, under `Templates/Sms/{type}.{culture}.text.scriban`, kept to one SMS segment: `{{ product_name }}: your verification code is {{ code }}. It expires in {{ expires_in_minutes }} minutes.` (and the `ms`, `zh-Hans` drafts). 3 files.
- **Transport.** `TwilioSmsTransport` (`ISmsTransport.SendAsync(SmsMessage, CancellationToken)`, `SmsMessage(DeliveryId, To, Body)` printing ids only): `POST https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json`, form `To`, `Body`, and `From` or `MessagingServiceSid`, Basic authentication with the SID and token, through the `sms-provider` client.

| Twilio answer | Reason | Kind |
|---|---|---|
| 400 with code 21211 (invalid `To`), 21614 (not a mobile), 21408 (region not enabled), 21610 (the recipient replied STOP) | `twilio_{code}` | permanent |
| 401 / code 20003 (authentication), 400 with code 21606 (bad `From`) or 21659 | `twilio_{code}` | transient (a configuration fault) |
| 429, 5xx, a timeout, a connection failure, an open breaker | `twilio_http_{status}`, `timeout`, `connection_failed`, `circuit_open` | transient |
| Any other 4xx, or an answer whose body is not Twilio's JSON | `twilio_http_{status}` | permanent for 4xx, transient for 5xx |

The response body is never logged or attached (it can quote the number); logs name the delivery id and type code only.

- **Privacy.** The number, the body and the code are never logged or put in a metric tag; the admin delivery list masks an SMS destination to its last 3 digits (`********789`).

### 5.5 Switches

Everything in Notifications goes with `--notifications false`. Auth's phone code stays unmarked, following the precedent of Auth's events and verification links, which are also sent only by Notifications: a project without Notifications keeps `Auth:Phone:Enabled` off. The docs say so.

## 6. Where it lands

| Area | Change |
|---|---|
| `Auth/Domain/Users/User.cs` | `PhoneNumber`, `PhoneNumberVerifiedAt`, `VerifyPhoneNumber(number, now)`, `RemovePhoneNumber(now)`. |
| `Auth/Domain/Verification/` | `PhoneVerificationCode` (issue, invalidate, consume, the try rules), `PhoneNumberRules` (parse, mobile, regions; wraps libphonenumber). |
| `Auth/Application/Phone/` | `RequestPhoneCode`, `ConfirmPhone`, `RemovePhone` (command, handler, validator each); `PhoneOptions`; `IPhoneVerificationCodeRepository`. |
| `Auth/Infrastructure/` | Persistence (configuration, repository with the two conditional updates), migration `AddPhoneNumbers`, cleanup job `AuthPhoneVerificationCodesCleanup`, the integration event publisher, `UserContactDirectory` reads `PhoneNumber`. |
| `Auth/Endpoints/` | The three routes (`auth-strict` on `POST /me/phone`), the `GET /me` fields. |
| `Auth.Contracts/` | `UserContact.PhoneNumber`, `PhoneVerificationRequestedIntegrationEvent`. |
| `Notifications/Domain` | `NotificationChannel.Sms = 4`, `SmsDestination`, the `AddDelivery` rule for SMS (one per notification). |
| `Notifications/Application` | Secret variable kinds; the scheduler's SMS branch and `SmsOverride`; the `PhoneVerificationRequested` consumer; the catalog type; `ISmsTransport`, `SmsMessage`, `SmsSendException`; `DestinationMask` for numbers. |
| `Notifications/Infrastructure/Channels/Sms/` | `SmsChannel`, `TwilioSmsTransport`, `SmsOptions` and validation; the `AddSms` registration. |
| `Notifications/Templates/Sms/` | 3 files. |
| `Directory.Packages.props` | `libphonenumber-csharp` (Apache-2.0; licence file and terms checked). |
| Docs | `auth.md`, `notifications.md`, `infrastructure-common.md` (the `sms-provider` example), production checklist (Twilio credentials, `From`, allowed regions, Twilio spend alerts and geo permissions), `blueprint-review.md`, ADR 0040, `docs/README.md` and the overview ADR rows. |
| Index | `bash build/scripts/project-index.sh`. |

## 7. Tests, first

Unit: `PhoneNumberRules` (E.164, mobile vs landline, a region outside the allowlist, a number without `+`, an over-long input); `PhoneVerificationCode` (issue, a code of 6 digits, the expiry boundary, consume once, invalidation, the try limit); the request handler (the cooldown and daily cap with `Retry-After`, step-up only when replacing a verified number, older codes invalidated, the event carries only the encrypted code); the confirm handler (a wrong code, the fifth wrong try ends it, the right code verifies, the taken number); the remove handler; the secret-kind checks (a `Code` refused when not digits, a `Link` refused when not a URL, a secret on an in-app-only type refused by the catalog); the scheduler's SMS branch (override, contact number, none); `SmsChannel`; `TwilioSmsTransport` against a fake handler (every row of the table, a non-JSON body, the number and body never in logs or exceptions); the option checks for both modules; `DestinationMask` for numbers.

Integration: the three routes and `GET /me`; parallel wrong guesses never exceed `MaxAttempts`; two users confirming the same number get one 204 and one 409; the full flow (request, the SMS recorded by a recording `ISmsTransport` on a host with SMS enabled, confirm with the recorded code); persistence, the unique filtered index, the cleanup job; the OpenAPI snapshot.

Smoke: all on, then `--notifications false`, `--includeSample false`, `--caching memory`.

## 8. Risks

- **SMS pumping.** The limits bound one account; many accounts can still pump. The region allowlist and Twilio's own geo permissions and spend alerts are the remaining guards (production checklist).
- **A stolen database brute-forces a code hash.** A 6-digit code hashed with a salt can be recovered offline, but it lives 10 minutes and allows 5 tries; accepted in ADR 0040.
- **Latency.** A code reaches the phone about two polling intervals after the request (Auth's outbox, then the delivery worker); lower `Outbox:PollingInterval` and `Notifications:Delivery:PollingInterval` if that is too slow.
- **libphonenumber metadata ages.** Number ranges change; the package is updated by Dependabot like the others.
- **Unicode text costs more.** A `zh-Hans` text is one segment only up to 70 characters; the template stays under it.

## 9. Open questions

None blocking. The native review of the `ms` and `zh-Hans` drafts stays open, as for every template.

## 10. Review focus

- 5.2's choice to text a number another account has verified and refuse only at confirmation.
- Step-up only when replacing or removing a verified number, not when adding the first.
- The secret-variable kinds (a change to the catalog that email links also go through).
- The Twilio failure table, in particular which codes count as configuration faults.
