# Production checklist

The checklist of the blueprint (section 16.3), each line with the code or setting that enforces it, what is left to you, and where the template falls short. "Enforced" means a test, a start-up validation or the pipeline refuses the wrong state; "yours" means nothing in the template checks it. Nothing on this page was verified on a real production system; the `container` job proves only that the image starts in Production against an empty SQL Server with the minimum configuration ([`docker.md`](docker.md#verifying-an-image-by-hand)).

| Line | Status |
|---|---|
| [`ASPNETCORE_ENVIRONMENT=Production`](#aspnetcore_environmentproduction) | Default of the image; yours elsewhere |
| [Scalar UI and a job dashboard disabled](#scalar-ui-and-a-job-dashboard-disabled-or-protected) | Enforced; there is no job dashboard |
| [HTTPS and HSTS, valid certificate](#https-hsts-and-a-valid-certificate) | Code enforces redirection and HSTS; the certificate is yours |
| [Migrations applied by a command, not on startup](#migrations-applied-by-a-command-not-on-startup) | Enforced by the defaults and the pipeline |
| [Secrets from the environment, none in files](#secrets-from-the-environment-none-in-files) | Partly enforced |
| [Data Protection keys persisted and protected](#data-protection-keys-persisted-and-protected) | Persisted; protected by a certificate you supply, which the host requires |
| [Forwarded headers behind a proxy](#forwarded-headers-behind-a-proxy-or-load-balancer) | Yours, validated when set |
| [Health checks wired](#health-checks-wired-to-the-load-balancer-or-monitor) | Endpoints and image check exist; the wiring is yours |
| [Log level and masking](#log-level-information-and-masking) | Defaults set; masking is limited |
| [Backups and a tested restore](#backups-and-a-tested-restore) | Yours |

## `ASPNETCORE_ENVIRONMENT=Production`

The environment of a .NET host that is not told otherwise is `Production`, and the image does not change it (`container-verify.sh` sets it explicitly). Production differs in code, so a wrong value is felt: the host refuses to start without a JWT signing key (`Auth:Jwt:SigningKeys`, validated on start; only Development and Testing may use an ephemeral one) and without a usable key ring certificate (`Auth:DataProtection:Certificate`, [below](#data-protection-keys-persisted-and-protected)), OpenAPI and Scalar are not mapped, HSTS and HTTPS redirection are on, logs are compact JSON, and the migrations do not run on start. On IIS set the variable on the app pool; leaving it unset gives Production too. **Yours:** check the deployed value (`Development` in a deployed environment exposes the API reference and the exception details of a 500 response).

## Scalar UI and a job dashboard disabled or protected

`Program.cs` maps `MapOpenApi` and `MapScalarApiReference` only when the environment is not Production, and `HostTests.Scalar_is_not_mapped_in_production` starts the host in Production and checks it. The template has **no job dashboard**: recurring jobs run on its own scheduler (ADR 0026), not on Hangfire, so there is nothing to protect. Nothing to do.

## HTTPS, HSTS and a valid certificate

`Program.cs` calls `UseHsts` and `UseHttpsRedirection` whenever the environment is not Development; HSTS acts on requests the application sees as https. The **certificate and TLS are not the application's**: they end at the proxy or load balancer, or at the IIS https binding. **Yours:** a valid certificate on that endpoint, and the proxy forwarding the scheme ([below](#forwarded-headers-behind-a-proxy-or-load-balancer)). The container needs no https port: with none configured, `UseHttpsRedirection` only logs a warning and passes the request on ([`docker.md`](docker.md#running-it)). `/health/*` is exempt from the redirect, so a health probe over plain HTTP is answered even when an https port is known, in the container and on IIS (an https binding gives the application its https port through `ASPNETCORE_ANCM_HTTPS_PORT`); every other path is redirected. `IIS_HEALTH_URL` can therefore be `http://localhost/health/ready` (an http binding, and `localhost` in `AllowedHosts`) or the site's public `https://<public host>/health/ready` ([`iis.md`](iis.md#health-url); [ADR 0033](../adr/0033-delivery-pipeline.md), amended 2026-11-26).

## Migrations applied by a command, not on startup

`Database:ApplyMigrationsOnStartup` is `false` in `appsettings.json` and nothing but a local setting turns it on. The pipeline applies migrations with the `migrate` mode before the roll-out (staging and production jobs, and the IIS script); there are no EF migration bundles, the mode runs the same code as development and tests (ADR 0006, [`migrations.md`](migrations.md)). **Yours:** make sure no deployed environment sets `Database__ApplyMigrationsOnStartup=true`, and that `MIGRATION_CONNECTION_STRING` is the DDL login while the running API has a login without DDL rights (unproven; see [`migrations.md`](migrations.md#two-logins)).

## Secrets from the environment, none in files

- The hard rule of the repository: no secret in a file (`CLAUDE.md`); `appsettings*.json` holds none.
- `gitleaks.yml` scans every pull request and the history on `main`; GitHub secret scanning with push protection is switched on for the template repository only; a generated project switches it on in its own repository settings.
- A missing required secret stops the host: `ConnectionStrings:Database` and the signing key are validated on start; a social-login provider that is enabled without its settings stops it too, and the Facebook app secret comes from user secrets or the environment, never a file.
- `container-verify.sh` generates its SQL password and signing key per run, masks them in the Actions log and writes them to no file; the deploy jobs pass `MIGRATION_CONNECTION_STRING` by name from an environment secret.

**Yours:** the template reads secrets from environment variables and user secrets; it ships no key vault integration. Inject them from your vault into the container, the app pool or the orchestrator, and keep `Auth:Seed:AdminPassword` set only for the first start.

<!--#if (notifications)-->
## Push credentials, if push is on

- Push is off by default (`Notifications:Push:Enabled`); a project that turns it on sets `Notifications:Push:Fcm:ProjectId` and either `Notifications:Push:Fcm:CredentialsJson` (the service-account JSON, from the environment or the vault, never a file) or Google's application default credentials on the host. The host does not start with push on and no project id, or with credentials that are not a service-account JSON ([notifications](../modules/notifications.md#push-channel)).
- iOS devices are reached through FCM: upload the app's APNs key to the Firebase project, or FCM answers `THIRD_PARTY_AUTH_ERROR` and every iOS push is retried and then dead-lettered (`fcm_third_party_auth_error`).

**Yours:** create the Firebase project and its service account (role Firebase Cloud Messaging API Admin), keep the key in your vault, and rotate it as you rotate other keys.

## SMS, if phone numbers are on

- SMS and phone numbers are off by default (`Notifications:Sms:Enabled`, `Auth:Phone:Enabled`); a project turns them on together and sets `Notifications:Sms:Twilio:AccountSid`, `Notifications:Sms:Twilio:AuthToken` (from the environment or the vault, never a file) and exactly one of `Notifications:Sms:Twilio:From` (a number in E.164) or `Notifications:Sms:Twilio:MessagingServiceSid`. The host does not start with SMS on and any of these missing ([notifications](../modules/notifications.md#sms-channel)).
- Set `Auth:Phone:AllowedRegions` to the countries you serve; empty accepts every region.
- Phone sign-in (`Auth:Phone:SignInEnabled`, off by default) turns a verified number into a way to sign in: turn it on only after the four Twilio steps below, decide whether `Auth:Phone:SignInDeniedRoles` (`SuperAdmin`, `Admin` by default) fits your roles, and encourage TOTP for accounts a SIM swap must not open ([ADR 0041](../adr/0041-phone-number-sign-in.md)).
- **Required before SMS goes live**, at Twilio, because the per-user limits bound one account and many accounts can still pump premium-rate numbers:
  - turn on **SMS Pumping Protection** for Programmable Messaging;
  - limit **geo permissions** to the regions in `Auth:Phone:AllowedRegions`;
  - set a **spend limit**: a usage trigger on SMS spend, and auto-recharge off or capped;
  - send **alerts** to the operators: Twilio's usage triggers, plus the app's signals (SMS dead letters, a `twilio_20003` or `twilio_21606` configuration fault, a jump in `auth.phone_verification` or `auth.phone_sign_in` notifications created).
- SMS is at least once and never guaranteed: `Sent` means Twilio accepted the text, and a timed-out send can arrive twice with the same code ([delivery guarantees](../modules/notifications.md#sms-channel)).

**Yours:** create the Twilio account and sender (a number or a Messaging Service, registered for the countries you text, which some require), keep the auth token in your vault, rotate it as you rotate other keys, and do the four steps above.
<!--#endif-->

## Data Protection keys persisted and protected

**Persisted: yes.** The key ring is stored in `auth.DataProtectionKeys` (`PersistKeysToDbContext<AuthDbContext>`, ADR 0017), so every instance and every restart reads the same keys; keep `Auth:DataProtection:ApplicationName` the same on every instance and never truncate the table or restore the rest of the schema without it ([Auth module](../modules/auth.md#recovering-access)).

**Protected: yes, by a certificate you supply.** Each key in the ring is wrapped with an RSA certificate (ASP.NET Core's `ProtectKeysWithCertificate`, [ADR 0042](../adr/0042-key-ring-protected-by-a-certificate.md)), so a copy of the database no longer reads the TOTP secrets of authenticator apps (ADR 0022) or the single-use links in pending outbox rows (ADR 0017, ADR 0020) without the certificate. The template ships no certificate; the key ring needs one that is **RSA, has a private key and is valid now**, and a self-signed one is enough, because nothing but the template uses it.

| Setting | Meaning |
|---|---|
| `Auth:DataProtection:Certificate:Source` | `Store` or `File` |
| `Auth:DataProtection:Certificate:Thumbprint`, `StoreLocation` (default `LocalMachine`), `StoreName` (default `My`) | `Store`: a certificate in the certificate store; on IIS the app pool identity needs read access to its private key ([`iis.md`](iis.md#the-key-ring-certificate)) |
| `Auth:DataProtection:Certificate:Path`, `Password` | `File`: a PFX; in Docker mounted read-only, the password a secret and never in the image or a committed file ([`docker.md`](docker.md#the-key-ring-certificate)) |
| `Auth:DataProtection:PreviousCertificates` | A list of the same shape: every certificate that ever wrapped a key, used only to unwrap |

**The host refuses to start** outside Development and Testing, with one message that names the setting and [`restore-backup.md`](../runbooks/restore-backup.md), when: no active certificate is configured or one cannot be loaded (not found, wrong password, no private key, not RSA); the active certificate is not valid now (the message gives its dates); the ring holds a **plaintext key** (one created before a certificate was configured); or a key in the ring is wrapped under a certificate that is not configured (the message gives its thumbprint). There is no flag to bypass it, and in Production with no certificate automatic key generation is off, so no plaintext key is ever saved. The `migrate` mode never starts the host, so it needs no certificate. Development and Testing keep an unwrapped ring unless a certificate is configured. **Upgrading a project that already ran in Production without a certificate** therefore stops at start, even with a certificate configured, because the existing ring holds a plaintext key and no converter is built: follow [procedure B of the restore runbook](../runbooks/restore-backup.md#b-start-a-new-protected-ring-a-plaintext-ring-or-a-certificate-that-is-lost) (a new ring, which ends enrolled authenticators and queued single-use links).

**Yours:**

- Make or buy the certificate, keep it **and every certificate that ever wrapped a key** with its password in a secret store and in the backup set next to the database backups: a restored ring cannot be read without them.
- Rotate **before the expiry date**: an expired active certificate stops the next restart, not the running process. Rotation moves the old certificate to `PreviousCertificates`, in two phases for a rolling deploy or a web farm (list the new one on every instance first, then make it active); it is never dropped while the ring exists ([`restore-backup.md`](../runbooks/restore-backup.md), procedure C).
- Not built: re-wrapping old keys under a new certificate, and Key Vault or DPAPI protectors (a project swaps the protector in its `AuthModule`, ADR 0042).

## Forwarded headers behind a proxy or load balancer

`ForwardedHeaders:KnownProxies` and `ForwardedHeaders:KnownNetworks` are empty by default, so only a loopback proxy is trusted and a proxy anywhere else has its `X-Forwarded-For` and `X-Forwarded-Proto` ignored. Each entry is validated on start (a valid address or CIDR). **Yours:** set the proxy's address or network, make the proxy send both headers, and set `AllowedHosts` to the public host names **plus `127.0.0.1`** for a container (the image's health check sends that host; without it the check gets 400 and the container is restarted), plus the host of `IIS_HEALTH_URL` on IIS (the public host, so already listed) and any host a load balancer's probe sends ([`docker.md`](docker.md#running-it), [`services/api.md`](../services/api.md#forwardedheaders)).

## Health checks wired to the load balancer or monitor

`/health/live` (the process answers) and `/health/ready` (the readiness checks: one per database schema, and any other check tagged `ready`) exist, are anonymous and exempt from rate limiting. The image's `HEALTHCHECK` probes `/health/live`, `container-verify.sh` and the CD smoke test call `/health/ready`, and the IIS script polls the URL you give it. **Yours:** point the load balancer or uptime monitor at `/health/ready` (a database outage then takes the instance out of rotation) and alert on it.
<!--#if (caching == "redis")-->
With Redis down `/health/ready` answers 200 `Degraded`, by design ([`services/api.md`](../services/api.md#health)).
<!--#endif-->

## Log level Information, and masking

`appsettings.json` sets `Serilog:MinimumLevel:Default` to `Information` (with `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore` and `System` at `Warning`); Production writes compact JSON to the console and sets `Application` and `Environment` on every event. Destructured objects have the values of properties named like `password`, `token`, `secret`, `otp` or `apikey` replaced by `***` (`SensitiveDataDestructuringPolicy`, tested by `SensitiveDataDestructuringPolicyTests`; `AccessTokenRedactionTests` covers the access token), and the modules' tests check that their flows log no token, link, code or secret. **That is not blanket masking**: free text in a message, a header you log yourself or a property with another name is not covered. **Yours:** read the production logs of a real sign-in and password reset once, and check the sink (Seq, Loki, a cloud service) for retention and access.

## Backups and a tested restore

Nothing in the template takes or tests a backup. **Yours:** schedule database backups and restore one into a scratch database. Back the database up and restore it **as a whole**: the schema holds `auth.DataProtectionKeys`, and a restore that loses it makes every stored TOTP secret unreadable ([Auth module](../modules/auth.md#recovering-access)). The backups contain the key ring wrapped with your certificate ([above](#data-protection-keys-persisted-and-protected)): keep the certificate with the backup set (but not its password beside the PFX), and protect both like secrets. Retention of rows inside the database (outbox, inbox, audit trails) is a different matter and is configured per kind of data ([`services/api.md`](../services/api.md#retention)).

The procedures for restoring a backup, the key ring and its certificate, a contract-phase release and the other operations are the [runbooks](../runbooks/README.md); read them before you need them.

## Also before the first release

- [ ] Every item above has an owner and a date.
- [ ] The key ring certificate is configured on every instance, its password is a secret, and the certificate and every previous one are in the backup set; the expiry date of the active one is in a calendar ([above](#data-protection-keys-persisted-and-protected)).
- [ ] `Auth:Links`,
  <!--#if (notifications)-->
  `Notifications:Email`,
  <!--#endif-->
  `Cors:AllowedOrigins` and the other environment-specific settings name your real hosts; the defaults point at `localhost`.
- [ ] The runtime database login has no DDL rights, and the migration login exists only as the environment secret.
- [ ] `build/deploy/maintenance.html` carries your brand and contact; `maintenance.sh` is written, and its `on` passes `/health/live` and `/health/ready` through to the API ([`docker.md`](docker.md#the-maintenance-hook)). A release in maintenance mode fails before `migrate` without the hook. A release that never completed its production deploy is still seen by the next one, because detection compares with the last successful production deployment; only a project that deployed by hand before the pipeline existed needs `maintenance: on` once, for its first pipeline release ([`README.md`](README.md#maintenance-mode)).
- [ ] `roll-out.sh` is written, `DEPLOY_ENABLED` is set only after it is, and `production` (ideally `staging` too) has required reviewers.
- [ ] Each environment's **Deployment branches and tags** allows only `main` and `v*` tags, and a tag ruleset protects `v*` (who may create, move or delete one). The workflow's own ref check guards against mistakes, not a malicious writer ([`README.md`](README.md#what-to-configure)).
- [ ] `MIGRATION_CONNECTION_STRING` is a secret of each environment, not a repository secret (a release run, called from `release.yml`, is not handed repository secrets).
- [ ] `AllowedHosts` lists the public host names and `127.0.0.1` ([`docker.md`](docker.md#running-it)).
- [ ] The checks `container`, `codeql` and `gitleaks` are required on `main`, and `.gitleaks.toml` and `.github/` have a CODEOWNERS entry ([`README.md`](README.md#codeql-and-gitleaks)).
- [ ] For IIS: the [manual checklist](iis.md#manual-checklist-for-a-real-server) has been run on a test server.
