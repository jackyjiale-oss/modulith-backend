# Push notifications channel — design

- Status: implemented 2026-11-23 (ADR 0039); approved 2026-11-23; implementation plan `docs/superpowers/plans/2026-11-23-push-channel.md`, whose "Deviations from the spec" section and ledger rulings record the changes made while building it.
- Date: 2026-11-23
- Roadmap row: "Push and SMS channels" in `docs/blueprint-review.md` (decided by the user on 2026-10-20). The user split it on 2026-11-23: this spec is the **push** half; the phone number in Auth and the SMS channel are a second spec, and phone-number OTP login a third. All three gate 1.0.0 (decided 2026-11-20).
- Builds on: the Notifications module (Plan 3: catalog, scheduler, delivery worker, preferences, quiet hours; ADR 0018, ADR 0020), the external HTTP clients and resilience pipelines (`AddExternalHttpClient`, Plan 5), sessions and the `sid` claim (Plan 2), ADR 0035 (switch markers), ADR 0038 (preferences `ETag`).
- Blueprint: Section 10.1 (Push: Firebase Cloud Messaging for Android, iOS and web; device tokens per user), 10.4 (retries, permanent errors disable the device), the `notify.PushDevices` table in Section 11.

## 1. Intent

**Outcome.** A team that builds a mobile (or web) app on a generated project can deliver notifications to the user's devices by push. They switch the channel on in configuration, add a Firebase service account, and the app registers its FCM token after sign-in; from then on the notification types that declare push reach every device the user is signed in on, through the same delivery worker as email and in-app (retries, dead-lettering, preferences, quiet hours, administration).

**Who it is for.** Projects started from the template that ship a mobile or web client. The template's own Auth alerts are the first users: a phone ping when someone signs in on a new device, when a stolen refresh token is replayed, or when the account is locked after failed sign-ins.

**Success.**

- With `Notifications:Push:Enabled` off (the default), nothing changes: no push delivery is created, the preferences API does not show the channel, and every existing test still passes.
- With it on and FCM credentials set, a registered device whose sign-in session is still active receives the three Auth alerts in the user's language; a device whose session ended receives nothing.
- No test needs a Firebase project: FCM is replaced by a fake HTTP handler.

## 2. Non-goals

- A direct APNs transport (iOS is reached through FCM with the APNs key uploaded to Firebase); the `IPushTransport` seam leaves room for one later.
- Web-push specifics beyond FCM's defaults, silent or data-only pushes, topics, broadcast or segment sends.
- Delivery receipts, opens, provider callbacks, fallback channels (push fails → email), digests (D17 stays for these).
- A "log" or development provider: an app on a real phone needs a real Firebase project anyway.
- The SMS channel and the phone number (the second spec).
- A template switch: the channel is switched by configuration (the blueprint review's "config selects providers" rule); `--notifications false` removes it with the module.

## 3. Decisions (the user, 2026-11-23)

1. **Two specs.** Push first, entirely inside Notifications plus one query in Auth's contracts; the phone number and SMS second.
2. **FCM only**, HTTP v1, behind an `IPushTransport` seam.
3. **Push types.** Push is an **optional** default channel (the user can switch it off; email stays mandatory) on `auth.new_device_login`, `auth.token_reuse_detected` and `auth.account_locked`. `auth.password_changed` was considered and dropped: a password change revokes every other session (a reset revokes all), so a session-bound push could reach no other phone.
4. **Devices are bound to the session** that registered them; before scheduling and again before sending, Notifications asks Auth which of those sessions are still active, and a device whose session ended gets nothing and is deactivated.
5. **One push delivery per device** (the domain rule "at most one delivery per channel" becomes "per channel and destination").
6. **`HttpClient` on the resilience pipeline** (`AddExternalHttpClient("push-provider", "push-provider")`) for FCM, with `Google.Apis.Auth` for the OAuth token.

One refinement made while writing this spec, not discussed separately: registration is `POST /devices` rather than the `PUT` shown in the design conversation. It is an upsert keyed by the token the client holds, not the replacement of a resource the client read, so it has no version to send; as a `PUT` it would have to be listed in the `ETag` coverage test's exclusions (ADR 0038).

## 4. Surface

Routes (tag `Notifications`, each with an explicit `.RequireAuthorization()`; the user is always the access token's):

| Method | Route | Purpose | Success | Errors |
|---|---|---|---|---|
| `POST` | `/api/v1/notifications/devices` | Registers the caller's device: `{ token, platform, deviceName?, appVersion? }`, upserted by token (5.1). | 200 `{ id, platform, deviceName, appVersion, registeredAt }` | 400 `validation.failed` (blank token or longer than 4096, undefined `platform`, `deviceName` over 100, `appVersion` over 50); 400 `request.malformed`; 401 `http.401`; 429 `rate_limit.exceeded` (with `Retry-After`) |
| `GET` | `/api/v1/notifications/devices` | The caller's active devices, newest registration first: `{ items: [{ id, platform, deviceName, appVersion, registeredAt }] }`. Not paged (at most `MaxDevicesPerUser`). The token is never returned. | 200 | 401 `http.401` |
| `DELETE` | `/api/v1/notifications/devices/{id:guid}` | Unregisters one of the caller's devices. | 204, also when it was already inactive | 401 `http.401`; 404 `notifications.device_not_found` (an unknown id and another user's device alike, never 403) |

`platform` is `Android`, `Ios` or `Web` (enum names, as every enum in the API). The routes work whether or not push is enabled, so an app can register before an operator turns push on.

New error code: `notifications.device_not_found`, in `en`, `ms` and `zh-Hans`.

Changed behaviour of existing routes:

- `GET /api/v1/notifications/preferences` lists the `Push` channel (after `Email` and `InApp`) for the three types only while push is enabled; `PUT` refuses `Push` with the existing `notifications.channel_not_supported` while it is disabled. Turning push on changes the preferences view and therefore its `ETag`: a client holding the old tag gets one 412 (ADR 0038, the known effect of a release that changes defaults).
- `GET /api/v1/admin/notifications/deliveries` accepts `channel=Push`; a push delivery's destination is its device id, which is not personal data and is shown unmasked.

New contract in `TemplateName.Modules.Auth.Contracts`: `IUserSessionDirectory.GetActiveSessionIdsAsync(Guid userId, IReadOnlyCollection<Guid> sessionIds, CancellationToken)` returning the subset that is active (`UserSession.IsActive(now)`: not revoked and before its absolute expiry), answered by one query over `auth.UserSessions`.

New configuration (validated on start; a message names the key, never a value):

| Key | Default | Meaning |
|---|---|---|
| `Notifications:Push:Enabled` | `false` | Turns the channel on. |
| `Notifications:Push:Provider` | `fcm` | The provider; `fcm` is the only accepted value. |
| `Notifications:Push:MaxDevicesPerUser` | `10` (1 to 100) | The most active devices a user has; registering one more deactivates the oldest. |
| `Notifications:Push:RegistrationsPerMinute` | `10` (1 to 1000) | The most registrations per user per minute; beyond it, 429. |
| `Notifications:Push:Fcm:ProjectId` | empty | The Firebase project id. Required while `Enabled` is on. |
| `Notifications:Push:Fcm:CredentialsJson` | empty (user secrets or environment) | The service-account JSON. Empty means Google's application default credentials (`GOOGLE_APPLICATION_CREDENTIALS`, workload identity). When set, it must parse as a service-account JSON, or the host does not start. Never set it in a file. |
| `Notifications:Retention:PushDevices` | `30.00:00:00` | How long a deactivated device is kept before the daily cleanup deletes it; `00:00:00` turns the cleanup off. |

New resilience pipeline name `push-provider`, documented with suggested values (attempt timeout 10 s, total 30 s), starting from `external-default` like any unnamed pipeline. The building block does not retry a `POST` unless `RetryUnsafeMethods` is set, and it stays off, so the worker's schedule is the retry (changed while building: the earlier "2 retries" would not apply to a POST and could show a push twice).

New table `notify.PushDevices`, migration `AddPushDevices`.

## 5. Behaviour

### 5.1 Devices

`PushDevice` (entity, `Domain/PushDevices/`): `Id`, `UserId`, `SessionId`, `Platform` (`Android = 1`, `Ios = 2`, `Web = 3`, `tinyint`), `TokenHash` (SHA-256 of the token, 32 bytes), `Token` (the FCM registration token, needed to send), `DeviceName` (cut to 100 with `BoundedText`), `AppVersion` (cut to 50), `CreatedAt`, `LastRegisteredAt`, `DeactivatedAt` and `DeactivationReason` (`RemovedByUser = 1`, `SessionEnded = 2`, `RejectedByProvider = 3`, `Replaced = 4`, `tinyint`). Active means `DeactivatedAt` is null.

Registering (`POST /devices`), in one save:

1. The caller's session is the access token's `sid` (`ICurrentUser.SessionId`); a token without one is 401 `http.401` (every access token the template issues has one).
2. The device with the same `TokenHash` is looked up, whoever owns it. Found: it moves to the caller and the current session, takes the new name, version and platform, `LastRegisteredAt = now`, and is reactivated (a phone that changed hands or was signed in again). Not found: a new device.
3. If the caller now has more than `MaxDevicesPerUser` active devices, the oldest by `LastRegisteredAt` are deactivated with `Replaced`.
4. Two registrations of the same token at once collide on the unique `TokenHash`: the loser re-reads and applies once more; a second collision answers 409 `concurrency.conflict`.

The token is a bearer value for sending to that device: it is never logged, never returned by an endpoint, never put in an exception message or a metric tag, and records that hold it override `PrintMembers` (as `EmailMessage` does).

Unregistering (`DELETE /devices/{id}`) deactivates the caller's device with `RemovedByUser`; the row stays until the cleanup. The daily job `NotificationsPushDevicesCleanup` deletes devices deactivated more than `Retention:PushDevices` ago.

### 5.2 Scheduling

In `NotificationScheduler`, step 6 (channels) gains push, and step 7 (due times) widens:

- A type's `Push` channel is dropped when `Notifications:Push:Enabled` is off, or when the recipient switched it off. Otherwise:
  1. The recipient's active devices are read.
  2. A device whose session is the request's `ExcludedSessionId` is skipped. `NotificationRequest` gains this optional field; the `NewDeviceSignedIn` consumer sets it to the event's `SessionId`, so the device that just signed in is not told about itself.
  3. `IUserSessionDirectory` answers which of the remaining devices' sessions are active; the others are deactivated with `SessionEnded`, staged in the same save as the inbox record.
  4. Each remaining device gets one `Delivery` with channel `Push` and destination the device id (`"D"` format). None left: no push delivery; the other channels go ahead, and the outcome is `NothingToDeliver` only when no channel at all is left.
- Due times: a push of a type that is not `Critical` inside the recipient's quiet hours is due at `QuietHours.NextAllowedAt`, as email is. The three Auth types are `Critical`, so they are never deferred.

`Notification.AddDelivery` allows at most one delivery per channel **and destination**. A push destination must parse as a `Guid` (`PushDestination`), an email destination stays one plain address (`EmailDestination`), and in-app keeps a null destination (so still one in-app delivery per notification).

### 5.3 Rendering and sending

The worker renders a push delivery from two template parts, `title` and `body`, in the notification's culture, cut to 200 and 1000 characters. Secret variables never reach push: the worker decrypts `ProtectedData` for email only, as today.

`PushChannel` (`Infrastructure/Channels/Push/`, scoped) loads the device in the worker's scope, so any change to it is saved in the same save as the delivery's outcome (as `InAppChannel` stages its row):

| Situation | Result | Device |
|---|---|---|
| The device row no longer exists (the cleanup removed it) | permanent `device_not_found` | — |
| The device is deactivated | permanent `device_inactive` | — |
| Another user registered the device since (a phone that changed hands) | permanent `device_reassigned` | kept (added while planning) |
| Its session is no longer active (`IUserSessionDirectory`) | permanent `session_ended` | deactivated, `SessionEnded` |
| Otherwise | sends through `IPushTransport` (5.4) | per 5.4 |

`IPushTransport.SendAsync(PushMessage, CancellationToken)`; `PushMessage(Token, Title, Body, Data, Urgent)`, where `Data` is `{ notificationId, typeCode, category }` and `Urgent` is true for `Critical` and `High` priority.

### 5.4 The FCM transport and its failures

`FcmPushTransport` (singleton) sends `POST https://fcm.googleapis.com/v1/projects/{ProjectId}/messages:send` through the named client `push-provider`:

```json
{ "message": {
    "token": "…",
    "notification": { "title": "…", "body": "…" },
    "data": { "notificationId": "…", "typeCode": "auth.new_device_login", "category": "Security" },
    "android": { "priority": "HIGH" },
    "apns": { "headers": { "apns-priority": "10" } } } }
```

(`NORMAL` and `"5"` when not urgent.) The bearer token comes from `IFcmAccessTokenSource`, implemented with `Google.Apis.Auth` (scope `https://www.googleapis.com/auth/firebase.messaging`; the library caches and refreshes it); tests substitute the seam.

Failures throw `PushSendException(isPermanent, reason, deactivateDevice)`; the reason is a short code (the `ChannelSendResult` rule), the response body is never attached or logged (it can quote the token). `PushChannel` turns it into a `ChannelSendResult` and, when `deactivateDevice`, deactivates the device with `RejectedByProvider`. The FCM `errorCode` in the response's `details` decides first, the HTTP status otherwise:

| FCM answer | Reason | Kind | Device |
|---|---|---|---|
| `UNREGISTERED` (404) | `fcm_unregistered` | permanent | deactivated |
| `SENDER_ID_MISMATCH` (403) | `fcm_sender_id_mismatch` | permanent | deactivated |
| `INVALID_ARGUMENT` (400) | `fcm_invalid_argument` | permanent | kept (may be the payload) |
| `QUOTA_EXCEEDED` (429) | `fcm_quota_exceeded` | transient | kept |
| `UNAVAILABLE` (503), `INTERNAL` (500) | `fcm_unavailable`, `fcm_internal` | transient | kept |
| `THIRD_PARTY_AUTH_ERROR` (401: the APNs key in Firebase is wrong) | `fcm_third_party_auth_error` | transient (a configuration fault, as a refused SMTP sender is) | kept |
| The OAuth token cannot be obtained | `fcm_auth_failed` | transient | kept |
| Another 4xx / 5xx | `fcm_http_{status}` | permanent / transient | kept |
| Timeout, connection failure | `timeout`, `connection_failed` | transient | kept |

The pipeline's own retries (on 429, 5xx and timeouts, honouring `Retry-After`) happen inside one attempt; the worker's schedule (1, 5, 15, 60 minutes) applies after them. Push is **at least once**, like email: a crash after FCM accepted a message, or a pipeline retry after a lost answer, can send it twice.

The transport logs one Information line per accepted message and a Warning per failure (reason, permanence, exception type), naming the delivery id, the device id and the type code only: never the token, the title or the body.

### 5.5 Catalog and templates

`AuthNotificationTypeSource` adds `Push` as a default, non-mandatory channel of `auth.new_device_login`, `auth.token_reuse_detected` and `auth.account_locked`. `auth.new_device_login` and `auth.account_locked` thereby become configurable (they were email only), so the preferences API offers them while push is enabled.

Templates: `Templates/Push/{type}.{culture}.title.scriban` and `{type}.{culture}.body.scriban`, the layout of `Templates/InApp/`, 3 types × 3 cultures × 2 parts = 18 files, using only each type's declared variables. The `ms` and `zh-Hans` wording are drafts awaiting a native review, like the existing ones. Total embedded parts: 132 → 150.

### 5.6 Disabled channel, removed module

- `Enabled` off: no push delivery is created; the preferences API hides and refuses the channel; device registration still works; a push delivery already pending (created while it was on) is dead-lettered by the worker with `channel_unavailable`, the existing rule for an unregistered channel, because the push channel is registered only while enabled.
- `--notifications false`: everything in this spec goes with the module. `IUserSessionDirectory` and its Auth implementation are used only by Notifications but carry no switch marker, following `IUserContactDirectory` (changed while planning): a project generated without Notifications keeps the small query, unused.

## 6. Where it lands

| Area | Change |
|---|---|
| `Auth.Contracts/Users/` | `IUserSessionDirectory` (marked `notifications`). |
| `Auth/Infrastructure/` | Its implementation over `auth.UserSessions`; registration in `AuthModule` (marked). |
| `Notifications/Domain/` | `PushDevices/` (`PushDevice`, `PushPlatform`, `PushDeviceDeactivationReason`); `NotificationChannel.Push = 3`; `PushDestination`; `Notification.AddDelivery` rule per channel and destination. |
| `Notifications/Application/` | `Devices/Register`, `Devices/List`, `Devices/Unregister` (message, handler, validator each); `IPushDeviceRepository`; `IPushTransport`, `PushMessage`, `PushSendException`; scheduler changes; `NotificationRequest.ExcludedSessionId`; the catalog change; preferences hiding the disabled channel. |
| `Notifications/Infrastructure/` | `Channels/Push/` (`PushChannel`, `FcmPushTransport`, `GoogleFcmAccessTokenSource`, `PushOptions` and validation); persistence (`PushDeviceConfiguration`, repository, migration `AddPushDevices`: table, unique `IX_PushDevices_TokenHash`, `IX_PushDevices_UserId` filtered on active, `IX_PushDevices_DeactivatedAt` for the cleanup); `Retention/` cleanup job. |
| `Notifications/Endpoints/` | The three device routes, `PushDeviceRateLimit` (policy `notifications-push-devices`). |
| `Notifications/Templates/Push/` | 18 template files. |
| `Resources/` | `notifications.device_not_found` in three languages. |
| `Directory.Packages.props` | `Google.Apis.Auth` (Apache-2.0; licence file and terms checked; its `Newtonsoft.Json` dependency is MIT). No Dockerfile change: no project is added. |
| Docs | `docs/modules/notifications.md` (endpoints, push channel section, domain, data, configuration, background processing, security, operations, D17), `docs/modules/auth.md` (the contract), `docs/building-blocks/infrastructure-common.md` (the `push-provider` pipeline example), `docs/blueprint-review.md` (the roadmap row split), production checklist (FCM credentials), ADR 0039 (session-bound devices, FCM only, one delivery per device), `CHANGELOG` via the commit. |
| Index | `bash build/scripts/project-index.sh` (routes, error code, configuration keys, job, switch markers, new tests). |

## 7. Tests, first

Unit (`tests/TemplateName.UnitTests/Notifications/`):

- `PushDeviceTests`: register, move to another user and session, reactivate, deactivate (each reason), `BoundedText` cuts, token never in `ToString`.
- Register handler: the upsert by hash, the cap deactivates the oldest with `Replaced`, the collision retry and the 409 after a second collision, no `sid` → 401.
- `NotificationSchedulerTests` additions: push dropped when disabled or switched off; one delivery per active device; `ExcludedSessionId` skipped; ended sessions deactivated in the same save; no device left keeps the other channels; non-critical push deferred by quiet hours.
- `NotificationTests` additions: two push deliveries with different devices allowed, the same device twice refused, a non-`Guid` push destination refused, still one in-app delivery.
- `PushChannelTests`: each row of the 5.3 table; deactivation on the 5.4 permanent rows.
- `FcmPushTransportTests` against a fake `HttpMessageHandler`: the request body and URL, each row of the 5.4 table by `errorCode` and by status, the token absent from logs and exceptions.
- `PushOptions` validation: missing project id while enabled, an unknown provider, unparsable credentials JSON; messages name keys only.
- Preferences: the channel hidden and refused while disabled, listed after `InApp` while enabled.

Architecture: the existing rules cover the new types (internal by default, module boundaries, handler layout).

Integration (`tests/TemplateName.IntegrationTests/Notifications/`):

- `PushDeviceEndpointsTests`: the three routes, the 404 for another user's device, the rate limit, the token never in a response.
- Persistence: the `PushDevices` round trip, the unique hash, the cleanup.
- `IUserSessionDirectory` against real sessions (active, logged out, expired).
- Flow: an Auth `NewDeviceSignedIn` event with two signed-in devices delivers to the other device only, through the fake FCM handler; after `POST /auth/logout` on one session the next alert skips that device and deactivates it; an `UNREGISTERED` answer deactivates the device.
- OpenAPI snapshot refreshed from CI's received file (the If-Match procedure).

Smoke: all on, then `--notifications false`, `--includeSample false`, `--caching memory`.

## 8. Risks

- **`Google.Apis.Auth` API churn.** `GoogleCredential.FromJson` is being replaced by `CredentialFactory` in recent versions; the plan pins the current API and isolates it behind `IFcmAccessTokenSource`.
- **Tokens at rest in clear.** The token is needed to send, so it is stored; it grants nothing without the project's service account, and it is kept out of logs, responses and exceptions. Encrypting it with `ISecretProtector` would add a key-rotation dependency for little gain; noted in the ADR.
- **A cross-module query per push delivery.** The session check at send time is one indexed query on `auth.UserSessions` per delivery; acceptable at the template's scale, and the reason the check is batched at scheduling time.
- **Duplicates.** At-least-once delivery plus pipeline retries can show a notification twice on a device.
- **FCM error format.** The mapping relies on the documented `FcmError` details; an unknown answer falls back to the status rows.
- **Lock-screen exposure.** Push titles and bodies show on lock screens; the three Auth texts must stay free of anything sensitive (no IP address in a push, unlike the email).

## 9. Open questions

None blocking. The native review of the `ms` and `zh-Hans` drafts stays open, as for every existing template.

## 10. Review focus

- Decision 3's replacement of `auth.password_changed` with `auth.account_locked`.
- The `POST` refinement of Section 3.
- The deactivation rules (which FCM answers deactivate a device, and that a session check deactivates it).
- Whether `Notifications:Push:MaxDevicesPerUser = 10` and the 30-day retention suit the projects this template is for.
