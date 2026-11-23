# 0039. Push notifications: FCM only, devices bound to their sign-in session, one delivery per device

- Status: Accepted
- Date: 2026-11-23
- Deciders: Lee Jia Le

## Context
The Notifications module delivered by email and in-app only. Projects that ship a mobile or web client need a push to the user's devices, and the template's own Auth alerts (a new sign-in, a replayed refresh token, a locked account) are most useful as a phone ping. A push needs device tokens, which change hands and go stale, and a session can end in eight ways (log out, log out everywhere, password change, administrator revoke, token reuse, expiry, an MFA change, an external-login change). A device that keeps receiving security alerts after its session ended would leak them to whoever holds the phone. The design is `docs/superpowers/specs/2026-11-23-push-channel-design.md`.

## Options considered
**Provider.** (1) FCM only, HTTP v1, which also reaches iOS through the APNs key uploaded to Firebase; (2) FCM plus a direct APNs transport; (3) a seam with no provider.

**Devices and sessions.** (1) Each device bound to the session that registered it, checked against Auth when scheduling and again when sending; (2) the same binding, deactivated when Auth publishes a session-ended event; (3) devices independent of sessions, removed only by the client or by FCM.

**Several devices.** (1) One delivery per device; (2) one delivery per notification that fans out at send time.

**Talking to FCM.** (1) An `HttpClient` on the template's resilience pipeline with `Google.Apis.Auth` for the token; (2) the FirebaseAdmin SDK.

## Decision
- FCM only, behind an `IPushTransport` seam (`FcmPushTransport`), off by default (`Notifications:Push:Enabled`).
- A `PushDevice` records the session (`sid`) of its latest registration. The scheduler asks `IUserSessionDirectory` (a new query in `Auth.Contracts`) which of a user's device sessions are active and deactivates the others; `PushChannel` asks again before it sends. A new-sign-in alert skips the new session's own devices.
- One push delivery per device: the domain allows several push deliveries per notification, one per device id; email and in-app keep one per channel.
- `FcmPushTransport` uses the named client `push-provider` on the pipeline of the same name, which does not retry the `POST`; the delivery worker's schedule retries.
- The token is stored in clear (it is the address FCM sends to and grants nothing without the project's service account), kept out of logs, responses and exceptions; its SHA-256 is the unique key a registration is matched by.
- Only an explicit FCM `UNREGISTERED` or `SENDER_ID_MISMATCH` deactivates a device; a status alone never does.
- Push goes on `auth.new_device_login`, `auth.token_reuse_detected` and `auth.account_locked`, optional for the user.

## Consequences
- Every way a session ends stops its device's pushes without a new event or client cooperation, and a phone that changed hands does not get the previous owner's queued alerts (`device_reassigned`).
- A password change ends every other session, so `auth.password_changed` has no push: it could reach no other device.
- Each push delivery costs one indexed query on `auth.UserSessions` at send time; the schedule-time check is one query per notification.
- Push is at least once, like email: a crash after FCM accepted a message sends it again after the lease.
- Encrypting the token with `ISecretProtector` was weighed and left out: it would add a key-rotation dependency for a value that is useless without the service account.
- With `--notifications false` the module and push go; `IUserSessionDirectory` stays in Auth unused, like `IUserContactDirectory`.

## Follow-ups
- The phone number in Auth and the SMS channel (the second spec of the push and SMS roadmap row).
- A direct APNs transport only if a project needs iOS without Firebase.
