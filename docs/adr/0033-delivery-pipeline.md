# 0033. Delivery pipeline: a chiseled image, a `migrate` mode, reusable workflows and a dormant deploy hook

- Status: Accepted
- Date: 2026-11-02
- Deciders: Lee Jia Le

## Context
Plans 1 to 5d built the service; nothing yet built it into an image, migrated a deployed database or rolled it out. ADR 0006 promised a `migrate` command-line mode for production, ADR 0031 made every schema change expand-only so a migration can run while the old version still serves, and the blueprint (13.3 to 13.5, 16, Phase 7, as amended by the review) asked for a Dockerfile, a CD pipeline, an IIS script, CodeQL and gitleaks. The design of this plan (`docs/superpowers/specs/2026-11-02-delivery-pipeline-design.md` in the template repository, which a generated project does not carry) settled twelve points; this ADR records the ones that are hard to reverse or that a project would otherwise have to rediscover. The working documents are [`docs/deployment/`](../deployment/README.md).

A template cannot know where a project runs, so the pipeline has to be real enough to gate and verify a release, and neutral about the target.

## Options considered

**The runtime image (D1, D3).**
1. **`aspnet:10.0` plus curl.** Easy to exec into, larger, more to patch, a root-capable shell.
2. **`aspnet:10.0-noble-chiseled`, no `HEALTHCHECK`.** Distroless and non-root, but plain `docker run` and Compose users get no health status.
3. **`aspnet:10.0-noble-chiseled-extra` with a `healthcheck` mode of the API binary.** The `-extra` variant carries ICU and tzdata, which the `ms` and `zh-Hans` resources and the time-zone validation need; the chiseled image has no curl, so the `HEALTHCHECK` runs `dotnet TemplateName.Api.dll healthcheck`. It probes `/health/live`, not `/health/ready`: a database blip must not make an orchestrator restart healthy containers.

**The database login for migrations (D2).**
1. **A second key, `ConnectionStrings:Migrations`.** The DDL secret then sits beside the runtime settings and can reach the running API by accident.
2. **The same key, `ConnectionStrings:Database`, set to the DDL login for the one `migrate` process.** No new key; the API never holds the DDL secret.

**The workflow shape (D4, D5, D7).**
1. **One large `cd.yml`.** Docker regressions appear after the merge.
2. **A `workflow_run` chain.** It runs against the default branch and offers no single run to hang an approval on.
3. **Reusable workflows.** `ci.yml` has `workflow_call` and a `container` job; `cd.yml` calls it and everything else `needs` it; `release.yml` calls `cd.yml`. A green push to `main` goes to staging, a release goes to staging and then to production.

**Deploying (D7, D8).**
1. **A concrete target (Kubernetes, one cloud).** Most projects would strip it out.
2. **No deploy jobs.** Every project rewrites the gating and the environments.
3. **Real but dormant jobs and one hook.** They run only when `DEPLOY_ENABLED` is `true`, run `migrate` from the image, call `build/deploy/roll-out.sh`, and smoke-test `/health/ready`. For IIS, a script migrates first and swaps the files second; the blueprint's order (offline, copy, migrate, online) was rejected because a failed migration would then happen with the site already offline.

**Scanning (D9, D10).**
1. **gitleaks-action.** Its terms ask for a paid licence key on organisation accounts.
2. **The gitleaks CLI** (MIT), pinned by version and checked against a SHA-256.
3. **CodeQL off by default.** Security tooling nobody turns on. **CodeQL always on** would turn private repositories without GitHub Advanced Security red.

## Decision
We chose the third option of the image, the same key for `migrate`, reusable workflows with dormant deploy jobs behind one hook, the gitleaks CLI and CodeQL on unless `CODEQL_ENABLED` is `false`.

- **Image.** `build/docker/Dockerfile` builds with `sdk:10.0` (`dotnet restore --locked-mode`, then `dotnet publish`) and runs on `chiseled-extra` as the image's non-root user. HTTP on 8080 only (`ASPNETCORE_HTTP_PORTS`); TLS ends at the proxy. The image sets no HTTPS port, because HTTPS redirection is on outside Development and would answer the `HEALTHCHECK` with a redirect, which the probe does not follow (it reports `unhealthy (307)`). `AllowedHosts` must list `127.0.0.1` beside the public names, because the probe sends that host; `container-verify.sh` runs with that shape. No secret is baked in.
- **Command modes.** `Program.cs` recognises `migrate` and `healthcheck` only as the exact first argument (`HostCommand`); anything else, `--urls` included, runs the web host. `healthcheck` builds no host: one GET to `http://127.0.0.1:{port}/health/live`, 3 seconds, exit 0 on a 2xx. `migrate` builds the host without starting it (no Kestrel, outbox dispatcher, scheduler or delivery worker), calls `MigrateModuleDatabasesAsync`, and exits 0 or 1; it ignores `Database:ApplyMigrationsOnStartup`.
- **One set of checks.** `build/scripts/container-verify.sh <image>` starts a throwaway SQL Server, runs `migrate` twice, starts the image in Production with the minimum configuration, waits for the image's own `HEALTHCHECK` to say `healthy`, calls `/health/ready` and asserts a non-root user. The `container` job of `ci.yml` runs it on every pull request, and `cd.yml` runs it again on the pushed digest, so CI and CD cannot drift apart and what is deployed is what was tested (D6).
- **Promotion (D5).** `cd.yml` runs on a push to `main`, on `workflow_call` and on `workflow_dispatch`. A release (`release` true: `x.y.z` and `latest` tags, production) is accepted only from `main` (the call from `release.yml`) or from a `v*` tag; `edge` and the staging deploy happen only from `main`; a dispatch from any other branch or tag without `release` is a dry run (`ci`, `image` with only `sha-<short>`, `verify`, no deploy). Its jobs: `detect`, `ci`, `image`, `verify`, `deploy-staging`, `deploy-production` (release only) and `deploy-iis` (release only), and, added by [ADR 0034](0034-maintenance-mode-for-contract-phase-releases.md), `maintenance` (release only). The image is `ghcr.io/<repository>/api` (lower-cased): `sha-<short>` and `edge` from `main`, `x.y.z` and `latest` from a release. Staging and production are GitHub environments; production has required reviewers.
- **Template guard (D11).** The `detect` job reads `.template.config/template.json`; where it exists (the template repository) `image` and everything after it are skipped, while the `container` job and the other checks still run. A generated project has no `.template.config`, so nothing is skipped.
- **Attestations (D12).** `docker/build-push-action` writes the SBOM and the provenance (`mode=max`) as BuildKit attestations pushed beside the image, so the job requests only `contents: read` and `packages: write`, not `id-token` or `attestations`.
- **Deploy hook (D7).** `build/deploy/roll-out.sh <environment> <image@digest>` ships as a stub that exits 1 with a message, so a project that sets `DEPLOY_ENABLED` without writing its roll-out gets a red job, not a deploy that did nothing.
- **IIS (D8).** `build/scripts/deploy-iis.ps1`: preflight, `migrate` while the old version serves, a copy of the site to `{SitePath}.previous`, `app_offline.htm`, a mirror of the publish folder (server-owned files kept), online, a bounded health check, and a restore of `.previous` on failure. The database is not rolled back; the migrations are expand-only (ADR 0031). This is the normal order: a release that adds a contract migration runs in a maintenance mode with the page up before `migrate` and no restore on failure, and the page is the shared `build/deploy/maintenance.html` ([ADR 0034](0034-maintenance-mode-for-contract-phase-releases.md)).
- **Scanners.** `codeql.yml` (job `codeql`, `build-mode: manual`, `security-extended`, pull requests, `main`, weekly) and `gitleaks.yml` (job `gitleaks`, CLI v8.30.1, a checksum, `--redact`; the pull request range on a pull request, the full history on `main` and weekly). `.gitleaks.toml` extends the default rules with four allowlist entries, each an exact path and an exact value.

## Consequences
- **Concurrency is two groups.** GitHub keeps one pending run per concurrency group and replaces it with a newer one even with `cancel-in-progress: false`, so a release run has its own group (`deploy-<ref>-release`) and a push to `main` cannot drop it while it waits. The cost: an `edge` roll-out to staging and a release roll-out to staging can overlap, so a project's `roll-out.sh` must tolerate two runs at once. The release commit is built twice (the push and the release call), staging is rolled out twice for it, and the second build re-points `sha-<short>` to a new digest.
- **Two approvals for a Docker and IIS release.** `deploy-production` and `deploy-iis` share the `production` environment. `deploy-iis` needs only `verify` and `maintenance` (the latter added by [ADR 0034](0034-maintenance-mode-for-contract-phase-releases.md)), so it can finish before the staging smoke test has passed.
- **The build can fail with no code change.** `sdk:10.0` is a moving tag (`global.json` pins the SDK inside it) and NuGet Audit runs in the restore, so a newly disclosed advisory fails the image build the day it is published. That is intended; the fix is a package bump.
- **The Production minimum is kept in step by the `container` job, not by a unit test.** The list in the header of `container-verify.sh` was read from the options classes; the job fails the day a new required key appears. It cannot rule out a problem that only shows under a real workload: the outbox, the jobs, the delivery worker and the seeding all run in the container, but against an empty database.
- **The scanner allowlist can be edited by a pull request.** A pull request that changes `.gitleaks.toml` is scanned with its own allowlist. A project should protect `.gitleaks.toml` and `.github/` with CODEOWNERS and required review.
- **CodeQL needs a licence on a private repository.** `CODEQL_ENABLED=false` skips the job, and a skipped job satisfies a required check named `codeql`. Scheduled workflows run only on the default branch and are disabled after 60 days without activity on a public repository.
- **A container rollback is a redeploy of the previous tag**; there is no automatic production rollback, no blue/green and no canary. IIS rolls back its own files only, and not at all in maintenance mode ([ADR 0034](0034-maintenance-mode-for-contract-phase-releases.md)).
- **The runner must reach the database.** A hosted runner usually cannot; the project uses a self-hosted runner or a VPN. Documented, not built.
- **Not run when this was written:** a real IIS server, a real deploy, the first CI runs of the `container`, `scripts`, CodeQL and gitleaks jobs, and every local run of Docker. The Pester tests mock the IIS and file calls; the manual checklist in [`docs/deployment/iis.md`](../deployment/iis.md) is the verification for a real server, and a first release is the verification for the rest.
- **Not built, by design:** a load test, ZAP, an image vulnerability scan, a Kubernetes or cloud target, EF migration bundles, an `api` service in `docker-compose.yml`.
- **Not built, and still open:** the upgrade-path test of ADR 0031 (a project is required to have it, the template does not ship it), a dedicated contract-phase runbook (ADR 0034 adds the maintenance mode a contract release runs in and its recovery steps; the full runbook is still open), the operational runbooks (user locked out, lost MFA device, rotating signing keys, restoring a backup, replaying dead letters), the encryption of the Data Protection key ring at rest ([production checklist](../deployment/production-checklist.md#data-protection-keys-persisted-and-protected)), and exempting `/health/*` from `UseHttpsRedirection`. Today an IIS site with an https binding redirects a plain local health request (the ASP.NET Core Module sets `ASPNETCORE_ANCM_HTTPS_PORT`), so `IIS_HEALTH_URL` must be the public https URL with a valid certificate, and a container must never be given an https port; the exemption would remove both constraints, but it changes the host pipeline and needs its own integration test and a line in an ADR. Earlier ADRs (0015, 0017, 0020, 0022) had expected the key-ring protector from this plan; it was not part of it.
- Follow-up actions: ADR 0006 is delivered. The open items above are unscheduled production-hardening follow-ups, and whether they gate 1.0 is undecided (the roadmap in the template repository lists them).
