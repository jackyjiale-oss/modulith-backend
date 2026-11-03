# Deployment

How a change becomes a verified image, a migrated database and a running service. The pipeline is GitHub Actions; the roll-out itself is yours (one hook), except for IIS, which has a script. Decisions and their reasons: [ADR 0033](../adr/0033-delivery-pipeline.md); the maintenance mode of a release that contracts the database: [ADR 0034](../adr/0034-maintenance-mode-for-contract-phase-releases.md).

What was and was not run: the workflows, the Dockerfile and the scripts were written and unit-checked in the template repository, and the IIS script has Pester tests with the IIS and file calls mocked. A real IIS server, a real deploy, a real proxy serving the maintenance page and the first CI runs of the `container`, `scripts`, CodeQL and gitleaks jobs had not been executed when this page was written, and the Docker runs on the developer machine had not either. Treat the first release of a project as the first real test of the pipeline, and the checklist in [`iis.md`](iis.md#manual-checklist-for-a-real-server) as the test of the IIS script.

| Page | Covers |
|---|---|
| [`docker.md`](docker.md) | Build, run, port, health check, proxy, the maintenance hook, rollback |
| [`iis.md`](iis.md) | The IIS script (normal and maintenance orders, recovery) and the checklist for a real server |
| [`migrations.md`](migrations.md) | `migrate`, the two logins, expand then contract and the maintenance mode |
| [`production-checklist.md`](production-checklist.md) | The production checklist, line by line |

## The pipeline

| Workflow | Runs on | What it does |
|---|---|---|
| `ci.yml` | pull request, push to `main`, `workflow_dispatch`, `workflow_call` | Jobs `build-test`, `template-smoke` (its step is skipped in a generated project, so the job passes at once), `licenses`, `container`, `actionlint`, `scripts`. |
| `pr-title.yml` | pull request | Job `commit-lint`: the title is a Conventional Commit. |
| `codeql.yml` | pull request to `main`, push to `main`, weekly | Job `codeql`: CodeQL for C# (`security-extended`). |
| `gitleaks.yml` | pull request, push to `main`, weekly, `workflow_dispatch` | Job `gitleaks`: secret scan with the gitleaks CLI. |
| `cd.yml` | push to `main`, `workflow_call`, `workflow_dispatch` | Calls `ci.yml`, then builds, verifies and deploys the image. |
| `release.yml` | push to `main` | release-please; when it creates a release it calls `cd.yml` with `release: true`. |

The `container` job builds `build/docker/Dockerfile` and runs `build/scripts/container-verify.sh` on it (a throwaway SQL Server, `migrate` twice, start in Production, wait for the image's own `HEALTHCHECK`, `GET /health/ready`, assert a non-root user). Every pull request does this, so a regression in the Dockerfile or in `migrate` fails the pull request, not the release. The `scripts` job runs the Pester tests of the IIS script, PSScriptAnalyzer and the tests of the two maintenance scripts (`build/scripts/tests/maintenance-scripts.test.sh`, against throwaway git repositories); `actionlint` lints every workflow.

### `cd.yml`

| Job | Runs when | What it does |
|---|---|---|
| `detect` | always | Reads whether `.template.config/template.json` exists, which is true only in the template repository. |
| `maintenance` | a release, and this is not the template repository | Decides whether the production release runs in maintenance mode ([below](#maintenance-mode)): detects a contract migration, applies the `maintenance` input and outputs `enabled`. Runs beside `ci` and does not gate staging; `deploy-production` and `deploy-iis` need it. |
| `ci` | always | Calls `ci.yml`, so what is published passed the same jobs as a pull request. |
| `image` | `ci` passed and this is not the template repository | Builds and pushes `ghcr.io/<repository>/api` (the name is lower-cased) with an SBOM and a provenance attestation. Tags: `sha-<short>` on every run, `edge` only on a non-release run from `main`, `x.y.z` and `latest` on a release. A release is refused unless the ref is `main` or a `v*` tag. Outputs the digest. |
| `verify` | after `image` | Pulls the image by digest and runs `container-verify.sh` on it. |
| `deploy-staging` | after `verify`, `DEPLOY_ENABLED` is `true`, and the run is on `main` or a release | Environment `staging`: `migrate` from the image, `build/deploy/roll-out.sh staging <image@digest>`, then `GET $STAGING_URL/health/ready`, retried up to ten times 6 seconds apart (each try at most 15 seconds); an empty `STAGING_URL` fails the step with a message. |
| `deploy-production` | a release, after `deploy-staging` and `maintenance`, and `DEPLOY_ENABLED` is `true` | Environment `production`: the same three steps. In maintenance mode the maintenance hook runs `on` before `migrate` and `off` after the smoke test passes ([below](#maintenance-mode)). |
| `deploy-iis` | a release, after `verify` and `maintenance`, and `IIS_ENABLED` is `true` | Environment `production`, on a self-hosted Windows runner: `dotnet publish`, then `deploy-iis.ps1` ([`iis.md`](iis.md)), with `-Maintenance` when the `maintenance` job said so. Independent of the Docker path; it does not wait for staging. |

A green push to `main` goes to staging; a release (merging the release-please pull request) goes to staging and then, after approval, to production. The template repository itself publishes and deploys nothing: where `.template.config` exists the `image` job and everything after it are skipped. What each trigger does:

| Trigger | Image tags | Deploys |
|---|---|---|
| Push to `main` | `sha-<short>`, `edge` | `deploy-staging` with `DEPLOY_ENABLED`; production and IIS are skipped |
| Release call from `release.yml` (ref `main`, `release` true) | `sha-<short>`, `x.y.z`, `latest` | staging, then production (`DEPLOY_ENABLED`); IIS if `IIS_ENABLED`; the `maintenance` input is always `auto` |
| Dispatch, `release` unticked, from `main` | `sha-<short>`, `edge` | as a push to `main` |
| Dispatch, `release` unticked, from another branch or a tag (a dry run) | `sha-<short>` only, never `edge` | none: `ci`, `image` and `verify` run, no deploy job |
| Dispatch, `release` ticked, from a `v*` tag | `sha-<short>`, `x.y.z`, `latest` | as a release; `version` must equal the tag without the leading `v`; `maintenance` may be `auto`, `on` or `off` |
| Dispatch, `release` ticked, from `main` | `sha-<short>`, `x.y.z`, `latest` | as a release; `version` is `x.y.z` without a leading `v`; `maintenance` may be `auto`, `on` or `off` |
| Dispatch, `release` ticked, from any other ref | none: a step of the `image` job (after `ci`) fails with "A release runs only from main or a v* tag" | none |
| Any run whose `version` is not exactly `x.y.z` (a pre-release such as `1.2.0-rc.1`, a leading `v` or zero, a second line), `release` ticked or not | none: the `image` job fails at "Check the version" | none |

The `version` input is validated as a whole string and only the validated value reaches the tag list, so an input cannot add tags of its own. Pre-releases are not releasable through this workflow, so `latest` never points at one.

`cd.yml` can be run by hand (Actions, Run workflow) for a dry run or to repeat a release. **To repeat or recover a release** (cancelled or failed after the image, or a missed deploy), run it **from the tag `vX.Y.Z`** (the "Use workflow from" selector), with `release` ticked and `version` set to `X.Y.Z`, never from a branch: `main` may be ahead of the release, and a release from `main` would publish and deploy whatever `main` holds now under that version. Two checks the workflow does not make: a release dispatched from an **old** `v*` tag publishes `latest` and deploys that old tag to production (there is no newest-tag check), and a release dispatched from `main` is not compared with the version in `Version.props`. Use the tag selector and the version you mean.

**Concurrency.** One deploy per ref runs at a time and none is cancelled half-way. A push to `main` uses the group `deploy-<ref>`; GitHub keeps only the newest pending run of a group, which suits `edge`. A release uses its own group, `deploy-<ref>-release`, so a later push cannot replace it while it waits. The two groups can overlap: an `edge` and a release roll-out to staging can run at the same time, and the release commit is built twice (the push and the release call), so staging is rolled out twice for it and `sha-<short>` is re-pointed to the second digest. Your `roll-out.sh` must tolerate that.

`release.yml` has a concurrency group of its own, `Release`, with no cancel, and its `cd` job is inside it: while a release waits for the `production` approval it holds that group, so the release-please runs of later pushes to `main` queue behind it (and GitHub keeps only the newest pending one). Approve or reject a pending release promptly.

### Maintenance mode

A production release that adds a contract migration (a drop or a rename, marked `[ContractMigration]`, [`migrations.md`](migrations.md#expand-then-contract)) cannot run with the old version still serving: after the migration the previous code cannot work on the schema. Such a release runs in **maintenance mode**: a maintenance page is up before `migrate` and comes down only after the new version passed its health check. Decision and reasons: [ADR 0034](../adr/0034-maintenance-mode-for-contract-phase-releases.md). Staging and a push to `main` never use it; the `maintenance` job and every maintenance step run for a release only.

The `maintenance` job runs `build/scripts/detect-contract-migration.sh` and `build/scripts/resolve-maintenance.sh` after a checkout of the whole history and every tag. The `maintenance` input of `cd.yml` is `auto` (the default), `on` or `off`:

| Input | Result |
|---|---|
| `auto` (or empty) | Maintenance mode when detection says `true`. |
| `on` | Maintenance mode; a notice when detection found nothing. |
| `off` | No maintenance mode. When detection found a contract migration the job prints a `::warning` annotation and the release deploys anyway, with the old code running on a schema it cannot use. It is not refused: the override is an explicit dispatch input and the required reviewers of the `production` environment gate the run. |
| anything else | The job fails, so production and IIS do not run. A dispatch offers only the three values and `release.yml` sends none, so this is reachable only through another caller of `cd.yml`. |

**Detection** answers `true` when a migration file **added** since the previous release tag mentions the word `ContractMigration`. The previous release tag is the highest `v[0-9]*` tag without a pre-release suffix (no `-` in the name, so `v1.1.0-rc.1` is never the base) merged into `HEAD` that does not point at `HEAD` (the release commit has its own tag). The diff covers `**/Migrations/*.cs` and skips `*ModelSnapshot.cs`; a `*.Designer.cs` file is read like any other. The word is matched anywhere in the file, so a marker in a list of attributes (`[Obsolete, ContractMigration("x")]`) or written fully qualified counts, as it does for the architecture test that reads the attribute by reflection; a comment that merely mentions it counts too, which can turn maintenance mode on needlessly (the safe direction). With no previous tag (the first release) the answer is `false`. A shallow checkout makes the script exit 1 rather than answer `false`. A migration file that was only modified does not count, and a file that was moved or renamed counts as added, with the same safe direction. The rule relies on the release tags being `v*` and reachable from the commit released.

**The limit of that rule, and the manual rule.** The base is the previous release **tag**, not what production last ran. A contract release that never reached production (the Docker hook stub failing at `on` on the first one, a failed staging smoke test, a rejected `production` approval) still leaves its tag. The next release compares with that tag, answers `false`, and migrates with the old code serving and no page. **Until a release has completed its production deploy, every later release must be dispatched by hand with `maintenance: on`** (a `workflow_dispatch` of `cd.yml`, `release` ticked, the `version`, `maintenance` set to `on`); the automatic release path cannot do it. The proper fix, basing the comparison on the commit of the last successful `production` deployment, is recorded in [ADR 0034](../adr/0034-maintenance-mode-for-contract-phase-releases.md).

**The override is reachable only by hand.** `release.yml` does not forward the input, so the automatic release path always uses `auto`. `maintenance: on` or `off` needs a manual run of `cd.yml` (Actions, Run workflow) with `release` ticked and the `version` set, from `main` or a `v*` tag ([above](#cdyml)).

**What it does.** The orders are in [`iis.md`](iis.md#maintenance-mode) (IIS) and [`docker.md`](docker.md#the-maintenance-hook) (Docker). In short: the page goes up before the migration; if a later step fails the **page stays up, nothing is restored and the run fails**. The operator then restores the database from a backup if a contract migration was applied, restores or redeploys the previous version, and removes the page. On Docker the page answers every request except `/health/live` and `/health/ready`, which reach the API so that the smoke test, which runs before the page comes down, can pass.

**The page** is one self-contained file, `build/deploy/maintenance.html`: English, Malay and Simplified Chinese, chosen by the browser's language list with English as the fallback (all three show without JavaScript), `noindex`, a refresh every 30 seconds, no external asset. IIS serves it as `app_offline.htm` (status 503 from the ASP.NET Core Module; there is no `Retry-After` on IIS, because it is a file). **A project edits that one file for its brand and contact.**

### The deploy hook

`build/deploy/roll-out.sh <environment> <image@digest>` is a stub that prints what to replace and exits 1. A project that sets `DEPLOY_ENABLED` without writing its roll-out gets a red job rather than a deploy that did nothing. Replace the body with your own commands (the file shows an SSH and Compose example and a Kubernetes one), keep credentials in the environment's secrets passed through `env:` on the workflow step, and use the digest, not a tag, so what runs is what was verified.

`build/deploy/maintenance.sh <on|off> <environment> <page-path>` is a second stub of the same kind, called only in maintenance mode ([`docker.md`](docker.md#the-maintenance-hook)): it exits 1 until the project writes it, so a contract release without the hook fails before the database is touched. Its contract: while `on`, every request answers the page with status 503 except `/health/live` and `/health/ready`, which pass through to the API (the smoke test needs `/health/ready` before `off` runs). A release that is not in maintenance mode never runs it.

## What to configure

**Repository variables** (Settings, Secrets and variables, Actions, Variables). The ones read in a job's `if` must be repository or organisation variables; an environment's own variables are not visible there.

| Variable | Used by | Meaning |
|---|---|---|
| `DEPLOY_ENABLED` | `deploy-staging`, `deploy-production` | `true` turns the Docker deploy jobs on. Unset, they are skipped. |
| `IIS_ENABLED` | `deploy-iis` | `true` turns the IIS job on. |
| `IIS_SITE_PATH` | `deploy-iis` | The IIS site's physical path. |
| `IIS_APP_POOL` | `deploy-iis` | The site's application pool. |
| `IIS_HEALTH_URL` | `deploy-iis` | The readiness URL the script polls: the site's public https URL, `https://<public host>/health/ready`, with a certificate valid for that host. Not `http://localhost`: HTTPS redirection would send it to `https://localhost` ([`iis.md`](iis.md#health-url)). |
| `CODEQL_ENABLED` | `codeql` | `false` skips the CodeQL job (see below). Anything else, or unset, runs it. |
| `STAGING_URL` | `deploy-staging` | Base URL of staging (a trailing slash is dropped); the smoke test calls `<url>/health/ready`. A repository variable, or one on the `staging` environment. |
| `PRODUCTION_URL` | `deploy-production` | The same for production. |

That is eight variables. `STAGING_URL` and `PRODUCTION_URL` also set the environment's link in the run; read a deployment's own page, not that link, if it is empty.

**Secret.** `MIGRATION_CONNECTION_STRING`, a secret of **each environment** (`staging`, `production`): the connection string of the login that may change the schema ([`migrations.md`](migrations.md)). It reaches `migrate` as `ConnectionStrings__Database` for that one process, by name, never on a command line; for IIS the script reads it from the same variable. It must be an **environment** secret: `release.yml` calls `cd.yml` without `secrets: inherit` (least privilege), so a repository secret of that name would reach a push to `main` but not a release. `RELEASE_PLEASE_TOKEN` (see `release.yml`) is a separate, optional secret for the release pull request.

**Environments** (Settings, Environments). Create `staging` and `production`. Add required reviewers to `production` (and ideally to `staging`), so the approval hangs on the run: a job that names an environment waits for its protection rules. A private repository needs a paid GitHub plan for required reviewers. `deploy-production` and `deploy-iis` both use `production`, so a release that deploys both ways asks twice. Put `MIGRATION_CONNECTION_STRING` on each environment, so the production secret is visible only to a job that was approved.

**Server-side controls.** Set each environment's **Deployment branches and tags** to selected branches and tags: `main` and the tag pattern `v*`, nothing else. Protect `v*` tags with a **tag ruleset** (Settings, Rules, Rulesets: restrict creation, update and deletion of `v*` to the people who may release). The ref check in `cd.yml` guards against mistakes, **not against a malicious writer**: `workflow_dispatch` runs the `cd.yml` of the selected ref, so a writer can edit it on a branch and dispatch it, or push a `v9.9.9` tag on an unreviewed commit and dispatch a release from that tag. Only the environment rules, the reviewers and the tag ruleset, which live in the repository settings, stop that.

**OIDC.** Nothing here requests an `id-token`, and nothing here talks to a cloud. A project that deploys to Azure, AWS or Google Cloud should not store a long-lived cloud key as a secret: add `id-token: write` to its deploy job and use the provider's federated credential action, scoped to the environment, in `roll-out.sh`'s step. That is guidance; the template does not wire it.

**The runner must reach the database.** `migrate` runs on the job's runner: a GitHub-hosted runner for the Docker path, the self-hosted one for IIS. If the database accepts connections only from a private network, a hosted runner cannot migrate; use a self-hosted runner inside that network (change `runs-on` of the deploy jobs) or a VPN. This is a prerequisite, not something the template solves.

### GHCR

The image is pushed to `ghcr.io/<repository>/api` with the repository's `GITHUB_TOKEN` (`packages: write`). A new GHCR package is **private**. The image carries the `org.opencontainers.image.source` label that `docker/metadata-action` writes, which normally connects the package to the repository; confirm it under the package's page (Package settings, Connect repository) and set its visibility there (Change visibility) if consumers outside the repository need to pull it. A deploy host pulls with its own credentials (a token with `read:packages`) unless the package is public. If the first push is refused, give the repository access under the package's Manage Actions access. The package link and permissions were not exercised before release.

Provenance and the SBOM are BuildKit attestations pushed beside the image (`sbom: true`, `provenance: mode=max`), so no `id-token` or `attestations` permission is requested; `docker buildx imagetools inspect <image>` is the way to look at them. A release commit builds the image twice; the second build re-points `sha-<short>` to a new digest, so deploy by digest.

### CodeQL and gitleaks

**CodeQL** is free for public repositories and needs GitHub Advanced Security on a private one. It runs unless `CODEQL_ENABLED` is `false`; switching it off is a visible choice, and a skipped job still satisfies a required check named `codeql`. The first run on a repository may report that code scanning is not enabled: allow it under Settings, Advanced Security. Scheduled workflows run only on the default branch, and on a public repository GitHub disables them after 60 days without activity. A pull request with a merge conflict gets no `pull_request` run at all. Whether CodeQL can upload its results for a fork or a Dependabot pull request is unverified here.

**gitleaks** runs as the CLI, not the official Action, because the Action asks organisation accounts for a paid licence key ([ADR 0033](../adr/0033-delivery-pipeline.md)). The version (8.30.1) and the SHA-256 of the archive are pinned in `gitleaks.yml`; raise both by hand, with a release at least two weeks old, and check the licence again. A pull request scans its own commits (the checkout is full-depth), `main` and the weekly run scan the whole history, and `--redact` keeps a secret out of the log. `.gitleaks.toml` extends the default rules with four allowlist entries, each an exact file and an exact value for a dummy in a test; add one only for a demonstrated false positive. A pull request is scanned with its own `.gitleaks.toml`, so protect that file and `.github/` with a CODEOWNERS entry and required review.

### Branch protection

Add the checks to the required list once they have run at least once: `build-test`, `template-smoke`, `commit-lint`, `licenses`, `container`, `codeql` and `gitleaks` (`bash build/scripts/configure-github-repo.sh --checks build-test,template-smoke,commit-lint,licenses,container,codeql,gitleaks` in the template repository). `actionlint` and `scripts` also exist and may be added. In the `cd.yml` run the called jobs show as `ci / build-test`; the pull request check names do not change.

## Tools the pipeline pins

| Tool | Version | Where | Licence |
|---|---|---|---|
| gitleaks | 8.30.1 (and a SHA-256) | `gitleaks.yml` | MIT |
| actionlint | 1.7.12 (the image tag) | `ci.yml`, job `actionlint` | MIT |
| Pester | 5.9.1 | `ci.yml`, job `scripts` | MIT |
| PSScriptAnalyzer | 1.25.0 | `ci.yml`, job `scripts` | MIT |

Dependabot sees the actions and the Dockerfile's base images, not these four; raise them by hand. The build stage of the image uses `sdk:10.0`, a moving tag (`global.json` pins the SDK inside it), and NuGet Audit runs in the restore, so a newly disclosed advisory can fail the image build with no code change.
