# Runbook: restore a backup, and the Data Protection key ring

The API keeps a Data Protection key ring in the database (`auth.DataProtectionKeys`). It protects the TOTP secrets of authenticator apps (`auth.UserMfaMethods.SecretEncrypted`) and the single-use links and codes that wait in the outbox and in queued messages, so losing or replacing it matters (ADR 0017, ADR 0022). Outside Development and Testing every key in the ring is wrapped with an RSA certificate, and the host refuses to start unless the ring is readable with the certificates in the settings. A database backup alone is therefore **not** enough to restore the service: the ring in it can be read only with the certificate that wrapped each key.

The start-up check (`KeyRingStartupCheck`) lists every problem it finds in one message that names this page. It refuses to start for: no active certificate configured, a certificate that is not found, has no private key or is not RSA, an active certificate that is not valid now (the message gives both dates and the time now), a previous certificate that cannot be loaded, a key in the ring stored without a certificate (plaintext), and a key wrapped under a certificate that is not in the settings (the message gives its thumbprint). Development and Testing skip the check. There is no flag to bypass it, and in Production with no certificate configured automatic key generation is off, so the framework never stores a plaintext key.

## When

- The database is restored: after a failure, onto a new server, or to prove the restore works.
- The API refuses to start with "The Data Protection key ring is not usable".
- The active certificate will expire: rotate **before** the expiry date. An expired certificate stops the next restart, not the running process, so the failure arrives at the worst moment.
- The ring is plaintext (it was created before a certificate was configured) or the certificate is lost: start a new protected ring.

## Who

The operator who owns the database backups and the host's secrets (the certificate files, the store and the password). Nothing here is an API route. After a new ring, a holder of `auth.user.reset_mfa` resets the users who cannot recover on their own ([`lost-mfa-device.md`](lost-mfa-device.md)).

## Steps

### A. Restore the database and the certificates together

1. **Gather every certificate the ring needs**: the active one and **every previous one**, with their private keys (PFX files and their passwords, or the store entries). A certificate that ever wrapped a key stays needed for as long as the ring exists, because data protected by that key still needs it. Your backup must hold them next to the database backup; the certificate password belongs in the secret store, never beside the PFX.
2. **Restore the database** as a whole with your backup tool. Never restore the rest of the schema without `auth.DataProtectionKeys`, and keep `Auth:DataProtection:ApplicationName` unchanged.
3. **Put the certificates on the host.**
   - File: mount each PFX read-only (Docker: as a secret or a read-only volume) and give the password to the process as a secret or environment variable.
   - Store (IIS, Windows): `Import-PfxCertificate -FilePath .\keyring.pfx -CertStoreLocation Cert:\LocalMachine\My -Password $password`, then allow the app pool identity to read the private key ([`../deployment/iis.md`](../deployment/iis.md)).
4. **Configure the settings** (user secrets, environment variables or your secret store, never a file in the repository). `Source` is `Store` or `File`. The active certificate:

   ```text
   Auth__DataProtection__Certificate__Source=File
   Auth__DataProtection__Certificate__Path=/run/secrets/keyring-2026.pfx
   Auth__DataProtection__Certificate__Password=<from the secret store>
   ```

   For `Source=Store` set `Thumbprint` instead of `Path` and `Password`; `StoreLocation` defaults to `LocalMachine` and `StoreName` to `My`. Every previous certificate is an entry of `Auth:DataProtection:PreviousCertificates`, with the same fields: `Auth__DataProtection__PreviousCertificates__0__Source`, `...__0__Thumbprint` (or `__Path` and `__Password`), then `__1__...`. They are used only to unwrap.
5. **Start the API and read the message** when it refuses. It names each problem and the setting. A key wrapped under a certificate you did not configure is named by its **thumbprint** (SHA-1, upper-case hex): that is the way to list what the ring needs. Find the certificate with that thumbprint (`Get-ChildItem Cert:\LocalMachine\My | Format-Table Thumbprint, Subject, NotAfter`, or for a file `openssl pkcs12 -in keyring.pfx -nokeys -clcerts | openssl x509 -noout -fingerprint -sha1`, ignoring the colons), add it to `PreviousCertificates` and start again, until the message is gone. If the active certificate is outside its validity period, the message names both dates: use a certificate that is valid now as the active one, and move the expired one to `PreviousCertificates`.
6. If a thumbprint named in the message has no certificate anywhere, the keys wrapped under it cannot be read: go to procedure B.

### B. Start a new protected ring (a plaintext ring, or a certificate that is lost)

The old keys cannot be unwrapped, or must not be kept as plaintext. This replaces the ring, so read the consequences first.

**What stops working.** Everything protected with the old ring:

- **TOTP secrets.** Every enrolled authenticator app fails with 400 `auth.invalid_mfa_code`, at sign-in and at step-up, and each attempt counts toward the lockout (15 minutes by default). Users sign in with a **recovery code** (they are hashes, no key needed), remove the method and set up a new one; users with no recovery code left need an administrator's reset ([`lost-mfa-device.md`](lost-mfa-device.md)). A setup that was pending cannot be confirmed (409 `auth.mfa_setup_not_pending`); the user starts it again.
- **Single-use links and codes that are queued**: email confirmation and password reset links and phone codes, in the outbox or in a message waiting to be sent, cannot be decrypted. Those messages are refused or fail permanently; replaying them does not help ([`dead-letter-replay.md`](dead-letter-replay.md)). The users ask again (forgot password, resend confirmation, a new phone code).

**What keeps working.** Passwords, sessions, refresh tokens and access tokens (the signing key is separate, [`signing-key-rotation.md`](signing-key-rotation.md)), recovery codes, trusted devices and pending sign-in challenges: they are hashes or signed values, not protected with the ring.

Steps:

1. Announce it, and decide who will handle the users who need a reset.
2. **Stop every instance** of the API. A running instance keeps the old ring in memory and would write keys of its own.
3. **Back up the database** and keep the backup secret: the old ring in it can still read old data if the certificate is ever found (for a plaintext ring, it is readable by anyone with the file).
4. **Make or obtain the certificate.** The key ring needs RSA with a private key, valid now. A self-signed certificate is enough, because nothing but the template ever uses it. For a file:

   ```bash
   openssl req -x509 -newkey rsa:3072 -nodes -keyout keyring.key -out keyring.crt -days 730 -subj "/CN=TemplateName key ring"
   openssl pkcs12 -export -inkey keyring.key -in keyring.crt -out keyring.pfx
   ```

   (the second command asks for the password; keep it in the secret store). On Windows: `New-SelfSignedCertificate -Subject "CN=TemplateName key ring" -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(2)`. Store the PFX and its password in your backup and secret store **now**, next to the database backup; they are the new ring's only key.
5. **Configure** `Auth:DataProtection:Certificate` as in procedure A, step 4, on every instance. Leave `PreviousCertificates` empty: nothing wrapped by an older certificate is kept.
6. **Clear the ring**, with the API stopped:

   ```sql
   SELECT COUNT(*) FROM auth.DataProtectionKeys;
   DELETE FROM auth.DataProtectionKeys;
   ```

7. **Start one instance.** With a certificate configured the framework creates a new key at start and wraps it. The check passes when the ring holds only wrapped keys under the configured certificate. Then start the rest; they read the same ring from the database.
8. Hand the affected users over to the steps above, starting with the ones who have no recovery code.

### C. Rotate the active certificate (on a schedule, before the expiry date)

Rotation never re-wraps the existing keys: they stay wrapped under the old certificate, and the data they protect needs it.

1. Make a new certificate as in procedure B, step 4, and import or mount it on every host.
2. In the settings, set the **new** certificate as `Auth:DataProtection:Certificate` and **move the old one to the end of `Auth:DataProtection:PreviousCertificates`** (add it as the next index). Never drop it from the list, even after it expires: a certificate that ever wrapped a key stays listed for as long as the ring exists. An expired certificate may stay in `PreviousCertificates`; only the active one must be valid.
3. Deploy. New keys are wrapped under the new certificate, and the check reads both. The framework creates a new key only when the current one nears its expiry (90 days by default), so for a while every key is still wrapped under the old certificate: keep it available all the same.
4. Add the new certificate to the backup set (procedure A, step 1). Set a reminder for the next rotation well before the new certificate's expiry date.

## Verify

- The API starts with no key ring message, on every instance.
- `GET /health/ready` answers 200.
- A user with an authenticator app signs in with a current code (the TOTP secret is readable), and a fresh `POST /api/v1/auth/password/forgot` produces a link that works (the shared protector round-trips).
- After a restore: a message that was queued before the backup is sent or retried without a decrypt error. After procedure B: `SELECT COUNT(*) FROM auth.DataProtectionKeys;` is at least 1 and every key's `Xml` holds an `encryptedSecret` element (`SELECT COUNT(*) FROM auth.DataProtectionKeys WHERE Xml NOT LIKE N'%encryptedSecret%' AND Xml LIKE N'<key %';` is 0).
- After procedure C: a sign-in with TOTP works for a user enrolled before the rotation (the old certificate still unwraps their key).

## Roll back

- Procedure A: nothing changes in the database; a wrong certificate list is corrected in the settings and the API started again.
- Procedure B: stop every instance, restore the database backup of step 3 and put the old certificate settings back. That puts the old ring back with its limits: a plaintext ring is still refused in Production, and a ring whose certificate is lost is still unreadable. Users who set up a new authenticator under the new ring lose that secret by the restore. In practice a new ring is not rolled back; it is completed.
- Procedure C: put the old certificate back as the active one and remove the new one from `PreviousCertificates` only if you are sure no key was wrapped under the new certificate yet (the ring creates a key when the current one nears its expiry, and a start of an empty ring creates one). If you are not sure, keep both listed: a listed certificate costs nothing.
