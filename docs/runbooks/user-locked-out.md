# Runbook: a user is locked out

The procedure lives in [Recovering access](../modules/auth.md#recovering-access) and [User administration](../modules/auth.md#user-administration) of the Auth page; this page is the order to follow and the check to make before using it. For a lost authenticator use [`lost-mfa-device.md`](lost-mfa-device.md).

## When

A user cannot sign in and the account is one of two kinds of locked:

- **Locked out after wrong passwords.** `Auth:Lockout:MaxFailedAttempts` failures in a row (5 by default) lock the account for `Auth:Lockout:Duration` (15 minutes by default), and it opens again by itself exactly at the end. Wrong codes at the two-step challenge count too. The user sees the same 401 `auth.invalid_credentials` as for a wrong password.
- **Suspended by an administrator.** The correct password answers 403 `auth.account_inactive`.

If the lockout is short, waiting is the safest answer.

## Who

A caller with the permission `auth.user.lock` (held by `SuperAdmin` and, by default, `Admin`). Only a `SuperAdmin` can unlock a `SuperAdmin` (403 `auth.cannot_manage_super_admin` otherwise). An administrator who is locked out needs another administrator; if none is left, the way back is a database edit or a new seed account ([Known limits](../modules/auth.md#known-limits)).

## Steps

1. **Check who is asking.** The route restores access to an account, so confirm the person is the owner before you use it: a call-back to a number on file, a ticket from the account's verified email, or your own identity process. Never take the word of the message that asks. A phone call from "the user" is how an account is taken over.
2. **Find out why the account is locked.** With `auth.user.view`: `GET /api/v1/admin/auth/users/{id}` returns `status`, `isLockedOut` and `lockoutEnd`. With `auth.audit.view`: `GET /api/v1/admin/auth/audit-logs` shows the failed sign-ins and who suspended the account. An account that an administrator suspended on purpose (a security case) is not unlocked on the user's request; ask the administrator who suspended it. A burst of failed sign-ins the owner did not make is an attack: unlock, then `POST /api/v1/admin/auth/users/{id}/force-password-reset` (permission `auth.user.reset_password`), which ends every session and emails a reset link.
3. **Unlock.**

   ```http
   POST /api/v1/admin/auth/users/{id}/unlock
   Authorization: Bearer <access token with auth.user.lock>
   ```

   204 on success, also for an account that is already active. The account is active again and the failed-sign-in count and the lockout are cleared. Sessions that a suspension revoked stay revoked: the user signs in again.
4. **Tell the user** to sign in, and to use "forgot password" if they do not know the password.

## Verify

- `GET /api/v1/admin/auth/users/{id}` shows `status` `Active` and `isLockedOut` `false`.
- The audit log has an `auth.admin_user_unlocked` row with your id as the actor.
- The user signs in.

## Roll back

`POST /api/v1/admin/auth/users/{id}/lock` (permission `auth.user.lock`; 400 `auth.cannot_lock_self` for your own account, 409 `auth.last_super_admin` for the last `SuperAdmin`) suspends the account again and revokes its sessions. Unlocking clears the lockout counters, which cannot be put back; the next wrong passwords start a new count.
