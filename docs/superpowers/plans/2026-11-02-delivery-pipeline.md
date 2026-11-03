# Delivery Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A generated project builds a chiseled non-root container image, migrates its database with an API `migrate` mode, and rolls out to Docker or IIS from CI-gated pipelines, with CodeQL and gitleaks watching.

**Architecture:** Two new command modes on the API binary (`migrate`, `healthcheck`) and one `Dockerfile` that uses both. One verification script (`container-verify.sh`) is run by a `container` job in `ci.yml` on every pull request and again by `cd.yml` on the pushed digest. `cd.yml` is reusable: it calls `ci.yml`, publishes to GHCR, deploys to staging on a green push to `main`, and to production on a release (called from `release.yml`). IIS, CodeQL and gitleaks are separate files.

**Tech Stack:** .NET 10 / ASP.NET Core, xUnit v3 with Shouldly and Testcontainers, Docker (BuildKit, GHCR), GitHub Actions (reusable workflows), bash, PowerShell with Pester and PSScriptAnalyzer, gitleaks CLI, CodeQL.

**Spec:** `docs/superpowers/specs/2026-11-02-delivery-pipeline-design.md` (read it first; decisions are numbered D1 to D12 there).

## Global Constraints

- The placeholder is `TemplateName`; never write a concrete project name. Everything that ships is renamed by `dotnet new`.
- Image: build on `sdk:10.0`, run on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`; port `8080` (`ASPNETCORE_HTTP_PORTS=8080`); entrypoint `dotnet TemplateName.Api.dll`; `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3` running `dotnet TemplateName.Api.dll healthcheck`.
- `healthcheck` probes `http://127.0.0.1:{port}/health/live` with a 3-second timeout; exit 0 on 2xx, else 1. `migrate` exits 0 or 1, reads `ConnectionStrings:Database`, starts no web server and no worker, never logs the connection string.
- Image tags: main push `sha-<short>` and `edge`; release `x.y.z` and `latest`. Registry `ghcr.io/<repository>/api`.
- Repository variables and secrets: `DEPLOY_ENABLED`, `IIS_ENABLED`, `IIS_SITE_PATH`, `IIS_APP_POOL`, `IIS_HEALTH_URL`, `CODEQL_ENABLED`, `STAGING_URL`, `PRODUCTION_URL`; the DDL login is the environment secret `MIGRATION_CONNECTION_STRING`. Environments `staging` and `production`.
- Template repository guard: `image`, `verify` and the deploy jobs are skipped where `.template.config/template.json` exists; `ci`, the `container` job and the file checks still run.
- Actions use their latest major tag (the repository convention, Dependabot updates them); every third-party tool downloaded in a workflow is pinned to a version and a SHA-256 taken from a release at least two weeks old, after reading its licence for usage fees.
- No secret in any file. No new NuGet package. Warnings are errors in Release. Tests are written first and run green. Every new document is linked from `docs/README.md`; documents that ship must not link to files that do not ship (`docs/superpowers/`, `docs/blueprint-review.md`, `docs/repository-management.md`), because the template smoke test checks the generated links.
- Commits: Conventional Commits, header at most 72 characters, scope from CONTRIBUTING.md 2.3 (`host`, `docker`, `ci`, `adr`, `template` among them), body trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Never `--no-verify`.
- Work in a short-path worktree (`C:\w\<name>`: `Directory.Build.targets` rejects long paths). The ECC GateGuard hook asks for facts before the first Bash call and before the first Write or Edit of a file: state them in text and retry. Docker may not run on this machine: where a step needs it and it is unavailable, push the branch and read the CI run instead, saying so in the report.

## Review Focus

Inputs and conditions the spec implies but no obvious task test would exercise; each has a test in the task named.

1. `migrate` against a server that is down or unreachable must exit 1 in bounded time, not hang on retries (Task 1).
2. A container started with `ASPNETCORE_URLS` instead of `ASPNETCORE_HTTP_PORTS`, or with an entry the probe cannot parse, must fall back to 8080, a documented behaviour, not crash or probe port 0 (Task 1).
3. `container-verify.sh` that fails halfway or is interrupted must not leave containers or networks behind on a shared runner (Task 3).
4. `deploy-iis.ps1` with a space in a path (`C:\Sites\My App`) and with a `.previous` folder left by an earlier run (Task 5).
5. A gitleaks scan on a shallow checkout misses the commit range; a committed token must fail the scan and a clean history must pass (Task 6).

---

### Task 1: `migrate` and `healthcheck` command modes

**Files:**
- Create: `src/Host/TemplateName.Api/HostCommand.cs`
- Modify: `src/Host/TemplateName.Api/Program.cs` (top of file and just after `builder.Build()`; the file must end `return 0;` after `app.Run();`)
- Modify: `tests/TemplateName.IntegrationTests/Infrastructure/IntegrationTestWebAppFactory.cs` (one helper)
- Test: `tests/TemplateName.IntegrationTests/Host/HostCommandTests.cs` (no SQL, no factory), `tests/TemplateName.IntegrationTests/Host/MigrateCommandTests.cs` (uses the factory)

**Interfaces:**
- Produces, in namespace `TemplateName.Api` (internal; `InternalsVisibleTo` already covers the test projects):
  - `enum HostCommandKind { None, Migrate, Healthcheck }`
  - `static HostCommandKind HostCommand.Parse(string[] args)`: exact, case-sensitive first argument only.
  - `static Uri HostCommand.HealthUrl(string? httpPorts)`: `http://127.0.0.1:{first entry of the semicolon-separated list that parses as a port}/health/live`; 8080 when null, blank or unparseable.
  - `static Task<int> HostCommand.RunHealthCheckAsync(string? httpPorts, TextWriter output, HttpMessageHandler? handler, TimeSpan timeout, CancellationToken cancellationToken)`: one GET, one line to `output` (status or failure), 0 on 2xx else 1.
  - `static Task<int> HostCommand.RunMigrationsAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)`: calls `MigrateModuleDatabasesAsync`; 0 on success; on any exception (cancellation included) logs it at Error and returns 1.
- Produces on `IntegrationTestWebAppFactory`: `public async Task<string> CreateEmptyDatabaseAsync(string name, CancellationToken cancellationToken)`: creates database `name` on the factory's SQL container and returns its connection string.

- [ ] **Step 1: Write the failing tests in `HostCommandTests`**
  - `Parse_recognises_only_the_exact_first_argument` (theory): `["migrate"]`→`Migrate`; `["healthcheck"]`→`Healthcheck`; `[]`, `["--urls","http://x"]`, `["Migrate"]`, `["--migrate"]`, `["x","migrate"]`→`None`.
  - `HealthUrl_uses_the_first_port_and_falls_back_to_8080` (theory): `null`, `""`, `"  "`, `"abc"`, `"http://+:9000"` → `http://127.0.0.1:8080/health/live`; `"5000"`→port 5000; `"8081;8082"`→8081; `"x;7000"`→7000 (Review Focus 2).
  - `Healthcheck_exits_0_on_2xx` (200 and 204) and `Healthcheck_exits_1_on_5xx_and_on_a_failed_request`, using a stub `HttpMessageHandler` that returns the status or throws `HttpRequestException`; the output line holds the status code or the exception type, and nothing else.
  - `Healthcheck_exits_1_when_the_timeout_passes`: stub awaits `Task.Delay(Timeout.Infinite, ct)`, timeout 100 ms, the call returns 1 in under 5 seconds.
  - `Healthcheck_exits_1_when_nothing_listens`: real handler (`null`), a port from a closed `TcpListener`.
- [ ] **Step 2: Write the failing tests in `MigrateCommandTests`** (constructor takes `IntegrationTestWebAppFactory`)
  - `Migrate_creates_every_module_schema_and_is_idempotent`: `CreateEmptyDatabaseAsync`, build `Factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Database", cs))`, call `RunMigrationsAsync(host.Services, ...)` → 0; `sys.schemas` contains `platform`, `auth`, `notify`, `sample`, `audit`; second call → 0 and the row counts of every `__EFMigrationsHistory` table are unchanged.
  - `Migrate_exits_1_for_an_unreachable_server_in_bounded_time_without_logging_the_password` (Review Focus 1): connection string `Server=127.0.0.1,1;Database=x;User Id=sa;Password=Pa55-secret-value;Connect Timeout=2;TrustServerCertificate=true`; returns 1 within 90 seconds; the log captured by `RecordingLoggerProvider` has an Error entry and no entry containing `Pa55-secret-value`.
- [ ] **Step 3: Run the new tests, expect compile failure**: `dotnet test --project tests/TemplateName.IntegrationTests --filter-class "*HostCommandTests" -c Release` fails with "HostCommand does not exist". (Docker is needed for the assembly fixture; see the Global Constraints if it is not available.)
- [ ] **Step 4: Implement `HostCommand` and the factory helper.** `RunHealthCheckAsync` uses `HttpClient` with `timeout` and `handler` (the real `SocketsHttpHandler` when null). `RunMigrationsAsync` is a plain try/catch around `MigrateModuleDatabasesAsync`; use the source-generated `LoggerMessage` style the repository uses.
- [ ] **Step 5: Wire `Program.cs`.** First statement: if `Parse(args)` is `Healthcheck`, `return await HostCommand.RunHealthCheckAsync(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), Console.Out, null, TimeSpan.FromSeconds(3), CancellationToken.None);` (before `CreateBuilder`: no host is built). Straight after `var app = builder.Build();`: if `Parse(args)` is `Migrate`, `await using (app)` run `RunMigrationsAsync(app.Services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrate"), CancellationToken.None)` and return its code, so logs flush and no hosted service starts. End the file with `return 0;`. Update the comment above the `ApplyMigrationsOnStartup` block to say the migrate mode exists.
- [ ] **Step 6: Run the tests**: same command plus `--filter-class "*MigrateCommandTests"`. Expected: all PASS. Then `dotnet build -c Release` (0 warnings) and `dotnet test --project tests/TemplateName.ArchitectureTests -c Release` PASS.
- [ ] **Step 7: Manual check** (needs no SQL Server): `dotnet run --project src/Host/TemplateName.Api -c Release -- healthcheck` prints one line and exits 1 with nothing listening.
- [ ] **Step 8: Commit** `feat(host): add the migrate and healthcheck modes`.

### Task 2: Dockerfile, `.dockerignore`, line endings, Dependabot

**Files:**
- Create: `build/docker/Dockerfile`, `.dockerignore`
- Modify: `.gitattributes`, `.github/dependabot.yml`

**Interfaces:**
- Consumes: Task 1's two modes.
- Produces: `build/docker/Dockerfile`, built with `docker build -f build/docker/Dockerfile -t api:ci .` from the repository root; `.gitattributes` pins `*.sh`, `Dockerfile` and `*.yml` to LF.

- [ ] **Step 1: Confirm the base image tag exists and carries ICU** (spec risk 1): `docker manifest inspect mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (or the MCR tags list over HTTPS) succeeds. If it does not, stop and report; do not fall back silently.
- [ ] **Step 2: Write the failing check.** Run `docker build -f build/docker/Dockerfile -t api:ci .` and expect it to fail because the file is missing. Without Docker, note it and rely on the Task 3 CI job.
- [ ] **Step 3: Write `build/docker/Dockerfile`** to spec section 5: two stages; build stage copies the solution, `Directory.*`, `global.json`, every `*.csproj` and `packages.lock.json` first, restores with `--locked-mode`, then copies the sources and publishes `src/Host/TemplateName.Api` (`-c Release --no-restore -o /app/publish /p:UseAppHost=false`); runtime stage copies `/app/publish`, sets `ENV ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`, `USER $APP_UID`, the entrypoint and the `HEALTHCHECK` from the Global Constraints. Dockerfile `COPY` cannot glob while keeping the folder layout, so keep the restore layer cached with the approach that builds (for example one `COPY` per project directory's project and lock files, or restoring only the Api project after `COPY . .` with a `.dockerignore` that drops everything volatile); say which in a Dockerfile comment.
- [ ] **Step 4: Write `.dockerignore`** per the spec list (`.git`, `**/bin`, `**/obj`, `tests`, `docs`, `.env*`, `**/appsettings.Development.json`, `TestResults`). Keep `tests` out only if restoring the Api project alone does not need them.
- [ ] **Step 5: `.gitattributes`**: add `*.sh text eol=lf`, `Dockerfile text eol=lf`, `*.yml text eol=lf` if they are not already there; run `git add --renormalize .` and check `git status` shows no content change beyond those files.
- [ ] **Step 6: `dependabot.yml`**: add a `docker` ecosystem entry for `/build/docker`, weekly, `commit-message` prefix `build`, `include: scope`.
- [ ] **Step 7: Verify.** With Docker: the build succeeds; `docker run --rm -e ConnectionStrings__Database="Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=2;TrustServerCertificate=true" api:ci migrate; echo $?` prints a logged error and `1`; `docker inspect --format '{{.Config.User}}' api:ci` is neither empty, `0` nor `root`. Without Docker: skip and cover in Task 3's CI run.
- [ ] **Step 8: Commit** `build(docker): add the chiseled non-root image`.

### Task 3: `container-verify.sh` and the `container` and `actionlint` jobs

**Files:**
- Create: `build/scripts/container-verify.sh`
- Modify: `.github/workflows/ci.yml` (add `workflow_call:` to `on`, a `container` job, an `actionlint` job)

**Interfaces:**
- Consumes: the image from Task 2 and its `migrate` and `healthcheck` modes.
- Produces: `bash build/scripts/container-verify.sh <image>`: exit 0 only if every check of spec section 6 passes; prints the container logs on failure; `VERIFY_TIMEOUT` (seconds, default 120) bounds each wait. Every container and network it creates carries the label `container-verify=<pid>` and is removed on exit, normal or not.

- [ ] **Step 1: Write the failing negative check** (Review Focus 3): `bash build/scripts/container-verify.sh alpine:3` must exit non-zero, and afterwards `docker ps -a --filter label=container-verify -q` and `docker network ls --filter label=container-verify -q` must print nothing. Run it now: it fails because the script does not exist.
- [ ] **Step 2: Write the script** to spec section 6, steps 1 to 7, with `set -euo pipefail` and a `trap cleanup EXIT INT TERM`. Generate the SQL password (`openssl rand -hex 12` plus `Aa1!`) and the ES256 key (`openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt`); when `GITHUB_ACTIONS` is set print `::add-mask::` for the password before any use. API environment: `ASPNETCORE_ENVIRONMENT=Production`, `ConnectionStrings__Database`, `Auth__Jwt__SigningKeys__0__KeyId=verify`, `Auth__Jwt__SigningKeys__0__PrivateKeyPem=<pem>`. Run the API, read its logs, and add whatever else Production refuses to start without (spec risk 2); list every setting you needed in the script's header comment. Publish the API port to a random loopback port (`-p 127.0.0.1::8080`) and read it with `docker port`.
- [ ] **Step 3: Run the negative check, then the positive one** (`docker build ... -t api:ci .` and `bash build/scripts/container-verify.sh api:ci`): negative exits non-zero and leaves nothing behind; positive exits 0 and prints each check. Without Docker, skip both and use the CI run in Step 6.
- [ ] **Step 4: `ci.yml`.** Add `workflow_call:` next to the other triggers. Add job `container` (`runs-on: ubuntu-latest`, `timeout-minutes: 20`, `permissions: contents: read`): checkout with `persist-credentials: false`, `docker/setup-buildx-action`, `docker/build-push-action` with `load: true`, `tags: api:ci`, GHA cache, then `bash build/scripts/container-verify.sh api:ci`. Add job `actionlint`: `docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:<pinned version> -color` (pin the version, MIT licence).
- [ ] **Step 5: Lint.** Run `actionlint` locally (the pinned image or binary) on `.github/workflows/*.yml`: no findings.
- [ ] **Step 6: Push the branch and open a draft pull request** so the `container` and `actionlint` jobs run; read the run. Expected: `container` and `actionlint` green, `build-test` unchanged. Fix the script until green. (Ask the user before pushing if they have not already agreed to a pull request for this work.)
- [ ] **Step 7: Commit** `ci: verify the container image on every pull request`.

### Task 4: `cd.yml`, the deploy hook and the release wiring

**Files:**
- Create: `.github/workflows/cd.yml`, `build/deploy/roll-out.sh`
- Modify: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `ci.yml` as a reusable workflow (Task 3), `container-verify.sh <image>`, the `migrate` mode.
- Produces: `cd.yml` with triggers `push` (branch `main`), `workflow_call` and `workflow_dispatch`, inputs `release` (boolean, default false) and `version` (string); `build/deploy/roll-out.sh <environment> <image@digest>` (stub, exits 1 with an explanatory message).

- [ ] **Step 1: Write `build/deploy/roll-out.sh`**: `set -euo pipefail`; prints that the project must replace this file with its own roll-out for `$1` using `$2`, with two commented examples (a `docker compose` pull and up over SSH, a `kubectl set image`), and `exit 1`.
- [ ] **Step 2: Write `cd.yml`** to spec section 7. Job graph and conditions:
  - `detect`: checkout, outputs `is_template` = whether `.template.config/template.json` exists.
  - `ci`: `uses: ./.github/workflows/ci.yml`, `permissions: contents: read, pull-requests: write, checks: write`. Not guarded.
  - `image`: `needs: [detect, ci]`, `if: needs.detect.outputs.is_template != 'true'`; `permissions: contents: read, packages: write`; login to GHCR with `GITHUB_TOKEN`; `docker/metadata-action` with the tags from the Global Constraints (`sha-` short, `edge` when not a release, `{{version}}` from `inputs.version` and `latest` when a release); `docker/build-push-action` with `push: true`, `sbom: true`, `provenance: mode=max`, GHA cache; output the digest.
  - `verify`: `needs: image`; login, `docker pull <image>@<digest>`, `bash build/scripts/container-verify.sh <image>@<digest>`.
  - `deploy-staging`: `needs: [verify]`, `if: vars.DEPLOY_ENABLED == 'true'`, `environment: { name: staging, url: ${{ vars.STAGING_URL }} }`; steps: login, `docker run --rm -e ConnectionStrings__Database <image>@<digest> migrate` with the value from `${{ secrets.MIGRATION_CONNECTION_STRING }}`, `bash build/deploy/roll-out.sh staging <image>@<digest>`, then `curl --fail --retry 10 --retry-delay 6 --retry-connrefused "$STAGING_URL/health/ready"`. The secret reaches the container through `env:` of the step, never interpolated into the script text.
  - `deploy-production`: same steps, `needs: deploy-staging`, `if: inputs.release && vars.DEPLOY_ENABLED == 'true'`, `environment: production`, `PRODUCTION_URL`.
  - `deploy-iis`: `needs: verify`, `if: inputs.release && vars.IIS_ENABLED == 'true'`, `runs-on: [self-hosted, windows, iis]`, `environment: production`; `dotnet publish src/Host/TemplateName.Api -c Release -o publish`, then `./build/scripts/deploy-iis.ps1` (Task 5) with `vars.IIS_SITE_PATH`, `vars.IIS_APP_POOL`, `vars.IIS_HEALTH_URL` as parameters and `ConnectionStrings__Database` from the secret in `env:`.
  - Top-level `permissions: contents: read`; `concurrency: { group: deploy-${{ github.ref }}, cancel-in-progress: false }`.
- [ ] **Step 3: Check the concurrency group** (a called workflow sharing the caller's group deadlocks, and groups compare case-insensitively): `grep -n "group:" .github/workflows/ci.yml .github/workflows/cd.yml`; `ci.yml`'s expands to `<workflow name>-<ref>`, so with caller name `CD` it is `CD-refs/heads/main`; the `cd.yml` group `deploy-refs/heads/main` must differ from it in lower case. Record the comparison in the commit body.
- [ ] **Step 4: `release.yml`**: add `version: ${{ steps.release.outputs.version }}` to the `release-please` job outputs, and a job `cd`: `needs: release-please`, `if: ${{ needs.release-please.outputs.release_created == 'true' }}`, `uses: ./.github/workflows/cd.yml`, `with: { release: true, version: ${{ needs.release-please.outputs.version }} }`, `secrets: inherit`, `permissions: contents: read, packages: write, pull-requests: write, checks: write`.
- [ ] **Step 5: Lint and dry-run.** `actionlint` finds nothing in `.github/workflows`. Push; the `cd.yml` run for the branch cannot start from a push (only `main`), so start it with `gh workflow run cd.yml --ref <branch>` (that is what `workflow_dispatch` is for): expected `detect` reports template, `ci` runs, `image`, `verify` and deploy jobs are skipped.
- [ ] **Step 6: Commit** `ci: add the reusable CD workflow and the release call`.

### Task 5: `deploy-iis.ps1` with Pester tests and the `scripts` CI job

**Files:**
- Create: `build/scripts/deploy-iis.ps1`, `build/scripts/tests/deploy-iis.Tests.ps1`
- Modify: `.github/workflows/ci.yml` (job `scripts`)

**Interfaces:**
- Produces, as dot-sourceable functions (the script runs `Invoke-Deployment` only when not dot-sourced):
  - `Test-IisPreflight -PublishPath <string> -AppPool <string>`: throws when the IIS module or Hosting Bundle is missing, the app pool is missing or not `No Managed Code`, or `TemplateName.Api.dll` is not in the publish path.
  - `Invoke-Migration -PublishPath <string>`: runs `dotnet TemplateName.Api.dll migrate` there; throws on a non-zero exit.
  - `Backup-Site -SitePath <string>`: replaces `{SitePath}.previous` with a copy of the site.
  - `Publish-Site -PublishPath <string> -SitePath <string>`: robocopy mirror keeping `appsettings.Production.json`, `logs` and `app_offline.htm`; robocopy exit codes below 8 are success.
  - `Set-SiteOffline -SitePath` / `Set-SiteOnline -SitePath`: write and remove `app_offline.htm`.
  - `Wait-Healthy -HealthUrl <string> -TimeoutSeconds <int>`: returns `$true` or `$false`.
  - `Restore-Site -SitePath <string>`: restores `{SitePath}.previous`.
  - `Invoke-Deployment -PublishPath <string> -SitePath <string> -AppPool <string> -HealthUrl <string>`: the order of spec section 8; the connection string is read from `ConnectionStrings__Database` in the environment and the script has no connection-string parameter.

- [ ] **Step 1: Write the failing Pester tests** (`BeforeAll` defines no-op stubs for the Windows-only cmdlets so `Mock` works on Linux; every function above is mocked and records calls):
  - `Runs in the order preflight, migrate, backup, offline, publish, online, health`.
  - `A failed migration stops before backup, offline and publish and throws` (the site is never touched).
  - `A failed health check restores the previous files, then throws`.
  - `The script has no ConnectionString parameter` (`(Get-Command ./deploy-iis.ps1).Parameters.Keys` does not contain it).
  - `Paths with spaces reach robocopy as single arguments` with `C:\Sites\My App` (Review Focus 4).
  - `A leftover .previous folder is replaced, not merged` (Review Focus 4): `Backup-Site` removes the old folder before copying.
  - `Publish-Site keeps the server-owned files` (the mirror arguments exclude `appsettings.Production.json`, `app_offline.htm` and the `logs` folder).
- [ ] **Step 2: Run to see them fail**: `pwsh -NoProfile -Command "Invoke-Pester -Path build/scripts/tests -CI"` fails because the script is missing.
- [ ] **Step 3: Implement the script** with the signatures above. Keep every IIS or file-system call inside one of the functions so the tests can mock it.
- [ ] **Step 4: Run Pester again**, all PASS; run `Invoke-ScriptAnalyzer -Path build/scripts -Recurse -Severity Warning,Error -EnableExit`: no findings (fix, or suppress one rule with a comment naming the reason).
- [ ] **Step 5: CI job `scripts`** in `ci.yml` (`ubuntu-latest`, `timeout-minutes: 10`, `permissions: contents: read`): `shell: pwsh`; install Pester and PSScriptAnalyzer at pinned versions (`Install-Module -RequiredVersion`, versions at least two weeks old, licences MIT); run the two commands of Step 2 and Step 4. Lint with `actionlint`.
- [ ] **Step 6: Commit** `ci: add the IIS deploy script with Pester tests`.

### Task 6: CodeQL and gitleaks

**Files:**
- Create: `.github/workflows/codeql.yml`, `.github/workflows/gitleaks.yml`, `.gitleaks.toml`

**Interfaces:**
- Produces: check names `codeql` (job `analyze` named `codeql`) and `gitleaks` (job named `gitleaks`), the names Task 7 puts in the required list.

- [ ] **Step 1: Write the failing gitleaks test** (Review Focus 5): download the pinned gitleaks release into the scratchpad directory (checksum verified against the pinned SHA-256), create a throwaway git repository there, commit a file holding a synthetic GitHub-token-shaped string (`ghp_` plus 36 random alphanumerics generated by the shell, never written into this repository), and run `gitleaks git --redact --exit-code 1 <repo>`: expect exit 1. Run it on a second throwaway repository with no secret: expect 0. Both runs are the acceptance test for the workflow's command line.
- [ ] **Step 2: `gitleaks.yml`**: triggers `pull_request`, `push` to `main`, weekly `schedule` and `workflow_dispatch`; `permissions: contents: read`; job `gitleaks` (name `gitleaks`): checkout with `fetch-depth: 0` and `persist-credentials: false`; download the pinned release and verify its SHA-256 (both pinned in `env:`); scan the pull request range with `--log-opts="origin/${BASE_REF}..HEAD"` through `env:`, otherwise the full history; always `--redact`.
- [ ] **Step 3: `.gitleaks.toml`**: `[extend] useDefault = true`. Run the CLI over this repository's full history locally; add an `[[allowlists]]` entry only for a demonstrated false positive (for example a test signing key), each with a `description` and the narrowest `paths` or `regexes`. If the first run is clean, add none.
- [ ] **Step 4: `codeql.yml`**: triggers `pull_request` (branches `main`), `push` (`main`), weekly `schedule`; `permissions: contents: read, security-events: write`; job `analyze` (name `codeql`) with `if: vars.CODEQL_ENABLED != 'false'`; `github/codeql-action/init` (language `csharp`, `build-mode: manual`, queries `security-extended`), `actions/setup-dotnet` with `global.json`, `dotnet build -c Release`, `github/codeql-action/analyze`. Comment at the top: free for public repositories, needs GitHub Advanced Security on private ones, set `CODEQL_ENABLED` to `false` to turn it off.
- [ ] **Step 5: Lint** with `actionlint`; push and read both runs (CodeQL's first run on this repository may need the code-scanning setting; report what the run says).
- [ ] **Step 6: Commit** `ci(security): add CodeQL and gitleaks workflows`.

### Task 7: Documents, ADR 0033, roadmap and template wiring

**Files:**
- Create: `docs/adr/0033-delivery-pipeline.md`, `docs/deployment/README.md`, `docs/deployment/docker.md`, `docs/deployment/iis.md`, `docs/deployment/migrations.md`, `docs/deployment/production-checklist.md`
- Modify: `docs/README.md` (ADR row, the deployment pages), `docs/architecture/overview.md` (ADR list), `docs/adr/0006-ef-core-writes-dapper-reads.md` ("delivered in Plan 6" to past tense), `docs/services/api.md` (the "Plan 6" mentions in Start-up, Environments and Migrations, plus a short "Command modes" section), `docs/blueprint-review.md` (row 6 marked done with links), `docs/repository-management.md` (replace "5. Later (Plan 6)" and add the checks stage), `build/template-content/CLAUDE.md` (the `migrate` command), `build/scripts/template-smoke.sh`
- Test: `tests/TemplateName.ArchitectureTests/DocumentationTests.cs` (existing; it must pass unchanged), `bash build/scripts/template-smoke.sh`

**Interfaces:**
- Consumes: everything from Tasks 1 to 6 (names, variables, files).
- Produces: nothing later tasks need.

- [ ] **Step 1: Run `dotnet test --project tests/TemplateName.ArchitectureTests -c Release` after adding only `docs/adr/0033-delivery-pipeline.md`** and expect `Every_adr_is_listed_in_the_docs_index_and_the_architecture_overview` to FAIL.
- [ ] **Step 2: Write ADR 0033** in the format of ADR 0006 (status, date 2026-11-02, deciders, context, options considered, decision, consequences): the chiseled image and `healthcheck` mode, `migrate` on the same connection-string key, reusable workflows and the promotion model, deploy hook, gitleaks CLI instead of the Action and why (licence), CodeQL default, the template guard. Link it from `docs/README.md` and `docs/architecture/overview.md` (`adr/0033-delivery-pipeline.md)` exact link form).
- [ ] **Step 3: Write `docs/deployment/`**: `README.md` (the pipeline, the five variables, the secret, the two environments with required reviewers, OIDC note, GHCR package linking and visibility, CodeQL private-repository note and `CODEQL_ENABLED`, the runner-needs-database-access prerequisite), `docker.md` (build, run, `ASPNETCORE_HTTP_PORTS` not `ASPNETCORE_URLS`, the healthcheck, forwarded headers behind a proxy, redeploying the previous tag as the rollback), `iis.md` (the script, its order and rollback, Hosting Bundle, app pool, Data Protection keys in the database, the manual checklist for a real server), `migrations.md` (the two-login split, expand-then-contract link to `api-compatibility.md`, running `migrate` by hand), `production-checklist.md` (blueprint 16.3, each line naming the code or setting that enforces it). Link only files that ship.
- [ ] **Step 4: Update the existing pages** listed under Files; in `docs/blueprint-review.md` mark row 6 `(done)` with the spec, plan, ADR and `deployment/README.md` links, and note three deviations of this plan from the spec: `workflow_dispatch` was added to `cd.yml` so it can be run from a branch; image provenance and SBOM use BuildKit attestations, so `id-token` and `attestations` write permissions are not requested; and `deploy-iis.ps1` has no `-KeepPrevious` switch, because the previous files are always kept for the rollback. Amend the spec in the same commit: the trigger line and `image` row of section 7, D12, and the parameter list of section 8.
- [ ] **Step 5: `template-smoke.sh`**: add to the required-files list `build/docker/Dockerfile`, `.dockerignore`, `build/scripts/container-verify.sh`, `build/scripts/deploy-iis.ps1`, `build/deploy/roll-out.sh`, `.github/workflows/cd.yml`, `.github/workflows/codeql.yml`, `.github/workflows/gitleaks.yml`, `docs/deployment/README.md`; add a check that the generated `Dockerfile` names `$project_name.Api.dll` and that no generated workflow or script contains `.template.config` other than the guard line in `cd.yml`.
- [ ] **Step 6: Verify**: `dotnet test --project tests/TemplateName.ArchitectureTests -c Release` PASS; `bash build/scripts/template-smoke.sh` ends `template-smoke: passed` (it also checks every relative link in the generated docs).
- [ ] **Step 7: Commit** `docs: document the delivery pipeline and add ADR 0033`.

### Task 8: Whole-branch verification and pull request

**Files:** none new.

- [ ] **Step 1: Local gates**: `dotnet build -c Release` (0 warnings), `dotnet format --verify-no-changes`, `dotnet test --project tests/TemplateName.UnitTests -c Release`, `... ArchitectureTests`, `bash build/scripts/template-smoke.sh`. All green.
- [ ] **Step 2: Integration tests** (Docker): `dotnet test --project tests/TemplateName.IntegrationTests -c Release`. Expected: previous totals plus the new Host tests; the one by-design skip stays. If Docker is unavailable, say so and rely on CI.
- [ ] **Step 3: CI**: the pull request's `build-test`, `template-smoke`, `licenses`, `container`, `actionlint`, `scripts`, `gitleaks`, `commit-lint` jobs are green and `codeql` is green or reported. Read the `container` job's log for each of the seven checks.
- [ ] **Step 4: Pull request title** `feat: add the delivery pipeline` (no `!`: no route, response or column changed; the OpenAPI snapshot test must be unchanged). The body lists the three spec deviations of Task 7 Step 4 and the deferred manual checks (real IIS server, a real deploy, CodeQL on a private repository). End it with the attribution line from the session reminder.
- [ ] **Step 5: Tell the user** the new repository settings to apply by hand after merge: `bash build/scripts/configure-github-repo.sh --checks build-test,template-smoke,commit-lint,licenses,container,codeql,gitleaks` (drop `codeql` if it is disabled for this repository), and the variables and environments of `docs/deployment/README.md` for generated projects.
