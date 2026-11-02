# IIS

Deploying to a Windows server running IIS with `build/scripts/deploy-iis.ps1`. The script is called by the `deploy-iis` job of `cd.yml` ([`README.md`](README.md)) and can be run by hand. The decision (migrate first, swap second) is in [ADR 0033](../adr/0033-delivery-pipeline.md).

**What was verified.** The script has 57 Pester tests (`build/scripts/tests/deploy-iis.Tests.ps1`) with the IIS, `dotnet`, health and robocopy calls mocked, plus one that runs the real `robocopy`; they passed under Windows PowerShell 5.1, and PSScriptAnalyzer reports nothing for the script. The CI job `scripts` runs the same tests under PowerShell 7 on Linux; it had not run when this page was written. **Its IIS calls were never run against a real IIS server**: the checklist [below](#manual-checklist-for-a-real-server) is the verification, and it is not optional before the first production release.

## The script

```powershell
./build/scripts/deploy-iis.ps1 -PublishPath publish -SitePath C:\inetpub\api -AppPool api -HealthUrl http://localhost:8081/health/ready
```

| Parameter | Meaning |
|---|---|
| `-PublishPath` | The folder `dotnet publish` wrote; it must hold `TemplateName.Api.dll`. |
| `-SitePath` | The IIS site's physical path. |
| `-AppPool` | The site's application pool; it must be set to No Managed Code. |
| `-HealthUrl` | The readiness URL that must answer 2xx after the swap. |

The connection string is never a parameter: the script reads it from the environment variable `ConnectionStrings__Database` (the **DDL login**, [`migrations.md`](migrations.md)) and never prints it. The script runs on Windows PowerShell 5.1 and PowerShell 7. It exits 0 on success and 1 on any failure, with the reason on stderr.

**Order.**

1. **Preflight.** The `IISAdministration` module is installed; the ASP.NET Core Hosting Bundle is installed (`%ProgramFiles%\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` exists); the app pool exists and has no .NET CLR version; the publish folder holds `TemplateName.Api.dll`. A failure stops here.
2. **Migrate.** `dotnet TemplateName.Api.dll migrate` from the publish folder, while the old version still serves. Migrations are expand-only (ADR 0031), so they are safe under the old code. A non-zero exit stops the script with the site untouched: no backup, no `app_offline.htm`.
3. **Back up.** Copy the site to `{SitePath}.previous`, replacing the copy of the last release (never merging into it). If there is no site yet, there is nothing to copy.
4. **Swap.** Write `app_offline.htm` (IIS then answers every request with that page and unloads the app), `robocopy /MIR` the publish folder over the site, remove `app_offline.htm`.
5. **Check.** Poll `HealthUrl` every 3 seconds for up to 120 seconds; a 2xx passes, each request has a 10 second timeout.
6. **Roll back on failure.** If the swap or the check fails, put the site offline again, mirror `{SitePath}.previous` back over it, bring it online and fail the script ("the deployment failed and the previous version was restored"). If the restore itself fails the site is **left offline** on purpose: a maintenance page is better than a half-restored site answering requests. The **database is not rolled back**: the migrations are expand-only, so the previous code runs on the new schema.

`{SitePath}.previous` is always kept after a run, so a manual rollback stays possible: mirror it back with the same robocopy switches the script uses (`/MIR /XF appsettings.Production.json app_offline.htm /XD logs`) while `app_offline.htm` is in place.

**Server-owned files.** The swap neither overwrites, deletes nor backs up `appsettings.Production.json`, a `logs` folder and `app_offline.htm`. They are matched by name at any depth, so a file or folder of those names inside the application is spared too. Everything else in the site folder is replaced by the publish folder's contents, so **never keep other server-only files in the site folder**.

## Health URL

Use the plain binding on the machine itself, `http://localhost:<port>/health/ready`, with a binding for it on the site. An `https` URL whose certificate does not match the host name in the URL fails the check, and Windows PowerShell 5.1 without TLS 1.2 enabled cannot make the request at all; either rolls the deploy back although the application is healthy. HTTPS redirection is on outside Development: if ASP.NET Core learns an https port (an https binding on the site can supply one), the plain request is redirected to it and the same certificate problem appears. Check this in the checklist below.

## The pipeline step

The `deploy-iis` job runs for a release when the repository variable `IIS_ENABLED` is `true`, on a self-hosted runner with the labels `self-hosted`, `windows` and `iis` (`.github/actionlint.yaml` declares the `iis` label so actionlint accepts it). It checks out the release, restores in locked mode, runs `dotnet publish src/Host/TemplateName.Api -c Release -o publish`, then runs the script in **Windows PowerShell 5.1** (`shell: powershell`), because a Windows runner may have no PowerShell 7 and `IISAdministration` is native to 5.1. The execution policy must not be `Restricted` (the default `RemoteSigned` is fine for a script from the checkout). The variables `IIS_SITE_PATH`, `IIS_APP_POOL` and `IIS_HEALTH_URL` and the environment secret `MIGRATION_CONNECTION_STRING` reach the script through `env:`, never interpolated into the command. The job belongs to the `production` environment, so it waits for the same approval as `deploy-production`, and it needs only `verify`: it can finish before the staging smoke test has.

The runner's service account needs: write access to the site folder and to its parent (for `{SitePath}.previous`), permission to read IIS configuration (`Get-IISAppPool`), the .NET SDK (the workflow installs it) and network access to the database.

## Server settings

- **Hosting Bundle** for .NET 10, installed after IIS, so the ASP.NET Core Module is registered. The publish is framework-dependent, so the Bundle's runtime must match the major version of the application.
- **App pool**: one per application, **No Managed Code**, the `ApplicationPoolIdentity` or another identity with least privilege (read on the site folder, write on `logs` if the application writes there).
- **Runtime configuration** is not in the publish. Keep non-secret settings in `appsettings.Production.json` in the site folder (the swap leaves it alone). Secrets (the runtime connection string, the JWT signing key `Auth:Jwt:SigningKeys:0:PrivateKeyPem`, the SMTP password) do not belong in a file: set them as environment variables of the app pool (`ConnectionStrings__Database`, `Auth__Jwt__SigningKeys__0__PrivateKeyPem`, and so on; IIS Manager, Configuration Editor, `system.applicationHost/applicationPools`, the pool's `environmentVariables`), or at machine level. A `web.config` is overwritten by every publish, so settings there would be lost. The runtime login has no DDL rights ([`migrations.md`](migrations.md)).
- **Data Protection keys** are in the database (`auth.DataProtectionKeys`, ADR 0017), not on the server's disk, so a web farm shares them with no shared folder: every instance uses the same database and the same `Auth:DataProtection:ApplicationName`. The ring is not encrypted at rest ([production checklist](production-checklist.md#data-protection-keys-persisted-and-protected)).
- **TLS** is the site's https binding (or a proxy in front). Set `ForwardedHeaders` if there is a proxy ([`docker.md`](docker.md#behind-a-proxy-or-load-balancer) applies as written), and `AllowedHosts` to the public host names.

## Manual checklist for a real server

Tick these on a staging or test server before the first production release, and again after changing the script. Nothing below has been run.

- [ ] IIS and the **IIS Management Scripts and Tools** feature are installed; in an elevated Windows PowerShell 5.1, `Get-Module -ListAvailable IISAdministration` lists the module.
- [ ] The .NET 10 **Hosting Bundle** is installed after IIS; `aspnetcorev2.dll` exists at the path the preflight checks.
- [ ] The app pool exists with **No Managed Code** and a least-privilege identity; the preflight fails with a clear message when the pool is set to a CLR version (change it and run again).
- [ ] The site's physical path is `IIS_SITE_PATH`, with the https binding for users and a plain `http://localhost:<port>` binding for `IIS_HEALTH_URL`.
- [ ] The runtime settings and secrets are set (app pool environment variables and `appsettings.Production.json`); the runtime login cannot change the schema.
- [ ] The self-hosted runner is registered with the labels `self-hosted`, `windows`, `iis`, runs as an account with the rights listed above, and can reach the database.
- [ ] **First deploy** by hand from an elevated Windows PowerShell 5.1 with a publish folder and `ConnectionStrings__Database` set: `migrate` runs, the site comes online, the health check passes, `{SitePath}.previous` exists. `appsettings.Production.json` and `logs` are unchanged and `app_offline.htm` is gone.
- [ ] **Second deploy** of a changed build: `{SitePath}.previous` now holds the first build; a file removed from the new publish is gone from the site.
- [ ] **Health failure drill.** Deploy with a wrong `-HealthUrl` port: after about two minutes the script exits 1, the previous version is back, the site is online, `app_offline.htm` is gone.
- [ ] **Migration failure drill.** Deploy with a wrong connection string: the script exits 1 before any file changes, and no `app_offline.htm` was written.
- [ ] **HTTPS redirection.** With the https binding in place, `Invoke-WebRequest http://localhost:<port>/health/ready` from the runner's account answers 2xx and is not redirected to an https URL with a certificate error. If it is, give the site a health binding that avoids the redirect or a certificate valid for the host name, and use that URL.
- [ ] **Web farm** (if used): two servers share the database, use the same `Auth:DataProtection:ApplicationName`, and a link or TOTP code protected by one is readable by the other.
- [ ] From the pipeline: the first release reaches `deploy-iis`, waits for the `production` approval and finishes. Do not rehearse this with a `workflow_dispatch` run that has `release` ticked: it publishes `x.y.z` and `latest` and deploys to production like a real release. A dispatch without `release` from a branch is a dry run that deploys nothing, IIS included. To repeat a release that failed at `deploy-iis`, run `cd.yml` (Actions, Run workflow) **from the tag `vX.Y.Z`** (the "Use workflow from" selector) with `release` ticked and `version` set to `X.Y.Z`, never from a branch: `main` may be ahead of the release. A dispatch from an old `v*` tag deploys that old tag, so choose the tag you mean ([`README.md`](README.md#cdyml)).
