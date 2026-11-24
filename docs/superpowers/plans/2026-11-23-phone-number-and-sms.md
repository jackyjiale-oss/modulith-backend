# Phone Number and SMS Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user add and verify a mobile number with a 6-digit SMS code, and give the Notifications module an SMS channel (Twilio), both off by default.

**Architecture:** Auth owns the number (`User.PhoneNumber`, verified only) and a `PhoneVerificationCode` aggregate; requesting a code publishes `PhoneVerificationRequestedIntegrationEvent` with the code encrypted; Notifications schedules type `auth.phone_verification` (mandatory SMS) to the event's number, and `SmsChannel` sends it through `TwilioSmsTransport` on the `sms-provider` pipeline. Secret template variables gain a kind (`Link` or `Code`) so a code can travel like a link does.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server), Dapper, Scriban, `libphonenumber-csharp`, `Microsoft.Extensions.Http.Resilience`, xUnit v3, Shouldly, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-23-phone-number-and-sms-design.md` (approved 2026-11-23). Section numbers below refer to it.

## Global Constraints

- `TemplateName` is the placeholder; every new type is `internal` except Contracts types.
- No `DateTime.Now`/`UtcNow`: inject `TimeProvider`. Enums are `tinyint` and never renumbered: `NotificationChannel.Sms = 4`; `SecretKind` `Link = 1`, `Code = 2`.
- The phone number, the SMS body and the code are never logged, put in an exception message or a metric tag; records holding them override `PrintMembers`. The code reaches the outbox only encrypted (`ISecretProtector`, ADR 0017).
- Reason codes match `^[a-z][a-z0-9_]*\z`, at most 50 characters.
- New error codes `auth.invalid_phone_number`, `auth.invalid_code`, `auth.phone_number_taken` in `en`, `ms`, `zh-Hans`.
- New package `libphonenumber-csharp` only through `Directory.Packages.props` (Apache-2.0); read its licence and terms.
- Commit headers at most 72 characters; Conventional Commits; never `--no-verify`. The squash title is `feat: add a verified phone number and the SMS channel`.
- After a route, error code, configuration key, job, test file or marker changes: `git add`, then `bash build/scripts/project-index.sh`, commit the result.
- SMS is at least once and never guaranteed (spec 5.4): `Sent` means Twilio accepted it; a stored SID matches `^(SM|MM)[0-9a-f]{32}\z`, else none is stored.
- Integration tests run in CI only; they must compile locally. An Auth doc comment never names the Notifications module (the switch-off dead-reference check); every new ADR is listed in `docs/README.md` and `docs/architecture/overview.md` (under a notifications marker when it is about Notifications).

## Review Focus

1. **A number typed with spaces, dashes or brackets** (`+60 12-345 6789`) is accepted and stored as `+60123456789`; the same number in another format is the same number. Pinned in Task 1 (`Formatting_is_normalized_to_e164`) and Task 3 (`The_same_number_in_another_format_is_taken`).
2. **A second code replaces the first**: the first code answers `auth.invalid_code`, the second verifies. Pinned in Task 3 (`A_new_code_ends_the_previous_one`).
3. **Another user verifies the number between request and confirm**: 409 `auth.phone_number_taken`, the caller's existing verified number unchanged. Pinned in Task 3 (`A_number_verified_by_someone_else_meanwhile_is_taken`).
4. **Twilio's error body is not the expected JSON** (an HTML 502, a `code` that is a string): the status decides, no exception escapes. Pinned in Task 6 (`An_unexpected_error_body_falls_back_to_the_status`).
5. **Auth phone on, Notifications SMS off**: the request answers 202, the event is recorded once as `NothingToDeliver` and never retried. Pinned in Task 5 (`Sms_off_records_the_code_event_once`).
6. **A send times out after Twilio accepted it**: the retry texts the same code, never a different one, and the user can confirm with it. Pinned in Task 7 (`A_transient_failure_is_retried_with_the_same_code`).

---

### Task 1: Auth domain — phone number, code and rules

**Files:**
- Modify: `src/Modules/Auth/TemplateName.Modules.Auth/Domain/Users/User.cs` (two properties, two methods)
- Create: `.../Auth/Domain/Verification/PhoneVerificationCode.cs`, `PhoneNumberRules.cs`, `PhoneErrors.cs`
- Modify: `Directory.Packages.props` (`libphonenumber-csharp`, latest stable), the Auth `.csproj`, lock files (`dotnet restore --force-evaluate`)
- Modify: `.../Auth/Resources/AuthErrorMessages*.resx` (three codes, three languages), `docs/modules/auth.md` error-code table
- Test: `tests/TemplateName.UnitTests/Auth/Phone/PhoneVerificationCodeTests.cs`, `PhoneNumberRulesTests.cs`, `UserPhoneTests.cs`

**Interfaces:**
- Produces:
  - `User.PhoneNumber : string?` (`[AuditMask]`, `MaxPhoneNumberLength = 32`), `User.PhoneNumberVerifiedAt : DateTimeOffset?`; `void VerifyPhoneNumber(string e164, DateTimeOffset now)`; `void RemovePhoneNumber()`.
  - `PhoneNumberRules.TryNormalize(string input, IReadOnlySet<string> allowedRegions, out string e164) : bool` — libphonenumber `Parse(input, null)`; valid; type `MOBILE` or `FIXED_LINE_OR_MOBILE`; region in `allowedRegions` when non-empty; `Format(E164)`.
  - `PhoneVerificationCode`: `Issue(Guid userId, string phoneNumber, string code, TimeSpan lifetime, DateTimeOffset now) : PhoneVerificationCode` (16-byte salt, `CodeHash = SHA256(salt ‖ UTF8(code))`); `static string NewCode()` (`RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6")`); `bool Matches(string code)` (`CryptographicOperations.FixedTimeEquals`); `bool IsPending(DateTimeOffset now)`; `void Invalidate(DateTimeOffset now)` (idempotent); `void Consume(DateTimeOffset now)`; properties `Id, UserId, PhoneNumber, CodeHash, Salt, CreatedAt, ExpiresAt, AttemptCount, ConsumedAt, InvalidatedAt`; `ToString` prints `Id`, `UserId` only.
  - `PhoneErrors.InvalidPhoneNumber`, `InvalidCode` (`Error.Validation`), `PhoneNumberTaken` (`Error.Conflict`).

- [ ] **Step 1: Write the failing tests.** `PhoneNumberRulesTests`: `A_malaysian_mobile_is_normalized` (`+60123456789` → itself), `Formatting_is_normalized_to_e164` (`+60 12-345 6789`, `+60 (12) 345-6789` → `+60123456789`), `A_number_without_plus_is_refused` (`0123456789`), `A_landline_is_refused` (`+60321234567`), `A_region_outside_the_allowlist_is_refused` (`+6581234567` with `{"MY"}`), `An_empty_allowlist_accepts_every_region`, `Garbage_is_refused` (`"+abc"`, 40 digits). `PhoneVerificationCodeTests`: `NewCode_is_six_digits`, `Matches_only_the_issued_code`, `ToString_never_contains_the_code_or_number`, `IsPending_until_expiry_exclusive`, `Invalidate_keeps_the_first_time`, `Consume_ends_it`. `UserPhoneTests`: `VerifyPhoneNumber_sets_both_fields`, `RemovePhoneNumber_clears_both`.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests -c Release --filter "FullyQualifiedName~Auth.Phone"` — Expected: build FAIL.
- [ ] **Step 3: Implement**; add the package (check licence and terms); the resx entries (`en`: `The phone number is not a valid mobile number in a supported region.` / `The code is invalid or has expired. Request a new one.` / `This phone number is already used by another account.`; `ms` and `zh-Hans` drafts in the module's existing style); the three rows in `auth.md`'s error codes.
- [ ] **Step 4: Run** the filter, then the unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(auth): add phone numbers and verification codes`.

### Task 2: Auth persistence, migration and cleanup

**Files:**
- Modify: `.../Auth/Infrastructure/Persistence/UserConfiguration.cs` (columns, `IX_Users_PhoneNumber` unique filtered `[PhoneNumber] IS NOT NULL`)
- Create: `.../Auth/Infrastructure/Persistence/PhoneVerificationCodeConfiguration.cs`, `PhoneVerificationCodeRepository.cs`; `.../Auth/Application/Abstractions/IPhoneVerificationCodeRepository.cs`
- Create: migration `AddPhoneNumbers` (Auth context); `.../Auth/Infrastructure/Retention/PhoneVerificationCodesCleanupJob.cs`
- Modify: `.../Auth/Infrastructure/Retention/AuthRetentionOptions.cs` (`PhoneVerificationCodes`, default 1 day), `AuthModule.cs`
- Test: `tests/TemplateName.IntegrationTests/Auth/PhoneVerificationCodePersistenceTests.cs`, the Auth cleanup job tests (add the job to their table), `AuthRetentionOptionsTests` (addition)

**Interfaces:**
- Produces: `IPhoneVerificationCodeRepository { void Add(PhoneVerificationCode code); Task<PhoneVerificationCode?> GetPendingAsync(Guid userId, DateTimeOffset now, CancellationToken ct); Task<IReadOnlyList<PhoneVerificationCode>> ListPendingAsync(Guid userId, DateTimeOffset now, CancellationToken ct); Task<DateTimeOffset?> GetLastIssuedAtAsync(Guid userId, CancellationToken ct); Task<int> CountIssuedSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct); Task<bool> TryCountWrongAttemptAsync(Guid id, int maxAttempts, DateTimeOffset now, CancellationToken ct); Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken ct); }` — the two `Try*` are one conditional `UPDATE` each (5.3); the wrong-attempt update also sets `InvalidatedAt = @now` when `AttemptCount + 1 >= @max`.
- Table `auth.PhoneVerificationCodes` per spec 5.1; indexes `IX_PhoneVerificationCodes_UserId_CreatedAt`, `IX_PhoneVerificationCodes_ExpiresAt`. Job `AuthPhoneVerificationCodesCleanup`, daily, `DELETE TOP (@Batch) ... WHERE ExpiresAt < @Cutoff`.

- [ ] **Step 1: Write the failing tests.** Persistence: `A_code_round_trips`, `The_verified_number_is_unique` (two users, same number → `DbUpdateException`), `Two_users_without_a_number_are_fine`, `Parallel_wrong_attempts_never_exceed_the_maximum` (10 parallel `TryCountWrongAttemptAsync` with max 5 → exactly 5 `true`, `InvalidatedAt` set), `TryConsume_wins_once`, `Cleanup_deletes_codes_expired_more_than_a_day_ago`. Options: `Negative_phone_code_retention_is_refused`.
- [ ] **Step 2: Build** — Expected: FAIL.
- [ ] **Step 3: Implement**, add the migration (check it holds only the two columns, the index, the table and its indexes); update the Auth schema-design integration test if one lists tables.
- [ ] **Step 4: Build, unit and architecture suites** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(auth): store phone numbers and verification codes`.

### Task 3: Auth phone routes, the event and the contract

**Files:**
- Create: `.../Auth/Application/Phone/PhoneOptions.cs`; `RequestCode/` (`RequestPhoneCodeCommand`, handler, validator, `PhoneCodeResponse`), `Confirm/` (`ConfirmPhoneCommand`, handler, validator), `Remove/` (`RemovePhoneCommand`, handler)
- Create: `.../Auth/Domain/Verification/Events/PhoneVerificationRequestedDomainEvent.cs`, `.../Auth/Application/IntegrationEvents/PublishPhoneVerificationRequestedDomainEventHandler.cs`
- Create: `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/IntegrationEvents/PhoneVerificationRequestedIntegrationEvent.cs`
- Modify: `Auth.Contracts/Users/UserContact.cs` (`string? PhoneNumber`, last positional, default null), `Infrastructure/Contracts/UserContactDirectory.cs` (select it), `Endpoints/AuthEndpoints.cs` (three routes; `GET /me` fields), the `GET /me` response record, `AuthModule.cs` (options)
- Test: `tests/TemplateName.UnitTests/Auth/Phone/RequestPhoneCodeCommandHandlerTests.cs`, `ConfirmPhoneCommandHandlerTests.cs`, `RemovePhoneCommandHandlerTests.cs`, `PhoneOptionsTests.cs`; `tests/TemplateName.IntegrationTests/Auth/PhoneEndpointsTests.cs`; `UserContactDirectoryTests` (addition)

**Interfaces:**
- Consumes: Task 1 and 2 types; `StepUpPolicy.Check(bool hasConfirmedMfa) : Result`; `ISecretProtector.Protect(string)`; `MfaErrors.StepUpRequired`.
- Produces:
  - `PhoneOptions` (`Auth:Phone`): `Enabled = false`, `AllowedRegions : string[] = []` (each must be a region libphonenumber knows, else start fails naming the key), `CodeLifetime = 10 min` (1–30 min), `ResendCooldown = 1 min` (0–10 min), `MaxCodesPerDay = 5` (1–50), `MaxAttempts = 5` (1–10).
  - `RequestPhoneCodeCommand(string PhoneNumber) : ICommand<PhoneCodeResponse>`; `PhoneCodeResponse(DateTimeOffset ExpiresAt)`. The cooldown and the daily cap fail with `PhoneErrors.TooManyCodes(TimeSpan retryAfter)` (code `rate_limit.exceeded`, the retry time in its parameters); the endpoint answers 429 with `Retry-After` from it (reuse the host's existing 429 mapping if `Error` has a too-many-requests type; otherwise map it in the endpoint).
  - `ConfirmPhoneCommand(string Code) : ICommand`; `RemovePhoneCommand : ICommand`.
  - `PhoneVerificationRequestedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string PhoneNumber, string ProtectedCode, DateTimeOffset ExpiresAt) : IIntegrationEvent` — `PrintMembers` prints `Id`, `UserId`, `ExpiresAt`.
  - Routes: `POST me/phone` (`RateLimitPolicies.AuthStrict`), `POST me/phone/confirm`, `DELETE me/phone`; each answers 404 `http.404` while `Enabled` is off (an endpoint filter reading `IOptions<PhoneOptions>`).

- [ ] **Step 1: Write the failing tests.** Request: `Issues_a_code_and_publishes_only_the_encrypted_code` (the domain event's protected value decrypts to the code; nothing else holds it), `A_new_code_ends_the_previous_one`, `The_cooldown_refuses_with_the_retry_time`, `The_daily_cap_refuses`, `Replacing_a_verified_number_needs_step_up`, `Adding_the_first_number_needs_no_step_up`, `An_invalid_number_is_refused`. Confirm: `The_right_code_verifies_the_number`, `A_wrong_code_counts_one_attempt`, `The_fifth_wrong_attempt_ends_the_code`, `No_pending_code_is_invalid_code`, `A_number_verified_by_someone_else_meanwhile_is_taken` (unique violation → 409, caller's old number unchanged). Remove: `Removes_with_step_up`, `Without_step_up_is_refused_when_a_number_exists`. Options: limits and an unknown region. Integration (`PhoneEndpointsTests`, a host with `Auth:Phone:Enabled` via `UseSetting`): `Routes_are_404_while_disabled`, `Request_then_confirm_with_the_code_from_the_event` (decrypt the outbox message's `ProtectedCode`), `The_same_number_in_another_format_is_taken`, `Get_me_shows_the_verified_number`. `UserContactDirectoryTests`: `The_verified_phone_number_is_returned`.
- [ ] **Step 2: Run** the Auth.Phone filter — Expected: FAIL.
- [ ] **Step 3: Implement** per spec 5.2 and 5.3 (one save each; the publish handler copies the protected code from the domain event into the integration event and decrypts nothing).
- [ ] **Step 4: Run** unit and architecture suites; build integration — Expected: PASS, 0 warnings.
- [ ] **Step 5: Index and commit** — `feat(auth): add the phone number routes`.

### Task 4: Notifications — secret variable kinds

**Files:**
- Create: `.../Notifications/Application/Catalog/SecretKind.cs`
- Modify: `NotificationTypeDefinition` (secret variables become `IReadOnlyDictionary<string, SecretKind>`), `NotificationCatalog` validation (a secret variable on a type whose default channels are only `InApp`/`Push` is refused), `AuthNotificationTypeSource` (the four link types declare `action_url` as `Link`), `NotificationScheduler` (check each secret by its kind; `Code` = 4–10 ASCII digits, refusal reason `unsafe_code`), `DeliveryDispatcher` (decrypt `ProtectedData` for `Email` and `Sms`)
- Test: the catalog, scheduler and dispatcher unit tests (additions); existing tests adjusted only for the new declaration shape

**Interfaces:**
- Produces: `internal enum SecretKind : byte { Link = 1, Code = 2 }`; `NotificationTypeDefinition.SecretVariables : IReadOnlyDictionary<string, SecretKind>`; scheduler refusal reasons `undecryptable`, `unsafe_url`, `unsafe_code`.

- [ ] **Step 1: Write the failing tests:** `A_code_secret_must_be_digits` (`"12a456"` → `Refused` with `unsafe_code`; `"123456"` → scheduled), `A_link_secret_is_still_a_safe_url`, `A_secret_on_an_in_app_only_type_is_refused_by_the_catalog`, `The_worker_decrypts_secrets_for_sms_but_not_in_app`.
- [ ] **Step 2: Run** the notifications unit filter — Expected: FAIL.
- [ ] **Step 3: Implement**; keep every existing link behaviour.
- [ ] **Step 4: Run** unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): give secret variables a kind`.

### Task 5: Notifications — the SMS channel in the domain, scheduler and catalog

**Files:**
- Modify: `NotificationChannel` (`Sms = 4`), `Notification.AddDelivery` (an SMS destination must pass `SmsDestination`; one SMS delivery per notification), `NotificationScheduler` (SMS branch: dropped while `SmsOptions.Enabled` is off; destination `request.SmsOverride ?? contact.PhoneNumber`, skipped with a Warning naming only the user id when neither is valid), `NotificationRequest` (`string? SmsOverride = null`, last; `PrintMembers` prints only `HasSmsOverride`), `PreferencesView.Offers` (SMS only while enabled), `Application/Admin/DestinationMask` (a number keeps its last 3 digits)
- Create: `.../Domain/Deliveries/SmsDestination.cs`, `.../Application/Sms/SmsOptions.cs` (`Notifications:Sms`: `Enabled = false`, `Provider = "twilio"`), `.../Application/AuthEvents/PhoneVerificationRequestedIntegrationEventHandler.cs`, the catalog type `auth.phone_verification` (Security, Critical, channels `[Sms]` mandatory, variable `expires_in_minutes`, secret `code` of kind `Code`), `Templates/Sms/auth.phone_verification.{en,ms,zh-Hans}.text.scriban`
- Modify: `EmbeddedTemplateStore.PartsOf(Sms)` = `["text"]`, `ScribanNotificationRenderer` (SMS: `RenderedMessage(Subject: "", TextBody: single-line text, HtmlBody: null)`)
- Test: `NotificationTests`, `NotificationSchedulerTests` (SMS cases), the consumer, `AuthNotificationTypesTests`, `AuthNotificationRenderTests`, `DestinationMaskTests`, preferences tests (additions)

**Interfaces:**
- Consumes: Task 3 `PhoneVerificationRequestedIntegrationEvent`, `UserContact.PhoneNumber`; Task 4 `SecretKind.Code`.
- Produces: `NotificationChannel.Sms`; `SmsDestination.IsValid(string?) : bool` (`^\+[1-9][0-9]{7,14}\z`); `SmsOptions`; `NotificationRequest.SmsOverride`.

- [ ] **Step 1: Write the failing tests:** `One_sms_delivery_to_the_override_number`, `Without_an_override_the_verified_contact_number_is_used`, `No_valid_number_skips_sms`, `Sms_off_records_the_code_event_once` (SMS disabled → `NothingToDeliver`, inbox recorded, no exception), `The_phone_code_type_is_critical_mandatory_sms`, `The_sms_template_fits_one_segment` (en ≤ 160 GSM characters, zh-Hans ≤ 70, with a 6-digit code and `expires_in_minutes` = `10`), `A_number_is_masked_to_its_last_three_digits` (`+60123456789` → `**********789`), `Sms_is_hidden_and_refused_while_disabled`, `The_consumer_passes_the_number_code_and_expiry`.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement**; templates: en `{{ product_name }}: your verification code is {{ code }}. It expires in {{ expires_in_minutes }} minutes.`; `ms` and `zh-Hans` faithful drafts within the segment limits.
- [ ] **Step 4: Run** unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): schedule the phone code by SMS`.

### Task 6: The SMS channel and the Twilio transport

**Files:**
- Create: `.../Application/Abstractions/ISmsTransport.cs`, `SmsMessage.cs`, `SmsSendException.cs`; `.../Infrastructure/Channels/Sms/SmsChannel.cs`, `TwilioSmsTransport.cs`, `TwilioOptions.cs` (+ validation)
- Modify: `NotificationsModule.cs` (`AddSms`: options always; channel, transport, `AddExternalHttpClient("sms-provider", "sms-provider")` only while `Notifications:Sms:Enabled`), `docs/building-blocks/infrastructure-common.md` (`sms-provider` paragraph under the notifications marker)
- Modify (the SID): `Application/Abstractions/ChannelSendResult.cs` (`string? ProviderMessageId`; `static ChannelSendResult SentAs(string providerMessageId)`, the existing `Sent` unchanged), `Domain/Deliveries/Delivery.cs` (`ProviderMessageId : string?`, max 64; `MarkSent(string? renderedSubject, DateTimeOffset now, string? providerMessageId = null)`), `DeliveryDispatcher` (passes it on), `Infrastructure/Persistence/DeliveryConfiguration.cs` (`nvarchar(64)` null), migration `AddDeliveryProviderMessageId` (Notifications context; that one column only), `Application/Admin/Deliveries/List/DeliveryResponse.cs` and its handler (`string? ProviderMessageId`, last)
- Test: `tests/TemplateName.UnitTests/Notifications/Sms/SmsChannelTests.cs`, `TwilioSmsTransportTests.cs`, `TwilioOptionsTests.cs`; `DeliveryTests` and the dispatcher tests (additions); the Notifications schema-design integration test

**Interfaces:**
- Produces: `ISmsTransport.SendAsync(SmsMessage message, CancellationToken ct) : Task<string?>` (the SID, or null when Twilio's `sid` is missing or malformed); `SmsMessage(Guid DeliveryId, string To, string Body)` printing `DeliveryId` only; `SmsSendException(bool isPermanent, string reason)`; `TwilioOptions` (`Notifications:Sms:Twilio`: `AccountSid`, `AuthToken`, `From`, `MessagingServiceSid`; while SMS is enabled the SID and token are required and exactly one of `From`/`MessagingServiceSid`, validated with the raw enabled flag read at registration, as `FcmOptionsValidation` does).
- `TwilioSmsTransport`: `POST https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json`, `FormUrlEncodedContent` (`To`, `Body`, `From` or `MessagingServiceSid`), Basic `AccountSid:AuthToken`; failures per the spec 5.4 table, the numeric `code` read only when it is a JSON number; `HttpRequestException` → `connection_failed`, `TimeoutRejectedException` → `timeout`, `BrokenCircuitException` → `circuit_open`.

- [ ] **Step 1: Write the failing tests:** `Sends_the_form_with_basic_auth` (URL, header, form fields with `From`; another with `MessagingServiceSid`), one `[Theory]` row per line of the spec 5.4 table, `An_unexpected_error_body_falls_back_to_the_status` (HTML 502; `{"code":"21211"}` as a string; `[1]`), `The_number_and_body_never_appear_in_logs_or_exceptions`, `Returns_the_message_sid` (`{"sid":"SM` + 32 hex + `"}` → that SID), `A_missing_or_malformed_sid_returns_none` (no `sid`; `"sid":"XX1"`; `"sid":"SM<script>"` → null, still sent); `SmsChannelTests`: `Sends_the_rendered_text_to_the_destination`, `Maps_a_send_exception_to_its_result`, `Returns_the_sid_with_the_sent_result`; `DeliveryTests`: `MarkSent_keeps_the_provider_message_id`; dispatcher: `The_provider_message_id_is_saved_with_the_sent_delivery`; `TwilioOptionsTests`: `Enabled_without_credentials_is_refused`, `Both_from_and_service_are_refused`, `Disabled_needs_nothing`, messages name keys only.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement** and register; add the migration (check it holds only the `ProviderMessageId` column).
- [ ] **Step 4: Run** unit and architecture suites; `dotnet build -c Release` — Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** — `feat(notifications): send SMS through Twilio`.

### Task 7: Flow, documentation and verification

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Infrastructure/RecordingSmsTransport.cs`, `tests/TemplateName.IntegrationTests/Notifications/SmsFlowTests.cs`
- Modify: `.template.config/template.json` (exclude `RecordingSmsTransport.cs` and ADR 0040 when `!notifications`), `docs/modules/auth.md`, `docs/modules/notifications.md`, `docs/deployment/production-checklist.md` (under the notifications marker: Twilio credentials, `From`, allowed regions, and the four required steps of spec 8 — SMS Pumping Protection on, geo permissions limited to `AllowedRegions`, a spend limit, operator alerts), `docs/modules/notifications.md` also gets "Delivery guarantees" (spec 5.4) and "What to monitor" (spec 8, Monitored), `docs/blueprint-review.md` (the row done when merged; a new row "SMS delivery receipts (Twilio status callbacks)"), `docs/README.md` and `docs/architecture/overview.md` (ADR 0040 rows under markers), the spec's status line
- Create: `docs/adr/0040-phone-verification-codes-and-sms.md`
- Modify: the OpenAPI snapshot from CI's `received-snapshots` artifact; the integration tests that list schema, catalog types or preferences (as for push)

- [ ] **Step 1: Write the flow tests** (`SmsFlowTests`, a host with `Auth:Phone:Enabled`, `Notifications:Sms:Enabled`, Twilio settings and a recording `ISmsTransport`): `Request_sends_the_code_by_sms_and_confirm_verifies` (read the code from the recorded body), `A_wrong_code_then_the_right_one`, `An_expired_code_is_never_sent` (advance past `CodeLifetime` before dispatch → delivery `Expired`, nothing recorded), `A_transient_failure_is_retried_with_the_same_code` (the recording transport throws a transient `SmsSendException` once; after the retry delay the second send's body holds the same code and confirming with it answers 204), `The_admin_delivery_list_shows_the_sid` (the recording transport returns a fixed SID).
- [ ] **Step 2: Build** — 0 warnings.
- [ ] **Step 3: Write the docs and ADR 0040** (context: SMS pumping, short codes; decision: own codes, a separate aggregate, Twilio only, the region allowlist, salted SHA-256 and the offline brute-force trade-off, accepted only while the controls listed in spec 8 hold, no number enumeration at request time, at-least-once sending with a possible duplicate text, the SID stored for later receipts, pumping across accounts stopped at Twilio rather than by an app-wide cap; consequences).
- [ ] **Step 4: Verify locally:** Release build, `dotnet format --verify-no-changes`, unit and architecture suites, the licence check, the four smoke runs; index; commit `docs(notifications): document the SMS channel`.
- [ ] **Step 5: Push, open** `feat: add a verified phone number and the SMS channel` (folding in draft #64); fix the integration tests CI reports and refresh the OpenAPI snapshot from `received-snapshots` (only the three routes, the `GET /me` fields, `Sms` in the channel enum and `providerMessageId` in the admin delivery list).
- [ ] **Step 6: Final review** by a fresh reviewer on the most capable model; fix Critical and Important findings test-first; the user gives the merge word.
