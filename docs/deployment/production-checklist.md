# Production checklist

The checklist of the blueprint (section 16.3), each line with the code or setting that enforces it, what is left to you, and where the template falls short. "Enforced" means a test, a start-up validation or the pipeline refuses the wrong state; "yours" means nothing in the template checks it. Nothing on this page was verified on a real production system; the `container` job proves only that the image starts in Production against an empty SQL Server with the minimum configuration ([`docker.md`](docker.md#verifying-an-image-by-hand)).

| Line | Status |
|---|---|
| [`ASPNETCORE_ENVIRONMENT=Production`](#aspnetcore_environmentproduction) | Default of the image; yours elsewhere |
| [Scalar UI and a job dashboard disabled](#scalar-ui-and-a-job-dashboard-disabled-or-protected) | Enforced; there is no job dashboard |
| [HTTPS and HSTS, valid certificate](#https-hsts-and-a-valid-certificate) | Code enforces redirection and HSTS; the certificate is yours |
| [Migrations applied by a command, not on startup](#migrations-applied-by-a-command-not-on-startup) | Enforced by the defaults and the pipeline |
| [Secrets from the environment, none in files](#secrets-from-the-environment-none-in-files) | Partly enforced |
| [Data Protection keys persisted and protected](#data-protection-keys-persisted-and-protected) | Persisted; **not protected** |
| [Forwarded headers behind a proxy](#forwarded-headers-behind-a-proxy-or-load-balancer) | Yours, validated when set |
| [Health checks wired](#health-checks-wired-to-the-load-balancer-or-monitor) | Endpoints and image check exist; the wiring is yours |
| [Log level and masking](#log-level-information-and-masking) | Defaults set; masking is limited |
| [Backups and a tested restore](#backups-and-a-tested-restore) | Yours |

## `ASPNETCORE_ENVIRONMENT=Production`

The environment of a .NET host that is not told otherwise is `Production`, and the image does not change it (`container-verify.sh` sets it explicitly). Production differs in code, so a wrong value is felt: the host refuses to start without a JWT signing key (`Auth:Jwt:SigningKeys`, validated on start; only Development and Testing may use an ephemeral one), OpenAPI and Scalar are not mapped, HSTS and HTTPS redirection are on, logs are compact JSON, and the migrations do not run on start. On IIS set the variable on the app pool; leaving it unset gives Production too. **Yours:** check the deployed value (`Development` in a deployed environment exposes the API reference and the exception details of a 500 response).

## Scalar UI and a job dashboard disabled or protected

`Program.cs` maps `MapOpenApi` and `MapScalarApiReference` only when the environment is not Production, and `HostTests.Scalar_is_not_mapped_in_production` starts the host in Production and checks it. The template has **no job dashboard**: recurring jobs run on its own scheduler (ADR 0026), not on Hangfire, so there is nothing to protect. Nothing to do.

## HTTPS, HSTS and a valid certificate

`Program.cs` calls `UseHsts` and `UseHttpsRedirection` whenever the environment is not Development; HSTS acts on requests the application sees as https. The **certificate and TLS are not the application's**: they end at the proxy or load balancer, or at the IIS https binding. **Yours:** a valid certificate on that endpoint, and the proxy forwarding the scheme ([below](#forwarded-headers-behind-a-proxy-or-load-balancer)). Do not set an https port inside the container: the health check would get a redirect and report `unhealthy (307)` ([`docker.md`](docker.md#running-it)). With no https port configured, `UseHttpsRedirection` only logs a warning and passes the request on. On IIS an https binding gives the application its https port (the ASP.NET Core Module sets `ASPNETCORE_ANCM_HTTPS_PORT`), so a plain `http://localhost` health URL is redirected: `IIS_HEALTH_URL` must be the site's public https URL, `https://<public host>/health/ready`, with a certificate valid for that host ([`iis.md`](iis.md#health-url)). Exempting `/health/*` from the redirect is an open follow-up ([ADR 0033](../adr/0033-delivery-pipeline.md)).

## Migrations applied by a command, not on startup

`Database:ApplyMigrationsOnStartup` is `false` in `appsettings.json` and nothing but a local setting turns it on. The pipeline applies migrations with the `migrate` mode before the roll-out (staging and production jobs, and the IIS script); there are no EF migration bundles, the mode runs the same code as development and tests (ADR 0006, [`migrations.md`](migrations.md)). **Yours:** make sure no deployed environment sets `Database__ApplyMigrationsOnStartup=true`, and that `MIGRATION_CONNECTION_STRING` is the DDL login while the running API has a login without DDL rights (unproven; see [`migrations.md`](migrations.md#two-logins)).

## Secrets from the environment, none in files

- The hard rule of the repository: no secret in a file (`CLAUDE.md`); `appsettings*.json` holds none.
- `gitleaks.yml` scans every pull request and the history on `main`; GitHub secret scanning with push protection is switched on for the template repository only; a generated project switches it on in its own repository settings.
- A missing required secret stops the host: `ConnectionStrings:Database` and the signing key are validated on start; a social-login provider that is enabled without its settings stops it too, and the Facebook app secret comes from user secrets or the environment, never a file.
- `container-verify.sh` generates its SQL password and signing key per run, masks them in the Actions log and writes them to no file; the deploy jobs pass `MIGRATION_CONNECTION_STRING` by name from an environment secret.

**Yours:** the template reads secrets from environment variables and user secrets; it ships no key vault integration. Inject them from your vault into the container, the app pool or the orchestrator, and keep `Auth:Seed:AdminPassword` set only for the first start.

## Data Protection keys persisted and protected

**Persisted: yes.** The key ring is stored in `auth.DataProtectionKeys` (`PersistKeysToDbContext<AuthDbContext>`, ADR 0017), so every instance and every restart reads the same keys; keep `Auth:DataProtection:ApplicationName` the same on every instance and never truncate the table or restore the rest of the schema without it ([Auth module](../modules/auth.md#recovering-access)).

**Protected: no.** The ring is not encrypted at rest. The code has no `ProtectKeysWith...` call, and this delivery plan did not add one. Anyone with a copy of the database can read the keys and so decrypt what they protect: the TOTP secrets of authenticator apps (ADR 0022) and the single-use links in pending outbox rows (ADR 0017, ADR 0020). Until your project adds a key protector (a certificate, Azure Key Vault, or on Windows a DPAPI protector), treat every database backup and every copy of the database as a secret. Adding a protector is a code change with its own ADR; do it before real users enrol an authenticator app. **This line is not satisfied by the template.**

## Forwarded headers behind a proxy or load balancer

`ForwardedHeaders:KnownProxies` and `ForwardedHeaders:KnownNetworks` are empty by default, so only a loopback proxy is trusted and a proxy anywhere else has its `X-Forwarded-For` and `X-Forwarded-Proto` ignored. Each entry is validated on start (a valid address or CIDR). **Yours:** set the proxy's address or network, make the proxy send both headers, and set `AllowedHosts` to the public host names **plus `127.0.0.1`** for a container (the image's health check sends that host; without it the check gets 400 and the container is restarted), plus the host of `IIS_HEALTH_URL` on IIS (the public host, so already listed) and any host a load balancer's probe sends ([`docker.md`](docker.md#running-it), [`services/api.md`](../services/api.md#forwardedheaders)).

## Health checks wired to the load balancer or monitor

`/health/live` (the process answers) and `/health/ready` (a check per database schema, and Redis when enabled) exist, are anonymous and exempt from rate limiting. The image's `HEALTHCHECK` probes `/health/live`, `container-verify.sh` and the CD smoke test call `/health/ready`, and the IIS script polls the URL you give it. **Yours:** point the load balancer or uptime monitor at `/health/ready` (a database outage then takes the instance out of rotation) and alert on it. With Redis down `/health/ready` answers 200 `Degraded`, by design ([`services/api.md`](../services/api.md#health)).

## Log level Information, and masking

`appsettings.json` sets `Serilog:MinimumLevel:Default` to `Information` (with `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore` and `System` at `Warning`); Production writes compact JSON to the console and sets `Application` and `Environment` on every event. Destructured objects have the values of properties named like `password`, `token`, `secret`, `otp` or `apikey` replaced by `***` (`SensitiveDataDestructuringPolicy`, tested by `SensitiveDataDestructuringPolicyTests`; `AccessTokenRedactionTests` covers the access token), and the modules' tests check that their flows log no token, link, code or secret. **That is not blanket masking**: free text in a message, a header you log yourself or a property with another name is not covered. **Yours:** read the production logs of a real sign-in and password reset once, and check the sink (Seq, Loki, a cloud service) for retention and access.

## Backups and a tested restore

Nothing in the template takes or tests a backup. **Yours:** schedule database backups and restore one into a scratch database. Back the database up and restore it **as a whole**: the schema holds `auth.DataProtectionKeys`, and a restore that loses it makes every stored TOTP secret unreadable ([Auth module](../modules/auth.md#recovering-access)). The backups contain the unencrypted key ring ([above](#data-protection-keys-persisted-and-protected)), so protect them like secrets. Retention of rows inside the database (outbox, inbox, audit trails) is a different matter and is configured per kind of data ([`services/api.md`](../services/api.md#retention)).

## Also before the first release

- [ ] Every item above has an owner and a date.
- [ ] `Auth:Links`, `Notifications:Email`, `Cors:AllowedOrigins` and the other environment-specific settings name your real hosts; the defaults point at `localhost`.
- [ ] The runtime database login has no DDL rights, and the migration login exists only as the environment secret.
- [ ] `roll-out.sh` is written, `DEPLOY_ENABLED` is set only after it is, and `production` (ideally `staging` too) has required reviewers.
- [ ] Each environment's **Deployment branches and tags** allows only `main` and `v*` tags, and a tag ruleset protects `v*` (who may create, move or delete one). The workflow's own ref check guards against mistakes, not a malicious writer ([`README.md`](README.md#what-to-configure)).
- [ ] `MIGRATION_CONNECTION_STRING` is a secret of each environment, not a repository secret (a release run, called from `release.yml`, is not handed repository secrets).
- [ ] `AllowedHosts` lists the public host names and `127.0.0.1` ([`docker.md`](docker.md#running-it)).
- [ ] The checks `container`, `codeql` and `gitleaks` are required on `main`, and `.gitleaks.toml` and `.github/` have a CODEOWNERS entry ([`README.md`](README.md#codeql-and-gitleaks)).
- [ ] For IIS: the [manual checklist](iis.md#manual-checklist-for-a-real-server) has been run on a test server.
