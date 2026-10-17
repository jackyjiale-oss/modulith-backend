# 0017. Single-use tokens in outbox events are protected with Data Protection

- Status: Accepted
- Date: 2026-10-08
- Deciders: Lee Jia Le

## Context
Registration and password reset send an email that contains a single-use link. The email goes out through the module outbox (ADR 0007), so a mail failure never loses the account or the request: the domain event is saved with the aggregate and a background service delivers it later, retrying on failure. The link's token must travel in the event, because the delivery code cannot recover it from the database, where only its SHA-256 hash is stored. A plaintext token in `auth.OutboxMessages` would be a bearer secret at rest: anyone who can read the table (a backup, a support query, an export) could reset a password or confirm an email before the user does.

## Options considered
1. Plaintext token in the event payload: simplest, but a readable secret in the database for as long as the row exists.
2. Send the email inside the request instead of through the outbox: no stored token, but a mail failure breaks registration and reset, and the request waits on SMTP.
3. Encrypt the token in the event with ASP.NET Core Data Protection (`IDataProtector` with a purpose string), decrypt it in the delivery handler, and keep the Data Protection key ring in the database.
4. Encrypt with a key from configuration that we manage ourselves: the same effect, but we own key generation, storage and rotation.

## Decision
We chose option 3.

- The event carries the token only as protected text (`ISecretProtector`, a dedicated Data Protection purpose string). The delivery handler unprotects it and builds the link. The hash in `auth.VerificationCodes` is unchanged.
- The key ring is stored in `auth.DataProtectionKeys` (`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`), so every instance shares the keys and a restart does not make queued events unreadable.
- A token is short-lived (60 minutes for email verification, 30 for password reset), so an event that is not delivered in that time is useless anyway.

## Consequences
- Positive: no plaintext bearer secret in the outbox table or its backups; key generation and rotation are Data Protection's job, not ours; all instances can decrypt each other's events.
- **Negative / trade-offs accepted: the key ring is not encrypted at rest.** Data Protection stores the keys as XML in `auth.DataProtectionKeys`, and without an `IXmlEncryptor` anyone who can read that table can read the keys, and with them decrypt tokens still in the outbox. The protection is therefore against casual exposure (logs, exports, queries), not against someone who has full database access.
- Amended by Plan 3 (decision D4): `ISecretProtector` moved to the building blocks (`TemplateName.Application.Common.Security`) so that Notifications can unprotect what Auth protects, and its Data Protection purpose changed from `TemplateName.Auth.Secrets.v1` to `TemplateName.Secrets.v1`. Values protected under the old purpose can no longer be read (Plan 2 was unreleased). The key ring is unchanged: still `auth.DataProtectionKeys`, still configured by the Auth module.
- Follow-up actions: configure a certificate or Key Vault key protector for the ring in Plan 6, before production use with real users; deleting delivered outbox rows promptly also narrows the window.
