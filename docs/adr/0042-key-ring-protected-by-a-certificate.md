# 0042. The Data Protection key ring is wrapped with a certificate, and production refuses to run without one

- Status: Accepted
- Date: 2026-11-26
- Deciders: Lee Jia Le

## Context
ADR 0017 keeps the Data Protection key ring in `auth.DataProtectionKeys` so every instance reads the same keys, and accepted that the ring is not encrypted at rest: anyone with a copy of the database reads the keys and so decrypts what they protect, the TOTP secrets of authenticator apps (ADR 0022) and the single-use links in queued outbox rows (ADR 0017, ADR 0020). ADR 0033 left a key protector on its "Not built" list, the production checklist carried the line as not satisfied, and the user decided on 2026-11-20 that this gates 1.0.0. The ring is also what a restored backup has to be read with, so whatever wraps it has to be backed up and rotated, and the operator needs a procedure for it.

## Options considered
**What wraps the keys.** (1) A certificate, from a PFX file or the certificate store; (2) Azure Key Vault; (3) Windows DPAPI; (4) nothing, with the checklist asking the project to add a protector.

**Where the code lives.** (1) In Auth, which owns the ring; (2) a new building-block package.

**How a certificate is named.** (1) One thumbprint in an environment variable; (2) a source (`Store` or `File`) with the fields it needs, for an active certificate and a list of previous ones.

**When it is required.** (1) Never, a setting turns it on; (2) a start-up refusal outside Development and Testing; (3) the refusal, with a flag that bypasses it.

Rejected: **Key Vault** and **DPAPI** as built-in options (a cloud account or a Windows-only dependency in a template that must run anywhere; a project swaps the protector, see the consequences), **no protector** (leaves the gap the checklist already names). In Auth, not a **new building-block package**: Auth is the only module with a ring, so a package would be a seam with one user. A **single thumbprint** in an environment variable is rejected because Docker has no certificate store and rotation needs a list of certificates. A **bypass flag** is rejected because a flag that is easy to set is the first thing a rushed release sets, and it would leave a plaintext ring in Production; Development and Testing already cover the cases where the ring may stay unwrapped.

## Decision
We chose a certificate, in Auth, named by source, and a start-up refusal with no bypass.

- **Certificate only.** `Auth:DataProtection:Certificate` (`Source` `Store` or `File`; `Thumbprint`, `StoreLocation`, `StoreName` for a store; `Path` and `Password` for a PFX) is the active RSA certificate with a private key. `Auth:DataProtection:PreviousCertificates` is a list of the same shape, used only to unwrap. `AddAuthModule` loads them (`KeyRingCertificates.Load`, options `KeyRingCertificateOptions`) and calls the framework's `ProtectKeysWithCertificate` and `UnprotectKeysWithAnyCertificate` with the loaded certificates. A certificate that cannot be loaded is recorded as a problem, never thrown, so registration does not fail; the check reports it. The `migrate` mode builds the host and never starts it, so it needs no certificate.
- **The host refuses to start** outside Development and Testing (the pair `SigningKeyProvider` already treats as non-production) when `KeyRingStartupCheck` finds any of: a settings problem (no file, wrong password, no private key, not RSA, an unreadable store); no active certificate; an active certificate outside its `NotBefore` to `NotAfter` at the start (the message gives both dates and the time now); a **plaintext key** in the ring (a `key` document with no `encryptedSecret`); a key wrapped under a certificate that is neither the active nor a previous one (the message names its thumbprint). It lists every problem in one message that names the setting and [`docs/runbooks/restore-backup.md`](../runbooks/restore-backup.md). There is no bypass flag: Development and Testing keep the unwrapped ring because they are not the places the ring protects anything.
- **No plaintext key is ever created in Production.** With no active certificate and an environment other than Development or Testing, automatic key generation is switched off (`KeyRingCertificates.RequireCertificateToCreateKeys`). The framework's own start-up step makes a key when the ring has none, and it runs before the check; without this, a host started without a certificate would save a plaintext key into the shared table first, and the check would then refuse on it at every later start, even after the configuration was fixed, while another instance could already have adopted it. Without a key the framework logs its failure and the check refuses with its own message.
- **A plaintext ring is refused, not converted.** The template has no production data to convert and a live in-place conversion is a risk with no customer. The restore runbook has the steps for starting a new protected ring and names what it invalidates (enrolled authenticators, queued single-use secrets).
- **Rotation lists.** A new certificate becomes active and the old one moves to `PreviousCertificates`, where it stays for as long as the ring exists ([`docs/runbooks/restore-backup.md`](../runbooks/restore-backup.md), procedure C).
- **Plaintext is told from wrapped by the framework's own output.** The inspection (`KeyRingInspector`) is a pure function over the stored XML; its tests feed it rings the framework produced with and without the protector.

## Consequences
- **A new start-up refusal.** A project that upgrades without a certificate no longer starts in Production; the message names the setting and the runbook, and the release notes and the production checklist say so. Development and Testing are unaffected. `container-verify.sh` makes a throwaway PFX for its Production run and proves the refusal without one.
- **The framework wraps with RSA PKCS#1 v1.5 key transport** (`ProtectKeysWithCertificate` offers no choice). The certificate's private key is used only to unwrap keys whenever they are read from the database (at start and whenever the framework refreshes its key cache); no request carries input to it. Accepted, because the framework has no other certificate-based protector.
- **A certificate that ever wrapped a key stays needed** for as long as the ring exists, because data protected by that key (a TOTP secret) still needs it, so it stays in `PreviousCertificates` and in the backup set, with its password in a secret store. Re-wrapping old keys is not built. A lost certificate means a new ring and the consequences in the runbook.
- **An expired active certificate stops the next restart**, not the running process; the runbook says to rotate before the expiry and the message gives the dates.
- **Certificate access is deployment work.** On IIS the application pool identity must be able to read the certificate's private key; in Docker the PFX is mounted read-only and its password is a secret ([`docs/deployment/iis.md`](../deployment/iis.md#the-key-ring-certificate), [`docs/deployment/docker.md`](../deployment/docker.md#the-key-ring-certificate)).
- **Key Vault and DPAPI are project-side swaps.** A project that wants either replaces the protector call in its own `AuthModule`; the start-up check and the `KeyRingCertificates` types then change with it. Nothing here needs Azure or a particular certificate authority; a self-signed certificate is enough, because nothing but the template uses it.
- **Tests.** Unit tests cover the loader, the inspector and the check over certificates generated in memory (no PFX is committed); integration tests cover the host in Production with and without a certificate and `migrate` with none.

### Known limits
- A key whose `encryptedSecret` carries no parseable certificate is neither named nor refused by the check; the framework fails when it first needs that key.
- A valid but expired certificate does not stop the framework's own start-up step from saving a new key wrapped with it before the check refuses; the saved key is wrapped, not plaintext, and the next start with a valid certificate reads it.
- The check reads the ring once, at start; a plaintext key written afterwards (by a hand-edited row, say) is seen at the next start.

## Follow-ups
- Re-wrapping old keys under a new certificate, so an old certificate can be retired.
- A built-in Key Vault or DPAPI protector, if projects ask for one.
- Amends [ADR 0017](0017-outbox-secrets-protected-with-data-protection.md) (its trade-off of an unencrypted ring) and removes the key-ring item from the "Not built" list of [ADR 0033](0033-delivery-pipeline.md).
