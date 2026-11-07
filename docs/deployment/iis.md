# IIS

Deploying to a Windows server running IIS with `build/scripts/deploy-iis.ps1`. The script is called by the `deploy-iis` job of `cd.yml` ([`README.md`](README.md)) and can be run by hand. The decision (migrate first, swap second) is in [ADR 0033](../adr/0033-delivery-pipeline.md); the maintenance mode for a release that adds a contract migration is in [ADR 0034](../adr/0034-maintenance-mode-for-contract-phase-releases.md).

**What was verified.** The script has 118 Pester tests (`build/scripts/tests/deploy-iis.Tests.ps1`). In all but six the IIS, `dotnet`, health and robocopy calls are mocked; one runs the real `robocopy` (Windows only), and five run the script itself in a child PowerShell: once with no arguments, twice the way the Actions runner runs the step, with the machine stubbed, to prove the exit code the step ends with, and twice with and without `-Maintenance`, to prove the switch reaches the deployment (the stubbed `migrate` call sees `app_offline.htm` in the site folder only with the switch). They passed under Windows PowerShell 5.1. PSScriptAnalyzer reported nothing for the script before the maintenance mode was added; it was not run again on the changed script. The CI job `scripts` runs the tests under PowerShell 7 on Linux and the analyzer; it had not run for the maintenance mode when this page was written. **Its IIS calls were never run against a real IIS server**, the maintenance mode included: the checklist [below](#manual-checklist-for-a-real-server) is the verification, and it is not optional before the first production release.

## The script

```powershell
./build/scripts/deploy-iis.ps1 -PublishPath publish -SitePath C:\inetpub\api -AppPool api -HealthUrl https://api.example.com/health/ready
```

| Parameter | Meaning |
|---|---|
| `-PublishPath` | The folder `dotnet publish` wrote; it must hold `TemplateName.Api.dll`. |
| `-SitePath` | The IIS site's physical path. A trailing `\` or `/` is dropped; a drive root (`C:\`) or a share root (`\\server\share`) is refused; a folder below a share is accepted. |
| `-AppPool` | The site's application pool; it must be set to No Managed Code. |
| `-HealthUrl` | The site's public readiness URL, `https://<public host>/health/ready`, that must answer 2xx after the swap ([Health URL](#health-url)). |
| `-MaintenancePage` | Optional. The page IIS serves while the site is offline; it is copied to `app_offline.htm`. Default: `build/deploy/maintenance.html`, found relative to the script (`build/scripts/..`), the page the Docker hook shares. The preflight fails when the file does not exist. |
| `-Maintenance` | Optional switch. Runs the [maintenance mode](#maintenance-mode) instead of the normal order. The `deploy-iis` job sets it when the `maintenance` job says so. |

The connection string is never a parameter: the script reads it from the environment variable `ConnectionStrings__Database` (the **DDL login**, [`migrations.md`](migrations.md)) and never prints it. The script runs on Windows PowerShell 5.1 and PowerShell 7. It exits 0 on success and 1 on any failure, with the reason on stderr; the 0 is explicit, because robocopy leaves 1 or 3 in `$LASTEXITCODE` after a good copy and the Actions runner ends a PowerShell step with `exit $LASTEXITCODE`.

**Order (normal mode).**

1. **Preflight.** The `IISAdministration` module is installed; the ASP.NET Core Hosting Bundle is installed (`IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` exists under `%ProgramW6432%`, or under `%ProgramFiles%` when that is not set); the app pool exists and has no .NET CLR version; the publish folder holds `TemplateName.Api.dll`; the maintenance page exists. A failure stops here.
2. **Migrate.** `dotnet TemplateName.Api.dll migrate` from the publish folder, while the old version still serves. Migrations are expand-only (ADR 0031), so they are safe under the old code. A non-zero exit stops the script with the site untouched: no backup, no `app_offline.htm`.
3. **Back up.** Copy the site to `{SitePath}.previous`, a folder next to the site (`C:\inetpub\api.previous`), replacing the copy of the last release (never merging into it). If there is no site yet, there is nothing to copy.
4. **Swap.** Copy the maintenance page to `app_offline.htm` (byte for byte; IIS then answers every request with that page, status 503, and unloads the app), `robocopy /MIR` the publish folder over the site, remove `app_offline.htm`. The page is the shared `build/deploy/maintenance.html`, in three languages.
5. **Check.** Poll `HealthUrl` every 3 seconds for up to 120 seconds; a 2xx passes, each request has a 10 second timeout.
6. **Roll back on failure.** If the swap or the check fails, put the site offline again, mirror `{SitePath}.previous` back over it, bring it online and fail the script ("the deployment failed and the previous files were restored"). The restored files are **not health-checked**: check the site yourself. If the restore itself fails the site is **left offline** on purpose: a maintenance page is better than a half-restored site answering requests. The **database is not rolled back**: the migrations are expand-only, so the previous code runs on the new schema.

**A failed first deployment** has no `{SitePath}.previous`, so there is nothing to restore and the script fails with "the previous files could not be restored: There is no previous version". Where it stopped decides the state: a failed **health check** leaves the **new build online** (the swap had finished and `app_offline.htm` was already removed); a failed **publish** (robocopy) leaves the site **offline**, with `app_offline.htm` in place, until you fix the cause and run the script again or remove the file.

`{SitePath}.previous` is always kept after a run, so a manual rollback stays possible: mirror it back with the same robocopy switches the script uses (`/MIR /XF appsettings.Production.json app_offline.htm /XD logs`) while `app_offline.htm` is in place.

**Server-owned files.** The swap neither overwrites, deletes nor backs up `appsettings.Production.json`, a `logs` folder and `app_offline.htm`. They are matched by name at any depth, so a file or folder of those names inside the application is spared too: a nested `wwwroot\logs`, or an `appsettings.Production.json` in a subfolder, is not updated by a release. Everything else in the site folder is replaced by the publish folder's contents, so **never keep other server-only files in the site folder**.

## Maintenance mode

A release that adds a contract migration runs with `-Maintenance` (the `maintenance` job decides, [`README.md`](README.md#maintenance-mode)). After such a migration the previous code cannot run on the schema, so the old version must not be serving while it is applied, and nothing is rolled back to it.

**Order (maintenance mode).**

1. **Preflight.** As above.
2. **Back up.** As above: the site is copied to `{SitePath}.previous` while it still serves.
3. **Page up.** Copy the maintenance page to `app_offline.htm` (the site folder is created if it is missing). From here on users get the page.
4. **Migrate.** `dotnet TemplateName.Api.dll migrate`, with the page up.
5. **Swap.** `robocopy /MIR` the publish folder over the site.
6. **Page down, then check.** Remove `app_offline.htm`, then poll `HealthUrl` as above.

**The failure rule.** A failure in the migration, the swap or the health check **leaves the page up, restores nothing and does not remove the page**; the script exits 1 and the message names the step (`migrate`, `swap` or `health check`). It says that, if a contract migration was applied, the database is to be restored from a backup first, and then `{SitePath}.previous` restored over the site and `app_offline.htm` removed. A failure in the preflight or the backup happens before the page is up and leaves the site as it was. After a failed health check the script puts the page back (the page had come down for the check), so users do not reach a version that is not healthy, and the last line of the error says that the page is still up.

**Recovering from a failed maintenance-mode run.**

1. Read the Error line: which step failed and why.
2. If the migration was applied (or may have been), restore the database from a backup taken before the release. The template takes no backup ([production checklist](production-checklist.md#backups-and-a-tested-restore)).
3. To go back to the previous version, mirror `{SitePath}.previous` over the site with the robocopy switches of the [script](#the-script) while `app_offline.htm` is in place, then delete `app_offline.htm`. To go forward instead, fix the cause and run the deployment again. A new run replaces `{SitePath}.previous` with the site as it stands then, so restore from `.previous` first if you still need the old files.
4. Check the site's health URL yourself.

**One failure leaves no page.** If the health check fails and putting the page back fails as well, the site is left serving the unhealthy new build with `app_offline.htm` removed. The log says so (`The maintenance page could not be put back: ...` with the reason), and the last line of the error says it too instead of saying that the page is still up: `The maintenance page could NOT be put back: the site is serving the new build that failed its health check; copy the maintenance page to app_offline.htm by hand now.` Do that first (copy `build/deploy/maintenance.html` to `app_offline.htm` in the site folder), then recover as above. After any failed maintenance-mode run you can also look in the site folder for `app_offline.htm` and put the page there by hand if it is missing.

**A release that was not detected.** Detection compares with the previous release tag, not with what production last ran ([`README.md`](README.md#maintenance-mode)). If a contract release never reached production (a failed staging smoke test, a rejected `production` approval, a run that failed before `deploy-iis`), the next release answers `false` and runs in the normal order, which migrates with the old code serving and no page. **Until a release has completed its production deploy, dispatch every later release by hand with `maintenance: on`**, so `deploy-iis` gets `-Maintenance`.

**The page** is the same file the Docker hook serves, so a project edits one file for its brand and contact ([`README.md`](README.md#maintenance-mode)). `app_offline.htm` is a file, so IIS answers 503 without a `Retry-After` header; the page refreshes itself every 30 seconds instead. A first deployment (no site, no `{SitePath}.previous`) has nothing to restore: a failure leaves the page up until the cause is fixed.

## Health URL

Set `IIS_HEALTH_URL` to the site's **public https URL**, `https://<public host>/health/ready`, served with a certificate valid for that host. A plain `http://localhost` URL does not work on a site with an https binding: the ASP.NET Core Module then tells the application its https port (`ASPNETCORE_ANCM_HTTPS_PORT`), HTTPS redirection (on outside Development, for the whole application) answers the plain request with a 307 to `https://localhost`, and that certificate does not match `localhost`, so the check fails and the deploy is rolled back although the application is healthy. A second binding does not avoid it: the redirect is the application's, not the binding's. The script adds TLS 1.2 to the protocols Windows PowerShell 5.1 may use before each request (PowerShell 7 needs nothing).

Three conditions, all checked in the list below: the runner's machine resolves the public host to **this** server (the runner runs on it; on a web farm behind a load balancer the public name may reach another server, so add a `hosts` entry on this server that points the public host at `127.0.0.1`; that reaches an https binding set to All Unassigned or to an SNI host name, but not one tied to a specific IP address, which a request to `127.0.0.1` does not match), the certificate is valid for that host, and `AllowedHosts` lists it (it must, as the public host). Exempting `/health/*` from the redirect would allow a plain local URL again; that is an open follow-up ([ADR 0033](../adr/0033-delivery-pipeline.md)), not built.

## The pipeline step

The `deploy-iis` job runs for a release when the repository variable `IIS_ENABLED` is `true`, on a self-hosted runner with the labels `self-hosted`, `windows` and `iis` (`.github/actionlint.yaml` declares the `iis` label so actionlint accepts it). It checks out the release, restores in locked mode, runs `dotnet publish src/Host/TemplateName.Api -c Release -o publish`, then runs the script in **Windows PowerShell 5.1** (`shell: powershell`), because a Windows runner may have no PowerShell 7 and `IISAdministration` is native to 5.1. The execution policy must not be `Restricted` (the default `RemoteSigned` is fine for a script from the checkout). The variables `IIS_SITE_PATH`, `IIS_APP_POOL` and `IIS_HEALTH_URL`, the `maintenance` job's output (`MAINTENANCE`, passed as `-Maintenance:($env:MAINTENANCE -eq 'true')`) and the environment secret `MIGRATION_CONNECTION_STRING` reach the script through `env:`, never interpolated into the command. The job belongs to the `production` environment, so it waits for the same approval as `deploy-production`, and it needs only `verify` and `maintenance`: it can finish before the staging smoke test has. The runner ends the step with `exit $LASTEXITCODE`; the script sets 0 itself on success, so a successful deployment is a green step.

The runner's service account needs: write access to the site folder and to its parent (for `{SitePath}.previous`), permission to read IIS configuration (`Get-IISAppPool`), the .NET SDK (the workflow installs it) and network access to the database.

## Server settings

- **Hosting Bundle** for .NET 10, installed after IIS, so the ASP.NET Core Module is registered. The publish is framework-dependent, so the Bundle's runtime must match the major version of the application.
- **App pool**: one per application, **No Managed Code**, the `ApplicationPoolIdentity` or another identity with least privilege (read on the site folder, write on `logs` if the application writes there).
- **Runtime configuration** is not in the publish. Keep non-secret settings in `appsettings.Production.json` in the site folder (the swap leaves it alone). Secrets (the runtime connection string, the JWT signing key `Auth:Jwt:SigningKeys:0:PrivateKeyPem`, any other credential) do not belong in a file: set them as environment variables of the app pool (`ConnectionStrings__Database`, `Auth__Jwt__SigningKeys__0__PrivateKeyPem`, and so on; IIS Manager, Configuration Editor, `system.applicationHost/applicationPools`, the pool's `environmentVariables`), or at machine level. A `web.config` is overwritten by every publish, so settings there would be lost. The runtime login has no DDL rights ([`migrations.md`](migrations.md)).
- **Data Protection keys** are in the database (`auth.DataProtectionKeys`, ADR 0017), not on the server's disk, so a web farm shares them with no shared folder: every instance uses the same database and the same `Auth:DataProtection:ApplicationName`. The ring is not encrypted at rest ([production checklist](production-checklist.md#data-protection-keys-persisted-and-protected)).
- **TLS** is the site's https binding (or a proxy in front). Set `ForwardedHeaders` if there is a proxy ([`docker.md`](docker.md#behind-a-proxy-or-load-balancer) applies as written), and `AllowedHosts` to the public host names, the host of `IIS_HEALTH_URL` among them (host filtering answers 400 to any other `Host`, the health check included), plus any host name a load balancer's probe sends.

## Manual checklist for a real server

Tick these on a staging or test server before the first production release, and again after changing the script. Nothing below has been run.

- [ ] IIS and the **IIS Management Scripts and Tools** feature are installed; in an elevated Windows PowerShell 5.1, `Get-Module -ListAvailable IISAdministration` lists the module.
- [ ] The .NET 10 **Hosting Bundle** is installed after IIS; `aspnetcorev2.dll` exists at the path the preflight checks.
- [ ] The app pool exists with **No Managed Code** and a least-privilege identity; the preflight fails with a clear message when the pool is set to a CLR version (change it and run again).
- [ ] The site's physical path is `IIS_SITE_PATH`, with the https binding for users; `IIS_HEALTH_URL` is `https://<public host>/health/ready`.
- [ ] The runtime settings and secrets are set (app pool environment variables and `appsettings.Production.json`); the runtime login cannot change the schema.
- [ ] The self-hosted runner is registered with the labels `self-hosted`, `windows`, `iis`, runs as an account with the rights listed above, and can reach the database.
- [ ] **First deploy** by hand from an elevated Windows PowerShell 5.1 with a publish folder and `ConnectionStrings__Database` set: `migrate` runs, the site comes online, the health check passes, `{SitePath}.previous` exists. `appsettings.Production.json` and `logs` are unchanged and `app_offline.htm` is gone.
- [ ] **Second deploy** of a changed build: `{SitePath}.previous` now holds the first build; a file removed from the new publish is gone from the site.
- [ ] **Health failure drill.** Deploy with a wrong `-HealthUrl` path: after about two minutes the script exits 1, the previous files are back, the site is online, `app_offline.htm` is gone; the old version answers its health URL.
- [ ] **Migration failure drill.** Deploy (normal mode) with a wrong connection string: the script exits 1 before any file changes, and no `app_offline.htm` was written.
- [ ] **The page.** Open the site with `app_offline.htm` in place (a deploy in progress, or the file copied by hand): the answer is status 503 with the page, in English, Malay and Chinese when scripts are blocked and in your browser's language when they are not; it refreshes after about 30 seconds.
- [ ] **Maintenance mode, success.** Deploy with `-Maintenance` and a changed build: `app_offline.htm` (the shared page) is in the site folder before the migration starts, the site answers 503 with it while the migration runs, the files are swapped, the page is gone after the swap and the health check passes. `{SitePath}.previous` holds the previous build.
- [ ] **Maintenance migration failure drill.** Deploy with `-Maintenance` and a wrong connection string: the script exits 1 naming `migrate`, `app_offline.htm` is still there, the site still answers 503 with the page and no file was changed. No file was changed, so once the database is in a state the old version can run on, deleting `app_offline.htm` brings it back.
- [ ] **Maintenance health failure drill.** Deploy with `-Maintenance` and a wrong `-HealthUrl` path: after about two minutes the script exits 1 naming `health check`, the new files are **not** restored, and `app_offline.htm` is in place again. Recover by the steps in [Maintenance mode](#maintenance-mode).
- [ ] **Preflight in maintenance mode.** Deploy with `-Maintenance` and an app pool set to a CLR version, or with a `-MaintenancePage` that does not exist: the script exits 1 before any change and no `app_offline.htm` is written.
- [ ] **Health URL.** From the runner's account in Windows PowerShell 5.1, `Invoke-WebRequest https://<public host>/health/ready -UseBasicParsing` answers 2xx from this server: no certificate error, no 400 from host filtering (`AllowedHosts` lists the host), and on a web farm the name resolves to this server.
- [ ] **The pipeline step exits 0 on success**: the `Deploy to IIS` step of a successful `deploy-iis` run is green, not red with robocopy's exit code.
- [ ] **Web farm** (if used): two servers share the database, use the same `Auth:DataProtection:ApplicationName`, and a link or TOTP code protected by one is readable by the other.
- [ ] From the pipeline: the first release reaches `deploy-iis`, waits for the `production` approval and finishes. Do not rehearse this with a `workflow_dispatch` run that has `release` ticked: it publishes `x.y.z` and `latest` and deploys to production like a real release. A dispatch without `release` from a branch is a dry run that deploys nothing, IIS included. To repeat a release that failed at `deploy-iis`, run `cd.yml` (Actions, Run workflow) **from the tag `vX.Y.Z`** (the "Use workflow from" selector) with `release` ticked and `version` set to `X.Y.Z`, never from a branch: `main` may be ahead of the release. A dispatch from an old `v*` tag deploys that old tag, so choose the tag you mean ([`README.md`](README.md#cdyml)). For the first release that adds a contract migration, check that the `maintenance` job's log says `maintenance page: true`, and that the `Deploy to IIS` step ran in maintenance mode (the log shows the page put up before the database migration).
