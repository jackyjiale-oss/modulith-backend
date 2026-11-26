# Runbooks

Step-by-step procedures for the operations that cannot wait for a developer to read the code: a release that drops a column, a user who cannot sign in, a restore, a rotation, a stuck message. Every page has the same five parts: **When** to use it, **Who** may do it (a role, a permission or an access), **Steps** (numbered, each with the exact command, route or SQL), **Verify** and **Roll back**. A step that the code or a setting defines was checked against it when the page was written; a step that only your environment can answer (the name of your secret store, your backup tool) says so.

| Runbook | Use it when |
|---|---|
| [`contract-phase.md`](contract-phase.md) | A release drops or renames a column or table: how to prove nothing reads it, write the marked migration and the seed it needs, and ship it in maintenance mode |
| [`user-locked-out.md`](user-locked-out.md) | A user cannot sign in because the account is suspended or locked after wrong passwords |
| [`lost-mfa-device.md`](lost-mfa-device.md) | A user lost the authenticator app and every recovery code |
| [`signing-key-rotation.md`](signing-key-rotation.md) | The access-token signing key must be replaced, on a schedule or after a leak |
| [`restore-backup.md`](restore-backup.md) | The database is restored, or the Data Protection key ring or its certificate is lost, replaced or about to expire |
| [`dead-letter-replay.md`](dead-letter-replay.md) | The gauge `platform.outbox.abandoned` is above zero: outbox messages gave up and wait for an operator |

Where a procedure needs a decision the template cannot make for you (which backup tool, which secret store, who approves a production change), the page says so and links the document that describes the setting. The pipeline, its variables and environments are in [`../deployment/README.md`](../deployment/README.md); the production checklist is [`../deployment/production-checklist.md`](../deployment/production-checklist.md).
