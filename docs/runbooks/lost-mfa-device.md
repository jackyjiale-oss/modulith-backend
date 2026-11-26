# Runbook: a user lost the authenticator and every recovery code

The procedure lives in [Recovering access](../modules/auth.md#recovering-access) and the reset itself in [User administration](../modules/auth.md#user-administration) of the Auth page; this page is the order to follow and the identity check before a reset. The reset removes the second factor, so the account is then only as safe as its password.

## When

The user can no longer produce a code from the authenticator app **and** has no recovery code left. If they still have a recovery code they do not need an administrator: they sign in with it at `POST /api/v1/auth/mfa/verify` (`{ challengeToken, recoveryCode }`), then remove the method with `DELETE /api/v1/auth/mfa/methods/{id}` or replace the codes with `POST /api/v1/auth/mfa/recovery-codes/regenerate`. Try that first. Every TOTP code failing for everyone after a restore or a certificate change is a different problem ([`restore-backup.md`](restore-backup.md)).

## Who

A caller with the permission `auth.user.reset_mfa` (held by `SuperAdmin` and, by default, `Admin`). The route refuses your own account (400 `auth.cannot_reset_own_mfa`), so an administrator who lost their own device asks another administrator. Only a `SuperAdmin` can reset a `SuperAdmin` (403 `auth.cannot_manage_super_admin` otherwise).

## Steps

1. **Check the identity of the person asking, strictly.** A reset lets whoever knows the password sign in without the second factor. Use a process that does not rely on what the requester sends you: a video or phone call to a contact on file, a manager's confirmation, or your own ID check. A request that arrives by the email of the account itself proves nothing here, because the second factor exists to protect against a stolen mailbox or password. Write down who verified whom and how, in the ticket.
2. **Look at the account.** With `auth.user.view`: `GET /api/v1/admin/auth/users/{id}` returns `mfaEnabled`; with `auth.audit.view`: `GET /api/v1/admin/auth/audit-logs` shows recent sign-ins, a password change or a new device the owner does not recognise. If the account looks compromised, force a password reset first: `POST /api/v1/admin/auth/users/{id}/force-password-reset` (permission `auth.user.reset_password`).
3. **Reset.**

   ```http
   POST /api/v1/admin/auth/users/{id}/reset-mfa
   Authorization: Bearer <access token with auth.user.reset_mfa>
   ```

   No body; 204 on success, also for a user without two-step verification (nothing changes). The method (a pending setup too) and every recovery code are deleted. When the method was confirmed, the security stamp also rotates (pending challenges and trusted devices die) and every session of the user ends; a setup that was only pending is just removed, with no stamp change and no session ended. The audit row `auth.admin_mfa_reset` records you as the actor.
4. **Tell the user** to sign in with the password alone and to set up a new authenticator at once (`POST /api/v1/auth/mfa/totp/setup`, then `/confirm`), and to keep the new recovery codes somewhere other than the phone.

## Verify

- `GET /api/v1/admin/auth/users/{id}` shows `mfaEnabled` `false`.
- The audit log has an `auth.admin_mfa_reset` row with your id as the actor.
- The user signs in with the password alone and the next sign-in asks for the new code.

## Roll back

A reset cannot be undone: the deleted secret and codes are gone, which is the purpose. If the reset was wrong (the requester was not the owner), lock the account (`POST /api/v1/admin/auth/users/{id}/lock`, permission `auth.user.lock`), force a password reset, and let the real owner come back through the verified process; they set up two-step verification again.
