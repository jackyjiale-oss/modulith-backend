# 0025. Trusted devices are hashed bearer tokens bound to the security stamp

- Status: Accepted
- Date: 2026-10-18
- Deciders: Lee Jia Le

## Context
Blueprint 9.5 asks for two-step verification at sign-in "on untrusted devices", 9.7 lists the endpoints that list and remove a user's trusted devices, and 11.2 gives them a table (`TrustedDevices`). So a user who has verified a code on a device may choose to remember it, and the next sign-ins from that device need the password only, until the device expires or is forgotten.

What is remembered is a credential: whoever presents it skips the second factor of that account. So it must be unguessable, it must not be readable from the database, it must die when the account's credentials change (a password change or reset, a change of two-step verification, a suspension, an administrator's reset), and it must be revocable one device at a time. It must also not weaken what a second factor proves: a session started through a remembered device did not prove a code **now**.

The template's clients are bearer-only (native apps and SPAs that send `Authorization: Bearer`); a backend-for-frontend with cookies is backlog.

## Options considered
1. **Device fingerprinting** (the user agent, the address, client hints, or a fingerprint the client computes). Nothing secret: every value is sent by the client and can be copied or forged by anyone who has seen one request, and the honest values change with every browser update or network. It would trust devices that are not the user's and forget the user's own.
2. **A signed cookie** (an HMAC or Data Protection payload with the user id, the stamp and the expiry). Stateless to check, but it needs cookies, which bearer clients do not send, and revoking one device needs a server-side list anyway. It also signs a credential with keys that protect other things.
3. **A hashed bearer token bound to the stamp**: 32 random bytes in base64url (`ISecureTokenService.Generate()`), returned once by the verify that remembered the device and sent by the client as `trustedDeviceToken` in the login body; `auth.TrustedDevices` keeps only its SHA-256, the user, a snapshot of the user's security stamp, the device name and user agent, and an absolute expiry. Revocation is a column; the stamp snapshot makes every credential change forget every device without a call anywhere.

## Decision
We chose option 3 (decision D14 of Plan 4).

- **Remembering.** `POST /api/v1/auth/mfa/verify` with `rememberDevice: true`, after a correct code: `TrustedDevice.Trust(userId, hash, challenge.DeviceName, userAgent, user.SecurityStamp, Auth:Mfa:TrustedDeviceLifetime, now)` in the same save as the session (the stamp snapshot is taken after `SessionStarter`, which does not rotate it), audited as `auth.trusted_device_added` with the device id. The answer carries `trustedDeviceToken` and `trustedDeviceExpiresAt`, shown only there.
- **Using it.** `POST /api/v1/auth/login` with `trustedDeviceToken`: looked at **only after** the password is verified, the account may sign in and has a confirmed method, and while `Auth:Mfa:TrustedDevicesEnabled` is on. A device of this user that `IsUsable(user.SecurityStamp, now)` (not revoked, `now < ExpiresAt`, the same stamp) is marked used and the session starts with `amr` `pwd`, audited as `auth.login_succeeded` with `{"trustedDeviceId":"..."}`. Any other token (unknown, expired, revoked, another user's, an older stamp, or the switch off) gets the challenge exactly as without one: the same answer, audit row and metric, nothing about the token.
- **Lifetime.** 30 days (`Auth:Mfa:TrustedDeviceLifetime`, 1 to 365), absolute: using the device never extends it.
- **`amr` `pwd`, not `mfa`** (D9). The device proved a code once, earlier; the session proves the password now. So a trusted-device session never satisfies an MFA step-up (ADR 0023): turning MFA off, regenerating codes or changing the password still asks for a code.
- **Forgetting.** One device: `DELETE /api/v1/auth/trusted-devices/{id}`, no step-up (reducing trust is safe, D11), audited as `auth.trusted_device_revoked`; another user's id is the same 404 as an unknown one. All devices: every rotation of the security stamp (password change or reset, enabling or disabling MFA, regenerating recovery codes, a suspension, an administrator's MFA reset).
- **Listing.** `GET /api/v1/auth/trusted-devices` (Dapper, cursor-paged, `createdAt` only) writes the rules of `IsUsable` in its SQL (owner, `RevokedAt IS NULL`, `ExpiresAt > @Now`, the stamp equal to `auth.Users.SecurityStamp`), so it shows exactly the devices that can still skip the challenge.

## Consequences
- Positive: nothing in the database can be replayed (only hashes); one rule, the stamp, forgets every device at every credential change; the list is exactly what works.
- **Clients store a 30-day credential.** A native app keeps the token in the platform's secure storage (Keychain, Keystore); a browser SPA has nothing better than its own storage, where script injection can read it. A cookie variant (`HttpOnly`, `Secure`, `SameSite`) comes with the backend-for-frontend.
- **A stolen device token still needs the password.** It replaces the code only, so a thief with the token alone gets nothing, and the owner forgets it by revoking the device, or every device by changing the password.
- **Revoking a device does not end the sessions started through it.** `DELETE /api/v1/auth/trusted-devices/{id}` only stops the device from skipping the challenge next time. The sessions and refresh tokens already started through it stay valid until they expire or are revoked, because a session records no link to the device that started it (a link would need a schema change and was left out). A client that wants to sign the device out as well also offers `DELETE /api/v1/auth/sessions/{id}` (or `POST /api/v1/auth/logout-all`); a password change forgets every device and ends the other sessions, as before.
- **Two simultaneous revocations of one device can both write an audit row.** The "already revoked" check is a read, not a conditional update; the device ends revoked either way.
- **Every stamp change forgets every device**, including regenerating recovery codes and a suspension that is lifted again: users verify a code once more on each device. That is the price of one revocation rule.
- **Rows stay after they stop working.** Expired, revoked and stale-stamp rows are hidden by the list and refused at login, and removed by the daily job `AuthTrustedDevicesCleanup` ([ADR 0028](0028-retention-policy-and-the-inbox-window.md), `Auth:Retention:TrustedDevices`, 30 days after expiry or revocation).
- **The switch.** With `Auth:Mfa:TrustedDevicesEnabled` off, verify remembers nothing and login ignores every token; the existing rows stay listable and revocable, and work again if the switch is turned back on within their lifetime and stamp.
- Follow-up actions: the cleanup job (done in Plan 5: `AuthTrustedDevicesCleanup`); the new-device notification must not fire for a sign-in through a trusted device (`SessionStarter`'s `viaTrustedDevice`, Plan 4 Task 11); a cookie variant with the backend-for-frontend.
