# Maintenance screen for production releases — design

- Status: Draft for review (2026-11-03). The design was approved in the design session (coverage, the Docker hook contract, the page, the trigger, then the flow and its failure rule); this file is the written form.
- Date: 2026-11-03
- Roadmap row: a follow-up to row 6, "Delivery pipeline", in `docs/blueprint-review.md` Section 6. It ships before Plan 7 (packaging), which then packages the finished result.
- Builds on: Plan 6 (`cd.yml`, `build/scripts/deploy-iis.ps1`, `build/deploy/roll-out.sh`; main `a0e0c4c`), ADR 0031 (expand-only database changes, `[ContractMigration]`), ADR 0033 (the delivery pipeline).

## 1. Intent

**Outcome.** A production release shows users a proper maintenance page whenever the site is not serving the new version, and a release whose database change the old code cannot survive (a contract-phase migration) takes the site offline *before* it migrates, without anyone having to remember to ask for it.

**Today.**

- IIS writes a one-line English `app_offline.htm` (hard-coded in the script) around the file swap only. `migrate` runs before it, while the old version still serves.
- The Docker path has no maintenance screen. `roll-out.sh` is a stub the project writes.
- Every migration is expand-only, so the old code runs on the new schema. The one exception is a `[ContractMigration]` (a drop or rename), after which the previous release cannot run (`docs/deployment/migrations.md`, "Expand, then contract"). Nothing in the pipeline treats such a release differently.

**Success looks like.**

1. The IIS swap gap of every release shows `build/deploy/maintenance.html`, not a hard-coded line.
2. A production release that adds a `[ContractMigration]` since the previous release tag runs in **maintenance mode** on its own, on the automatic `release.yml` path too: the page goes up before `migrate` and comes down only after the health check passes.
3. A dispatch input forces maintenance mode on or off.
4. On Docker the template defines the contract (one project-owned hook) and fails before touching the database if the project has not written it.
5. Nothing changes a route, a response, a table or a column (ADR 0031).

## 2. Non-goals

- Staging. The screen is for production only.
- A manual on/off workflow outside a release (a restore, an incident). Not asked for; a later row if wanted.
- A reverse proxy or load balancer shipped by the template. The Docker hook is the project's (ADR 0033, "Not built, by design", still holds).
- A `Retry-After` header on IIS: `app_offline.htm` is a file the ASP.NET Core Module serves with 503 and the file cannot add headers, so the page refreshes itself instead.
- Changing the normal (non-maintenance) release order on IIS or Docker.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| M1 | One shared self-contained page, `build/deploy/maintenance.html`: inline CSS, no external asset, English, Malay and Chinese (Simplified) in the one file, chosen from `navigator.languages` with English as the fallback and all three shown without JavaScript; `noindex`; light and dark; meta refresh every 30 seconds. | A static file cannot use the API's `.resx`; one file keeps a project's branding to one edit. |
| M2 | Two modes. **Normal**: the order of Plan 6, with the shared page as `app_offline.htm`. **Maintenance**: preflight, backup, page up, migrate, swap, online, health check. | Only a contract migration needs the page before `migrate`; making every release an outage contradicts ADR 0031. |
| M3 | Auto-detect with an override. `build/scripts/detect-contract-migration.sh <base-ref>` prints `true` when a migration file added since the previous `v*` tag (without a pre-release suffix) mentions the word `ContractMigration`. A `maintenance` input, `auto` (default), `on` or `off`, overrides it. | An architecture test already forces the marker on every drop and rename, so the marker is the signal, and `release.yml` (which cannot pass a flag) is covered. |
| M4 | **Failure in maintenance mode: the page stays up and the run fails.** No file restore, no page removal. | After a contract migration the previous code cannot run on the schema; restoring it would serve errors. The operator restores the database first (`docs/deployment/migrations.md`). |
| M5 | Docker: `build/deploy/maintenance.sh <on\|off> <environment> <page-path>`, a failing stub like `roll-out.sh`. `on` runs before `migrate`, `off` after the smoke test passes. While `on`, the hook answers the page for every request except `/health/live` and `/health/ready`, which it passes through to the API. | The template cannot know the project's proxy. A missing hook is a red job before any database change. The smoke test calls `/health/ready` before `off`; a hook that answered the page for the health paths too would fail it, `off` would never run and the page would stay up although the new version is healthy (the step order of `cd.yml` is kept, the contract is what changed). |

## 4. Pieces

| Piece | Role |
|---|---|
| `build/deploy/maintenance.html` | The page (M1). |
| `build/deploy/maintenance.sh` | The Docker hook stub (M5), with nginx, Caddy and load-balancer examples in its header. |
| `build/scripts/detect-contract-migration.sh` | Detection (M3). Reads git history, so the job that runs it checks out with `fetch-depth: 0` and `fetch-tags: true`. |
| `build/scripts/resolve-maintenance.sh` | Resolves the `maintenance` input and the detection answer to the final `true` or `false` (section 5), so the override and its warning are testable. |
| `.github/workflows/cd.yml` | The `maintenance` input on `workflow_dispatch` and `workflow_call`; a `maintenance` job (needs `detect`, production only, skipped for the template) that resolves `auto\|on\|off` to `true` or `false`; the extra steps in `deploy-production` and a `-Maintenance` flag in `deploy-iis`. `release.yml` is unchanged. |
| `build/scripts/deploy-iis.ps1` | `-MaintenancePage <path>` (the page to copy as `app_offline.htm`; defaults to the file beside `build/deploy`) and `-Maintenance` (M2, M4). `Set-SiteOffline` copies the page instead of writing a string. `app_offline.htm` is already among the server-owned files, so the swap's `/MIR` never deletes it mid-run. |

## 5. Flow

**IIS, normal.** Preflight → migrate (old version serves) → backup → page up → swap → page down → health check; on a failed swap or check, restore the previous files (unchanged from Plan 6).

**IIS, maintenance.** Preflight → backup → page up → migrate → swap → page down → health check. Any failure after the page is up (migrate, swap or health check) leaves the page up and exits 1 with a message that names the step and says to restore the database before restoring `{SitePath}.previous` (M4). A failure in preflight or the backup happens before the page is up and leaves the site as it was.

**Docker, maintenance.** `maintenance.sh on production <page>` → migrate → `roll-out.sh` → smoke test → `maintenance.sh off production <page>`. While `on` the hook passes `/health/live` and `/health/ready` through to the API, so the smoke test, which runs before `off`, reaches the new version. A failure after `on` leaves it on (M4). **Docker, normal:** unchanged.

**Resolving the mode.** `on` → true. `off` → false, plus a warning annotation when detection found a contract migration. `auto` → the script's answer. With no previous `v*` tag (the first release) detection answers `false` and says why in the log: there is nothing to contract from.

## 6. Detection

`detect-contract-migration.sh [base-ref]`: lists the files added (`--diff-filter=A`, renames off) between `<base-ref>` and `HEAD` under any `**/Migrations/*.cs`, skipping `*ModelSnapshot.cs`, and prints `true` when any contains the word `ContractMigration`, else `false`. The word is matched anywhere in the file (a `*.Designer.cs` partial, a list of attributes such as `[Obsolete, ContractMigration("x")]` and a fully qualified name all count), because the architecture test reads the attribute by reflection and accepts all of them; a comment that mentions the word is a false positive, the safe direction. Exit 0 either way; exit 1 only on a git error, a base ref that does not resolve or a shallow checkout (a shallow clone has no tags and would answer `false` silently). Without an argument the script derives the base ref itself, so the rule is tested: the highest `v*` tag without a pre-release suffix (no `-` in the name: `v1.1.0-rc.1` sorts above `v1.1.0`, and the pipeline never deploys a pre-release to production) reachable from `HEAD` that does not point at `HEAD` (the release commit carries its own tag).

`resolve-maintenance.sh <mode> <detected>`: `mode` is `auto` (or empty), `on` or `off`; `detected` is the answer above. It prints `true` or `false` and exits 1 on any other mode. A forced `off` with `detected` true prints a `::warning` annotation to stderr, where the runner reads it; `on` with nothing detected prints a notice.

## 7. Documents and housekeeping

- New ADR 0034, "Maintenance mode for contract-phase releases" (M2 to M5).
- `docs/deployment/README.md` (the `maintenance` input, the job, the trigger table), `iis.md` (the two flows and the failure rule), `docker.md` and `migrations.md` (the hook contract; a contract release goes out in maintenance mode), `production-checklist.md` (a line: edit `maintenance.html`), ADR 0033 ("Not built" gains a pointer).
- `docs/blueprint-review.md` Section 6: a row for this follow-up, marked done when merged; Plan 7 notes that packaging ships `build/deploy/maintenance.*`.
- `BACKEND_TEMPLATE_BLUEPRINT.md` is not edited; the review wins.

## 8. Tests

- **Pester** (`build/scripts/tests/deploy-iis.Tests.ps1`): maintenance order (migrate after the page is up); the page stays up and the run exits 1 on a failed migrate, swap and health check in maintenance mode; normal mode unchanged; the page file is copied and removed; a missing page file fails before any change.
- **Script tests** for `detect-contract-migration.sh` against a throwaway git repository (no tag, a tag with no change, an added expand migration, an added `[ContractMigration]`, a modified file, a Designer file that carries the attribute, a combined attribute list, a fully qualified attribute, a ModelSnapshot file that mentions it, a tag on `HEAD`, a pre-release tag that is never the base, a shallow clone) and for `resolve-maintenance.sh`. Run by the `scripts` CI job.
- **`template-smoke.sh`**: `maintenance.html` and `maintenance.sh` ship; `maintenance.sh` fails by design in the template; the page has the three languages and no external reference.
- **actionlint** covers the workflow change. The `container` and `build-test` jobs are unaffected.

## 9. Risks and points the plan must settle

- **Never run on a real server.** Like Plan 6, the Pester tests mock IIS; the manual checklist in `docs/deployment/iis.md` gains the maintenance-mode steps.
- **A forced `off` on a contract release** deploys with the old code running on a schema it cannot use. Decided: the warning annotation is the only extra guard, because the override is an explicit dispatch input and the `production` environment's required reviewers already gate the run. ADR 0034 records it.
- **The base-ref rule** assumes release tags are `v*` and reachable from `main`; a squash or rebase that drops a tag changes the diff. The plan covers a release run from a `v*` tag.
- **The base is the previous release tag, not what production last ran.** A contract release that never reached production (the Docker stub failing at `on` on the first one, a failed staging smoke test, a rejected approval) leaves its tag; the next release compares with it, answers `false`, and migrates with the old code serving and no page. Decided: the mechanism is not changed in this design, the limit and a manual rule are documented (ADR 0034, `docs/deployment/README.md`, `docker.md`, `iis.md` and the stub's message): until a release has completed its production deploy, every later release is dispatched by hand with `maintenance: on`. The proper fix, recorded as a follow-up in ADR 0034: base the comparison on the commit of the last successful `production` deployment, read through the Deployments API (`deployments: read`), falling back to the tag rule.
- **Language choice in the page** is by the browser's language list, not by the API's `Accept-Language` handling; a visitor with an unlisted language sees English.
- **Concurrency.** `cd.yml` allows one deploy per ref and never cancels one half-way, and a release has its own group. Two releases cannot therefore interleave their `on` and `off` calls; the plan must still confirm that a main-push run (edge, staging only) can never reach the maintenance steps.
