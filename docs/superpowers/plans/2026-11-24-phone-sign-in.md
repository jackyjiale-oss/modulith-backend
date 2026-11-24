# Phone-Number Sign-In Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user sign in with a 6-digit code texted to the account's verified number (`amr` `sms`, the TOTP challenge still applies), and guard the number like any sign-in method: a step-up to add it, other sessions ended on replace and remove, an email on every change.

**Architecture:** `PhoneVerificationCode` gains a `Purpose` (`Verify`, `SignIn`) and keeps one SMS budget per user. Two anonymous routes under `phone/sign-in/` issue and verify `SignIn` codes; verify either starts a session through `SessionStarter` or issues an `MfaChallenge` whose first factor is `sms`. `StepUpPolicy` refuses an `sms`-only session. Number changes raise `PhoneNumberChangedDomainEvent`, published for the Notifications module, which also gains the `auth.phone_sign_in` SMS type.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server), Dapper, Scriban, `libphonenumber-csharp`, xUnit v3, Shouldly, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-24-phone-sign-in-design.md` (approved 2026-11-24). Section numbers below refer to it. Spec 2's plan (`2026-11-23-phone-number-and-sms.md`) shows the patterns this one extends.

## Global Constraints

- `TemplateName` is the placeholder; new types are `internal` except Contracts types.
- No `DateTime.Now`/`UtcNow`: inject `TimeProvider`. Enums are stored as `tinyint` and never renumbered: `PhoneCodePurpose` `Verify = 1`, `SignIn = 2`; `SessionRevokedReason.PhoneNumberChanged = 8`; Contracts `PhoneNumberChangeKind` `Added = 1`, `Replaced = 2`, `Removed = 3`.
- `amr` value `"sms"` (`AuthenticationMethods.Sms`). Joined values stay under the 64 characters of `AuthMethods`; `MfaChallenge.FirstFactor` stays within 16.
- The phone number and the code are never logged, put in an exception message, an audit row or a metric tag; commands, requests and events print ids only (`PrintMembers`/`ToString` overrides as in spec 2). The code reaches the outbox only encrypted (`ISecretProtector`). The change event carries only the last 3 digits.
- No new error code: verify's refusals reuse `auth.invalid_code` (`PhoneErrors.InvalidCode`), send's `auth.invalid_phone_number`.
- Audit event names: `auth.phone_sign_in_requested`, `auth.phone_sign_in_failed`; reasons match `^[a-z][a-z0-9_]*\z`: `inactive`, `locked`, `role_denied`, `cooldown`, `daily_cap`, `no_code`, `wrong_code`, `lost_race`. Metric `auth.phone_sign_ins` with tag `outcome` (`sent`, `suppressed`, `signed_in`, `challenged`, `refused`).
- An Auth doc comment never names the Notifications module (the switch-off dead-reference check). ADR 0041 is listed in `docs/README.md` and `docs/architecture/overview.md` outside any notifications marker; a line naming the module or its types sits on its own marked line (ADR 0035).
- Commit headers at most 72 characters, Conventional Commits, never `--no-verify`. The squash commit is `feat(auth)!: sign in with a phone number` with the footer `BREAKING CHANGE: POST /api/v1/auth/me/phone needs a fresh step-up for a first number, and replacing or removing a number ends the caller's other sessions.`
- After a route, configuration key, audit event, test file or marker changes: `git add`, `bash build/scripts/project-index.sh`, commit the result.
- Integration tests run in CI; they must compile locally with 0 warnings in Release.

## Review Focus

1. **Verifying the number the account already has** (a code requested for the current number): nothing changes, no `Replaced` event, no session ended. Pinned in Task 1 (`Verifying_the_current_number_again_changes_nothing`).
2. **A number typed in another format at sign-in** (`+60 12-345 6789`) finds the account verified as `+60123456789`. Pinned in Task 4 (`Formatting_is_normalized_at_send`) and Task 5 (`Formatting_is_normalized_at_verify`).
3. **A code of one purpose presented to the other route**: a verification code never signs in and a sign-in code never confirms a number. Pinned in Task 3 (`A_sign_in_code_does_not_confirm`) and Task 5 (`A_verification_code_does_not_sign_in`).
4. **`rememberDevice: true` after an `sms` first factor** creates no trusted device and no token. Pinned in Task 5 (`Remember_device_is_ignored_for_an_sms_challenge`).
5. **Denied roles written in another case or with spaces** (`" admin "`) still deny. Pinned in Task 4 (`Denied_roles_compare_normalized_names`).

---

### Task 1: Auth domain and the step-up rule

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth/Domain/Verification/PhoneCodePurpose.cs`, `.../Domain/Verification/Events/PhoneSignInCodeIssuedDomainEvent.cs`, `.../Domain/Users/Events/PhoneNumberChangedDomainEvent.cs`
- Modify: `.../Domain/Verification/PhoneVerificationCode.cs`, `.../Domain/Users/User.cs` (`VerifyPhoneNumber`, `RemovePhoneNumber`), `.../Domain/Sessions/AuthenticationMethods.cs`, `.../Domain/Sessions/SessionRevokedReason.cs`, `.../Domain/Mfa/MfaChallenge.cs` (`Issue` accepts `sms`), `.../Application/StepUp/StepUpPolicy.cs`
- Create: `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/Users/PhoneNumberChangeKind.cs` (the domain event uses the Contracts enum)
- Test: `tests/TemplateName.UnitTests/Auth/Phone/PhoneVerificationCodeTests.cs`, `UserPhoneTests.cs`, `tests/TemplateName.UnitTests/Auth/Mfa/StepUpPolicyTests.cs`, `AuthenticationMethodsTests.cs`, `SessionRevokedReasonTests.cs`, the `MfaChallenge` tests

**Interfaces:**
- Produces:
  - `enum PhoneCodePurpose : byte { Verify = 1, SignIn = 2 }`; `PhoneVerificationCode.Purpose`; `Issue(Guid userId, PhoneCodePurpose purpose, string phoneNumber, string code, string protectedCode, TimeSpan lifetime, DateTimeOffset now)` raises `PhoneVerificationRequestedDomainEvent` for `Verify` and `PhoneSignInCodeIssuedDomainEvent(Guid CodeId, Guid UserId, string PhoneNumber, string ProtectedCode, DateTimeOffset ExpiresAt)` for `SignIn`; `static void SpendMatchCost(string code)` (hashes with a fresh random salt, discards the result).
  - `User.VerifyPhoneNumber(string e164, DateTimeOffset now) : PhoneNumberChangeKind?` — `null` when `e164` equals the current number (nothing changes); `Added` when there was none (stamp kept); `Replaced` otherwise (stamp rotated). Raises `PhoneNumberChangedDomainEvent(Guid UserId, PhoneNumberChangeKind Kind, string LastDigits, DateTimeOffset ChangedAt)` unless `null`.
  - `User.RemovePhoneNumber(DateTimeOffset now) : bool` — false and nothing raised when there is none; otherwise clears both fields, rotates the stamp, raises the event (`Removed`, the removed number's last 3 digits).
  - `AuthenticationMethods.Sms = "sms"`; `SessionRevokedReason.PhoneNumberChanged = 8`; `MfaChallenge.Issue` accepts `pwd`, `fed` or `sms`.
  - `StepUpPolicy.Check(bool hasConfirmedMfa)`: first, if `AuthMethods` contains `sms` and neither `otp` nor `rcvr` → `MfaErrors.StepUpRequired`; the existing rules after.

- [ ] **Step 1: Write the failing tests.** Code: `Issue_for_sign_in_raises_the_sign_in_event` (and not the verification event), `Issue_for_verify_raises_the_verification_event`, `Purpose_is_kept`. User: `Verifying_a_first_number_is_added_and_keeps_the_stamp`, `Verifying_another_number_is_replaced_and_rotates_the_stamp`, `Verifying_the_current_number_again_changes_nothing` (returns null, no event, same stamp), `The_event_holds_only_the_last_three_digits` (`+60123456789` → `"789"`), `Removing_a_number_rotates_the_stamp_and_raises_removed`, `Removing_without_a_number_changes_nothing`. Step-up: `Fresh_sms_alone_is_refused` (`amr ["sms"]`, `auth_time` now, both MFA values), `Sms_otp_mfa_passes_within_max_age`, `Sms_rcvr_mfa_passes_within_max_age`, `Sms_otp_mfa_expires_after_max_age`. `AuthenticationMethodsTests`: `Sms_is_sms`. `SessionRevokedReasonTests`: the new value is 8. Challenge: `Sms_is_an_accepted_first_factor`; `Another_value_is_still_refused` (`"otp"`).
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests -c Release --filter "FullyQualifiedName~Auth"` — Expected: build FAIL.
- [ ] **Step 3: Implement.** Update spec 2's callers to the new signatures so the build stays green (the request handler passes `PhoneCodePurpose.Verify`; confirm and remove call the new methods; their behaviour changes come in Task 3).
- [ ] **Step 4: Run** the filter, then the unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(auth): add sign-in codes and sms to the domain`.

### Task 2: Persistence and the migration

**Files:**
- Modify: `.../Infrastructure/Persistence/PhoneVerificationCodeConfiguration.cs` (`Purpose`, `tinyint`, default `1`), `PhoneVerificationCodeRepository.cs`, `.../Application/Abstractions/IPhoneVerificationCodeRepository.cs`, `.../Application/Abstractions/IUserRepository.cs`, `.../Infrastructure/Persistence/UserRepository.cs`
- Create: migration `AddPhoneCodePurpose` (Auth context, `--output-dir Infrastructure/Persistence/Migrations`)
- Test: `tests/TemplateName.IntegrationTests/Auth/PhoneVerificationCodePersistenceTests.cs` (additions)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `GetPendingAsync(Guid userId, PhoneCodePurpose purpose, DateTimeOffset now, CancellationToken)` — the newest pending code of that purpose.
  - `ListPendingAsync(Guid userId, PhoneCodePurpose? purpose, DateTimeOffset now, CancellationToken)` — `null` lists both purposes.
  - `GetLastIssuedAtAsync` and `ListIssuedSinceAsync` unchanged: they count both purposes (the shared budget, spec 5.1).
  - `IUserRepository.GetByPhoneNumberAsync(string e164, CancellationToken) : Task<User?>` — the verified number, soft-deleted users excluded (the query filter), with the roles loaded as `GetByIdAsync` loads them.

- [ ] **Step 1: Write the failing tests.** `Purpose_round_trips`, `Rows_written_before_the_column_read_as_verify` (insert a row by SQL without `Purpose`, read `Verify`), `Pending_lookup_is_per_purpose`, `Listing_with_no_purpose_returns_both`, `The_budget_counts_both_purposes`, `GetByPhoneNumber_finds_the_verified_number_only` (a soft-deleted user's number is not found).
- [ ] **Step 2: Build** — Expected: FAIL.
- [ ] **Step 3: Implement and add the migration**; check it holds only the column with its default (`Down` drops it).
- [ ] **Step 4: Build; unit and architecture suites** — Expected: PASS, 0 warnings.
- [ ] **Step 5: Commit** — `feat(auth): store the purpose of a phone code`.

### Task 3: The number's guard: step-up, change effects and the change event

**Files:**
- Create: `.../Application/Phone/PhoneChangeEffects.cs`, `.../Application/IntegrationEvents/PublishPhoneNumberChangedDomainEventHandler.cs`, `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/IntegrationEvents/PhoneNumberChangedIntegrationEvent.cs`
- Modify: `.../Application/Phone/RequestCode/RequestPhoneCodeCommandHandler.cs`, `.../Phone/Confirm/ConfirmPhoneCommandHandler.cs`, `.../Phone/Remove/RemovePhoneCommandHandler.cs`, `docs/modules/auth.md` (Phone number, Endpoints rows, Audit events, Events)
- Test: `tests/TemplateName.UnitTests/Auth/Phone/RequestPhoneCodeCommandHandlerTests.cs`, `ConfirmPhoneCommandHandlerTests.cs`, `RemovePhoneCommandHandlerTests.cs`, `tests/TemplateName.UnitTests/Auth/IntegrationEvents/PhoneNumberChangedPublisherTests.cs`, `tests/TemplateName.IntegrationTests/Auth/PhoneEndpointsTests.cs`, `IntegrationEventPublishingTests` (addition)

**Interfaces:**
- Consumes: Tasks 1 and 2; `CallerSession`, `ISessionRepository.GetActiveByUserAsync`, `UserSession.Revoke`, `UserSession.AdoptSecurityStamp` (as the unlink handler uses them).
- Produces:
  - `PhoneChangeEffects.ApplyAsync(User user, Guid currentSessionId, DateTimeOffset now, CancellationToken) : Task<int>` (static or scoped, as `MfaChangeEffects` is): revokes every other active session with `PhoneNumberChanged`, the current one adopts the stamp, ends the pending codes of both purposes; returns the number revoked.
  - `PhoneNumberChangedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, PhoneNumberChangeKind Kind, string LastDigits)`.

- [ ] **Step 1: Write the failing tests.** Request: `Adding_the_first_number_needs_step_up` (replaces spec 2's `Adding_the_first_number_needs_no_step_up`), `A_new_verification_code_ends_only_pending_verification_codes` (a pending sign-in code stays). Confirm: `A_first_number_ends_no_session`, `A_replacement_ends_the_other_sessions_and_keeps_this_one` (current session adopts the new stamp, others `PhoneNumberChanged`), `A_replacement_ends_pending_sign_in_codes`, `A_sign_in_code_does_not_confirm` (only a `Verify` code is looked up), audit details carry `revokedSessionCount`. Remove: `Removal_ends_the_other_sessions_and_pending_codes`, `Removal_without_a_number_ends_no_session`. Publisher: `Maps_kind_and_last_digits_only`. Integration: `First_number_needs_a_step_up` (a sign-in older than `Auth:StepUp:MaxAge` → 403 `auth.step_up_required`), `Replacing_the_number_signs_out_the_other_sessions` (their refresh is 401 `auth.invalid_refresh_token`), `Each_change_publishes_one_event` (add, replace, remove → three events, `Added`, `Replaced`, `Removed`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Auth.Phone"` — Expected: FAIL.
- [ ] **Step 3: Implement** spec 5.5: one save per handler (the user, the sessions, the codes, the audit row, the outbox row).
- [ ] **Step 4: Run** unit and architecture suites; build integration — Expected: PASS.
- [ ] **Step 5: Index and commit** — `feat(auth): guard the phone number like a sign-in method`.

### Task 4: Sending a sign-in code

**Files:**
- Create: `.../Application/PhoneSignIn/Send/SendPhoneSignInCodeCommand.cs`, `SendPhoneSignInCodeCommandHandler.cs`, `SendPhoneSignInCodeCommandValidator.cs`; `.../Application/PhoneSignIn/PhoneSignInGate.cs`; `.../Application/Phone/PhoneCodeBudget.cs`; `.../Application/IntegrationEvents/PublishPhoneSignInCodeIssuedDomainEventHandler.cs`; `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/IntegrationEvents/PhoneSignInCodeRequestedIntegrationEvent.cs`; `.../Endpoints/PhoneSignInEndpoints.cs`
- Modify: `.../Application/Phone/PhoneOptions.cs`, `.../Application/Phone/RequestCode/RequestPhoneCodeCommandHandler.cs` (use `PhoneCodeBudget`), `.../Endpoints/PhoneRequests.cs`, the audit event constants, the module's metrics class (beside `auth.external_logins`), `AuthModule.cs` (map the endpoints beside `PhoneEndpoints`)
- Test: `tests/TemplateName.UnitTests/Auth/PhoneSignIn/SendPhoneSignInCodeCommandHandlerTests.cs`, `PhoneSignInGateTests.cs`, `tests/TemplateName.UnitTests/Auth/Phone/PhoneOptionsTests.cs`, `tests/TemplateName.UnitTests/Auth/IntegrationEvents/PhoneSignInCodeRequestedPublisherTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3; `PhoneNumberRules.TryNormalize`, `IRoleRepository.GetByIdsAsync`, `Role.NormalizeName`, `IMfaMethodRepository.HasConfirmedAsync`.
- Produces:
  - `PhoneOptions`: `SignInEnabled = false`; `SignInCodeLifetime = 5 min` (`[Range]` 1–10 min); `SignInDeniedRoles : string[] = ["SuperAdmin", "Admin"]`; `Validate` adds: `SignInEnabled` without `Enabled` is an error naming both keys; a null or blank role entry is an error.
  - `PhoneCodeBudget.CheckAsync(Guid userId, PhoneOptions settings, DateTimeOffset now, CancellationToken) : Task<PhoneCodeBudgetRefusal?>` with `record PhoneCodeBudgetRefusal(TimeSpan RetryAfter, string Reason)` (`cooldown` or `daily_cap`); the rules move unchanged from the request handler, which keeps answering 429 from `RetryAfter`.
  - `PhoneSignInGate.IsDeniedAsync(User user, CancellationToken) : Task<bool>` — true when one of the user's roles' normalized names is in the normalized `SignInDeniedRoles` and the user has no confirmed MFA method.
  - `SendPhoneSignInCodeCommand(string PhoneNumber) : ICommand` (answers success for every path after parsing; the endpoint maps success to 202).
  - `PhoneSignInCodeRequestedIntegrationEvent(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string PhoneNumber, string ProtectedCode, DateTimeOffset ExpiresAt)`, `PrintMembers` prints `Id`, `UserId`, `ExpiresAt`.
  - Route `POST phone/sign-in/send`: `.AllowAnonymous()`, `RateLimitPolicies.AuthStrict`, an endpoint filter answering 404 while `SignInEnabled` is off (the `NotFoundWhileDisabledAsync` pattern of `PhoneEndpoints`), `Produces(202)`, `ProducesProblem(400, 404)`.

- [ ] **Step 1: Write the failing tests.** Handler (spec 5.2): `Unknown_number_saves_nothing`, `Suspended_is_silent_and_audited_inactive`, `Locked_is_silent_and_audited_locked`, `Denied_role_is_silent_and_audited_role_denied`, `Cooldown_is_silent_and_audited_cooldown`, `Daily_cap_is_silent_and_audited_daily_cap`, `Issues_a_sign_in_code_and_ends_pending_sign_in_codes_only`, `Sets_PhoneCodeRequestedAt`, `A_lost_row_version_race_is_still_success` (the save throws `DbUpdateConcurrencyException` → success, nothing else), `Formatting_is_normalized_at_send`, `An_invalid_number_is_invalid_phone_number`; each silent path asserts no code added and no event raised. Gate: `Admin_without_mfa_is_denied`, `Admin_with_mfa_is_not_denied`, `User_role_is_not_denied`, `Denied_roles_compare_normalized_names` (`" admin "` denies `Admin`), `An_empty_list_denies_nobody`. Options: `Sign_in_without_phone_enabled_is_refused`, `A_blank_role_is_refused`, `Lifetime_bounds` (59 s and 10 min 1 s refused). Publisher: `Copies_the_protected_code_and_skips_a_code_no_longer_pending`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Auth.PhoneSignIn|FullyQualifiedName~Auth.Phone"` — Expected: FAIL.
- [ ] **Step 3: Implement**; a known user's audit row and the metric per outcome; nothing about an unknown number is written.
- [ ] **Step 4: Run** unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Index and commit** — `feat(auth): send a phone sign-in code`.

### Task 5: Verifying a sign-in code

**Files:**
- Create: `.../Application/PhoneSignIn/Verify/VerifyPhoneSignInCommand.cs`, `VerifyPhoneSignInCommandHandler.cs`, `VerifyPhoneSignInCommandValidator.cs`
- Modify: `.../Endpoints/PhoneSignInEndpoints.cs`, `PhoneRequests.cs`, `.../Application/Mfa/Verify/VerifyMfaCommandHandler.cs` (remember a device only when `FirstFactor` is `pwd`)
- Test: `tests/TemplateName.UnitTests/Auth/PhoneSignIn/VerifyPhoneSignInCommandHandlerTests.cs`, `tests/TemplateName.UnitTests/Auth/Mfa/VerifyMfaCommandHandlerTests.cs` (addition)

**Interfaces:**
- Consumes: Tasks 1 to 4; `SessionStarter.StartAsync(User, string authMethods, string deviceName, bool viaTrustedDevice, DateTimeOffset, CancellationToken)`; `LoginResult.Signed`, `LoginResult.Challenge`; `MfaChallenge.Issue(..., firstFactor: AuthenticationMethods.Sms, ...)` with the token from `ISecureTokenService` as `LoginCommandHandler` issues one; `IUserRepository.RecordFailedSignInAsync` with `LockoutOptions`; `IUnitOfWork.TrySaveChangesAsync`.
- Produces: `VerifyPhoneSignInCommand(string PhoneNumber, string Code, string? DeviceName) : ICommand<LoginResult>`; validator: `code` exactly 6 ASCII digits, `deviceName` at most `UserSession.MaxDeviceNameLength`, `phoneNumber` not blank and at most 32. Route `POST phone/sign-in/verify`: `.AllowAnonymous()`, `AuthStrict`, `.WithNoStore()`, the 404 filter, `Produces<LoginResult>(200)`, `ProducesProblem(400, 403, 404)`.

- [ ] **Step 1: Write the failing tests.** Handler (spec 5.3): `Right_code_signs_in_with_amr_sms` (session `AuthMethods` `"sms"`, failures reset, `auth.login_succeeded` with `{"method":"sms"}`), `Mfa_account_gets_a_challenge_with_first_factor_sms` (failure count not reset), `Wrong_code_counts_on_the_code_and_on_the_account`, `The_failure_that_locks_raises_the_lockout_event`, `No_pending_code_counts_nothing` (and `SpendMatchCost` ran), `Unknown_number_is_invalid_code`, `Invalid_number_is_invalid_code_not_invalid_phone_number`, `Locked_account_is_invalid_code_and_spends_nothing`, `Suspended_after_the_right_code_is_account_inactive`, `Denied_role_after_the_right_code_is_invalid_code_and_the_code_is_spent`, `A_code_for_an_old_number_is_invalid_code`, `A_lost_consume_is_invalid_code`, `A_lost_session_save_is_invalid_code`, `A_verification_code_does_not_sign_in`, `Formatting_is_normalized_at_verify`, `Unconfirmed_email_still_signs_in`, `No_trusted_device_token_is_read`. Every refusal returns the identical `Error` (`PhoneErrors.InvalidCode`). MFA verify: `Remember_device_is_ignored_for_an_sms_challenge`, `An_sms_challenge_verifies_to_sms_otp_mfa`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Auth.PhoneSignIn|FullyQualifiedName~Auth.Mfa"` — Expected: FAIL.
- [ ] **Step 3: Implement** in the order of spec 5.3; saves after a claim run with `CancellationToken.None`.
- [ ] **Step 4: Run** unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Index and commit** — `feat(auth): sign in with a phone code`.

### Task 6: Notifications — the sign-in text and the change email

**Files:**
- Modify: `src/Modules/Notifications/TemplateName.Modules.Notifications/Application/Catalog/AuthNotificationTypes.cs` (`PhoneSignIn = "auth.phone_sign_in"`, `PhoneNumberChanged = "auth.phone_number_changed"`), `AuthNotificationTypeSource.cs`
- Create: `.../Application/AuthEvents/PhoneSignInCodeRequestedIntegrationEventHandler.cs`, `PhoneNumberChangedIntegrationEventHandler.cs`; `Templates/Sms/auth.phone_sign_in.{en,ms,zh-Hans}.text.scriban`; `Templates/Email/auth.phone_number_changed.{en,ms,zh-Hans}.{subject,text,html}.scriban`
- Test: `tests/TemplateName.UnitTests/Notifications/AuthNotificationRenderTests.cs`, `AuthNotificationTypesTests.cs`, the Auth event handler tests (additions)

**Interfaces:**
- Consumes: Task 3 and 4 Contracts events.
- Produces: `auth.phone_sign_in` registered like `auth.phone_verification` (SMS only, mandatory, Security, Critical; variable `expires_in_minutes`; secret `code` of kind `Code`; `SmsOverride` = the event's number). `auth.phone_number_changed` registered like `auth.mfa_changed` (email only, mandatory, Security, Critical) with variables `display_name`, `kind` (`added`, `replaced`, `removed`), `last_digits`, `occurred_at`, `time_zone`, filled as `ExternalLoginLinkedIntegrationEventHandler` fills its own.

- [ ] **Step 1: Write the failing tests.** Render: `Phone_sign_in_fits_one_segment_in_every_culture` (the existing segment check), `Phone_number_changed_renders_each_kind_in_every_culture` (the subject and text name the kind and `***789`; no full number). Types: both registered with the stated channel, category, priority and mandatory flag. Handlers: `Sign_in_code_is_scheduled_to_the_events_number_with_the_protected_code_unchanged`, `Change_maps_the_kind_and_last_digits`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~Notifications"` — Expected: FAIL.
- [ ] **Step 3: Implement**; the `en` SMS text: `{{ app_name }}: your sign-in code is {{ code }}. It expires in {{ expires_in_minutes }} minutes. Never share it.` (keep the `ms` and `zh-Hans` drafts inside one segment); the email per kind: added / changed / removed, the time in the user's zone, then "If this wasn't you, reset your password and contact your administrator."
- [ ] **Step 4: Run** the Notifications filter, unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): text sign-in codes and email number changes`.

### Task 7: Flow, documentation and verification

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Auth/PhoneSignInTests.cs`, `docs/adr/0041-phone-number-sign-in.md`
- Modify: `docs/modules/auth.md` (new "Phone-number sign-in" section after "Phone number"; Endpoints, Step-up, Challenge and verify (`sms` first factor), Audit events, Configuration, Observability, Known limits, Testing), `docs/modules/notifications.md` (Auth's types), `docs/deployment/production-checklist.md` (`SignInEnabled` needs the Twilio pumping and geo controls first), `docs/blueprint-review.md` (row 128: done when merged), `docs/README.md` and `docs/architecture/overview.md` (ADR 0041), the OpenAPI snapshot
- Test: the new integration file

**Interfaces:**
- Consumes: everything above; spec 2's test helpers (`EnabledHost`, the code read from the outbox's `ProtectedCode`) moved to a shared helper if both files need them.

- [ ] **Step 1: Write the integration tests** (a host with `Auth:Phone:Enabled`, `Auth:Phone:SignInEnabled` on): `Send_and_verify_sign_in_with_amr_sms` (the access token's `amr` is `sms`, `GET /me` works), `Known_and_unknown_numbers_get_identical_202s` (status, headers, body), `Every_verify_refusal_is_one_body` (`ProblemResponseComparison`: unknown number, wrong code, expired code, locked, denied role), `Totp_account_gets_a_challenge_then_sms_otp_mfa`, `An_sms_session_needs_a_step_up_for_totp_setup` (403, then a password step-up passes), `Admin_without_totp_is_denied_and_with_totp_challenged`, `Five_wrong_codes_lock_the_account` (then the right password is the generic 401), `Two_verifies_with_one_code_give_one_session`, `Two_sends_at_once_issue_one_code`, `A_number_change_kills_the_pending_code`, `Verify_is_no_store`, `Send_and_verify_are_rate_limited`, `Routes_are_404_while_sign_in_is_off`, `Neither_number_nor_code_reaches_a_column_or_a_log` (scan every text column and log line, as `MfaEnrollmentTests` does).
- [ ] **Step 2: Build; run** the unit and architecture suites — Expected: PASS; the integration project compiles with 0 warnings (CI runs it).
- [ ] **Step 3: Write the docs** (spec 6), ADR 0041 (decisions 1 to 7, the accepted limits of spec 8, the compatibility note of spec 9). Refresh the OpenAPI snapshot from CI's `received-snapshots` artifact if CI reports a difference.
- [ ] **Step 4: Verify** `dotnet build -c Release`, `dotnet format --verify-no-changes`, `bash build/scripts/template-smoke.sh`, then `--notifications false`, `--includeSample false`, `--caching memory`; `git add` and `bash build/scripts/project-index.sh` — Expected: all succeed, the index committed.
- [ ] **Step 5: Commit** — `docs(auth): document phone-number sign-in` (and `test(auth): ...` for the flow file if kept separate).
