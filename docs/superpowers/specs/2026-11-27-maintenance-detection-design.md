# Maintenance-screen detection from the production deployment history — design

- Status: design approved section by section by the user (2026-11-27); written spec waiting for the user's review.
- Date: 2026-11-27
- Roadmap row: the last open 1.0 gate (decided 2026-11-20): the maintenance-screen detection base named as "Follow-up actions" in ADR 0034.
- Builds on: ADR 0034 (maintenance mode; its detection paragraph and its first consequence are the part this changes), ADR 0033 (delivery pipeline; both deploy jobs declare `environment: production`), ADR 0031 (expand-only migrations, `[ContractMigration]`), `docs/superpowers/specs/2026-11-03-maintenance-screen-design.md` (the original design, sections 5 and 6).
- Scope: how the `maintenance` job of `cd.yml` picks the base commit it compares with. Nothing else about maintenance mode changes (the page, the hooks, the failure rule, the `maintenance` input).

## 1. Intent

**Outcome.** A contract release that never reached production cannot hide its migration from the next release. Detection compares the release with the commit that production last ran, not with the previous release tag, so the rule "until a release has completed its production deploy, dispatch every later release by hand with `maintenance: on`" is no longer needed.

**Success.**

- When the last release failed before or during its production deploy (the Docker hook stub failing at `on`, a rejected `production` approval, a failed swap), the next release still answers `true` for a contract migration that release carried.
- When production has no successful deployment on record at all, detection answers `false` and says why in the log.
- When the lookup cannot answer, the `maintenance` job fails and blocks production and IIS; it never answers `false` by default.
- `maintenance: on` works without the lookup, so an API outage never blocks a release that wants the page anyway.
- The rule in ADR 0034's third consequence and the interim wording in `docs/deployment/README.md` are removed.

## 2. Non-goals

- **A second source of truth.** The template does not record the deployed commit itself (a repository variable or ref); GitHub's deployment records already hold it. Rejected for the write permission and the drift risk.
- **Folding the API call into the detection script.** Rejected: it mixes network and git in one file and is harder to test.
- **Changing the override.** `auto`, `on` and `off`, the forced-`off` warning and the page contract stay as ADR 0034 records them.
- **Staging.** It has no maintenance page (ADR 0034) and is not read.
- **A converter or a manual-deploy tracker.** A project that deployed to production by hand before adopting the pipeline needs `maintenance: on` once (section 6).

## 3. Design

### 3.1 The base is the commit production last ran

A new `build/scripts/resolve-deploy-base.sh` prints the SHA of the last deployment to the `production` environment that ever had a `success` status, or nothing when there is none.

- **Source.** `gh api` against the repository's Deployments API: `GET /repos/{repo}/deployments?environment=production&per_page=100`, newest first, then `GET /repos/{repo}/deployments/{id}/statuses` for each deployment until one has a `success` status. The repository comes from `GITHUB_REPOSITORY` (an optional first argument overrides it, for the tests).
- **Why "ever had a success".** GitHub marks an earlier successful deployment `inactive` when a later one succeeds, so the latest status of an older deployment is not `success`. A deployment counts when any status in its history is `success`.
- **What does not count.** A deployment that failed, errored, never left `waiting` (a rejected approval) or is still `in_progress` is skipped, and so is a deployment of another environment. This is what closes the gap: a release that never completed leaves no `success`.
- **The deployed commit** is the deployment's `sha`, not its `ref` (a release deploys from a `v*` tag; the SHA is the commit behind it).
- **Output.** The SHA on stdout and nothing else; everything else goes to stderr, where the runner reads `::notice` and `::warning`. With no successful deployment: nothing on stdout, exit 0, and stderr says there is nothing deployed to contract from.
- **Failure (exit 1, a message that names the cause).** `gh` is not installed; the API call fails (including a missing `deployments: read`); the response cannot be read as the expected shape; the SHA is not a commit in the checkout; the SHA is not an ancestor of `HEAD` (a deployment from another branch, such as a hotfix); the newest 200 deployments have been read without a success and more remain (the script gives up instead of walking an unbounded history; a history that ends before 200 with no success is the "nothing on record" answer above, not a failure). Each message says that `maintenance: on` bypasses the lookup.

### 3.2 Detection no longer picks a tag

`detect-contract-migration.sh` keeps its diff and marker logic unchanged: the files added since the base, `*ModelSnapshot.cs` skipped, the word `ContractMigration` matched anywhere at `HEAD`, a shallow checkout refused.

- A base ref still comes in as the optional argument (that is how the workflow hands over the SHA from 3.1).
- **Without a base argument it answers `false`** and says on stderr that no production deployment is on record. The code that chooses the highest stable `v*` tag is removed, and with it the dependency on tags being reachable from `main` (ADR 0034, fourth consequence). Full history is still fetched for the diff; fetching tags is no longer needed.

### 3.3 The `maintenance` job

- The job gains `permissions: deployments: read` beside `contents: read`, and passes `GH_TOKEN` (the workflow token) to the lookup step.
- **Mode `on`** skips the lookup and detection: the answer is `true` whatever was detected. The one thing lost is the notice "forced on although no contract migration was detected".
- **Modes `auto` and `off`** run the lookup, then detection with its output as the base, then `resolve-maintenance.sh` as today. A failed lookup fails the step, and `deploy-production` and `deploy-iis`, which need this job, do not run.
- `resolve-maintenance.sh` is unchanged.

### 3.4 Order of events and re-runs

The `maintenance` job runs before either deploy job, so the current run's own production deployment record does not exist yet when it looks. Re-running a failed release finds the same base (a failed attempt leaves no `success`) and answers the same. Re-running a release whose deployment already succeeded finds `HEAD` as the base, adds no migration files and answers `false`, which is right: nothing new would migrate.

## 4. Tests

A new `build/scripts/tests/resolve-deploy-base.test.sh` puts a fake `gh` first on `PATH` that serves canned JSON from fixture files, and uses throwaway git repositories (the style of `maintenance-scripts.test.sh`). Cases:

- the newest deployment succeeded: its SHA is printed;
- the newest failed and an older one succeeded: the older SHA;
- a success followed by a later `inactive` status: it still counts;
- a rejected or `waiting` deployment before an older success: skipped;
- another environment's deployment: ignored;
- no deployment, and deployments with no success: nothing on stdout, exit 0;
- more than 100 deployments: the second page is read;
- no success in the newest 200 deployments and more behind them: exit 1;
- exactly 200 or fewer deployments in total and no success: nothing on stdout, exit 0;
- the SHA is not an ancestor of `HEAD`, or not in the checkout: exit 1;
- `gh` missing, `gh` exiting non-zero, unreadable output: exit 1 with the cause named.

`maintenance-scripts.test.sh` changes for the no-base case (`false` and the stderr line) and drops the tag-selection cases. `bash build/scripts/project-index.sh` is run for the new files, and the `cd.yml` edit passes `actionlint` in CI.

## 5. Documentation

- **ADR 0034** is amended in place, the way ADRs 0017, 0020, 0022, 0031 and 0033 were: the detection paragraph describes the deployment-based base and the "no deployment means false" and "fail on error" rules; the third consequence (the previous-tag base and the interim rule) is replaced by the new behaviour; the fourth (tags reachable from `main`) is removed; the first follow-up action is marked done.
- **`docs/deployment/README.md`** ("Maintenance mode"): the detection description and the removal of the interim rule; the break-glass line (`maintenance: on` bypasses the lookup).
- **`docs/deployment/migrations.md`** and **`docs/releasing.md`**: wherever they quote the interim rule or the tag base.
- **`docs/blueprint-review.md`** and the project index (`docs/superpowers/index/`): regenerated or updated by the index script.

## 6. Known limits

- **Not run against a real deployment history.** The tests use a fake `gh`. The first production release of a project is the first real read of the Deployments API, as ADR 0034 says of the page and the hook.
- **A first pipeline release after a manual deploy answers `false`.** A project that deployed to production by hand before adopting the pipeline has no `success` on record. Its first pipeline release that carries a contract migration needs `maintenance: on` once. The runbook text says so.
- **The base is "what the pipeline last deployed", not "what is running".** A rollback by redeploying an older image outside the pipeline, or a hand-edit of the site, is invisible to the lookup.
- **A hotfix from another branch** fails the ancestor check on purpose; the operator dispatches with `maintenance: on` or `off` after deciding by hand.
- **Rate limits and permissions** are the runner token's; a repository that forbids `deployments: read` for the workflow token fails the job with the message in 3.1.

## 7. Decisions recorded

- **Failure on a lookup error** (the user, 2026-11-27): the run fails; a silent fallback to the tag rule would reopen the gap.
- **No successful deployment** (the user, 2026-11-27): the answer is `false`; nothing is serving, so there is nothing to protect. ADR 0034 had planned a fallback to the tag rule; that fallback is dropped.
- **A separate script** (the user, 2026-11-27, approach A): rejected the lookup inside the detection script (B) and a self-recorded commit (C).
