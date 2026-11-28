# Version 1.0 Graduation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish `Modulith.Backend.Templates` 1.0.0 from a release PR that release-please proposes itself, after the native review, the delivery proof and the owner's settings, then leave the repository in its post-1.0 shape.

**Architecture:** Seven tasks in the spec's five streams. Task 1 (the graduation PR) merges first; Tasks 2 to 5 run in parallel and feed one 1.0.0 release PR; Task 6 is the cut; Task 7 is the post-release cleanup. Most tasks are configuration, documentation and GitHub operations, so their "test" is a script check or an observed workflow run.

**Tech Stack:** release-please 17.6.0 (via `googleapis/release-please-action@v5`), Bash test scripts under `build/scripts/tests/`, .NET 10 resx and Scriban templates, GitHub Actions, `gh`, the `anthropic-skills:xlsx` skill for the sheets.

**Spec:** `docs/superpowers/specs/2026-11-28-v1-graduation-design.md` (read it first; section numbers below refer to it).

## Global Constraints

- Work in a short-path worktree (`C:\w\<name>`), never on `main` of the user's checkout; branch names `ci/v1-graduation`, `fix/i18n-native-review`, `ci/drop-rc-keys`.
- Every merge, repository creation, repository or package deletion, and release merge happens only on the user's word in that turn; the user approves the `nuget` environment.
- Commit titles match `^(feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert)(\((<allowed-scope>)\))?!?: [^A-Z\s].*[^.\s]$`; scopes used here: `release`, `i18n`, `ci`, `docker`, `adr`.
- No visible commit (`feat`, `fix`, `perf`, `revert`) reaches `main` before Task 1 is merged.
- The 1.0.0 release PR is not merged before Tasks 2, 4 and 5 are done.
- Never print `src/Host/TemplateName.Api/appsettings.Development.json` (real SMTP credentials).
- After any change that ships: `bash build/scripts/template-smoke.sh`, then `--notifications false`, `--includeSample false`, `--caching memory`; run `bash build/scripts/project-index.sh` after `git add` of new files and commit its output.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies end with the Claude Code line.

## Review Focus

1. Removing the rc keys before `v1.0.0` exists would make release-please propose `1.0.1-rc.11`: Task 7 checks the manifest says `1.0.0` before touching the config.
2. A translation edit that drops or changes a placeholder (`{0}`, `{{ name }}`) breaks rendering at runtime: Task 4 relies on `Translations_use_the_same_placeholders` for resx and the Notifications template unit tests for Scriban.
3. HTML email bodies drifting from their approved text bodies: Task 4 has an explicit comparison step per edited type.
4. A release PR opened at `1.0.0-rc.12` because a fix landed early: Task 1 step 1 confirms no commits since `v1.0.0-rc.11`; spec §9 row 2 is the recovery.
5. A `nuget` approval that does not register: Task 6 confirms the deployment left `waiting`.

---

### Task 1: The graduation PR

**Files:**
- Modify: `release-please-config.json` (`"prerelease": true` → `false`)
- Modify: `build/scripts/tests/release-package.test.sh:314-326` (comment + one assertion)
- Create: `docs/adr/0043-version-1-0-and-what-the-package-promises.md`
- Modify: `docs/adr/0037-publishing-the-template-package.md:42` (its last sentence)
- Modify: `docs/releasing.md` Section 4 (lines 61-71) and one sentence of Section 3 (line 47)
- Modify: `docs/blueprint-review.md:133` (7c row: the v1.0 sentence)
- Modify: `docs/README.md` (ADR index, a row after 0042 at line 122)
- Modify: `docs/deployment/README.md:66` ("after a checkout of the whole history and every tag")
- Add: the spec and this plan

**Interfaces:**
- Produces: on `main`, a root config with `"prerelease": false`; ADR number 0043 (Tasks 6 and 7 link to it).

- [ ] **Step 1: Confirm the start state**

Run: `git fetch origin && git log --oneline v1.0.0-rc.11..origin/main && gh pr list --repo jackyjiale-oss/modulith-backend --state open`
Expected: no commits; no release PR. If a visible commit is there, stop and follow spec §9 row 2.

- [ ] **Step 2: Create the worktree** `git worktree add C:/w/v1g -b ci/v1-graduation origin/main`; copy the spec and this plan in from the user's checkout.

- [ ] **Step 3: Write the failing assertion** in `release-package.test.sh`, inside the `else` branch after the key loop:

```bash
  grep -qE '^[[:space:]]*"prerelease": false,?$' "$root_config" ||
    fail "release-please-config.json must set \"prerelease\": false until v1.0.0 exists (ADR 0043)"
```

Replace the comment above `root_config=` so it says: the three keys stay until `v1.0.0` exists; `prerelease` is `false` so release-please graduates `1.0.0-rc.N` to `1.0.0` (ADR 0043); they go in the post-release PR, which also removes this assertion and the key list. (This assertion goes beyond spec §5.2's "only the comment changes": it is the test-first guard for the one-key change.)

- [ ] **Step 4: Run it, expect FAIL**

Run: `bash build/scripts/tests/release-package.test.sh`
Expected: exit 1 with the new message.

- [ ] **Step 5: Set `"prerelease": false`** in `release-please-config.json`; rerun the test: expected exit 0.

- [ ] **Step 6: Write ADR 0043** in the 0042 format (`# 0043. Version 1.0 and what the package promises`, Status Accepted, Date 2026-11-28, Deciders Lee Jia Le; Context, Options considered, Decision, Consequences, Follow-ups). Content: spec §5.2's ADR bullets (public surface, breaking, not breaking, enforcement), spec §3's version table, options A/B/C with A chosen and B as fallback. Follow-up: the automated cross-version `modulith-module` check.

- [ ] **Step 7: Amend ADR 0037 line 42**: replace "The 1.0.0 cut drops the three keys and sets a `release-as` of `1.0.0` for that one release (it is not proven how release-please graduates from `1.0.0-rc.1`), removed again afterwards." with: the 1.0.0 cut sets `prerelease` to `false` and drops the three keys after the release (ADR 0043); a `release-as` of `1.0.0` is the fallback. Also note that the keys did count rc.2 to rc.11.

- [ ] **Step 8: Rewrite `docs/releasing.md` Section 4** (`## 4. Cutting v1.0`) as the checklist of spec §4 to §5.7: gates G1-G4, the order rules, reading the release PR with the expected values of §5.6 step 2, the polish as the last step, merge and approve, the checks of §5.6 step 5, the post-release PR of §5.7; a "Fallback" paragraph keeps the `release-as` route. In Section 3, "Whether they count `rc.2`, `rc.3` after the first candidate is not proven" becomes: they did, rc.2 to rc.11.

- [ ] **Step 9: Small doc fixes**
  - `docs/blueprint-review.md:133`: replace "remove the three prerelease keys from the root `release-please-config.json` and set a `release-as` of `1.0.0` for that one release, since graduation from the candidate is not proven" with "set `prerelease` to `false` (ADR 0043) and drop the three keys after the release".
  - `docs/README.md`: after the 0042 row add `| [0043](adr/0043-version-1-0-and-what-the-package-promises.md) | Version 1.0: what a major version of the package protects (package ID, template identities and short names, parameters and values, the .NET major, the built-in HTTP API, modulith-module within a major); graduation by `prerelease: false` |`.
  - `docs/deployment/README.md:66`: "after a checkout of the whole history and every tag" → "after a checkout of the whole history; the base is the commit of the last successful `production` deployment (`build/scripts/resolve-deploy-base.sh`)".

- [ ] **Step 10: Verify**

Run: `bash build/scripts/tests/release-package.test.sh`, then the four smoke runs (Global Constraints); then `git add -A && bash build/scripts/project-index.sh` and stage its output.
Expected: all exit 0.

- [ ] **Step 11: Commit, push, open the PR** titled `ci(release): graduate the release configuration to 1.0`; ask the user to merge. After the merge, `gh pr list --state open` shows no release PR (hidden type).

---

### Task 2: The owner's settings (G1)

**Files:** none.

- [ ] **Step 1: Ask the user** to save `RELEASE_PLEASE_TOKEN` (fine-grained, this repository only, Contents and Pull requests read/write) and to add the required checks `package-smoke (all-on)`, `package-smoke (all-off)`, `module-template (all-on-contracts)`, `module-template (all-off-multiword)`.
- [ ] **Step 2: Verify** with the owner token, GET only:

```bash
export GH_TOKEN="$(gh auth token --user jackyjiale-oss)"
gh api repos/jackyjiale-oss/modulith-backend/actions/secrets --jq '.secrets[].name'
gh api repos/jackyjiale-oss/modulith-backend/branches/main/protection/required_status_checks --jq '.contexts[]' | sort
```

Expected: `RELEASE_PLEASE_TOKEN` listed; 17 contexts including the four.

---

### Task 3: The review sheets

**Files:**
- Create (git-ignored): `.superpowers/i18n-review/review-ms.xlsx`, `.superpowers/i18n-review/review-zh-Hans.xlsx`

**Interfaces:**
- Produces: rows keyed by ID: `<repo-relative path>#<key>` (resx), `<repo-relative path>` (template file), `build/deploy/maintenance.html#<lang>-<n>` (maintenance text elements numbered in document order). Task 4 reads the sheets back by this ID.

- [ ] **Step 1: Build the inventory** from `git ls-files | grep -E '\.(ms|zh-Hans)\.' | grep -v '^tests/'` plus the `lang="ms"` and `lang="zh-Hans"` blocks of `build/deploy/maintenance.html`. Expected: 5 resx pairs with 76 entries per language; 56 template files per language (40 Email, 8 InApp, 6 Push, 2 Sms). `tests/TemplateName.UnitTests/Notifications/Templates/` holds only test-only `test.*` fixtures, which do not copy production text: out of scope.
- [ ] **Step 2: Build both workbooks** with the `anthropic-skills:xlsx` skill: tabs *Messages*, *Notifications* (subject, text, InApp, Push, Sms files; not `.html.scriban`), *Maintenance page*; columns ID, English, Current text, Suggestion, Why, Decision (a data-validation list: `Keep`, `Use suggestion`, `Own text`), Reviewer's text, Notes; placeholders highlighted; a header row saying placeholders must stay exactly as written. English comes from the neutral resx, the `.en.` template or the English block.
- [ ] **Step 3: Check** each workbook opens and has one row per inventory item; give the user both paths.

---

### Task 4: Apply the review (G2)

Starts when the user returns both completed sheets, and after Task 1 is merged.

**Files:**
- Modify: the 10 `.ms.resx`/`.zh-Hans.resx` files, the edited `.ms.`/`.zh-Hans.` templates and their `.html.scriban` siblings, `build/deploy/maintenance.html`
- Modify: `docs/blueprint-review.md:192` (§8.3), `README.md:30` (the drafts sentence)
- Test: `tests/TemplateName.ArchitectureTests/TranslationTests.cs` (existing), the Notifications template unit tests (existing)

- [ ] **Step 1: Read the sheets back**; list every row whose Decision is empty, or `Own text` with no Reviewer's text, and ask the user about them before editing.
- [ ] **Step 2: Apply** each `Use suggestion` and `Own text` row to its file; remove every `Draft – needs native review` comment. Check: `grep -rn "needs native review" src templates` prints nothing.
- [ ] **Step 3: Carry each edited text body into its `.html.scriban` sibling**, changing only human text, never markup or `{{ }}` expressions; for each edited type compare the visible text of the HTML and text bodies and confirm they match.
- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TemplateName.ArchitectureTests --filter FullyQualifiedName~TranslationTests` and `dotnet test tests/TemplateName.UnitTests --filter FullyQualifiedName~Notifications`
Expected: PASS.

- [ ] **Step 5: Docs**: close §8.3 ("Reviewed by native speakers of Malay and Simplified Chinese on <date>; strings added later follow CONTRIBUTING §5"); remove the README drafts sentence.
- [ ] **Step 6: Verify**: the four smoke runs and `dotnet format --verify-no-changes`.
- [ ] **Step 7: Commit, PR** `fix(i18n): review the Malay and Chinese text`; ask the user to merge. Afterwards release-please opens `chore(release): release 1.0.0`; if it proposes anything else, spec §9.

---

### Task 5: The delivery proof (G3)

**Files:** in this repository only for a fix (step 5) and the record (step 7).

- [ ] **Step 1: Ask the user** to create, or OK creating with the owner login, a private repository (suggested name `modulith-cd-proof` on `jackyjiale-oss`).
- [ ] **Step 2: Generate** under a short path with a fresh `NUGET_HTTP_CACHE_PATH`: `dotnet new install Modulith.Backend.Templates@1.0.0-rc.11 --debug:custom-hive C:/w/cdp-hive`, then `dotnet new modulith-backend -n Acme.CdProof -o C:/w/cdp/Acme.CdProof --debug:custom-hive C:/w/cdp-hive`; `git init`, commit, push to `main`.
- [ ] **Step 3: Watch the push path** (`gh run list` / `gh run view --repo <proof repo>`). Expected: `ci`, `image`, `verify` green; `deploy-staging` skipped; GHCR shows `sha-<short>` and `edge`.
- [ ] **Step 4: The release path**: with the user's OK, merge the generated project's release PR. Expected: `release-please`, `third-party-notices`, `detect-package`, `cd` → `maintenance` green, its log showing the Deployments lookup found no successful production deployment and `enabled=false`; `image` adds `x.y.z` and `latest`; `verify` green; `deploy-production`, `deploy-iis`, `publish-package` skipped.
- [ ] **Step 5: On a failure**: superpowers:systematic-debugging; fix test-first in the template on its own branch (`fix(ci): ...` or `fix(docker): ...`), merge on the user's word, regenerate from a checkout install, repeat steps 3-4.
- [ ] **Step 6: Ask** whether to delete the proof repository and its GHCR package (permanent) or keep them.
- [ ] **Step 7: Record** in `docs/deployment/README.md` and the Plan 6 row of `docs/blueprint-review.md`: what ran, the date, the deploy jobs still unproven. PR `docs(ci): record the first generated-project pipeline run` (hidden); merge on the user's word.

---

### Task 6: The cut

**Interfaces:**
- Consumes: Tasks 1, 4 and 5 merged; Task 2 verified; the open release PR.

- [ ] **Step 1: Read the release PR**: title `chore(release): release 1.0.0`; `<Version>1.0.0</Version>` in `Version.props`; `".": "1.0.0"` in `.release-please-manifest.json`; a `## [1.0.0]` entry comparing with `v1.0.0-rc.11`; its checks started without "Approve workflows to run". Anything else: stop, spec §9 row 1.
- [ ] **Step 2: Last before merging**: add to the 1.0.0 entry in `CHANGELOG.md` on the release branch and to the PR body: "The first stable release. What a major version protects: [ADR 0043](docs/adr/0043-version-1-0-and-what-the-package-promises.md)." Ask the user to merge, then to approve `nuget`.
- [ ] **Step 3: Confirm the deployment left `waiting`**: the Release run's `publish-package` is running or done.
- [ ] **Step 4: Check**: `gh release view v1.0.0` (not a pre-release, Latest, the `.nupkg` attached, the intro present; if not, ask the user to edit the Release); `curl -s https://api.nuget.org/v3-flatcontainer/modulith.backend.templates/index.json` lists `1.0.0`; once the registration index lists it, `dotnet new install Modulith.Backend.Templates --debug:custom-hive <new folder>` with no version and a fresh cache installs 1.0.0; generate `Acme.Smoke` under `C:\w\v1s`, `dotnet build -c Release`: 0 warnings, 0 errors.

---

### Task 7: The post-release PR

**Files:**
- Modify: `release-please-config.json` (drop `versioning`, `prerelease`, `prerelease-type`)
- Modify: `build/scripts/tests/release-package.test.sh:314-326`
- Modify: `README.md` (status line 30, install section lines 49-61), `docs/releasing.md` §3-4, `docs/blueprint-review.md:133`, `docs/adr/0037-publishing-the-template-package.md`, `docs/adr/0043-version-1-0-and-what-the-package-promises.md`

- [ ] **Step 1: Precondition**: on `origin/main`, `.release-please-manifest.json` says `"1.0.0"` and tag `v1.0.0` exists; otherwise stop.
- [ ] **Step 2: Failing test first**: replace the key loop, the `prerelease: false` assertion and the `grep -v` comparison with `cmp -s "$root_config" "$content_config" || fail "release-please-config.json differs from build/template-content/release-please-config.json"`; update the comment. Run it: expected FAIL (the keys are still there).
- [ ] **Step 3: Remove the three keys**; rerun: expected PASS.
- [ ] **Step 4: Docs**: README stable install form and status (everything implemented, no "Expect breaking changes until 1.0", the exact-version form kept as an option); `docs/releasing.md` §4 marked executed with the date, §3 noted as the recipe for a future candidate line (add the keys back first); the blueprint 7c row, ADR 0037 and ADR 0043 record the cut as done.
- [ ] **Step 5: Verify**: the release-package test and the four smoke runs.
- [ ] **Step 6: Commit, PR** `ci(release): drop the release-candidate keys`; merge on the user's word; confirm no release PR opens. Update the memory notes (`v1-graduation-spec-state`, `open-items`, `MEMORY.md`).
