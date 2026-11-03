# 0034. Maintenance mode for contract-phase releases: auto-detected, a page that stays up on failure, a project-owned Docker hook

- Status: Accepted
- Date: 2026-11-03
- Deciders: Lee Jia Le

## Context
ADR 0031 made every migration expand-only, so a migration can run while the old version still serves, and ADR 0033 built the delivery pipeline on that: `migrate` first, the swap second, a rollback of the files only. The one exception is a contract migration (`[ContractMigration]`, a drop or a rename): after it the previous release cannot run on the schema, so the old version must not be serving when it is applied, and nothing can be rolled back to it. Plan 6 gave IIS a one-line English `app_offline.htm` written by the script around the file swap only, and gave Docker no maintenance screen at all. The design of this change (`docs/superpowers/specs/2026-11-03-maintenance-screen-design.md` in the template repository, which a generated project does not carry) settled five points, M1 to M5; this ADR records the ones that are hard to reverse or that a project would otherwise have to rediscover. The working documents are [`docs/deployment/`](../deployment/README.md).

A production release should show users a proper page whenever the site is not serving the new version, and a contract release should take the site offline before it migrates without anyone having to remember to ask for it.

## Options considered

**The trigger (M3).**
1. **Manual input only.** A person ticks a box for a contract release. `release.yml` calls `cd.yml` without any input, so the automatic release path could never use it, and the one release that needs it is the one where the box is forgotten.
2. **Always on.** Every release becomes an outage, which contradicts ADR 0031: most releases need none.
3. **Auto-detect with an override.** An architecture test already refuses a drop or a rename without `[ContractMigration]`, so the marker is the signal. A `maintenance` input (`auto`, `on`, `off`) overrides the answer.

**The Docker path (M5).**
1. **IIS only.** Docker releases of a contract migration would take the site down with no page, or the project would write the whole thing itself with no contract to write it to.
2. **A shipped proxy or load-balancer configuration.** The template cannot know what sits in front of the API (ADR 0033 rejected a concrete target for the same reason).
3. **A hook the project owns,** `build/deploy/maintenance.sh <on|off> <environment> <page-path>`, a stub that fails on purpose like `roll-out.sh`, with a contract that says what `on` and `off` must do.

**The failure rule (M4).**
1. **Restore the previous files and take the page down,** as the normal order does. After a contract migration the previous code cannot run on the schema, so this would serve errors where the page was.
2. **Leave the page up, restore nothing, and fail the run.** Users keep seeing the page; the operator restores the database first and then decides.

## Decision
We chose the third option of the trigger, the project-owned hook, and the second option of the failure rule.

- **Two modes (M2).** *Normal* keeps the order of ADR 0033: preflight, migrate, back up, page up, swap, page down, health check; on a failed swap or check the previous files are restored. *Maintenance*: preflight, back up, page up, migrate, swap, page down, health check. In maintenance mode any failure after the page is up (the migration, the swap or the health check) leaves the page up, does not restore the files and does not remove the page, and the run fails. A failure in the preflight or the backup happens before the page is up and leaves the site as it was.
- **The failure rule (M4).** The failure message of the IIS script says that if a contract migration was applied the database is to be restored from a backup first, and then `{SitePath}.previous` restored over the site and `app_offline.htm` removed. On Docker a failure after `on` leaves `on` in place, and the operator runs `off` once the site is healthy.
- **Detection (M3).** `build/scripts/detect-contract-migration.sh` prints `true` when a migration file *added* since the previous release tag carries `[ContractMigration` at `HEAD`. The previous release tag is the highest `v[0-9]*` tag merged into `HEAD` that does not point at `HEAD`, because the release commit carries its own tag. Files named `*.Designer.cs` and `*ModelSnapshot.cs` are skipped. With no previous tag (the first release) the answer is `false` and the log says there is nothing to contract from. `build/scripts/resolve-maintenance.sh` turns the `maintenance` input (`auto`, the default, `on` or `off`) and that answer into the final `true` or `false`; any other input value is an error. The `maintenance` job of `cd.yml` runs both, with a full-history checkout and every tag, because a shallow clone would answer `false`, the unsafe direction.
- **A forced `off` only warns.** `off` on a release where detection found a contract migration prints a `::warning` annotation and deploys without the page, with the old code running on a schema it cannot use. It is not refused: the override is an explicit dispatch input, and the `production` environment's required reviewers already gate the run. `on` with nothing detected prints a notice.
- **The override is reachable only by hand.** `release.yml` does not forward the input, so the automatic release path always uses `auto`. `maintenance: on` or `off` needs a manual `workflow_dispatch` of `cd.yml` with `release` ticked and the version, from `main` or a `v*` tag.
- **The Docker hook (M5).** In maintenance mode `deploy-production` runs `bash build/deploy/maintenance.sh on production build/deploy/maintenance.html` before `migrate` and `off` after the smoke test passes; neither step has `always()`. A hook that is missing or still the stub fails the job before the database is touched. A normal Docker release does not run the hook.
- **IIS.** `deploy-iis.ps1` takes `-Maintenance` (and `-MaintenancePage`, which defaults to `build/deploy/maintenance.html`); `deploy-iis` passes the `maintenance` job's answer. The page is copied byte for byte as `app_offline.htm`, so it never passes through a re-encoding.
- **The page (M1).** One shared, self-contained `build/deploy/maintenance.html` serves both paths: inline CSS, no external asset, English, Malay and Simplified Chinese in the one file, the language chosen from the browser's language list with English as the fallback, all three shown without JavaScript, `noindex`, a refresh every 30 seconds. IIS serves it as `app_offline.htm`, which the ASP.NET Core Module answers with status 503. A project edits that one file for its brand and contact.

## Consequences
- **Never run on a real server or proxy.** The Pester tests mock IIS and the file calls (the maintenance order, the failure rule and the page copy are tested that way), and the script tests for detection and resolution run against throwaway git repositories. No real IIS server, no real proxy and no real release ran this. The manual checklist in [`docs/deployment/iis.md`](../deployment/iis.md#manual-checklist-for-a-real-server) gains maintenance-mode steps, and the first contract release of a project is the first real test of the Docker hook.
- **No `Retry-After` on IIS.** `app_offline.htm` is a file; the module serves it with 503 and the file cannot add headers, so the page refreshes itself. A project's Docker hook can send the header.
- **The base-tag rule depends on `v*` tags reachable from `main`.** A squash or a rebase that drops a tag, a tag that is not `v*`, or a checkout without tags changes the base, and so the answer. The `maintenance` job fetches the whole history and the tags for this reason.
- **A moved or renamed migration file counts as added** (renames are off in the diff), so it carries its `[ContractMigration` marker into the answer and turns maintenance mode on. That is the safe direction: a needless page, never a missing one.
- **A forced `off` can deploy a contract release without the page.** Recorded above: a warning, no refusal.
- **The runner is believed to read `::warning` and `::notice` annotations written to stderr;** this was not verified on a runner. If it does not, the warning is still in the log.
- **The `maintenance` job runs beside `ci` and does not gate staging.** `deploy-production` and `deploy-iis` need it, so a value it rejects fails the job and blocks production and IIS. That is unreachable in practice: `release.yml` sends nothing, so the input is `auto`, and a dispatch offers a list of three.
- **A failure in maintenance mode is not recovered by the pipeline.** The page stays up until a person restores the database, restores the files (IIS) or redeploys the previous image (Docker), and removes the page. That is the intent of M4, and it adds an on-call step to a failed contract release.
- **IIS in maintenance mode, a failed health check, and the page cannot be put back.** The page comes down for the health check, so a failed check puts it back, best effort. If that fails too, the site is left serving the unhealthy build with the page removed, and the log line says so (`The maintenance page could not be put back`). The final error line of the script still says that the maintenance page is still up. That wording is a known defect, to be fixed; it is not the intended behaviour.
- **Staging has no page,** and a manual on and off outside a release (a restore, an incident) is not built.
- Follow-up actions: the wording defect above; the Docker hook is a project's to write; a dedicated contract-phase runbook beyond the recovery steps in [`docs/deployment/migrations.md`](../deployment/migrations.md#expand-then-contract) is still open (ADR 0033, "Not built, and still open"). Plan 7 (packaging) ships `build/deploy/maintenance.*` with the template.
