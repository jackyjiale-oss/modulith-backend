# Push Notifications Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver notifications to a user's signed-in devices by FCM push, as a third channel of the Notifications module, off by default.

**Architecture:** A `PushDevice` per registered FCM token, bound to the session that registered it; the scheduler creates one push `Delivery` per device whose session Auth still reports active (`IUserSessionDirectory`); the delivery worker hands each one to `PushChannel`, which re-checks the device and session and sends through `IPushTransport` (`FcmPushTransport`, an `HttpClient` on the `push-provider` resilience pipeline with a `Google.Apis.Auth` token).

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server), Dapper, Scriban, `Microsoft.Extensions.Http.Resilience`, `Google.Apis.Auth`, xUnit v3, Shouldly, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-23-push-channel-design.md` (approved 2026-11-23). Read it with this plan; section numbers below refer to it.

## Global Constraints

- `TemplateName` is the placeholder; never a concrete project name.
- Every new type is `internal` (the architecture tests allow no other public type than the module entry point); a Contracts type is `public`.
- No `DateTime.Now` / `DateTime.UtcNow`: inject `TimeProvider`.
- Enums stored as `tinyint`; values never renumbered: `NotificationChannel.Push = 3`; `PushPlatform` `Android = 1`, `Ios = 2`, `Web = 3`; `PushDeviceDeactivationReason` `RemovedByUser = 1`, `SessionEnded = 2`, `RejectedByProvider = 3`, `Replaced = 4`.
- The FCM token is never logged, returned by an endpoint, put in an exception message or a metric tag; records holding it override `PrintMembers`.
- Logs name delivery ids, device ids, user ids and type codes only; never a token, title, body or address.
- Reason codes match `^[a-z][a-z0-9_]*\z`, at most 50 characters (`ChannelSendResult`).
- Every new error code exists in `en`, `ms` and `zh-Hans` (`NotificationsErrorMessages*.resx`).
- New package only through `Directory.Packages.props`, with an allowed licence (`build/licenses/allowed-licenses.json`: MIT, Apache-2.0, BSD-2/3).
- Never `--no-verify`. Commits are Conventional Commits; the squash title is `feat: add the push notifications channel`.
- After a marked region, route, error code, configuration key, job, test file or source file with a paired test changes: `git add` new files, then `bash build/scripts/project-index.sh`, and commit the result.
- Integration tests run in CI only (no Docker locally); they must compile locally.

## Deviations from the spec (decided while planning)

1. **`IUserSessionDirectory` is not in a switch marker** (spec 5.6 said it would be). The existing `IUserContactDirectory`, also used only by Notifications, is unmarked in `Auth.Contracts` and `AuthModule`; following that precedent keeps markers out of a Contracts file. With `--notifications false` the interface and its implementation stay, unused, as `IUserContactDirectory` does.
2. **`PushChannel` refuses a device that changed hands**: a delivery whose device now belongs to another user (the token was re-registered by them, spec 5.1 step 2) fails permanent `device_reassigned`, so a queued alert never reaches the new owner. Not in the spec's 5.3 table; added there in Task 8.
3. **The integration flow uses a recording `IPushTransport`** (as the email flow uses `RecordingEmailSender`), not a fake FCM handler; `FcmPushTransport` itself is covered by unit tests against a fake `HttpMessageHandler`.
4. **`PushOptions` (application) and `FcmOptions` (infrastructure) are two option classes**: the scheduler and preferences need `Enabled` without referencing infrastructure, as `HubOptions` sits in `Application/HubTickets/`.

## Review Focus

1. **A phone that changes hands** (user A registers a token, user B registers the same token): A's queued push must not reach B. Pinned in Task 7 (`A_device_registered_to_another_user_since_is_refused`).
2. **FCM answers with a body that is not JSON** (an HTML 502 from a proxy): the transport must fall back to the status mapping, not throw a parse error. Pinned in Task 7 (`A_non_json_error_body_falls_back_to_the_status`).
3. **The new device is the user's only device**: `auth.new_device_login` creates no push delivery but still sends the email. Pinned in Task 6 (`Excluding_the_only_device_keeps_the_email`).
4. **Sign out, sign in again, re-register the same token**: the device moves to the new session and receives the next alert. Pinned in Task 4 (`Re_registering_in_a_new_session_moves_the_device`) and Task 8 (flow).
5. **A token with control characters or white space**: refused with `validation.failed`, never stored. Pinned in Task 4 (`Token_with_control_characters_is_refused`).

---

### Task 1: Auth's session directory

**Files:**
- Create: `src/Modules/Auth/TemplateName.Modules.Auth.Contracts/Users/IUserSessionDirectory.cs`
- Create: `src/Modules/Auth/TemplateName.Modules.Auth/Infrastructure/Contracts/UserSessionDirectory.cs`
- Modify: `src/Modules/Auth/TemplateName.Modules.Auth/AuthModule.cs:84` (register next to `IUserContactDirectory`)
- Modify: `docs/modules/auth.md` (the contracts list: one paragraph for the new query)
- Test: `tests/TemplateName.IntegrationTests/Auth/UserSessionDirectoryTests.cs`

**Interfaces:**
- Produces: `public interface IUserSessionDirectory { Task<IReadOnlySet<Guid>> GetActiveSessionIdsAsync(Guid userId, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken); }` — the subset of `sessionIds` that belong to `userId`, are not revoked and are before their `ExpiresAt` (the rule of `UserSession.IsActive(now)`). An empty input answers an empty set without a query.

- [ ] **Step 1: Write the failing integration tests** (`UserSessionDirectoryTests`, patterned on `UserContactDirectoryTests`): `Active_sessions_are_returned` (two signed-in sessions → both ids), `A_logged_out_session_is_not_returned` (`POST /api/v1/auth/logout` on one → only the other), `An_expired_session_is_not_returned` (advance the factory's `FakeTimeProvider` past the absolute lifetime), `Another_users_session_is_not_returned`, `Empty_input_answers_empty`.
- [ ] **Step 2: Build the test project** — Run: `dotnet build tests/TemplateName.IntegrationTests -c Release` — Expected: FAIL, `IUserSessionDirectory` not found.
- [ ] **Step 3: Implement `UserSessionDirectory(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)`** with Dapper: `SELECT Id FROM auth.UserSessions WHERE UserId = @UserId AND Id IN @SessionIds AND RevokedAt IS NULL AND ExpiresAt > @Now;` (`@Now` from `timeProvider.GetUtcNow()`). Register `services.AddScoped<IUserSessionDirectory, UserSessionDirectory>();`.
- [ ] **Step 4: Build again** — Expected: 0 warnings, 0 errors (the tests run in CI).
- [ ] **Step 5: Commit** — `git add` the files; `git commit -m "feat(auth): add the session directory for other modules"`.

### Task 2: Domain — the push channel, destinations and devices

**Files:**
- Modify: `src/Modules/Notifications/TemplateName.Modules.Notifications/Domain/Notifications/NotificationChannel.cs` (`Push = 3`)
- Create: `.../Domain/Deliveries/PushDestination.cs`
- Modify: `.../Domain/Notifications/Notification.cs:94-110` (`AddDelivery`)
- Create: `.../Domain/PushDevices/PushDevice.cs`, `PushPlatform.cs`, `PushDeviceDeactivationReason.cs`, `PushDeviceErrors.cs`
- Test: `tests/TemplateName.UnitTests/Notifications/PushDeviceTests.cs`, `tests/TemplateName.UnitTests/Notifications/NotificationTests.cs` (additions)

(`...` = `src/Modules/Notifications/TemplateName.Modules.Notifications` in every task.)

**Interfaces:**
- Produces:
  - `internal static class PushDestination { public static bool IsValid([NotNullWhen(true)] string? destination); public static string For(Guid deviceId); public static Guid DeviceIdOf(string destination); }` — a destination is a device id in `"D"` format.
  - `Notification.AddDelivery(NotificationChannel channel, string? destination, DateTimeOffset firstAttemptAt, DateTimeOffset now)` — unchanged signature; at most one delivery per `(channel, destination)`; a `Push` destination must pass `PushDestination.IsValid`, else `ArgumentException` (no value quoted).
  - `internal sealed class PushDevice` with `Id, UserId, SessionId, Platform, TokenHash (byte[32]), Token, DeviceName?, AppVersion?, CreatedAt, LastRegisteredAt, DeactivatedAt?, DeactivationReason?`, `bool IsActive`, `const int MaxTokenLength = 4096, MaxDeviceNameLength = 100, MaxAppVersionLength = 50`;
    `static PushDevice Register(Guid userId, Guid sessionId, PushPlatform platform, string token, string? deviceName, string? appVersion, DateTimeOffset now)` (new `SequentialGuid` id, hash = `HashOf(token)`);
    `void Reregister(Guid userId, Guid sessionId, PushPlatform platform, string? deviceName, string? appVersion, DateTimeOffset now)` (moves owner and session, reactivates);
    `void Deactivate(PushDeviceDeactivationReason reason, DateTimeOffset now)` (idempotent: keeps the first time and reason);
    `static byte[] HashOf(string token)` (`SHA256.HashData` of the UTF-8 bytes).
  - `PushDeviceErrors.NotFound(Guid id)` → `Error.NotFound("notifications.device_not_found", $"Device '{id}' was not found.")` with parameter `id`.

- [ ] **Step 1: Write the failing tests.** `PushDeviceTests`: `Register_sets_the_hash_of_the_token`, `Register_cuts_name_and_version` (101-character name → 100, via `BoundedText.Cut`), `Reregister_moves_the_device_and_reactivates_it`, `Deactivate_keeps_the_first_reason`, `ToString_never_contains_the_token`. `NotificationTests` additions: `Two_push_deliveries_for_different_devices_are_allowed`, `The_same_device_twice_is_refused` (`InvalidOperationException`), `A_push_destination_that_is_not_a_guid_is_refused` (`ArgumentException`, message without the value), `A_second_in_app_delivery_is_still_refused`.
- [ ] **Step 2: Run** — `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~PushDeviceTests|FullyQualifiedName~NotificationTests"` — Expected: build FAIL (types missing).
- [ ] **Step 3: Implement** the types above; `PushDevice` overrides `ToString` to print `Id`, `UserId`, `Platform`, `IsActive` only.
- [ ] **Step 4: Run** the same filter — Expected: PASS; then the whole unit suite — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(notifications): add push devices and per-device deliveries to the domain`.

### Task 3: Persistence, repository and cleanup

**Files:**
- Create: `.../Infrastructure/Persistence/PushDeviceConfiguration.cs`, `.../Infrastructure/Persistence/PushDeviceRepository.cs`
- Create: `.../Application/Abstractions/IPushDeviceRepository.cs`
- Modify: `.../Infrastructure/Persistence/NotificationsDbContext.cs` (`DbSet<PushDevice> PushDevices`)
- Create: migration `AddPushDevices` (command in `CLAUDE.md`, `--context NotificationsDbContext`)
- Create: `.../Infrastructure/Retention/PushDevicesCleanupJob.cs`
- Modify: `.../Infrastructure/Retention/NotificationsRetentionOptions.cs` (`PushDevices`, default `TimeSpan.FromDays(30)`, in the negative check)
- Modify: `.../NotificationsModule.cs` (repository, `AddRecurringJob<PushDevicesCleanupJob>()`)
- Test: `tests/TemplateName.IntegrationTests/Notifications/PushDevicePersistenceTests.cs`, `tests/TemplateName.UnitTests/Notifications/NotificationsRetentionOptionsTests.cs` (addition)

**Interfaces:**
- Consumes: `PushDevice` (Task 2).
- Produces: `internal interface IPushDeviceRepository { Task<PushDevice?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken ct); Task<PushDevice?> GetAsync(Guid id, CancellationToken ct); Task<IReadOnlyList<PushDevice>> ListActiveAsync(Guid userId, CancellationToken ct); void Add(PushDevice device); }` — all tracked (saved through `IUnitOfWork`); `ListActiveAsync` orders by `LastRegisteredAt DESC`.
- Table `notify.PushDevices`: `Id` (PK, `ValueGeneratedNever`), `UserId`, `SessionId`, `Platform tinyint`, `TokenHash varbinary(32)`, `Token nvarchar(4096)`, `DeviceName nvarchar(100) NULL`, `AppVersion nvarchar(50) NULL`, `CreatedAt`, `LastRegisteredAt`, `DeactivatedAt NULL`, `DeactivationReason tinyint NULL` (timestamps `datetime2(3)` by the conventions). Indexes: `IX_PushDevices_TokenHash` (unique), `IX_PushDevices_UserId` (filtered `[DeactivatedAt] IS NULL`), `IX_PushDevices_DeactivatedAt`.
- Job: `JobName = "NotificationsPushDevicesCleanup"`, `DefaultInterval = TimeSpan.FromDays(1)`, `DELETE TOP (@Batch) FROM {table} WHERE DeactivatedAt < @Cutoff;` (the `HubTicketsCleanupJob` pattern).

- [ ] **Step 1: Write the failing tests.** Persistence: `A_device_round_trips` (every column, `Utc` kinds), `The_token_hash_is_unique` (`DbUpdateException` on a second row with the same hash), `ListActive_skips_deactivated_devices_newest_first`, `Cleanup_deletes_only_devices_deactivated_before_the_cutoff`. Options: `Negative_push_devices_retention_is_refused`.
- [ ] **Step 2: Build** — `dotnet build -c Release` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the configuration, repository, job and option; add the migration; check the generated migration creates only the table and its three indexes.
- [ ] **Step 4: Build and run unit tests** — Expected: 0 warnings; unit PASS.
- [ ] **Step 5: Commit** — `feat(notifications): store push devices and clean up deactivated ones`.

### Task 4: Push options and the device API

**Files:**
- Create: `.../Application/Devices/PushOptions.cs`
- Create: `.../Application/Devices/Register/RegisterDeviceCommand.cs`, `RegisterDeviceCommandHandler.cs`, `RegisterDeviceCommandValidator.cs`, `DeviceResponse.cs`
- Create: `.../Application/Devices/List/ListDevicesQuery.cs`, `ListDevicesQueryHandler.cs`, `DevicesResponse.cs`
- Create: `.../Application/Devices/Unregister/UnregisterDeviceCommand.cs`, `UnregisterDeviceCommandHandler.cs`
- Create: `.../Endpoints/DeviceRequests.cs`, `.../Endpoints/PushDeviceRateLimit.cs`
- Modify: `.../Endpoints/NotificationEndpoints.cs` (three routes), `.../NotificationsModule.cs` (options, rate limit policy)
- Modify: `.../Resources/NotificationsErrorMessages.resx`, `.ms.resx`, `.zh-Hans.resx` (`notifications.device_not_found`, param `{id}`)
- Test: `tests/TemplateName.UnitTests/Notifications/Devices/RegisterDeviceCommandHandlerTests.cs`, `RegisterDeviceCommandValidatorTests.cs`, `UnregisterDeviceCommandHandlerTests.cs`, `PushOptionsTests.cs`; `tests/TemplateName.IntegrationTests/Notifications/PushDeviceEndpointsTests.cs`

**Interfaces:**
- Consumes: `IPushDeviceRepository`, `PushDevice`, `PushDeviceErrors` (Tasks 2-3); `ICurrentUser.UserId`, `ICurrentUser.SessionId`.
- Produces:
  - `internal sealed class PushOptions { const string SectionName = "Notifications:Push"; bool Enabled = false; string Provider = "fcm"; [Range(1,100)] int MaxDevicesPerUser = 10; [Range(1,1000)] int RegistrationsPerMinute = 10; }`, `IValidatableObject`: `Provider` other than `fcm` (ordinal, case-insensitive) → a result naming `Notifications:Push:Provider` without the value.
  - `RegisterDeviceCommand(PushPlatform Platform, string Token, string? DeviceName, string? AppVersion) : ICommand<DeviceResponse>` (`PrintMembers` without the token); `DeviceResponse(Guid Id, PushPlatform Platform, string? DeviceName, string? AppVersion, DateTimeOffset RegisteredAt)` (no token).
  - `ListDevicesQuery : IQuery<DevicesResponse>`; `DevicesResponse(IReadOnlyList<DeviceResponse> Items)`.
  - `UnregisterDeviceCommand(Guid Id) : ICommand`.
  - Routes in `NotificationEndpoints` (same group, `.RequireAuthorization()`): `POST devices` (`RequireRateLimiting(PushDeviceRateLimit.PolicyName)`, policy `"notifications-push-devices"`, per user, `RegistrationsPerMinute` per one-minute window, as `HubTicketRateLimit`), `GET devices`, `DELETE devices/{id:guid}`.

- [ ] **Step 1: Write the failing unit tests.** Validator: `Blank_token_is_refused`, `Token_longer_than_4096_is_refused`, `Token_with_control_characters_is_refused` (refused when any character is `char.IsControl` or `char.IsWhiteSpace`; cases `"abc"+(char)0` and `"a b"`), `Undefined_platform_is_refused`, `Device_name_over_100_is_refused`, `App_version_over_50_is_refused`. Register handler (NSubstitute repository, `FakeTimeProvider`): `A_new_token_adds_a_device_bound_to_the_session`, `A_known_token_moves_to_the_caller` (another user's device → owner and session change), `Re_registering_in_a_new_session_moves_the_device`, `Registering_beyond_the_cap_deactivates_the_oldest_with_Replaced` (cap 2, three devices), `A_duplicate_key_on_save_retries_once` and `A_second_duplicate_answers_409_concurrency_conflict` (the unit of work's `SaveChangesUnlessConflictAsync` returning false), `Without_a_session_the_caller_is_unauthenticated` (the existing `UnauthenticatedCaller` error). Unregister: `Another_users_device_is_not_found`, `An_inactive_device_answers_success`, `Deactivates_with_RemovedByUser`. `PushOptionsTests`: `Unknown_provider_is_refused_without_its_value`, `Defaults_are_off_fcm_10_and_10`.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~Notifications.Devices"` — Expected: build FAIL.
- [ ] **Step 3: Implement** the options, commands, handlers, validator, routes, rate limit and the three resx entries (`ms`: `Peranti '{id}' tidak ditemui.`; `zh-Hans`: `未找到设备“{id}”。`). The register handler follows spec 5.1 steps 1-4; reuse the preferences' conflict pattern (`SaveChangesUnlessConflictAsync`) for step 4.
- [ ] **Step 4: Run** the filter, then the whole unit and architecture suites — Expected: PASS.
- [ ] **Step 5: Write the integration tests** (`PushDeviceEndpointsTests`): `Register_returns_the_device_without_the_token` (the response body has no `token` property), `Register_twice_keeps_one_device`, `List_returns_active_devices_newest_first`, `Delete_of_another_users_device_is_404_device_not_found`, `Register_beyond_the_limit_is_429` (set `RegistrationsPerMinute = 1` in the test's configuration), `Routes_require_a_token` (401). Build: 0 warnings.
- [ ] **Step 6: Regenerate the index** (`git add` new files; `bash build/scripts/project-index.sh`) and **commit** — `feat(notifications): add the push device registration endpoints`.

### Task 5: Catalog, templates and rendering

**Files:**
- Modify: `.../Application/Catalog/AuthNotificationTypeSource.cs` (channel arrays)
- Modify: `.../Infrastructure/Templates/EmbeddedTemplateStore.cs:32-37` (`PartsOf(Push)` = `title`, `body`)
- Modify: `.../Infrastructure/Templates/ScribanNotificationRenderer.cs:49-55` (`Push` renders like `InApp`: single-line `title`, then `body`)
- Create: 18 files `.../Templates/Push/{type}.{culture}.{title|body}.scriban` for `auth.new_device_login`, `auth.token_reuse_detected`, `auth.account_locked` × `en`, `ms`, `zh-Hans`
- Test: the existing `TemplateCompletenessChecker` tests (they pick the new channel up), the catalog tests (additions), `ScribanNotificationRendererTests` (addition)

**Interfaces:**
- Produces: `NotificationChannel.Push` in `DefaultChannels` (not mandatory) of the three types; `INotificationRenderer.Render(typeCode, NotificationChannel.Push, culture, variables)` returns `RenderedMessage(Subject: title, TextBody: body, HtmlBody: null)`.
- Template copy (en; `ms` and `zh-Hans` are faithful drafts): `auth.new_device_login` title `New sign-in to {{ product_name }}`, body `{{ display_name }}, your account was signed in on a new device at {{ occurred_at }} ({{ time_zone }}). If this wasn't you, change your password.` — **no `ip_address`** (lock screen, spec Risks); `auth.token_reuse_detected` title `Security alert`, body `{{ display_name }}, a signed-out session of your {{ product_name }} account was used again at {{ occurred_at }} ({{ time_zone }}). We signed it out everywhere.`; `auth.account_locked` title `Account locked`, body `{{ display_name }}, your {{ product_name }} account is locked after failed sign-in attempts until {{ locked_until }} ({{ time_zone }}).`. Check each against the email template of the same type so the facts agree; adjust wording, not facts.

- [ ] **Step 1: Write the failing tests:** `The_three_alert_types_offer_optional_push` (defaults include `Push`, `IsMandatory(Push)` false, email still mandatory), `Other_auth_types_have_no_push`, renderer `Push_renders_title_and_body`; run the completeness tests too.
- [ ] **Step 2: Run** `dotnet test tests/TemplateName.UnitTests --filter "FullyQualifiedName~Notifications"` — Expected: FAIL (missing channel, then missing templates).
- [ ] **Step 3: Implement** the catalog change, `PartsOf`, renderer branch and the 18 files (confirm the `.csproj` embeds `Templates/**`).
- [ ] **Step 4: Run** the filter — Expected: PASS, completeness included.
- [ ] **Step 5: Commit** — `feat(notifications): add push templates to three auth alerts`.

### Task 6: Scheduling and preferences

**Files:**
- Modify: `.../Application/Scheduling/NotificationRequest.cs` (`Guid? ExcludedSessionId = null` as the last positional parameter; `PrintMembers` prints it)
- Modify: `.../Application/AuthEvents/NewDeviceSignedInIntegrationEventHandler.cs` (`AuthEventRequests.Notice(...) with { ExcludedSessionId = integrationEvent.SessionId }`)
- Modify: `.../Application/Scheduling/NotificationScheduler.cs` (constructor gains `IPushDeviceRepository pushDevices`, `IUserSessionDirectory sessions`, `IOptions<PushOptions> pushOptions`; `DeliveriesAsync`; `EmailDueAtAsync` becomes the due time of email and push)
- Modify: `.../Application/Preferences/PreferencesView.cs` (`Build(catalog, stored, bool pushEnabled)`) and its callers; `.../Application/Preferences/Update/UpdatePreferencesCommandHandler.cs` (`Push` while disabled → `PreferenceErrors.ChannelNotSupported`)
- Test: `tests/TemplateName.UnitTests/Notifications/NotificationSchedulerTests.cs`, `NotificationSchedulerHarness.cs`, `tests/TemplateName.UnitTests/Notifications/Preferences/*` (additions)

**Interfaces:**
- Consumes: `IPushDeviceRepository.ListActiveAsync`, `PushDevice.Deactivate`, `PushDestination.For` (Tasks 2-3); `IUserSessionDirectory` (Task 1); `PushOptions.Enabled` (Task 4).
- Produces: the scheduler behaviour of spec 5.2: push dropped when disabled or switched off; skip `ExcludedSessionId`; deactivate devices of inactive sessions with `SessionEnded` (staged, saved with the inbox record); one `(Push, PushDestination.For(device.Id))` per remaining device; the push due time follows email's rule (non-`Critical` deferred by quiet hours, settings read once). `NothingToDeliver` only when no channel is left. `PreferencesView` omits `Push` when `pushEnabled` is false.

- [ ] **Step 1: Write the failing tests:** `Push_is_dropped_while_disabled`, `Push_is_dropped_when_switched_off`, `One_push_delivery_per_active_device`, `The_excluded_session_gets_no_push`, `Excluding_the_only_device_keeps_the_email`, `Devices_of_ended_sessions_are_deactivated_in_the_same_save`, `No_device_keeps_the_other_channels`, `A_non_critical_push_waits_for_quiet_hours` (a test type in the harness with `Normal` priority and `Push`), `A_critical_push_is_due_now`; preferences: `Push_is_hidden_while_disabled`, `Push_is_listed_after_in_app_while_enabled`, `Updating_push_while_disabled_is_channel_not_supported`; consumer: `NewDeviceSignedIn_handler_excludes_the_new_session`.
- [ ] **Step 2: Run** the notifications unit filter — Expected: FAIL.
- [ ] **Step 3: Implement.** Read sessions only when there are devices: one `GetActiveSessionIdsAsync` call per schedule with the distinct session ids.
- [ ] **Step 4: Run** the unit and architecture suites — Expected: PASS (existing scheduler tests change only through the harness's new constructor arguments).
- [ ] **Step 5: Commit** — `feat(notifications): schedule push deliveries per signed-in device`.

### Task 7: The push channel and the FCM transport

**Files:**
- Create: `.../Application/Abstractions/IPushTransport.cs`, `PushMessage.cs`, `PushSendException.cs`
- Modify: `.../Application/Abstractions/DeliveryWork.cs` (add `NotificationPriority Priority` after `Category`), `.../Infrastructure/Deliveries/DeliveryDispatcher.cs:299` (pass `notification.Priority`)
- Create: `.../Infrastructure/Channels/Push/PushChannel.cs`, `FcmPushTransport.cs`, `FcmOptions.cs`, `FcmOptionsValidation.cs`, `IFcmAccessTokenSource.cs`, `GoogleFcmAccessTokenSource.cs`
- Modify: `.../NotificationsModule.cs` (`AddPushChannel(services, configuration)`: the options always; channel, transport, token source and `AddExternalHttpClient("push-provider", "push-provider")` only when `Notifications:Push:Enabled` is true)
- Modify: `Directory.Packages.props`, the module `.csproj`, its `packages.lock.json` (`dotnet restore --force-evaluate`)
- Modify: `.../Application/Admin/Deliveries/List/ListDeliveriesQueryHandler.cs:108` (mask the destination only for `Email`; a `Push` destination, a device id, is returned as stored — `DestinationMask` would turn it into `****`)
- Test: `tests/TemplateName.UnitTests/Notifications/PushChannelTests.cs`, `FcmPushTransportTests.cs`, `FcmOptionsTests.cs`; `tests/TemplateName.IntegrationTests/Notifications/` admin delivery list tests (`A_push_destination_is_the_device_id_unmasked`)

**Interfaces:**
- Consumes: `IPushDeviceRepository.GetAsync`, `PushDevice` (Tasks 2-3); `IUserSessionDirectory` (Task 1); `PushOptions` (Task 4).
- Produces:
  - `internal interface IPushTransport { Task SendAsync(PushMessage message, CancellationToken cancellationToken); }` — returns on acceptance, throws `PushSendException` otherwise.
  - `internal sealed record PushMessage(Guid DeliveryId, Guid DeviceId, string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data, bool Urgent)` — `PrintMembers` prints `DeliveryId`, `DeviceId`, `Urgent` only.
  - `internal sealed class PushSendException(bool isPermanent, string reason, bool deactivateDevice) : Exception(reason)` with matching properties.
  - `internal sealed class FcmOptions { const string SectionName = "Notifications:Push:Fcm"; string ProjectId = ""; string CredentialsJson = ""; }`; `FcmOptionsValidation : IValidateOptions<FcmOptions>` (takes `IOptions<PushOptions>`): when push is enabled, a blank `ProjectId` fails naming `Notifications:Push:Fcm:ProjectId`; a non-blank `CredentialsJson` that does not parse as a JSON object with `"type": "service_account"` fails naming `Notifications:Push:Fcm:CredentialsJson`; no value is quoted. Registered with `ValidateOnStart()`.
  - `internal interface IFcmAccessTokenSource { Task<string> GetAccessTokenAsync(CancellationToken cancellationToken); }` — `GoogleFcmAccessTokenSource` builds the credential once (from `CredentialsJson`, else application default credentials), scope `https://www.googleapis.com/auth/firebase.messaging`; any failure becomes `PushSendException(false, "fcm_auth_failed", false)`.
  - `PushChannel.Channel => NotificationChannel.Push`; `SendAsync` per spec 5.3 plus deviation 2, in this order: device missing → `Permanent("device_not_found")`; inactive → `Permanent("device_inactive")`; `device.UserId != work.RecipientUserId` → `Permanent("device_reassigned")`; session inactive → deactivate `SessionEnded`, `Permanent("session_ended")`; else send `PushMessage(work.DeliveryId, device.Id, device.Token, title cut to 200, body cut to 1000, { notificationId, typeCode, category }, Urgent: work.Priority is Critical or High)` → `Sent`; `PushSendException` → `Permanent`/`Transient(reason)`, deactivating with `RejectedByProvider` when `deactivateDevice`.
  - `FcmPushTransport(IHttpClientFactory httpClientFactory, IFcmAccessTokenSource tokens, IOptions<FcmOptions> options, ILogger<FcmPushTransport> logger)`: the request of spec 5.4 to `https://fcm.googleapis.com/v1/projects/{ProjectId}/messages:send` with the bearer token; error mapping by `error.details[].errorCode` (the detail whose `@type` ends with `google.firebase.fcm.v1.FcmError`), else by status, exactly as the spec 5.4 table; `HttpRequestException` → `connection_failed`; a timeout (`TimeoutRejectedException`, or `TaskCanceledException` not from the caller's token) → `timeout`.

- [ ] **Step 1: Write the failing tests.** `PushChannelTests`: one test per row of spec 5.3, plus `A_device_registered_to_another_user_since_is_refused`, `Urgent_follows_priority`, `Title_and_body_are_cut`, `A_provider_rejection_that_deactivates_marks_the_device_RejectedByProvider`, `A_transient_failure_keeps_the_device`. `FcmPushTransportTests` (a fake `HttpMessageHandler` recording the request): `Sends_the_v1_request_with_the_bearer_token` (URL, `Authorization`, the JSON body of spec 5.4 with `HIGH` / `"10"`), `Not_urgent_uses_normal_and_5`, a `[Theory]` row per line of the spec 5.4 table by `errorCode` and by status alone, `A_non_json_error_body_falls_back_to_the_status`, `The_token_never_appears_in_logs_or_exceptions` (a `FakeLogger`; scan every record and the exception message for the token). `FcmOptionsTests`: `Enabled_without_project_id_is_refused`, `Unparsable_credentials_are_refused_without_their_value`, `Disabled_needs_nothing`.
- [ ] **Step 2: Run** the notifications unit filter — Expected: FAIL.
- [ ] **Step 3: Add the package:** `Google.Apis.Auth`, latest stable, in `Directory.Packages.props`; read its licence (Apache-2.0) and the package terms (no usage fee); `dotnet build` passes the licence check (`build/licenses/`). Use the credential API that version recommends (`CredentialFactory` where `GoogleCredential.FromJson` is obsolete), only inside `GoogleFcmAccessTokenSource`.
- [ ] **Step 4: Implement** the types and registration; add the `push-provider` example to `docs/building-blocks/infrastructure-common.md` (`"push-provider": { "AttemptTimeout": "00:00:10", "TotalTimeout": "00:00:30", "Retries": 2 }`).
- [ ] **Step 5: Run** the unit and architecture suites; `dotnet build -c Release` — Expected: PASS, 0 warnings.
- [ ] **Step 6: Commit** — `feat(notifications): send push deliveries through FCM`.

### Task 8: End-to-end flow, documentation and verification

**Files:**
- Create: `tests/TemplateName.IntegrationTests/Infrastructure/RecordingPushTransport.cs`
- Modify: `tests/TemplateName.IntegrationTests/Infrastructure/IntegrationTestWebAppFactory.cs` (`Notifications:Push:Enabled = true`, `Notifications:Push:Fcm:ProjectId = "test-project"`; replace `IPushTransport` with a `PushTransport` property, as `IEmailTransport` is replaced at line 217)
- Create: `tests/TemplateName.IntegrationTests/Notifications/PushFlowTests.cs`
- Modify: `docs/modules/notifications.md`, `docs/blueprint-review.md` (row "Push and SMS channels" split into "Push channel" (this plan) and "Phone number and SMS channel"), `docs/deployment/production-checklist.md` (FCM credentials; the APNs key uploaded to Firebase), `docs/superpowers/specs/2026-11-23-push-channel-design.md` (status: implemented; the `device_reassigned` row in 5.3; deviation 1 in 5.6)
- Create: `docs/adr/0039-session-bound-push-devices.md`
- Modify: the OpenAPI snapshot (`*.verified.*` under the integration tests) from CI's `received-snapshots` artifact

**Interfaces:**
- Consumes: everything above.
- Produces: `RecordingPushTransport : IPushTransport` with `IReadOnlyList<PushMessage> Sent`, `Clear()` and `FailNextWith(PushSendException exception)`.

- [ ] **Step 1: Write the flow tests** (`PushFlowTests`, with `DispatchNotificationsAsync()`): `A_new_sign_in_pushes_to_the_earlier_devices_only` (two sessions each register a token; a third sign-in registers one too → `auth.new_device_login` pushes to the first two tokens and not the third), `After_logout_the_device_gets_no_push_and_is_deactivated`, `Re_registering_after_signing_in_again_receives_the_next_alert`, `An_unregistered_answer_deactivates_the_device` (`FailNextWith(new PushSendException(true, "fcm_unregistered", true))` → delivery dead-lettered, device inactive), `Switching_push_off_in_preferences_stops_it`, `Account_lock_pushes_to_signed_in_devices` (failed sign-ins up to the lockout threshold).
- [ ] **Step 2: Build** — 0 warnings (they run in CI).
- [ ] **Step 3: Write the docs.** `notifications.md`: the three routes in Endpoints; a "Push channel" section (the 5.3 and 5.4 tables with `device_reassigned`, the transport, logs); Domain model (`PushDevice`, the enums, the `AddDelivery` rule); Scheduling steps 6-7; Configuration (the seven keys of spec Section 4); Data (`notify.PushDevices`; migration 4 `AddPushDevices`); Background processing (the new reasons; "at least once for email and push"); Cleanup (the new job); Security (tokens); Error codes; Testing; Operations and known limits (D17: push built, SMS planned); Changelog. ADR 0039: context (push needs device tokens; a session ends in eight ways), decision (session-bound devices checked at schedule and send time; FCM only; one delivery per device; the token stored in clear, and why), consequences (a password change ends the other sessions, so its alert is not pushed; one Auth query per push delivery; at least once).
- [ ] **Step 4: Verify locally:** `dotnet build -c Release` (0 warnings), `dotnet format --verify-no-changes`, `dotnet test tests/TemplateName.UnitTests`, `dotnet test tests/TemplateName.ArchitectureTests`, `bash build/scripts/template-smoke.sh`, then with `--notifications false`, `--includeSample false`, `--caching memory`; `git add` new files and `bash build/scripts/project-index.sh`. Expected: all pass, index committed.
- [ ] **Step 5: Commit and open the PR** — `docs(notifications): document the push channel`; push the branch; open `feat: add the push notifications channel` (folding in draft PR #61); let CI fail on the OpenAPI snapshot; `gh run download` the `received-snapshots` artifact; check the diff is only the three device routes and the `Push` enum value where channels appear; replace the verified file; push.
- [ ] **Step 6: Final review** by a fresh reviewer on the most capable model over the whole branch against the spec and this plan; fix the findings; the user gives the merge word.
