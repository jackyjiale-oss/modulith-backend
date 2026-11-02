# Delivery pipeline — design

- Status: Draft for review (2026-11-02). The design was approved part by part in the design session (the image and its two modes, the pipelines, then the IIS script, scanners, documents and tests); this file is the written form.
- Date: 2026-11-02
- Roadmap row: 6, "Delivery pipeline", in `docs/blueprint-review.md` Section 6.
- Builds on: Plans 1–5d, all merged (main `553c258`); ADR 0006 (the `migrate` mode, promised for this plan), ADR 0015 (signing keys from configuration), ADR 0017 (Data Protection keys in the Auth database), ADR 0031 (expand-only database changes), `docs/repository-management.md`.
- Source: `BACKEND_TEMPLATE_BLUEPRINT.md` 13.3–13.5, 16, 17 and Phase 7, as amended by `docs/blueprint-review.md` 2.13 and the Section 4 row "Migrations in CD" (the review wins).

## 1. Intent

**Outcome.** A project generated from this template can be built into a container image, have its database migrated and be rolled out to Docker or IIS from a pipeline that cannot ship a red build. It also gets code and secret scanning. Whatever the pipeline does is exercised in CI for the template itself, so a regression in the Dockerfile or in `migrate` fails a pull request instead of a release.

**Success looks like.**

1. `docker build -f build/docker/Dockerfile .` produces a chiseled, non-root image that starts, reports `healthy` through its own `HEALTHCHECK` and answers `/health/ready` against a migrated SQL Server.
2. `dotnet TemplateName.Api.dll migrate` applies every module's migrations with the same code path as development and tests (`MigrateModuleDatabasesAsync`), exits 0 or 1, and starts no web server and no background worker.
3. Every pull request builds and checks the image (`container` job); every push to `main` that is green builds, checks and pushes it and, once `DEPLOY_ENABLED` is set, rolls out to staging; a release rolls out to staging and then to production behind required reviewers.
4. The deploy to staging and production is wired and gated but target-neutral: a project plugs its own roll-out into one marked hook.
5. IIS has a script with a rollback, tested with mocks in CI and with a manual checklist on a real server.
6. CodeQL and gitleaks run on pull requests and on a schedule, and can be made required checks.
7. Nothing in the template repository publishes an image or deploys; only a generated project does.
8. Nothing here changes a route, a response, a table or a column (ADR 0031).

**Said by you:** CD goes as far as publishing and verifying the image, with real environment-gated deploy jobs behind one per-project hook (not a deploy to one concrete target); the base image is chiseled with a `healthcheck` mode; `migrate` reads the same `ConnectionStrings:Database` and the job supplies the DDL login; a push to `main` goes to staging and a release goes to production; the pipeline is reusable workflows with a `container` job in `ci.yml`.

**Assumed by me:** k6 and ZAP stay out (blueprint Phase 7 lists them, the roadmap row does not); generated projects deploy from GitHub Actions; a project's database may be unreachable from a hosted runner, in which case it uses a self-hosted runner (documented, not built).

## 2. Non-goals

- **No load test, no ZAP baseline, no image vulnerability scan** (`load-test.yml`, Trivy). They are 1.x candidates.
- **No deploy target built in.** No Kubernetes manifests, Terraform, Helm chart, Azure or AWS specifics; the roll-out is a hook.
- **No `api` service in `docker-compose.yml`.** Developers run `dotnet run`; a project that wants it adds it.
- **No EF migration bundles** (review Section 4).
- **No blue/green or canary rollout, no automatic production rollback.** IIS rolls back its own files on a failed health check; a container rollback is "redeploy the previous tag" (runbook).
- **The runbooks of blueprint 18** (user locked out, lost MFA device, rotating signing keys, restoring a backup, replaying dead letters) stay in Plan 7. This plan writes the delivery runbooks only.
- **No new NuGet package.** The pipeline adds tools to workflows (gitleaks, actionlint, PSScriptAnalyzer, Pester), none to the solution, so Central Package Management and the licence check are untouched.

## 3. Decisions

| # | Decision | Why | Rejected, and cost if you disagree |
|---|---|---|---|
| D1 | **Runtime image `aspnet:10.0-noble-chiseled-extra`, with a `healthcheck` mode of the API binary.** | No shell or package manager to attack, non-root by default. The `-extra` variant carries ICU and tzdata; without ICU the `ms` and `zh-Hans` resources fail. A chiseled image has no curl, so the `HEALTHCHECK` runs the binary. | **Debian image plus curl:** easier to exec into, larger, more to patch. **Chiseled with no `HEALTHCHECK`:** plain `docker run` and Compose users get no health status. |
| D2 | **`migrate` mode reads `ConnectionStrings:Database`; the job sets the DDL login for that one process.** | No new key, and the running API never holds the DDL secret. | **A `ConnectionStrings:Migrations` key:** the DDL secret sits beside the runtime settings and can reach the API by accident. |
| D3 | **`healthcheck` probes `/health/live`, not `/health/ready`.** | A database blip must not make an orchestrator restart healthy containers. `/health/ready` is for load balancers and CD verification. | **`/health/ready` in the image:** restart storms on a database outage. |
| D4 | **Reusable workflows.** `ci.yml` gains `workflow_call` and a `container` job; `cd.yml` calls it and everything below `needs` it; `release.yml` calls `cd.yml`. | The container is tested on the pull request, and production approval hangs on one run. A pushed tag made with `GITHUB_TOKEN` would not start another workflow, a reusable call does not depend on the token. | **One large `cd.yml`:** Docker regressions appear after merge. **`workflow_run` chain:** runs against the default branch and has no single run to gate. |
| D5 | **A green push to `main` goes to staging; a release goes to staging, then production.** | Staging is always fresh and production is a deliberate act (blueprint 13.5). | **Every main push to production:** releases gate nothing. **Tags only:** staging is as old as the last release. |
| D6 | **The pushed digest is verified, with the same script CI uses.** `build/scripts/container-verify.sh <image>`. | What was tested is exactly what is deployed, and CI and CD cannot drift apart. | **Trust the CI build:** a different build could be what gets pushed. |
| D7 | **Deploy jobs are real but dormant.** They run only when the repository variable `DEPLOY_ENABLED` is `true`, run `migrate` from the image, call one hook (`build/deploy/roll-out.sh`) and smoke-test `/health/ready`. | The template cannot know the target; a failing placeholder would redden every project, a skipped job costs nothing. | **A concrete target:** most projects would strip it out. **No deploy jobs:** every project writes the gating and environment wiring again. |
| D8 | **IIS migrates first, then swaps files; the previous files are kept for rollback.** | Migrations are expand-only (ADR 0031), so they are safe while the old version still serves, and a failed migration leaves the site untouched. This deliberately differs from blueprint 16.2 (offline, copy, migrate, online). | **The blueprint order:** longer downtime, and a failed migration happens with the site already offline. |
| D9 | **CodeQL runs unless `CODEQL_ENABLED` is `false`.** | CodeQL is free for public repositories but needs GitHub Advanced Security on private ones; turning scanning off must be a visible choice. | **Off by default:** security tooling nobody turns on. **Always on:** a red build on private repositories without a licence. |
| D10 | **gitleaks runs as the pinned CLI, checksum-verified, not as the official Action.** | The Action asks for a paid licence key for organisation accounts, and the repository rules say to read the terms of anything we adopt for usage fees. The CLI is MIT. | **The Action:** a fee or a failure for organisation repositories. |
| D11 | **The template repository does not publish or deploy.** The `image` and deploy jobs are skipped where `.template.config` exists; the `container` job and the checks on the files still run. | The template's own image would be named `TemplateName` and published for nobody. The same idiom `ci.yml` already uses for the smoke test, inverted. | **Publish from the template repo:** a pointless image and a `packages: write` token with no purpose. |
| D12 | **Images carry provenance and SBOM attestations.** | Nearly free with `docker/build-push-action`, and the supply-chain check of a client's security review asks for it. | **None:** a gap to explain. |

## 4. Command modes

`Program.cs` recognises `migrate` and `healthcheck` only as the exact first argument. Anything else (`--urls`, `--environment`) falls through to the normal web host, so `dotnet run -- --urls ...` is unchanged. The recognition and the two behaviours live in a small internal class in the Host project (`HostCommand`) so they can be unit-tested; `Program.cs` calls it before `app.Run()`.

**`migrate`.**
- Builds the host (all module registrations, so the registered contexts are known), calls `MigrateModuleDatabasesAsync`, then returns without `app.Run()`. Hosted services start only with the host, so no Kestrel, outbox dispatcher, scheduler or delivery worker runs.
- Exit code 0 on success, 1 on any failure (missing connection string, login failure, DDL failure, an unreachable server). The failure is logged with the exception; the connection string is never logged.
- Uses `ConnectionStrings:Database` (D2). It is idempotent: a second run applies nothing and exits 0.
- It does not honour `Database:ApplyMigrationsOnStartup`; that flag stays Development-only (ADR 0006).

**`healthcheck`.**
- Does not build the host. One HTTP GET to `http://127.0.0.1:{port}/health/live`, a short timeout (3 seconds), exit 0 on a 2xx and 1 otherwise. The port is the first entry of `ASPNETCORE_HTTP_PORTS`, 8080 when unset.
- Prints one line (status or the failure) and nothing else.

## 5. The Dockerfile

`build/docker/Dockerfile`, with a root `.dockerignore` (`.git`, `bin`, `obj`, `tests`, `docs`, `.env*`, `**/appsettings.Development.json`, `TestResults`).

- **Build stage** (`sdk:10.0`): copy the solution file, `Directory.*`, `global.json`, `nuget.config` if present, every project file and every `packages.lock.json` first, `dotnet restore --locked-mode`, then copy the sources and `dotnet publish src/Host/TemplateName.Api -c Release --no-restore -o /app/publish /p:UseAppHost=false`. The layers above the sources are cached until a project file or a lock file changes.
- **Runtime stage** (`aspnet:10.0-noble-chiseled-extra`): `WORKDIR /app`, copy `/app/publish`, `ENV ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`, run as the image's non-root user, `ENTRYPOINT ["dotnet", "TemplateName.Api.dll"]`, `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "TemplateName.Api.dll", "healthcheck"]`.
- **Labels** (`org.opencontainers.image.*`) come from `docker/metadata-action`; the version is the release tag, which release-please writes into `Version.props`.
- **Configuration** is environment variables only; no secret is baked in. TLS ends at the proxy; `ForwardedHeaders` with known proxies is already configured and the runbook says it must be set behind a load balancer (blueprint review 2.11).
- `.github/dependabot.yml` gains a `docker` ecosystem entry for the base images.

## 6. The `container` job and `container-verify.sh`

`build/scripts/container-verify.sh <image>` is the one set of checks, used by CI and by CD:

1. Start a throwaway SQL Server container on a private Docker network, with a generated, masked password (never a value in a file).
2. Run `<image> migrate` against it, retrying while the server starts, then run it a second time and expect exit 0 (idempotent).
3. Start `<image>` as the API, in `Production`, with the minimum required configuration (signing key, connection string; the plan pins the exact set and a test keeps it in step with the options classes).
4. Wait until `docker inspect` reports the health status `healthy` (so the `HEALTHCHECK` is exercised) or fail after a timeout.
5. Call `/health/ready` from the runner: 200.
6. Assert the container's configured user is not root.
7. Always remove the containers and the network, and print the container logs on failure.

The `container` job in `ci.yml` runs on every pull request and every push to `main`: build the image with the buildx cache, then run the script against the local image. It runs in parallel with `build-test`.

## 7. `cd.yml` and the release wiring

`cd.yml` triggers on `push` to `main` and on `workflow_call` (inputs: `release` boolean, `version` string). Top-level permissions are `contents: read`; each job raises only what it needs. `concurrency` is per ref with `cancel-in-progress: false`, so a deploy is never cancelled half-way. Actions use their latest major tag, as the other workflows do.

| Job | Runs when | What it does |
|---|---|---|
| `ci` | always | Calls `ci.yml`. The caller grants `pull-requests: write` and `checks: write`, because a called workflow cannot raise its own permissions. |
| `image` | `ci` passed, not in the template repository (D11) | Builds and pushes `ghcr.io/<repo>/api` with provenance and SBOM. A main push gets `sha-…` and `edge`; a release gets `x.y.z` and `latest`. Permissions: `contents: read`, `packages: write`, `id-token: write`, `attestations: write`. Outputs the digest. |
| `verify` | after `image` | Pulls the image by digest and runs `container-verify.sh` (D6). |
| `deploy-staging` | after `verify`, `DEPLOY_ENABLED` is `true` | Environment `staging`. Runs `migrate` from the image with the environment's DDL secret, calls `build/deploy/roll-out.sh staging <image@digest>`, then polls `<STAGING_URL>/health/ready`. |
| `deploy-production` | `release` is true, after `deploy-staging` | Environment `production` with required reviewers; the same three steps. |
| `deploy-iis` | `IIS_ENABLED` is `true`, `release` is true | Self-hosted Windows runner, environment `production`: `dotnet publish`, then `deploy-iis.ps1`. |

`build/deploy/roll-out.sh` ships as a documented stub: it explains what to replace and exits 1 with a clear message, so a project that sets `DEPLOY_ENABLED` without writing its roll-out fails loudly instead of "deploying" nothing.

`release.yml` gains a job `cd` that `needs: release-please`, runs when `release_created` is `true`, and calls `./.github/workflows/cd.yml` with `release: true` and the tag, `secrets: inherit` and the permissions above. The release commit is therefore built by two runs (the push to `main` and the release call); both are queued by `concurrency`, so staging is rolled out twice for that commit (the second time with the release tags). The plan may remove the duplicate with a `release` input; it is accepted for 1.0.

Branch protection is unaffected: on pull requests `ci.yml` runs directly and its check names do not change; only the `cd.yml` run shows them prefixed (`ci / build-test`).

## 8. `deploy-iis.ps1`

`build/scripts/deploy-iis.ps1 -PublishPath -SitePath -AppPool -HealthUrl [-KeepPrevious]`. The connection string comes from the environment (`ConnectionStrings__Database`), never from an argument.

1. **Preflight:** the IIS administration module is present, the ASP.NET Core Hosting Bundle is installed, the app pool exists with `No Managed Code`, the publish path holds `TemplateName.Api.dll`.
2. **Migrate:** `dotnet TemplateName.Api.dll migrate` from the staged publish folder, while the old version still serves (D8). A non-zero exit stops the script with the site untouched.
3. **Swap:** copy the current site files to `{SitePath}.previous` (replacing the one before), write `app_offline.htm`, mirror the publish folder over the site while keeping server-owned files (`appsettings.Production.json`, `logs`, `app_offline.htm`), then remove `app_offline.htm`.
4. **Check:** poll `HealthUrl` (`/health/ready`) for a bounded time.
5. **Roll back on failure:** restore `{SitePath}.previous` and report; the database is not rolled back, since the migrations are expand-only and the old code runs on the new schema (ADR 0031).

The IIS calls sit in small functions so Pester can mock them. The runbook carries the manual checklist for a real server (Hosting Bundle, app pool identity with least privilege, Data Protection keys already in the database per ADR 0017, so a web farm works).

## 9. Security scanning

- **`codeql.yml`:** language `csharp`, `build-mode: manual` (`dotnet build -c Release`), query suite `security-extended`; on pull requests to `main`, on push to `main` and weekly. The job runs unless `vars.CODEQL_ENABLED == 'false'` (D9); `docs/deployment/` says why and how.
- **`gitleaks.yml`:** the CLI at a pinned version with a verified checksum (D10); on pull requests it scans the PR's commit range, on push to `main` and weekly it scans the full history; `--redact` so a finding never prints the secret. `.gitleaks.toml` extends the default rules and allowlists only the test fixtures that need it, each with a comment.
- Both can be required checks; `configure-github-repo.sh` and `docs/repository-management.md` add `container`, `codeql` and `gitleaks` to the required list.

## 10. Documents and housekeeping

- `docs/deployment/`: `README.md` (the pipeline and its variables, secrets and environments), `docker.md`, `iis.md`, `migrations.md` (the two-login split, expand-then-contract, running `migrate` by hand), `production-checklist.md` (blueprint 16.3, with each line tied to the code that enforces it).
- ADR 0033, "Delivery pipeline": the image, the modes, the promotion model, the scanner choices and the licence reasoning. ADR 0006 changes "delivered in Plan 6" to the past tense.
- `docs/blueprint-review.md` Section 6 marks row 6 done, with the spec, plan and ADR links; `docs/README.md` lists the new pages; `CLAUDE.md` (the one under `build/template-content/`) adds the `migrate` command.
- The `.github/pull_request_template.md` checklist keeps its lines; no change.

## 11. Tests

- **Unit:** `HostCommand` parsing (exact first argument only, others fall through), the `healthcheck` probe against a local listener (2xx, 5xx, timeout, no listener, port from `ASPNETCORE_HTTP_PORTS`), exit codes.
- **Integration (Testcontainers SQL Server):** `migrate` on an empty database creates every module schema and exits 0; run again, nothing changes; with a bad connection string it exits 1 and the log holds no password.
- **Container (CI, every pull request):** `container-verify.sh` on the built image.
- **Workflow files:** `actionlint` on every workflow in CI.
- **IIS script:** PSScriptAnalyzer and Pester with mocked IIS and file functions, run with PowerShell on the Ubuntu runner.
- **Template smoke:** the generated project contains no leftover `TemplateName` in the Dockerfile, scripts or workflows, and its `cd.yml` jobs are not skipped by the template guard.
- The coverage gate (80 % on the gated namespaces) is unchanged; the Host project is outside it, so these tests are for correctness, not for the number.

## 12. Risks and points the plan must settle

1. **The chiseled-extra tag.** Confirm `aspnet:10.0-noble-chiseled-extra` exists and carries ICU before the Dockerfile is written; fall back to `-chiseled` plus a documented globalization setting only if it does not.
2. **Minimum Production configuration** for the container to start (signing key, Data Protection, rate limits, notification settings): the plan lists it and a test checks it against the options classes.
3. **The template guard.** `hashFiles` is not available in a job-level `if`; the guard is a small first step or job that outputs whether `.template.config` exists. Check that a generated project passes it.
4. **GHCR.** A new package is private to the repository by default; the runbook says how to link it to the repository and set its visibility.
5. **Real IIS and a real deploy** cannot run in CI. The manual checklist in `docs/deployment/iis.md` is the verification; it is a deferred item of this plan, like the social-login provider checks.
6. **CodeQL on private repositories** (D9) and **runner access to the database** (D7) are documented prerequisites, not solved here.
7. **Third-party action and tool versions** (gitleaks, actionlint, Pester, PSScriptAnalyzer) are pinned by the plan after checking their licences and the dates of their releases.
