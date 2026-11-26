# Production hardening — design

- Status: design approved section by section by the user (2026-11-25 and 2026-11-26); written spec waiting for the user's review.
- Date: 2026-11-26
- Roadmap row: "Unscheduled: Production hardening follow-ups" in `docs/blueprint-review.md` (row 135; blueprint 16.3, 17, 18). The user decided on 2026-11-20 that every remaining gate blocks 1.0.0; this is the second of the two left (the other is the maintenance-screen detection).
- Builds on: ADR 0017 (the Data Protection ring in `auth.DataProtectionKeys`, unencrypted at rest), ADR 0031 (compatibility policy; its upgrade-path test is "Not built"), ADR 0033 (delivery pipeline and its "Not built" list), ADR 0034 (maintenance mode), the `migrate` host mode, the production checklist, ADR 0035 (switch markers).
- Scope: the row only. The other items of `docs/superpowers/plans/2026-11-04-production-readiness-and-maintenance.md` (deployment locking, journals, migration planning, privileged-action guards, proof claims, idempotency receipts) stay a backlog.

## 1. Intent

**Outcome.** A project generated from the template can go to production without the four gaps ADR 0033 and the production checklist name: the key ring is wrapped by a certificate and the host refuses to run in production without one; a release is proved, by test, to migrate over data written by every earlier release of every module; the operator has a written procedure for each recovery task blueprint 18 lists; and an IIS site health-checks over a plain local URL.

**Success.**

- In any environment except Development and Testing, the API does not start unless `Auth:DataProtection:Certificate` names a usable certificate and the ring holds no plaintext key. The message names the setting and the runbook.
- The `migrate` mode needs no certificate.
- `dotnet test` includes an upgrade-path test that walks every module's migrations in order with seed data and fails when a migration loses or breaks a seeded row.
- `docs/runbooks/` holds the six runbooks, indexed and linked from the production checklist.
- Plain-HTTP `GET /health/live` and `/health/ready` answer without a redirect; every other route still redirects.
- No part of this needs Azure, Key Vault or a particular certificate authority.

## 2. Non-goals

- **Key Vault or DPAPI built in.** A project that wants either swaps the protector in its own `AuthModule` (the ADR says how).
- **Converting a live plaintext ring** in place. Production refuses one; the restore runbook gives the "start a new protected ring" steps. The template has no production data to convert.
- **Re-wrapping old keys** under a new certificate. A certificate that ever wrapped a key stays in `PreviousCertificates` (section 3.5).
- **Replay code.** Dead-letter replay is a reviewed SQL procedure, not an endpoint or job.
- **The maintenance-screen detection**, the rest of the 2026-11-04 plan, and any change to token signing.

## 3. Key protection

### 3.1 Why a certificate, and where it lives

`PersistKeysToDbContext<AuthDbContext>` (AuthModule) stores the ring. ASP.NET Core's `ProtectKeysWithCertificate` wraps each new key with the certificate's public key; `UnprotectKeysWithAnyCertificate` reads keys wrapped under any of a list of certificates. Auth owns the ring, so Auth owns the settings: a class `DataProtectionCertificateOptions` in `Auth/Infrastructure`, bound from `Auth:DataProtection:Certificate`. Rejected: a new building-block package (Auth is the only module with a ring) and a single environment thumbprint (Docker has no certificate store; rotation needs a list).

### 3.2 Configuration

| Key | Meaning |
|---|---|
| `Auth:DataProtection:Certificate:Source` | `Store` or `File` |
| `...:Thumbprint`, `...:StoreLocation` (default `LocalMachine`), `...:StoreName` (default `My`) | `Store`: a certificate in the Windows store; for IIS the app pool identity needs read access to its private key |
| `...:Path`, `...:Password` | `File`: a PFX; for Docker, mounted as a secret, the password from a secret or environment variable, never committed |
| `Auth:DataProtection:PreviousCertificates` | a list of the same shape, used only to decrypt |

The certificate is RSA with a private key. `AddAuthModule` reads the certificates when the section is configured and calls `ProtectKeysWithCertificate` and `UnprotectKeysWithAnyCertificate` with them (both take loaded certificates; the framework's thumbprint overload is eager too and searches only the current user's store). A certificate that cannot be loaded is recorded in a status object, not thrown, so registration never fails; the startup check (3.3) reports it. `migrate` is given none of these settings by the pipeline, so it loads nothing and needs no certificate.

### 3.3 Startup rules

A startup check (a hosted service ordered before the first request, `ValidateOnStart` for the options) runs in every environment except Development and Testing, the same pair `SigningKeyProvider` already treats as non-production. It refuses to start when:

1. the active certificate is not configured, is not found, has no private key, is not RSA, or is outside its validity period (an expired certificate stops a restart, so the runbook says to rotate before expiry and the message gives the expiry date);
2. a previous certificate is not found or has no private key;
3. `auth.DataProtectionKeys` holds a plaintext key (section 3.4);
4. a key in the ring is wrapped under a certificate that is not in the configuration (the message names its thumbprint; the framework would fail on first use of that key, the check fails at start instead).

Each failure message names the setting and the runbook. There is no bypass flag; `build/scripts/container-verify.sh` generates a throwaway certificate for its Production-mode run (section 7).

Development and Testing keep today's unprotected ring unless the certificate is configured. The check is a hosted service, so the `migrate` mode, which builds the host and never starts it, skips it.

### 3.4 Ring inspection

Wrapping applies only to keys created after the protector is configured; old plaintext rows would stay readable and unprotected. The check reads the ring through the context and inspects each `<key ...>` document. The framework's own output, checked in a spike on 2026-11-26, marks the two forms: a plaintext key holds a `masterKey` element (with the comment "the key below is in an unencrypted form"); a wrapped key holds an `encryptedSecret` element whose `EncryptedKey` embeds the wrapping certificate as `X509Certificate`. So a key is plaintext when it has no `encryptedSecret`, and a wrapped key names its certificate (the SHA-1 thumbprint of the embedded certificate), which is how rule 4 and the restore runbook identify the certificates a ring needs. Revocation documents (root `revocation`) are not key material and are ignored. A missing table (first start, before migrations) counts as an empty ring. The inspection is a pure function over the XML strings, unit-testable without a database; its tests feed it rings the framework itself produced, with and without the protector, so the markers are proved against real output and not a hand-written sample.

### 3.5 Rotation

Rotation adds the new certificate as active and moves the old one to `PreviousCertificates`. Keys already in the ring stay wrapped under the old certificate, and data protected by them (TOTP secrets, outbox secrets) needs them indefinitely, so a certificate that ever wrapped a key stays listed for as long as the ring exists. Re-wrapping is out of scope; the runbook states the consequence and what to back up.

### 3.6 Tests

- A ring wrapped under the certificate is readable by a second, separately built provider.
- After rotation, keys wrapped under the old certificate are still readable; new keys use the new one.
- A missing certificate, a wrong thumbprint, a certificate with no private key and an expired one each fail with the message naming the setting.
- A plaintext ring is refused; an encrypted ring and an empty ring pass; revocation rows are ignored; a ring wrapped under a certificate that is not configured is refused with its thumbprint.
- `migrate` runs in Production with no certificate configured.
- The certificates are generated in memory by the tests; no PFX is committed.

## 4. Upgrade-path test (the seed ladder)

ADR 0031 says a project is required to have this test and the template does not ship it. The two hand-written "previous migration" tests (`MfaPersistenceTests`, `ExternalLoginPersistenceTests`) check one migration each; this adds one general mechanism and leaves their specific assertions.

### 4.1 Mechanism

A fixture in `tests/TemplateName.IntegrationTests/UpgradePath/`, using the integration test SQL Server container (Docker needed, as for every integration test):

1. Create one fresh database for every context that has migrations (the same set the `migrate` mode applies), as production has one database with a schema per module; seeds therefore keep their ids unique across modules.
2. Migrate to each of the module's migrations in order. After a migration that has a seed, run `Seeds/{Module}/{MigrationName}.seed.sql`, where `{MigrationName}` is the migration class name without its timestamp (`InitialAuth`, not `20261008053947_InitialAuth`), and `{Module}` is the context's name without `DbContext` (`Auth`, `Audit`, `Notifications`, `Sample`, `Platform`). The files are embedded resources of the integration test project.
3. Migrate to head.
4. Run each seed's `{MigrationName}.verify.sql`. A verify script selects what is wrong and must return no rows: a seeded row that vanished, a value that did not survive a data-rewriting migration, an orphaned reference.
5. The test fails if any migration step throws or any verify script returns a row.

Modules migrate independently (no foreign keys cross schemas), so the order of modules does not matter.

### 4.2 Guard tests (no Docker)

They sit in the integration test project beside the fixture, read the migrations by reflection and the seeds as embedded resources, and use no container themselves. The project's assembly-wide fixture still starts the SQL Server container for every test in it, so they run wherever the integration tests run (Docker needed); a developer without Docker checks them by disabling the assembly fixture for a local run.

- Every context that has migrations has at least one seed.
- A seed file names an existing migration (a renamed migration cannot leave a stale seed), and each seed has its verify script and the other way round.
- Every migration marked `[ContractMigration]` has a seed on the migration before it, so the contract phase is proved over data.
- The `modulith-module` template ships a placeholder seed and verify script for the migration `add-module.sh` creates (`Create{Module}Schema`), so a generated module passes the guard; the module-template smoke covers it.

### 4.3 Authoring rule

A migration that rewrites or deletes existing data adds a seed in the same change, and `docs/coding-conventions.md` says so. A migration that only adds a nullable column needs none; the ladder still migrates through it. Seeds of a switchable module (Notifications, the sample) live in that module's folder and go with it, so the smoke with each switch off keeps passing.

### 4.4 Docs

ADR 0031's "Not built" entry and ADR 0033's list point to the ladder; `docs/api-compatibility.md` and `docs/deployment/migrations.md` describe it in place of "a project is required to have it".

## 5. Runbooks

Six pages in `docs/runbooks/`, with `docs/runbooks/README.md` as the index, linked from `docs/README.md`, `docs/deployment/README.md` and the production checklist. Each page has the same shape: when to use it, who may do it, steps, how to verify, how to roll back.

| Page | Content |
|---|---|
| `contract-phase.md` | The release order of `api-compatibility.md`: expand, release, check nothing reads the old column, contract with `[ContractMigration]` under the maintenance mode of ADR 0034. |
| `user-locked-out.md` | Mostly a link: the `unlock` endpoint and "Recovering access" in `docs/modules/auth.md`; what to check before unlocking. |
| `lost-mfa-device.md` | Mostly a link: `reset-mfa` and recovery codes in `auth.md`; the identity check before a reset. |
| `signing-key-rotation.md` | Signing keys already support several keys at once: add the new key as current, deploy, keep the old key listed for the token lifetime, retire it. |
| `restore-backup.md` | Restore the database and the key-ring certificate together (a restored ring cannot be read without it); how to verify; the "start a new protected ring" steps for a plaintext ring, naming the sessions, MFA secrets and queued outbox secrets it invalidates. |
| `dead-letter-replay.md` | A reviewed SQL procedure, below. |

### 5.1 Dead-letter replay

A module's outbox message is abandoned when `ProcessedAt IS NULL AND AttemptCount >= MaxAttempts` (the `platform.outbox.abandoned` gauge counts them). The procedure, per module schema:

1. `SELECT` the abandoned rows of the module's `OutboxMessages` (type, error, count) and read the errors; fix the cause first.
2. In one transaction, set `AttemptCount = 0, NextAttemptAt = NULL, LockedUntil = NULL, Error = NULL` for the chosen ids only, check `@@ROWCOUNT` against step 1, then commit.
3. Watch the gauge fall; a replayed message that fails again returns to abandoned.

Replay is safe because each consumer records `InboxMessages (MessageId, Consumer)`: a handler that already succeeded is skipped. The page says this is operator SQL against the owning module's own schema, the one sanctioned exception to "never touch another module's tables", never run from a different module's code. The page names Notifications' `Retry` endpoint (`AdminDeliveryEndpoints`) for a failed delivery, which is not an outbox replay. A line naming Notifications sits on its own `<!--#if (notifications)-->` marked line (ADR 0035).

## 6. Health endpoints and HTTPS redirection

In `src/Host/TemplateName.Api/Program.cs`, pipeline step 6 keeps `UseHsts` and wraps `UseHttpsRedirection` in a conditional branch that skips paths under `/health`. HSTS needs no change: it adds nothing to plain-HTTP responses. The pipeline-order comment in `Program.cs` and the docs that describe it are updated.

- **Exposure is unchanged.** The health endpoints were already anonymous over HTTPS and return only a status word; HTTP to a bound port now answers them instead of redirecting.
- **Integration test.** Over plain HTTP, `/health/live` and `/health/ready` answer with no `Location` header; an `/api/v1/...` route and the root still answer the HTTPS redirect.
- **Docs follow.** `docs/deployment/docker.md` loses "Do not set `HTTPS_PORT`..."; `docs/deployment/iis.md` lets `IIS_HEALTH_URL` be `http://localhost/health/ready`; the comments in `build/docker/Dockerfile` and `build/scripts/container-verify.sh` that carry the rule are corrected; ADR 0033 gets a dated amendment line.
- **Not changed.** The container still serves HTTP on 8080 behind whatever terminates TLS.

## 7. Delivery, docs and the template

- **ADR 0042** records the certificate-only key protector and amends ADR 0017; ADR 0033 gets the health amendment line and an updated "Not built" list; ADR 0031 points to the ladder.
- **Production checklist.** "Persisted; **not protected**" becomes satisfied, with the certificate settings, the startup refusal and the runbook links.
- **`container-verify.sh`** generates a throwaway PFX (OpenSSL, in a temp directory) and passes its path and password to the container, and its comment about https ports is corrected.
- **`docs/modules/auth.md`** documents the new keys; the configuration reference pages that list `Auth:` keys and the `appsettings` examples get the same, and the project-index generator is re-run.
- **Template.** The seeds of Notifications and the sample go in the `exclude` lists of `.template.config/template.json` beside those modules' tests. Nothing else here is switch-marked except the Notifications line of 5.1. The checks before merge are the default smoke and the three off-switch smokes of `CLAUDE.md`, plus `--from-package --generate-only` only if the pack excludes change.

## 8. Order of work (for the plan)

1. Health exemption (small, independent), with its test and docs.
2. Upgrade-path ladder: the fixture, the Auth and Audit seeds first, the other modules, the guards, the template starter seed.
3. Key protection: options and loader, startup check, plaintext detection, tests, `container-verify.sh`.
4. Runbooks, ADR 0042, the checklist and the other docs, then the index.

Steps 1 to 3 touch different files and can be reviewed separately; they land in one release because the checklist change depends on all of them.

## 9. Risks

- **A new startup refusal** can lock out a project that upgrades without a certificate. Intended: the release note and the checklist say so, and the message names the fix. Development and Testing are unaffected.
- **Certificate access on IIS** fails when the app pool identity cannot read the private key; the message says so and the IIS page lists the grant.
- **The ladder's run time** grows with the migration count. Each step is a SQL Server migration on a near-empty database; if it becomes slow, the plan may batch migrations that have no seed (migrate to the next seeded rung in one call).
- **An expired active certificate** stops a restart. The runbook says to rotate before expiry; the startup message gives the expiry date.
