# 0020. Single-use links cross module boundaries encrypted

- Status: Accepted
- Date: 2026-10-09
- Deciders: Lee Jia Le

## Context
ADR 0017 keeps a single-use token out of `auth.OutboxMessages` in plaintext: the event carries it encrypted with Data Protection and the Auth outbox handler that sends the email decrypts it. Plan 3 moves sending into a Notifications module, which consumes Auth's integration events in process and through an inbox (ADR 0018, blueprint review Section 2.2). Notifications stores each notification and its deliveries before a worker renders and sends them, retrying on failure, so the link must now cross a module boundary and rest in another module's tables until it is sent. A plaintext link there would be the bearer secret at rest that ADR 0017 removed from the outbox, and Notifications must not need to know how Auth builds its links or tokens (ADR 0001).

## Options considered
1. Publish the plaintext link (or token) and let Notifications store it: simplest, but a working link in `notify.*` and in its backups for as long as the rows exist.
2. Publish the token encrypted as it is in the outbox and let Notifications build the link: Notifications would learn Auth's link formats (`Auth:Links`) and token format, and every new link type would change two modules.
3. Auth builds the link and publishes it encrypted with the shared `ISecretProtector`; Notifications stores that ciphertext unchanged and decrypts it only in memory (as amended below: to check the link when it consumes the event, and while rendering).
4. As option 3, but Auth re-encrypts the link with a Notifications-specific Data Protection purpose: the same exposure (both purposes use the same key ring), one more step and one more purpose to keep in sync.

## Decision
We chose option 3 (decision D4 of Plan 3).

- `ISecretProtector` lives in the building blocks (`TemplateName.Application.Common.Security`) with one Data Protection purpose, `TemplateName.Secrets.v1`, shared by every module.
- Auth keeps owning link formats. Its outbox handler for `VerificationCodeIssuedDomainEvent` decrypts the token, builds the link from `Auth:Links` and publishes it re-encrypted as `ProtectedActionUrl` on `EmailVerificationRequestedIntegrationEvent` or `PasswordResetRequestedIntegrationEvent`. The plaintext exists only in that method's memory; it is never logged, stored or put in an exception message.
- The handler publishes nothing for a code that is no longer pending (replaced, used or expired), a user who no longer exists, or a user whose address is no longer the one the code was issued for, so a stale link is never sent.
- Notifications stores `ProtectedActionUrl` unchanged (`Notifications.ProtectedData`) and decrypts it only in memory, at the two points named in the amendment below. A secret may appear only in an email body: never in a subject, an in-app text, a log, a metric tag or `LastError`.
- Amended by Plan 3 (Task 10): the plaintext link exists only in memory, at two points. (1) When the event is consumed, `NotificationScheduler` decrypts each protected value to check that it is an absolute `http` or `https` URL (HTML encoding does not stop a `javascript:` link in an `href`), then discards the plaintext; an undecryptable or unsafe value refuses the event (recorded in the inbox, Warning with the event id and type only, nothing created). (2) The delivery worker decrypts it again while rendering an email. In both places it is never stored, logged or put in an exception message, and `notify.Notifications.ProtectedData` keeps the event's ciphertext unchanged.

## Consequences
- Positive: neither schema ever holds a working link at rest; Notifications needs no knowledge of Auth's token or link format; one key ring serves both modules.
- **Negative / trade-offs accepted: the key ring in `auth.DataProtectionKeys` now protects Notifications data too.** Anyone who can read that table can decrypt pending links in `notify.*` as well as in `auth.OutboxMessages`, so the ring must be encrypted at rest (a certificate or Key Vault key protector) before production use. A module other than Auth that registers no key store of its own still depends on Auth's `PersistKeysToDbContext` configuration.
- Follow-up actions: encrypt the key ring at rest in Plan 6 (as ADR 0017 already requires). The Notifications side (storing the ciphertext, decrypting it only in memory for the consumption-time check and while rendering, the template rule that keeps secrets out of subjects and in-app parts) lands with that module in Plan 3.
