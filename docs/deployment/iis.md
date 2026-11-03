# IIS

Deploying to a Windows server running IIS with `build/scripts/deploy-iis.ps1`. The script is called by the `deploy-iis` job of `cd.yml` ([`README.md`](README.md)) and can be run by hand. The decision (migrate first, swap second) is in [ADR 0033](../adr/0033-delivery-pipeline.md).

**What was verified.** The script has 89 Pester tests (`build/scripts/tests/deploy-iis.Tests.ps1`). In all but four the IIS, `dotnet`, health and robocopy calls are mocked; one runs the real `robocopy` (Windows only), and three run the script itself in a child PowerShell: once with no arguments, and twice the way the Actions runner runs the step, with the machine stubbed, to prove the exit code the step ends with. They passed under Windows PowerShell 5.1, and PSScriptAnalyzer reports nothing for the script. The CI job `scripts` runs the same tests under PowerShell 7 on Linux; it had not run when this page was written. **Its IIS calls were never run against a real IIS server**: the checklist [below](#manual-checklist-for-a-real-server) is the verification, and it is not optional before the first production release.

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

The connection string is never a parameter: the script reads it from the environment variable `ConnectionStrings__Database` (the **DDL login**, [`migrations.md`](migrations.md)) and never prints it. The script runs on Windows PowerShell 5.1 and PowerShell 7. It exits 0 on success and 1 on any failure, with the reason on stderr; the 0 is explicit, because robocopy leaves 1 or 3 in `$LASTEXITCODE` after a good copy and the Actions runner ends a PowerShell step with `exit $LASTEXITCODE`.

**Order.**

1. **Preflight.** The `IISAdministration` module is installed; the ASP.NET Core Hosting Bundle is installed (`IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` exists under `%ProgramW6432%`, or under `%ProgramFiles%` when that is not set); the app pool exists and has no .NET CLR version; the publish folder holds `TemplateName.Api.dll`. A failure stops here.
2. **Migrate.** `dotnet TemplateName.Api.dll migrate` from the publish folder, while the old version still serves. Migrations are expand-only (ADR 0031), so they are safe under the old code. A non-zero exit stops the script with the site untouched: no backup, no `app_offline.htm`.
3. **Back up.** Copy the site to `{SitePath}.previous`, a folder next to the site (`C:\inetpub\api.previous`), replacing the copy of the last release (never merging into it). If there is no site yet, there is nothing to copy.
4. **Swap.** Write `app_offline.htm` (IIS then answers every request with that page and unloads the app), `robocopy /MIR` the publish folder over the site, remove `app_offline.htm`.
5. **Check.** Poll `HealthUrl` every 3 seconds for up to 120 seconds; a 2xx passes, each request has a 10 second timeout.
6. **Roll back on failure.** If the swap or the check fails, put the site offline again, mirror `{SitePath}.previous` back over it, bring it online and fail the script ("the deployment failed and the previous files were restored"). The restored files are **not health-checked**: check the site yourself. If the restore itself fails the site is **left offline** on purpose: a maintenance page is better than a half-restored site answering requests. The **database is not rolled back**: the migrations are expand-only, so the previous code runs on the new schema.

**A failed first deployment** has no `{SitePath}.previous`, so there is nothing to restore and the script fails with "the previous files could not be restored: There is no previous version". Where it stopped decides the state: a failed **health check** leaves the **new build online** (the swap had finished and `app_offline.htm` was already removed); a failed **publish** (robocopy) leaves the site **offline**, with `app_offline.htm` in place, until you fix the cause and run the script again or remove the file.

`{SitePath}.previous` is always kept after a run, so a manual rollback stays possible: mirror it back with the same robocopy switches the script uses (`/MIR /XF appsettings.Production.json app_offline.htm /XD logs`) while `app_offline.htm` is in place.

**Server-owned files.** The swap neither overwrites, deletes nor backs up `appsettings.Production.json`, a `logs` folder and `app_offline.htm`. They are matched by name at any depth, so a file or folder of those names inside the application is spared too: a nested `wwwroot\logs`, or an `appsettings.Production.json` in a subfolder, is not updated by a release. Everything else in the site folder is replaced by the publish folder's contents, so **never keep other server-only files in the site folder**.

## Health URL

Set `IIS_HEALTH_URL` to the site's **public https URL**, `https://<public host>/health/ready`, served with a certificate valid for that host. A plain `http://localhost` URL does not work on a site with an https binding: the ASP.NET Core Module then tells the application its https port (`ASPNETCORE_ANCM_HTTPS_PORT`), HTTPS redirection (on outside Development, for the whole application) answers the plain request with a 307 to `https://localhost`, and that certificate does not match `localhost`, so the check fails and the deploy is rolled back although the application is healthy. A second binding does not avoid it: the redirect is the application's, not the binding's. The script adds TLS 1.2 to the protocols Windows PowerShell 5.1 may use before each request (PowerShell 7 needs nothing).

Three conditions, all checked in the list below: the runner's machine resolves the public host to **this** server (the runner runs on it; on a web farm behind a load balancer the public name may reach another server, so add a `hosts` entry on this server that points the public host at `127.0.0.1`; that reaches an https binding set to All Unassigned or to an SNI host name, but not one tied to a specific IP address, which a request to `127.0.0.1` does not match), the certificate is valid for that host, and `AllowedHosts` lists it (it must, as the public host). Exempting `/health/*` from the redirect would allow a plain local URL again; that is an open follow-up ([ADR 0033](../adr/0033-delivery-pipeline.md)), not built.

## The pipeline step

The `deploy-iis` job runs for a release when the repository variable `IIS_ENABLED` is `true`, on a self-hosted runner with the labels `self-hosted`, `windows` and `iis` (`.github/actionlint.yaml` declares the `iis` label so actionlint accepts it). It checks out the release, restores in locked mode, runs `dotnet publish src/Host/TemplateName.Api -c Release -o publish`, then runs the script in **Windows PowerShell 5.1** (`shell: powershell`), because a Windows runner may have no PowerShell 7 and `IISAdministration` is native to 5.1. The execution policy must not be `Restricted` (the default `RemoteSigned` is fine for a script from the checkout). The variables `IIS_SITE_PATH`, `IIS_APP_POOL` and `IIS_HEALTH_URL` and the environment secret `MIGRATION_CONNECTION_STRING` reach the script through `env:`, never interpolated into the command. The job belongs to the `production` environment, so it waits for the same approval as `deploy-production`, and it needs only `verify`: it can finish before the staging smoke test has. The runner ends the step with `exit $LASTEXITCODE`; the script sets 0 itself on success, so a successful deployment is a green step.

The runner's service account needs: write access to the site folder and to its parent (for `{SitePath}.previous`), permission to read IIS configuration (`Get-IISAppPool`), the .NET SDK (the workflow installs it) and network access to the database.

## Server settings

- **Hosting Bundle** for .NET 10, installed after IIS, so the ASP.NET Core Module is registered. The publish is framework-dependent, so the Bundle's runtime must match the major version of the application.
- **App pool**: one per application, **No Managed Code**, the `ApplicationPoolIdentity` or another identity with least privilege (read on the site folder, write on `logs` if the application writes there).
- **Runtime configuration** is not in the publish. Keep non-secret settings in `appsettings.Production.json` in the site folder (the swap leaves it alone). Secrets (the runtime connection string, the JWT signing key `Auth:Jwt:SigningKeys:0:PrivateKeyPem`, the SMTP password) do not belong in a file: set them as environment variables of the app pool (`ConnectionStrings__Database`, `Auth__Jwt__SigningKeys__0__PrivateKeyPem`, and so on; IIS Manager, Configuration Editor, `system.applicationHost/applicationPools`, the pool's `environmentVariables`), or at machine level. A `web.config` is overwritten by every publish, so settings there would be lost. The runtime login has no DDL rights ([`migrations.md`](migrations.md)).
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
- [ ] **Migration failure drill.** Deploy with a wrong connection string: the script exits 1 before any file changes, and no `app_offline.htm` was written.
- [ ] **Health URL.** From the runner's account in Windows PowerShell 5.1, `Invoke-WebRequest https://<public host>/health/ready -UseBasicParsing` answers 2xx from this server: no certificate error, no 400 from host filtering (`AllowedHosts` lists the host), and on a web farm the name resolves to this server.
- [ ] **The pipeline step exits 0 on success**: the `Deploy to IIS` step of a successful `deploy-iis` run is green, not red with robocopy's exit code.
- [ ] **Web farm** (if used): two servers share the database, use the same `Auth:DataProtection:ApplicationName`, and a link or TOTP code protected by one is readable by the other.
- [ ] From the pipeline: the first release reaches `deploy-iis`, waits for the `production` approval and finishes. Do not rehearse this with a `workflow_dispatch` run that has `release` ticked: it publishes `x.y.z` and `latest` and deploys to production like a real release. A dispatch without `release` from a branch is a dry run that deploys nothing, IIS included. To repeat a release that failed at `deploy-iis`, run `cd.yml` (Actions, Run workflow) **from the tag `vX.Y.Z`** (the "Use workflow from" selector) with `release` ticked and `version` set to `X.Y.Z`, never from a branch: `main` may be ahead of the release. A dispatch from an old `v*` tag deploys that old tag, so choose the tag you mean ([`README.md`](README.md#cdyml)).
