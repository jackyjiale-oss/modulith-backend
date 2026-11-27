# Maintenance detection from the production deployment history — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The `maintenance` job of `cd.yml` compares a release with the commit that production last ran (read from the GitHub Deployments API), so a contract release that never reached production cannot hide from the next release.

**Architecture:** A new script, `build/scripts/resolve-deploy-base.sh`, prints the SHA of the last `production` deployment that ever had a `success` status. `detect-contract-migration.sh` keeps its git logic, takes that SHA as its base ref, and no longer chooses a tag. The workflow runs the two in order, and mode `on` skips both.

**Tech Stack:** Bash, `gh api` (its built-in `--jq`, so no `jq` install is needed), git, GitHub Actions, markdown docs.

**Spec:** `docs/superpowers/specs/2026-11-27-maintenance-detection-design.md`

## Global Constraints

- The environment is `production`; the API call filters on it. Statuses count when **any** status in a deployment's history is `success` (later `inactive` does not undo it).
- A page is `per_page=100`; at most 200 deployments are examined; the script exits 1 only when 200 were examined without a success **and** more remain.
- No successful deployment on record: stdout empty, exit 0, stderr says nothing is deployed to contract from. Detection without a base answers `false` and says so on stderr.
- Every lookup failure exits 1 with the cause named and the words `maintenance: on` (the bypass): `gh` missing, API error (including a missing `deployments: read`), unreadable output, SHA not a commit in the checkout, SHA not an ancestor of `HEAD`, `GITHUB_REPOSITORY` unset with no argument.
- stdout carries only the SHA (resolve script) or `true`/`false` (detect script); everything else is stderr.
- Mode `on` skips the lookup and detection. The job gains `deployments: read` beside `contents: read`. `resolve-maintenance.sh` does not change.
- The shallow-checkout refusal stays; tags are no longer needed, so `fetch-tags` goes and `fetch-depth: 0` stays.
- Conventional Commits with a scope from `CONTRIBUTING.md` (use `ci` or `adr`); end each commit message with the attribution line the session supplies. No secrets in files.

## Review Focus

- A success **older** than a later `inactive`, failed or `waiting` deployment must still be found (Task 1 tests `case_success_then_inactive_counts`, `case_failed_newer_than_success`).
- A deployment SHA that a force-push removed from history must fail loudly, not answer `false` (Task 1 `case_sha_not_in_checkout`).
- The page boundary: exactly 100 and exactly 200 deployments behave as specified (Task 1 `case_second_page_read`, `case_exactly_200_no_success`, `case_over_200_no_success`).
- The request must really filter on `environment=production` and ask for `per_page=100` (Task 1 `case_request_shape`).
- `GITHUB_REPOSITORY` unset with no argument must not call the API with an empty repository (Task 1 `case_repository_missing`).

---

### Task 1: `resolve-deploy-base.sh` and its test

**Files:**
- Create: `build/scripts/resolve-deploy-base.sh`
- Create: `build/scripts/tests/resolve-deploy-base.test.sh`
- Modify: `.github/workflows/ci.yml` (the `scripts` job, after the `Maintenance scripts` step near line 243)

**Interfaces:**
- Produces: `bash build/scripts/resolve-deploy-base.sh [repository]` run inside the git work tree. `repository` (`owner/name`) defaults to `$GITHUB_REPOSITORY`. Stdout: one 40-hex SHA, or nothing. Exit 0 or 1 as in Global Constraints. Task 3 consumes it.
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the failing test file.** Model it on `build/scripts/tests/maintenance-scripts.test.sh` (`must`, `make_repo`, `commit_file`, `assert_eq`, `assert_contains`, `run_script`, `run_case`; copy only the helpers it needs). It puts a fake `gh` first on `PATH`: a script in a temp `bin/` that appends its arguments to `$FAKE_GH_LOG` and serves **pre-rendered** output because the real script uses `--jq` (the fake ignores `--jq`). Protocol: `gh api repos/R/deployments?environment=production&per_page=100&page=N --jq ...` prints the lines of `$FAKE_GH_DIR/deployments.N` (each `<id> <sha>`, newest first; an empty or missing file prints nothing); `gh api repos/R/deployments/<id>/statuses?per_page=100 --jq ...` prints the lines of `$FAKE_GH_DIR/statuses.<id>` (each a state, newest first); `FAKE_GH_FAIL=1` makes it exit 1 printing an HTTP 403 message; `FAKE_GH_GARBAGE=1` makes it print `not json`. Each case makes a repository with commits `c1..c3` on `main` (HEAD is `c3`), writes the fixtures it needs with the real SHAs from `git rev-parse`, and calls `run_script "$resolve" owner/repo`. Cases and assertions:
  - `case_newest_success`: deployment 3 (`c2`) has `success`: stdout is `c2`'s SHA, exit 0.
  - `case_failed_newer_than_success`: deployment 3 (`c3`) `failure`, deployment 2 (`c2`) `success`: stdout `c2`.
  - `case_success_then_inactive_counts`: deployment 2 statuses `inactive` then `success`: stdout is its SHA.
  - `case_waiting_skipped`: deployment 3 `waiting` only, 2 `success`: stdout deployment 2's SHA.
  - `case_none_on_record`: no deployments: stdout empty, exit 0, stderr contains `nothing deployed`.
  - `case_no_success_in_short_history`: three deployments, none `success`: stdout empty, exit 0.
  - `case_second_page_read`: page 1 holds 100 deployments with no success, page 2 holds one with `success`: stdout that SHA, and the log shows `page=2`.
  - `case_exactly_200_no_success`: pages 1 and 2 hold 100 each, page 3 empty, no success: stdout empty, exit 0.
  - `case_over_200_no_success`: pages 1 and 2 full with no success, page 3 non-empty: exit 1, stderr contains `maintenance: on`.
  - `case_sha_not_in_checkout`: the successful deployment's SHA is `0000…` (40 zeros): exit 1, stderr contains `not a commit` and `maintenance: on`.
  - `case_sha_not_ancestor`: a side branch commit not merged into `main` is the deployed SHA: exit 1, stderr contains `not an ancestor`.
  - `case_gh_missing`: `PATH` without any `gh`: exit 1, stderr contains `gh` and `maintenance: on`.
  - `case_gh_fails`: `FAKE_GH_FAIL=1`: exit 1, stderr names the failed API call and mentions `deployments: read`.
  - `case_unreadable_output`: `FAKE_GH_GARBAGE=1`: exit 1, stderr contains `unexpected`.
  - `case_repository_missing`: no argument and `GITHUB_REPOSITORY` unset: exit 1, and `$FAKE_GH_LOG` is empty.
  - `case_request_shape`: after a normal run the log contains `environment=production` and `per_page=100`.
  The file ends with the same `for c in …; do run_case` loop and `passed`/failure summary as `maintenance-scripts.test.sh`.
- [ ] **Step 2: Run it and see it fail.** Run: `bash build/scripts/tests/resolve-deploy-base.test.sh`. Expected: FAIL lines (the script does not exist), exit 1.
- [ ] **Step 3: Implement `resolve-deploy-base.sh`.** `set -euo pipefail`; a `fail()` that prefixes `resolve-deploy-base:` and appends the sentence "Dispatch cd.yml with maintenance set to on to bypass this lookup."; check `command -v gh`; resolve the repository from `$1` or `$GITHUB_REPOSITORY`. Read deployments page by page with `gh api "repos/$repo/deployments?environment=production&per_page=100&page=$page" --jq '.[] | "\(.id) \(.sha)"'`; validate each line against `^[0-9]+ [0-9a-f]{40}$` (`unexpected output` on a mismatch). For each deployment read `gh api "repos/$repo/deployments/$id/statuses?per_page=100" --jq '.[].state'` and stop at the first one that prints a line equal to `success`. Examine pages 1 and 2 fully; read page 3 only to see whether it is non-empty. On a found SHA check `git cat-file -e "$sha^{commit}"` (`not a commit in this checkout`) and `git merge-base --is-ancestor "$sha" HEAD` (`not an ancestor of HEAD`), then print it. A `gh` failure names the call and mentions `deployments: read`.
- [ ] **Step 4: Run the test and see it pass.** Run: `bash build/scripts/tests/resolve-deploy-base.test.sh`. Expected: every case `ok`, final line `resolve-deploy-base: passed`, exit 0.
- [ ] **Step 5: Check the jq expressions against the real API (read-only).** Run: `gh api "repos/jackyjiale-oss/modulith-backend/deployments?environment=production&per_page=100" --jq '.[] | "\(.id) \(.sha)"'` and, for one id it prints (if any), `gh api "repos/jackyjiale-oss/modulith-backend/deployments/<id>/statuses?per_page=100" --jq '.[].state'`. Expected: either no output (no production deployment yet, which proves only that the call and filter are accepted) or lines of the shapes the script validates. Record what it printed in the commit message body.
- [ ] **Step 6: Wire it into CI.** In `.github/workflows/ci.yml` add, directly after the `Maintenance scripts` step, a step named `Deployment base script` that runs `bash build/scripts/tests/resolve-deploy-base.test.sh`, with a one-line comment like the neighbour's.
- [ ] **Step 7: Commit.** `git add build/scripts/resolve-deploy-base.sh build/scripts/tests/resolve-deploy-base.test.sh .github/workflows/ci.yml` then `git commit -m "feat(ci): find the last production deployment for maintenance detection"`.

---

### Task 2: `detect-contract-migration.sh` stops choosing a tag

**Files:**
- Modify: `build/scripts/detect-contract-migration.sh` (the header comment, the tag-selection block at lines 29-49, the shallow-clone message at line 25)
- Modify: `build/scripts/tests/maintenance-scripts.test.sh`

**Interfaces:**
- Consumes: Task 1's SHA is passed as the optional base-ref argument (existing behaviour).
- Produces: `bash build/scripts/detect-contract-migration.sh [base-ref]`: with no argument it prints `false` and stderr says `no production deployment on record`; with an argument it behaves as today.

- [ ] **Step 1: Change the tests first.** In `maintenance-scripts.test.sh`: replace `case_no_previous_tag` with `case_no_base` (no argument: stdout `false`, exit 0, stderr contains `no production deployment on record`); delete `case_head_carries_its_own_tag`, `case_unmerged_tag_ignored`, `case_prerelease_tag_never_the_base` and `case_tag_with_no_change`; in the remaining detection cases (`case_added_expand_migration`, `case_added_contract_migration`, `case_modified_not_added`, `case_designer_file_counts`, `case_combined_attribute_list_counts`, `case_fully_qualified_attribute_counts`, `case_model_snapshot_ignored`) pass the tagged commit as the explicit base (the `tag` helper can stay; call `run_script "$detect" "$(git rev-parse v1.0.0)"`) and drop the `comparing with release tag` assertions; in `case_shallow_clone_refused` drop the `fetch-tags: true` assertion and keep `fetch-depth: 0`; update the case list at the bottom and the header comment. Keep `case_explicit_base_ref`, `case_unresolvable_base_ref` and the three `resolve` cases as they are.
- [ ] **Step 2: Run and see the new case fail.** Run: `bash build/scripts/tests/maintenance-scripts.test.sh`. Expected: FAIL on `case_no_base` (the script still picks a tag), everything else ok.
- [ ] **Step 3: Edit the script.** Remove the `tags=` loop and the "no previous release tag" branch; when `$# -eq 0` print to stderr `detect-contract-migration: no production deployment on record; there is nothing to contract from` and `false`, exit 0. Change the shallow message to name only `fetch-depth: 0` (the whole history). Rewrite the header comment (lines 9-14) for the new rule: the base is the argument, which `resolve-deploy-base.sh` supplies; without one the answer is `false`.
- [ ] **Step 4: Run and see it pass.** Run: `bash build/scripts/tests/maintenance-scripts.test.sh`. Expected: all `ok`, final line `maintenance-scripts: passed`.
- [ ] **Step 5: Commit.** `git add build/scripts/detect-contract-migration.sh build/scripts/tests/maintenance-scripts.test.sh` then `git commit -m "refactor(ci): take the detection base from the caller, not from a tag"`.

---

### Task 3: wire the `maintenance` job in `cd.yml`

**Files:**
- Modify: `.github/workflows/cd.yml` (the comment above the job at lines 85-88, the job at lines 89-114)

**Interfaces:**
- Consumes: Task 1's script (prints a SHA or nothing, exit 1 on a lookup failure) and Task 2's `detect-contract-migration.sh [base-ref]`.
- Produces: the job's `enabled` output, unchanged in name and meaning.

- [ ] **Step 1: Edit the job.** `permissions:` gains `deployments: read`. The checkout keeps `fetch-depth: 0`, drops `fetch-tags: true`. The `Resolve the maintenance mode` step gets `env:` `GH_TOKEN: ${{ github.token }}` beside `MODE`, and the script body becomes: if `MODE` is `on`, skip the lookup (`detected=true`, print that the lookup was skipped); otherwise `base="$(bash build/scripts/resolve-deploy-base.sh)"` (a failure fails the step) and `detected="$(bash build/scripts/detect-contract-migration.sh ${base:+"$base"})"`; then `resolve-maintenance.sh "$MODE" "$detected"` and the two `echo` lines as today (the log line adds the base, or `none`). Update the comment above the job to say the comparison is with the last successful production deployment and that `on` bypasses the lookup.
- [ ] **Step 2: Check the workflow.** Run: `bash build/scripts/tests/static-rules.test.sh` and read its output for a `cd.yml` rule failure; run `git diff --check`. `actionlint` is not installed locally; CI runs it on the pull request. Expected: static rules pass, no whitespace errors.
- [ ] **Step 3: Commit.** `git add .github/workflows/cd.yml` then `git commit -m "feat(ci): base maintenance detection on the last production deployment"`.

---

### Task 4: documentation, project index and final checks

**Files:**
- Modify: `docs/adr/0034-maintenance-mode-for-contract-phase-releases.md`, `docs/deployment/README.md` (lines 75-77), `docs/runbooks/contract-phase.md` (step 6, lines 63-66), `docs/blueprint-review.md` (row 6b, line 130), `docs/releasing.md` (line 65), `build/deploy/maintenance.sh` (comment lines 21-25), `docs/deployment/migrations.md` and `docs/deployment/docker.md` if they quote the tag base or the interim rule (check with `grep -n "previous release tag\|until a release has completed" docs -r`)
- Regenerate: the project index under `docs/superpowers/index/`

**Interfaces:**
- Consumes: the final behaviour from Tasks 1-3.
- Produces: no code; docs that match the spec's section 5.

- [ ] **Step 1: Amend ADR 0034 in place.** The `Detection (M3)` decision paragraph describes the base as the commit of the last successful `production` deployment (any `success` in its status history), `resolve-deploy-base.sh`, the answers `false` when none is on record and exit 1 on a lookup error, and that `on` skips the lookup; replace the third consequence (previous tag and the manual rule) with the new behaviour and its limits (manual deploys before the pipeline, redeploys outside the pipeline, hotfix branches fail the ancestor check on purpose); delete the fourth consequence (tags reachable from `main`); mark the first follow-up action done. Update the title only if it no longer fits.
- [ ] **Step 2: Update the working docs.** In `docs/deployment/README.md` rewrite the **Detection** and "limit of that rule" paragraphs for the new base, the failure behaviour and the `maintenance: on` bypass, and remove "The rule relies on the release tags being `v*`…". In `contract-phase.md` step 6 replace the tag wording and the last bullet with the deployment-based rule and the one-time `maintenance: on` for a first pipeline release after a manual deploy. In `blueprint-review.md` row 6b replace the last-but-two sentence ("Detection compares with the previous release tag… maintenance: on") with the new base, and in `releasing.md` line 65 remove the maintenance-detection item from the open decisions (the gate is now built). Replace the comment block in `build/deploy/maintenance.sh` lines 21-25 with one sentence saying detection compares with the last successful production deployment.
- [ ] **Step 3: Regenerate the index.** Run `git add -A build/scripts docs` (new files must be tracked) then `bash build/scripts/project-index.sh`, then re-run it and confirm a clean `git diff` on the second run (the `project-index` CI check fails otherwise). Expected: the new script and test appear in the index.
- [ ] **Step 4: Run the template smoke.** Run: `bash build/scripts/template-smoke.sh` then `--notifications false`, `--includeSample false`, `--caching memory` (the files that ship changed). Expected: each ends with its success line. If Docker is needed and still broken locally, say which switch could not run; CI covers it.
- [ ] **Step 5: Run every script test and a final grep.** Run: `bash build/scripts/tests/maintenance-scripts.test.sh`, `bash build/scripts/tests/resolve-deploy-base.test.sh`, and `grep -rn "previous release tag\|until a release has completed\|fetch-tags: true" docs build .github` (expected: no hit in the maintenance docs or the `maintenance` job; hits about publishing release tags in `cd.yml` descriptions are unrelated).
- [ ] **Step 6: Commit.** `git add -A docs build .github` then `git commit -m "docs(adr): record the deployment-based maintenance detection"`. Do not push: opening the pull request is the user's decision.
