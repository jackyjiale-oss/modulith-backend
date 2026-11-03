# Maintenance screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A production release shows a shared three-language maintenance page whenever the site is not serving the new version, and a release that adds a `[ContractMigration]` goes into maintenance mode (page up before `migrate`) on its own.

**Architecture:** Two small bash scripts decide the mode (`detect-contract-migration.sh`, `resolve-maintenance.sh`); `cd.yml` gets a `maintenance` input and job whose `enabled` output drives extra steps in `deploy-production` and a `-Maintenance` switch of `deploy-iis.ps1`; one static page, `build/deploy/maintenance.html`, is what IIS serves as `app_offline.htm` and what the Docker hook `build/deploy/maintenance.sh` receives by path.

**Tech Stack:** bash and git (scripts and their tests), PowerShell 5.1/7 with Pester 5.9.1 (IIS script), GitHub Actions (`cd.yml`, `ci.yml`), static HTML.

**Spec:** `docs/superpowers/specs/2026-11-03-maintenance-screen-design.md` (read it first; decisions M1 to M5 are referenced below).

**Before you start:** PR #21 (spec and this plan) should be merged; branch `feature/maintenance-screen` from `main` in a short-path worktree (`C:\w\...`). The Docker on this machine is broken and `pwsh`/`actionlint`/`shellcheck` are not installed locally: CI's `scripts`, `actionlint`, `container` and `template-smoke` jobs are the authority for what cannot run here. Local Pester needs 5.9.1: `powershell -NoProfile -Command "Install-Module Pester -RequiredVersion 5.9.1 -Scope CurrentUser -Force -SkipPublisherCheck"` (the machine has 3.4.0; the tests are Pester 5).

## Global Constraints

- `TemplateName` is the placeholder; never hard-code a concrete project name (root `CLAUDE.md`).
- Files under `build/` ship in generated projects; `build/template-content/` replaces root counterparts. Run `bash build/scripts/template-smoke.sh` after changing anything that ships.
- No secrets in files; no `DateTime.Now`; `--no-verify` never; Conventional Commits (`feat:`, `docs:`, `test:`, `ci:`).
- The page: `build/deploy/maintenance.html`, inline CSS, no external asset, languages `en`, `ms`, `zh-Hans` in one file, English fallback, all three visible without JavaScript, `noindex`, light and dark, meta refresh every 30 seconds (M1).
- Inputs and outputs: `maintenance` is `auto` (default), `on` or `off`; `detect-contract-migration.sh [base-ref]` prints `true` or `false` and exits 0 (1 only on a git error or an unresolvable base ref); `resolve-maintenance.sh <mode> <detected>` prints `true` or `false` and exits 1 on any other mode.
- Detection reads migration files **added** (`--diff-filter=A`, `--no-renames`) under `*/Migrations/*.cs`, skipping `*.Designer.cs` and `*ModelSnapshot.cs`, containing `[ContractMigration`. Base ref without an argument: the highest `v[0-9]*` tag merged into `HEAD` whose commit is not `HEAD`; none means `false`.
- Hook contract: `build/deploy/maintenance.sh <on|off> <environment> <page-path>`; a failing stub in the template. `on` runs before `migrate`, `off` after the smoke test passes (M5).
- IIS maintenance order: preflight, backup, page up, migrate, swap, page down, health check. A failure after the page is up leaves the page up, never restores files, never removes the page, and exits 1 (M2, M4). Normal order is unchanged: preflight, migrate, backup, offline, publish, online, health.
- Production only; staging and `release.yml` are unchanged. A main-push run (`inputs.release` false) never reaches the maintenance job or steps.
- Nothing changes a route, a response, a table or a column (ADR 0031).

## Review Focus

- First release, no `v*` tag: detection answers `false` (Task 1, `no_previous_tag`), not an error.
- The release commit carries its own tag: the base must be the tag before it, or a contract migration in the release is missed (Task 1, `head_carries_its_own_tag`).
- A migration file that was only modified, or a Designer or snapshot file that merely mentions the attribute, must not trigger maintenance mode (Task 1, `modified_not_added`, `designer_and_snapshot_ignored`).
- Maintenance mode, health check fails after the page came down: the page must go back up and the old files must not be restored (Task 3, `maintenance health failure`).
- A `maintenance` value other than `auto|on|off` through `workflow_call` (a free string there) must fail the job loudly, not silently mean `auto` (Task 1, `resolve_rejects_unknown_mode`).

---

### Task 1: Detection and mode-resolution scripts

**Files:**
- Create: `build/scripts/detect-contract-migration.sh`
- Create: `build/scripts/resolve-maintenance.sh`
- Create: `build/scripts/tests/maintenance-scripts.test.sh`
- Modify: `.github/workflows/ci.yml` (job `scripts`: one step after `Pester`)

**Interfaces:**
- Produces: `bash build/scripts/detect-contract-migration.sh [base-ref]` and `bash build/scripts/resolve-maintenance.sh <mode> <detected>` exactly as in Global Constraints. Both write only the answer to stdout and everything else to stderr; both are run from inside a git work tree (`detect`) with `set -euo pipefail`.

- [ ] **Step 1: Write the failing test** `maintenance-scripts.test.sh`. A bash harness with `make_repo` (a temp dir with `git init -b main`, a local `user.name`/`user.email`, `trap` cleanup), `commit_file <path> <content> <message>`, `tag <name>` and `assert_eq <expected> <actual> <name>`; it counts failures and exits 1 if any. Cases, each in its own fresh repo, running the script with `cd` into the repo and capturing stdout and stderr separately:
  - `no_previous_tag`: one commit, no tag → stdout `false`, stderr contains `no previous release tag`.
  - `tag_with_no_change`: tag `v1.0.0`, then add `README.md` → `false`.
  - `added_expand_migration`: tag `v1.0.0`, add `src/Modules/A/Infrastructure/Persistence/Migrations/20261101000000_AddX.cs` without the attribute → `false`.
  - `added_contract_migration`: same path, content `[ContractMigration("drop old column")]` → `true`.
  - `modified_not_added`: the contract migration committed *before* `v1.0.0`, edited after → `false`.
  - `designer_and_snapshot_ignored`: added `..._AddX.Designer.cs` and `ADbContextModelSnapshot.cs` both containing `[ContractMigration` → `false`.
  - `head_carries_its_own_tag`: tag `v1.0.0`, add the contract migration, tag `v1.1.0` on `HEAD`, no argument → `true`.
  - `unmerged_tag_ignored`: a `v9.9.9` tag on a side branch that is not merged into `HEAD`; the contract migration added after `v1.0.0` on `main` → `true` (the tag `v9.9.9` is not the base).
  - `explicit_base_ref`: pass `v1.0.0` as the argument with a contract migration added since → `true`; pass `HEAD` → `false`.
  - `unresolvable_base_ref`: argument `nope` → exit code 1.
  - `resolve_auto`: `resolve ""` and `auto` with `true` → `true`, with `false` → `false`; `on false` → `true`; `off false` → `false`.
  - `resolve_off_warns`: `off true` → stdout `false`, stderr contains `::warning`.
  - `resolve_rejects_unknown_mode`: `maybe true` → exit code 1, stdout empty, stderr names `auto`, `on` and `off`.
- [ ] **Step 2: Run it to verify it fails**

  Run: `bash build/scripts/tests/maintenance-scripts.test.sh`
  Expected: FAIL lines for every case (the scripts do not exist), exit 1.
- [ ] **Step 3: Implement `detect-contract-migration.sh`** per Global Constraints. Derive the base with `git tag --merged HEAD --list 'v[0-9]*' --sort=-v:refname`, skipping a tag whose `git rev-list -n1` equals `git rev-parse HEAD`; list files with `git diff --no-renames --name-only --diff-filter=A "$base"..HEAD -- '*/Migrations/*.cs'`; read each file's content at `HEAD` with `git show "HEAD:$file"`. Resolve an explicit base with `git rev-parse --verify --quiet "$1^{commit}"` and exit 1 with a message on failure.
- [ ] **Step 4: Implement `resolve-maintenance.sh`**: `case` on the mode; messages as in Global Constraints; the warning is `::warning title=Maintenance mode::` followed by a sentence that says the release deploys with the old code on a contract schema, on stderr.
- [ ] **Step 5: Wire CI.** In `ci.yml` job `scripts`, add the step `Maintenance scripts` running `bash build/scripts/tests/maintenance-scripts.test.sh`, after the `Pester` step.
- [ ] **Step 6: Run the tests to verify they pass**

  Run: `bash build/scripts/tests/maintenance-scripts.test.sh`
  Expected: every case `ok`, exit 0.
- [ ] **Step 7: Commit**

```bash
git add build/scripts .github/workflows/ci.yml
git commit -m "feat: add contract-migration detection and maintenance mode resolution"
```

### Task 2: The page, the Docker hook stub and the smoke checks

**Files:**
- Create: `build/deploy/maintenance.html`
- Create: `build/deploy/maintenance.sh`
- Modify: `build/scripts/template-smoke.sh` (the `required` list near line 60; checks after the Dockerfile check)

**Interfaces:**
- Produces: the page file (consumed by Tasks 3 and 4 by path) and the hook contract `maintenance.sh <on|off> <environment> <page-path>`.

- [ ] **Step 1: Write the failing smoke checks.** Add `build/deploy/maintenance.html` and `build/deploy/maintenance.sh` to the `required` list, then after the Dockerfile check add checks on `$out/build/deploy/maintenance.html`: it contains `lang="en"`, `lang="ms"` and `lang="zh-Hans"`; contains `http-equiv="refresh"` and `noindex`; contains no `http://`, `https://`, `src=` or `<link`; and `bash "$out/build/deploy/maintenance.sh" on production "$out/build/deploy/maintenance.html"` exits non-zero (the stub fails by design) with its message on stderr. Each failure is `fail "..."`, in the style of the neighbouring checks.
- [ ] **Step 2: Run to verify it fails**

  Run: `bash build/scripts/template-smoke.sh`
  Expected: `FAIL: build/deploy/maintenance.html is missing` and the same for `maintenance.sh`; the run ends non-zero.
- [ ] **Step 3: Write `maintenance.html`.** Wording is fixed: English heading `Down for maintenance`, body `We are updating the service and will be back shortly. This page refreshes by itself.`; Malay heading `Dalam penyelenggaraan`, body `Kami sedang mengemas kini perkhidmatan dan akan kembali sebentar lagi. Halaman ini dimuat semula secara automatik.`; Chinese heading `系统维护中`, body `我们正在更新服务，很快就会恢复。本页面会自动刷新。` One `<section lang="...">` per language; a small inline script hides the sections that do not match the first of `navigator.languages` starting `ms` or `zh` (English when none), and every section is visible when the script does not run. `<meta charset="utf-8">`, viewport, `<meta name="robots" content="noindex">`, `<meta http-equiv="refresh" content="30">`, `color-scheme` light dark in the inline CSS. Saved as UTF-8 without BOM.
- [ ] **Step 4: Write `maintenance.sh`** in the style of `build/deploy/roll-out.sh` (shebang, `set -euo pipefail`, header comment). The header states the contract: `on` makes every request to the site answer the page with status 503 until `off`, is idempotent, and must exit non-zero on failure; `off` removes it; `<page-path>` is the file to serve; it runs before `migrate`, and `off` runs only after the smoke test passed. Two commented examples (nginx and Caddy over SSH, a load balancer's maintenance rule). The body prints that the project has not written its maintenance hook, names `<on|off>` and `<environment>`, says to replace the file or run the release with `maintenance: off` only if no contract migration is in it, and exits 1.
- [ ] **Step 5: Run to verify it passes**

  Run: `bash build/scripts/template-smoke.sh`
  Expected: no `FAIL:` lines; the script exits 0 (it also builds and tests the generated project, which takes several minutes).
- [ ] **Step 6: Commit**

```bash
git add build/deploy build/scripts/template-smoke.sh
git commit -m "feat: add the maintenance page and the Docker maintenance hook"
```

### Task 3: `deploy-iis.ps1` serves the page and runs maintenance mode

**Files:**
- Modify: `build/scripts/deploy-iis.ps1` (parameters, `Test-IisPreflight`, `Set-SiteOffline`, `Restore-Site`, `Invoke-Deployment`, the header comment)
- Test: `build/scripts/tests/deploy-iis.Tests.ps1`

**Interfaces:**
- Consumes: `build/deploy/maintenance.html` from Task 2.
- Produces: script parameters `[string] $MaintenancePage` and `[switch] $Maintenance` (both optional, so dot-sourcing still never prompts); `Test-IisPreflight -PublishPath -AppPool [-MaintenancePage]`; `Set-SiteOffline -SitePath -MaintenancePage`; `Restore-Site -SitePath -MaintenancePage`; `Invoke-Deployment -PublishPath -SitePath -AppPool -HealthUrl [-MaintenancePage] [-Maintenance]`. When `MaintenancePage` is empty `Invoke-Deployment` uses `Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy/maintenance.html'`.

- [ ] **Step 1: Write the failing tests** in `deploy-iis.Tests.ps1`:
  - In `Describe 'deploy-iis.ps1 as a script'`: the parameters include `MaintenancePage` and `Maintenance` (a switch), and none is mandatory.
  - `Set-SiteOffline and Set-SiteOnline`: update the existing tests to pass `-MaintenancePage` (a file made with `Add-ContentFile` holding non-ASCII text) and add `copies the page byte for byte as app_offline.htm` (compare `[IO.File]::ReadAllBytes` of both).
  - `Test-IisPreflight`: `fails before any change when the maintenance page does not exist` (throws `*maintenance page*`), and passes when it exists; existing calls without `-MaintenancePage` still pass.
  - `Restore-Site`: passes the page to `Set-SiteOffline` (`Should -Invoke Set-SiteOffline -ParameterFilter { $MaintenancePage -eq $page }`).
  - `Invoke-Deployment`: update the existing mocks to accept the new parameter, and add, each asserting the `$script:Calls` order string and that `Restore-Site` is not called where stated:
    - `maintenance order`: `preflight,backup,offline,migrate,publish,online,health` with `-Maintenance`.
    - `maintenance migrate failure`: throws `*failed at migrate*` and `*restore the database*`; calls end `preflight,backup,offline,migrate`; no `Restore-Site`, no `Set-SiteOnline`.
    - `maintenance swap failure`: same message with `*failed at swap*`; calls end `...,migrate,publish`.
    - `maintenance health failure`: `Wait-Healthy` returns `$false`; throws `*failed at health check*`; calls end `...,online,health,offline` (the page goes back up); no `Restore-Site`.
    - `maintenance failure before the page is up`: a throwing `Test-IisPreflight`, and separately a throwing `Backup-Site`, leave no `offline` call.
    - `maintenance success leaves LASTEXITCODE 0`.
    - `normal mode passes the page`: `Should -Invoke Set-SiteOffline -ParameterFilter { $MaintenancePage -eq $page }`.
- [ ] **Step 2: Run to verify they fail**

  Run: `powershell -NoProfile -Command "Import-Module Pester -RequiredVersion 5.9.1; Invoke-Pester -Path build/scripts/tests/deploy-iis.Tests.ps1 -CI"`
  Expected: the new tests and the updated ones FAIL (parameters not defined).
- [ ] **Step 3: Implement** the parameters and signatures above. `Set-SiteOffline` creates the folder when missing and uses `Copy-Item -LiteralPath $MaintenancePage -Destination (Join-Path $SitePath 'app_offline.htm') -Force`; keep the `SuppressMessageAttribute` it carries. `Test-IisPreflight` checks the page last. In `Invoke-Deployment`, the `-Maintenance` branch runs `Backup-Site`, `Set-SiteOffline`, then inside one `try`: `Invoke-Migration`, `Publish-Site`, `Set-SiteOnline`, `Wait-Healthy` (throw when false), tracking the current step name (`migrate`, `swap`, `health check`); its `catch` puts the page back with `Set-SiteOffline` (best effort), logs, and throws `The deployment failed at {step} in maintenance mode: {reason} The maintenance page is still up. If a contract migration was applied, restore the database from a backup first; then restore '{previous}' over '{site}' and remove app_offline.htm.` The normal branch keeps today's behaviour with the page passed through. Update the header `.DESCRIPTION` steps and `.PARAMETER` entries.
- [ ] **Step 4: Run to verify they pass**

  Run: the command of Step 2.
  Expected: all tests pass, including the unchanged real-robocopy test (it already asserts `app_offline.htm` survives the mirror).
- [ ] **Step 5: Commit**

```bash
git add build/scripts/deploy-iis.ps1 build/scripts/tests/deploy-iis.Tests.ps1
git commit -m "feat: serve the maintenance page and add maintenance mode to the IIS deploy"
```

### Task 4: `cd.yml` wiring

**Files:**
- Modify: `.github/workflows/cd.yml`

**Interfaces:**
- Consumes: the two scripts (Task 1), `build/deploy/maintenance.sh` and `maintenance.html` (Task 2), `-Maintenance` (Task 3).
- Produces: input `maintenance` on `workflow_call` (`type: string`, default `auto`) and `workflow_dispatch` (`type: choice`, options `auto`, `on`, `off`, default `auto`); job `maintenance` with output `enabled` (`'true'` or `'false'`).

- [ ] **Step 1: Add the inputs** with a one-line description each, and extend the header comment (the "deploy jobs" paragraph) by one sentence naming the maintenance mode and its ADR 0034.
- [ ] **Step 2: Add job `maintenance`**: `needs: detect`, `if: inputs.release && needs.detect.outputs.is_template != 'true'`, `ubuntu-latest`, `timeout-minutes: 5`, `permissions: contents: read`, output `enabled: ${{ steps.resolve.outputs.enabled }}`. Steps: `actions/checkout@v7` with `fetch-depth: 0`, `fetch-tags: true`, `persist-credentials: false`; then a step `Resolve the maintenance mode` (`id: resolve`) with `MODE: ${{ inputs.maintenance }}` in `env`, that runs `detected="$(bash build/scripts/detect-contract-migration.sh)"`, then `enabled="$(bash build/scripts/resolve-maintenance.sh "$MODE" "$detected")"`, writes `enabled=$enabled` to `$GITHUB_OUTPUT` and echoes one line saying the mode and why.
- [ ] **Step 3: Wire `deploy-production`**: add `maintenance` to `needs`; before `Migrate the database` a step `Maintenance page on` (`if: needs.maintenance.outputs.enabled == 'true'`, `run: bash build/deploy/maintenance.sh on production build/deploy/maintenance.html`); after `Smoke test` a step `Maintenance page off` with the same `if` and `off`. The `off` step has no `always()`, so a failure leaves the page up (M4).
- [ ] **Step 4: Wire `deploy-iis`**: `needs: [verify, maintenance]`; add `MAINTENANCE: ${{ needs.maintenance.outputs.enabled }}` to the step's `env`; call the script with `-Maintenance:($env:MAINTENANCE -eq 'true')`.
- [ ] **Step 5: Verify.** Run: `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/cd.yml'))"` (expect no output), then `awk '/deploy-production:/,/deploy-iis:/' .github/workflows/cd.yml | grep -nE "maintenance.sh (on|off)|docker run"` (expect `on` on an earlier line than the first `docker run`, `off` last). Then confirm by reading that `maintenance` and every maintenance step sit under `inputs.release`, so a main-push run cannot reach them. `actionlint` runs in CI only (the `actionlint` job); `release.yml` needs no change because the input defaults to `auto`.
- [ ] **Step 6: Commit**

```bash
git add .github/workflows/cd.yml
git commit -m "ci: run production releases in maintenance mode when a contract migration ships"
```

### Task 5: Documents, ADR 0034 and the roadmap

**Files:**
- Create: `docs/adr/0034-maintenance-mode-for-contract-phase-releases.md`
- Modify: `docs/adr/0033-delivery-pipeline.md` ("Not built, and still open": one pointer sentence), `docs/README.md` (ADR table row 0034), `docs/architecture/overview.md` (ADR list row), `docs/deployment/README.md`, `docs/deployment/iis.md`, `docs/deployment/docker.md`, `docs/deployment/migrations.md`, `docs/deployment/production-checklist.md`, `docs/blueprint-review.md` (Section 6)

**Interfaces:**
- Consumes: the behaviour built in Tasks 1 to 4; every statement below must match the code.

- [ ] **Step 1: Write ADR 0034** with the layout of ADR 0033 (`# 0034. ...`, Status Accepted, Date 2026-11-03, Deciders Lee Jia Le; sections `Context`, `Options considered`, `Decision`, `Consequences`). Options: the trigger (auto-detect with override, manual input only, always on); the Docker path (hook contract, IIS only, a shipped proxy); the failure rule. Decision: M1 to M5 and that a forced `off` on a contract release only warns, because the override is an explicit dispatch input and the `production` environment's reviewers gate the run. Consequences: never run on a real server; the base-ref rule depends on `v*` tags reachable from `main`; a moved migration file counts as added and turns maintenance mode on (safe direction); no `Retry-After` on IIS.
- [ ] **Step 2: Update the deployment pages.** `README.md`: the `maintenance` input, the `maintenance` job in the jobs list and trigger table, and a line telling a project to edit `build/deploy/maintenance.html`. `iis.md`: the two flows, the failure rule and its recovery steps, and the manual checklist gains maintenance-mode steps (page up before migrate, page stays after a forced failure, page down after health). `docker.md` and `migrations.md`: the hook contract, and that a contract release goes out in maintenance mode (the sentence in "Expand, then contract" about a contract migration links to it). `production-checklist.md`: one line, "`build/deploy/maintenance.html` carries your brand and contact; `maintenance.sh` is written".
- [ ] **Step 2b: Update the indexes.** ADR row in `docs/README.md` and in `docs/architecture/overview.md`, in the format of the 0033 rows; the 0033 pointer sentence.
- [ ] **Step 3: Update `docs/blueprint-review.md` Section 6**: a row `6b. Maintenance screen` after row 6 (what it adds, spec and plan paths, ADR 0034, "never run on a real server"), and in the Plan 7 row a note that packaging ships `build/deploy/maintenance.*`. Do not edit `BACKEND_TEMPLATE_BLUEPRINT.md`.
- [ ] **Step 4: Verify the documents.**

  Run: `dotnet test --project tests/TemplateName.ArchitectureTests -c Release`
  Expected: all pass (the tests that police the docs index and ADR listing included).
- [ ] **Step 5: Commit**

```bash
git add docs
git commit -m "docs: record the maintenance mode decision and its runbooks"
```

### Task 6: Whole-branch verification and the pull request

**Files:** none new.

- [ ] **Step 1: Run everything that runs locally.** `dotnet build -c Release`; `dotnet test --project tests/TemplateName.UnitTests -c Release` and `...ArchitectureTests`; `bash build/scripts/tests/maintenance-scripts.test.sh`; the Pester command of Task 3; `bash build/scripts/template-smoke.sh`. Expected: all green (unit and architecture counts are unchanged: 2706 and 131 on `main` at `1adab33`).
- [ ] **Step 2: Push the branch and open the pull request** only after the owner approves; title `feat: add a maintenance screen to production releases`; body lists what could not run locally (actionlint, PSScriptAnalyzer, the `container` job, any real IIS server, any real proxy). Then bind it with `bind_pr` and read its checks with `get_status`; do not poll.
- [ ] **Step 3: Report** what CI could not prove and the unscheduled items still open (Plan 7, the production-hardening row).
