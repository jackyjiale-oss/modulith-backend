# Version 1.0 graduation — design

- Status: design approved section by section by the user (2026-11-28); written spec waiting for the user's review.
- Date: 2026-11-28
- Roadmap row: the v1.0 cut, the last step after the four 1.0 gates decided on 2026-11-20 (push and SMS channels #62/#65, phone-number sign-in #68, production hardening #76, maintenance detection #78), all merged and released as `1.0.0-rc.11`.
- Builds on: ADR 0037 (publishing the template package; its release-candidate decision is the part this changes), `docs/releasing.md` Sections 3 and 4, `docs/api-compatibility.md`, `docs/blueprint-review.md` §8.3 and the 7c row, `CONTRIBUTING.md` §5.
- Scope: what must be true before `Modulith.Backend.Templates` 1.0.0 is published, how release-please is made to propose `1.0.0`, what the 1.0 version promises, and the steps after the release. No product feature changes.

## 1. Intent

**Outcome.** `Modulith.Backend.Templates` 1.0.0 is on nuget.org, installed by `dotnet new install Modulith.Backend.Templates` with no version, and from then on its version number means something written down: a major version protects a defined public surface.

**For whom.** Teams that generate projects from the package, and the maintainer, who needs a release path without a forced-version window and a rule for when a change is breaking.

**Success.**

1. The 1.0.0 release PR is proposed by release-please itself, from the configuration, without a `release-as`.
2. Before it is merged: every Malay and Chinese string the template ships has been reviewed by a native speaker; the delivery pipeline has run in a generated project up to the image push and its first release; the owner's two GitHub settings are in place.
3. After it is merged and approved: the GitHub Release `v1.0.0` is not a pre-release and carries the package; nuget.org serves 1.0.0; an install with no version installs it and the generated project builds in Release with no warning.
4. The root `release-please-config.json` equals the copy generated projects receive.

## 2. Non-goals

- No new feature, module or switch.
- No real deploy: the deploy jobs (staging, production, IIS) stay unproven and documented as such.
- No automated check of the cross-version promise for `modulith-module` (Section 5.2): review enforces it for now; a check is a follow-up.
- The deferred minors (SMS, push, production hardening, maintenance detection), the three small items Plan 6 left to the owner (a CODEOWNERS file, a `windows-latest` leg for the robocopy Pester test, a wider `.dockerignore`), the flaky tests, .NET 11, `configure-github-repo.sh`, the Docker repair on the maintainer machine, SMS delivery receipts and the Push metric name. One exception: the one-line factual error at `docs/deployment/README.md:66` ("every tag") is fixed in the graduation PR.

## 3. Evidence: how release-please numbers the step

`release.yml` uses `googleapis/release-please-action@v5`, whose lock file bundles **release-please 17.6.0**. The rules below are read from that version's source (`src/versioning-strategies/prerelease.ts`, `default.ts`, `src/versioning-strategy.ts`, `src/strategies/base.ts`, `src/manifest.ts`). They were **not run**; the release PR is the check (Section 5.6).

What the next release PR proposes from `1.0.0-rc.11`, by configuration and by the largest change since the last release:

| Configuration | `fix` only | a `feat` | a breaking change |
|---|---|---|---|
| Today: `versioning: prerelease`, `prerelease: true`, `prerelease-type: rc` | `1.0.0-rc.12` | `1.0.0-rc.12` | `1.0.0-rc.12` |
| The three keys removed (default strategy) | `1.0.1-rc.11` | `1.1.0-rc.11` | `2.0.0-rc.11` |
| `versioning: prerelease`, **`prerelease: false`** | **`1.0.0`** | **`1.0.0`** | **`1.0.0`** |
| Any of the above plus a per-package `release-as: 1.0.0` | `1.0.0` | `1.0.0` | `1.0.0` |

Why:

- The default strategy's version updaters copy the pre-release part unchanged (`MajorVersionUpdate`, `MinorVersionUpdate` and `PatchVersionUpdate` pass `version.preRelease` through). So removing the three keys, as `docs/releasing.md` Section 4 plans today, proposes a wrong version unless a `release-as` forces it. That is why the old checklist needed one.
- The prerelease strategy bumps `1.0.0-rc.11` to `1.0.0-rc.12` for any change (the patch updater always bumps only the pre-release number; the minor updater does so while patch is 0, the major updater while minor and patch are 0; the version is 1.0.0, so all three cases bump the number). With `prerelease` not `true`, it then keeps only `major.minor.patch` of the result: `1.0.0`.
- After `1.0.0`, `prerelease: false` behaves like the default strategy (there is no pre-release part left to carry: `1.0.1`, `1.1.0`, `2.0.0`). So there is no window in which a forced version is still active.
- A release PR opens only when the release notes are not empty. Hidden changelog types (`refactor`, `test`, `ci`, `build`, `chore`, `style`, `docs`) produce none, so a change of those types alone opens no release PR (observed for a `ci` commit on 2026-11-17). The visible types that the commit check allows are `feat`, `fix`, `perf` and `revert`. There are no commits on `main` since `v1.0.0-rc.11`.
- The GitHub Release is marked pre-release only when `prerelease` is true in the configuration. With `false`, `v1.0.0` is a normal release.
- A per-package `release-as` in the configuration wins over everything, then a `Release-As:` footer in a commit since the last release, then the strategy.

## 4. What must be true before 1.0

| Gate | Done when | Who |
|---|---|---|
| G1. Owner settings | The repository secret `RELEASE_PLEASE_TOKEN` exists, and `package-smoke (all-on)`, `package-smoke (all-off)`, `module-template (all-on-contracts)` and `module-template (all-off-multiword)` are required checks on `main` (13 are required today; no repository secret exists) | The owner; confirmed with read-only `gh api` calls |
| G2. Native review | Every Malay and Chinese string the template ships has been reviewed and the reviewed text is merged (Section 5.3) | A native speaker per language; the sheets and the PR by Claude |
| G3. Delivery proof | `ci.yml` and `cd.yml` have run in a generated project through the image push, on a push to `main` and on its first release (Section 5.4) | Claude, with the owner's OK for each outward step |
| G4. Graduation configuration | The graduation PR (Section 5.2) is merged | Claude; merged on the owner's word |

The four engineering gates decided on 2026-11-20 are done (see the header). `docs/releasing.md` line 65, which still lists push, SMS and phone-number sign-in as open, is stale and is rewritten (Section 7).

## 5. Design

### 5.1 Order

| Stream | Content | Lands as |
|---|---|---|
| 1 | G1, the owner's settings | GitHub settings |
| 2 | G4, the graduation PR | `ci(release): ...` (hidden: opens no release PR) |
| 3 | G2, the native review | `fix(i18n): ...` (visible: opens the 1.0.0 release PR) |
| 4 | G3, the delivery proof | evidence in the docs; any fix as `fix(ci): ...` or `fix(docker): ...` |
| 5 | The cut, then the post-release PR | the release PR; then `ci(release): ...` |

Rules:

1. **Stream 2 is merged before any visible commit reaches `main`.** A `fix` merged earlier would open a release PR for `1.0.0-rc.12`.
2. Once stream 2 is in, every visible commit lands in one release PR that proposes `1.0.0`. **It is not merged until G1, G2 and G3 are met.**
3. Streams 1, 3 and 4 run in parallel; stream 3 waits for the reviewers, stream 4 for the owner's OK to create the throwaway repository.

### 5.2 The graduation PR (stream 2)

Title `ci(release): graduate the release configuration to 1.0`. Contents:

- **`release-please-config.json`:** `"prerelease": true` becomes `"prerelease": false`. `versioning` and `prerelease-type` stay until after the release (Section 5.7).
- **`build/scripts/tests/release-package.test.sh`:** the test still requires the three keys in the root config and none in the copy, so only its comment changes: it says why `prerelease` is `false` and that the three keys go once `v1.0.0` exists.
- **ADR 0043, "Version 1.0 and what the package promises".** It records:
  - **The public surface a major version protects:**
    - the package ID `Modulith.Backend.Templates`;
    - the template identities `Modulith.Backend.Templates` and `Modulith.Module.Templates` and the short names `modulith-backend` and `modulith-module`;
    - every template parameter's name and accepted values (today `notifications`, `includeSample`, `caching` with `redis` and `memory`; `prefix`, `contracts`);
    - the .NET major the template targets (`global.json` pins 10.0.401 with `rollForward: latestFeature`);
    - the HTTP API of the built-in modules as the OpenAPI baseline describes it, under the rules of `docs/api-compatibility.md` (the gate that already requires `!` for a break in this repository);
    - `modulith-module` from a 1.y release adds a module to a project generated by any 1.x release with x ≤ y.
  - **Breaking, so `!` and a major:** removing or renaming any of the above names or values; a new .NET major; an API break the OpenAPI gate reports; a `modulith-module` change that no longer works in a project generated by an earlier release of the same major.
  - **Not breaking:** adding a parameter, a value or an endpoint; changing a parameter's default (named in the release notes); a higher SDK feature band within the same major (named in the release notes); any other change to the code, documents or workflows of a generated project, which is a snapshot its owners keep and version themselves.
  - **Enforcement:** the OpenAPI gate for the API; review for everything else. An automated cross-version check for `modulith-module` is a follow-up.
  - **The graduation mechanism** (Section 3) and its evidence, with the `release-as` route as the fallback.
- **ADR 0037:** its release-candidate decision ends "The 1.0.0 cut drops the three keys and sets a `release-as` of `1.0.0`". That sentence is amended in place to point to ADR 0043 (set `prerelease` to `false`; the keys go after the release).
- **`docs/releasing.md` Section 4:** rewritten as the checklist of Sections 4 to 5.7 of this spec, with the `release-as` route kept as the documented fallback.
- **`docs/blueprint-review.md` 7c row:** its sentence on how the v1.0 cut works (drop the keys, `release-as`) is corrected.
- **`docs/README.md`:** the index lists ADR 0043.
- **`docs/deployment/README.md:66`:** the stale "every tag" statement is corrected.

### 5.3 The native review (stream 3)

**Inventory**, rechecked when the sheets are built (`git ls-files` for `.ms.` and `.zh-Hans.` files plus the language blocks of `build/deploy/maintenance.html`):

- Error messages: five `.resx` pairs (Infrastructure.Common, Web.Common, Auth, Notifications, Sample), 76 entries per language. 21 per language carry the `Draft – needs native review` comment; the Auth and Notifications entries never carried it and were never reviewed either. The module template's pair holds no entry.
- Notification templates: 15 notification types plus the email layout, 56 files per language (Email subject, text and HTML; in-app; push; SMS).
- The maintenance page: the Malay and Chinese blocks of `build/deploy/maintenance.html`.

**The sheets.** One `.xlsx` per language, in `.superpowers/i18n-review/` (git-ignored, so they are never committed, and inside the checkout, so the app can open them). Tabs: *Messages*, *Notifications*, *Maintenance page*. Columns: ID (file and key), English, Current text, Suggestion (Claude's, empty when the current text is fine), Why, Decision (a list: Keep, Use suggestion, Own text), Reviewer's text, Notes. Placeholders (`{0}` in the messages, Scriban `{{ ... }}` in the templates) are highlighted, and the sheet's header says they must stay as they are. HTML email bodies are not on the sheet: they repeat the text body, and the approved text is carried into them.

**Applying the result.** One PR, `fix(i18n): review the Malay and Chinese text`:

- every decision applied; the HTML bodies brought in line with their text bodies;
- every `Draft – needs native review` comment removed;
- unit-test fixtures under `tests/TemplateName.UnitTests/Notifications/Templates/` updated where they copy the production text;
- `docs/blueprint-review.md` §8.3 closed with the date (reviewer roles, no names); the README sentence "The Malay and Simplified Chinese messages are drafts awaiting native review" removed; the rule in `CONTRIBUTING.md` §5 stays for strings added later.

It is the first visible commit after the graduation PR, so it opens the 1.0.0 release PR.

### 5.4 The delivery proof (stream 4)

**Setup.** A new private throwaway repository on the owner's account, created by the owner or by Claude with the owner's login after the owner's OK. Claude generates a project into it from `1.0.0-rc.11` on nuget.org (all switches at their defaults, an isolated template hive, a short path) and pushes it to `main`. No repository variable is set, so `DEPLOY_ENABLED` and `IIS_ENABLED` are off.

**Expected results.**

| Path | Must run green | Must be skipped (reason) |
|---|---|---|
| The push to `main` | `ci`, `image` (pushes `sha-<short>` and `edge` to GHCR), `verify` | `deploy-staging` (`DEPLOY_ENABLED` is not set) |
| The first release (merge the generated project's release PR; the throwaway repository has no branch protection, so it merges without its checks) | `release-please`, `third-party-notices`, `detect-package`, `cd` → `maintenance` (calls the real Deployments API, finds no successful production deployment, answers `false`), `image` (adds `x.y.z` and `latest`), `verify` | `deploy-production`, `deploy-iis` (not enabled); `publish-package` (no pack project in a generated project) |

**Passes when** every job in the left column is green, every job in the right column is skipped for the reason given, and GHCR lists the four tags.

**A failure** is diagnosed first, then fixed in the template test-first as a `fix(ci)` or `fix(docker)` PR, which joins the 1.0.0 release PR; the proof is then repeated from a checkout install (the `package-smoke` check already proves that a checkout install generates the same project as the package).

**Recording.** `docs/deployment/README.md` and the Plan 6 row of `docs/blueprint-review.md` say what ran, on which date, and that the deploy jobs remain unproven.

**Cost and cleanup.** A private repository uses the owner's Actions minutes: about 1 to 1.5 hours for both paths. Deleting the repository and its GHCR package afterwards is permanent, so Claude asks first; the owner may keep it.

### 5.5 The owner's settings (stream 1)

The owner saves `RELEASE_PLEASE_TOKEN` (a fine-grained token for this repository only, Contents and Pull requests read and write, as `CONTRIBUTING.md` §5 says) and adds the four checks to the protection of `main`. Claude confirms both with read-only calls (the secret's name in the repository's secret list; the required checks of `main`). The release PR's checks starting without "Approve workflows to run" proves the token.

### 5.6 The cut (stream 5)

1. G1 to G4 are met and `main` is green.
2. **Read the release PR.** Expect the title `chore(release): release 1.0.0`, `<Version>1.0.0</Version>` in `Version.props`, `".": "1.0.0"` in `.release-please-manifest.json`, and a `## [1.0.0]` CHANGELOG entry that compares with `v1.0.0-rc.11`. If it proposes anything else, do not merge: use the fallback (a per-package `release-as` of `1.0.0`, with the removal step of `docs/releasing.md` §3 step 5).
3. **Last, just before merging:** add a two-line introduction to the 1.0.0 entry, in `CHANGELOG.md` on the release branch and in the release PR's body: the first stable release, and ADR 0043 for what a major version protects. It must be the last step because release-please rewrites the branch on every push to `main`. That the edited PR body becomes the GitHub Release text is not proven; Claude checks after the release and the owner edits the Release by hand if it did not.
4. **The owner merges the release PR**, then approves the `nuget` environment. Claude confirms that the deployment left "waiting" (one earlier approval did not register).
5. **Checks:** the GitHub Release `v1.0.0` is not a pre-release, is shown as Latest and carries `Modulith.Backend.Templates.1.0.0.nupkg`; nuget.org's flat container lists `1.0.0`; once the registration index lists it (about 10 to 14 minutes after the push), `dotnet new install Modulith.Backend.Templates` with no version, in an isolated hive with a fresh `NUGET_HTTP_CACHE_PATH`, installs 1.0.0; `dotnet new modulith-backend -n Acme.Smoke` under a short path builds with `dotnet build -c Release` with no warning.

### 5.7 After the release

One PR, `ci(release): drop the release-candidate keys`, a hidden type, so it opens no release PR:

- `release-please-config.json` loses `versioning`, `prerelease` and `prerelease-type` and then equals `build/template-content/release-please-config.json`. **Only after `v1.0.0` exists and the manifest says `1.0.0`:** before that, the default strategy would propose `1.0.1-rc.11`.
- `release-package.test.sh` drops its key list and asserts that the two files are identical.
- `README.md`: the stable install form, the status rewritten (everything listed as implemented, "Expect breaking changes until 1.0" removed), the candidate form kept only as how to install an exact version.
- `docs/releasing.md`: Section 4 marked executed with the date; Section 3 kept as the recipe for a future candidate line, with a note that the keys would have to be added back.
- `docs/blueprint-review.md` 7c row, ADR 0037 and ADR 0043: the cut is recorded as done.

## 6. Tests and checks

- Graduation PR: `release-package.test.sh`; `bash build/scripts/template-smoke.sh`, then with `--notifications false`, `--includeSample false` and `--caching memory`; `bash build/scripts/project-index.sh` (new ADR, changed configuration documentation), its result committed.
- Native-review PR: the existing `TranslationTests` (same keys and placeholders as the neutral resource), the notification template unit tests, the full unit suite; the smoke with the three switch-off legs (the Sample and Notifications files are switch-marked areas).
- Delivery proof: the GitHub Actions runs of Section 5.4 are the test.
- Post-release PR: `release-package.test.sh` (now asserting identical files) and the smoke.
- CI runs everything on each PR; integration tests run only in CI (Docker on the maintainer machine is damaged).

## 7. Documentation

| File | Change | PR |
|---|---|---|
| `docs/adr/0043-version-1-0-and-what-the-package-promises.md` | new | graduation |
| `docs/adr/0037-publishing-the-template-package.md` | release-candidate decision amended; later, cut recorded | graduation; post-release |
| `docs/releasing.md` | Section 4 rewritten (also fixes line 65); later, marked executed | graduation; post-release |
| `docs/blueprint-review.md` | 7c row; §8.3 closed; Plan 6 row proof note; later, cut recorded | graduation; native review; proof; post-release |
| `docs/README.md` | ADR 0043 in the index | graduation |
| `docs/deployment/README.md` | line 66 fixed; proof note | graduation; proof |
| `README.md` | draft sentence removed; later, stable form and status | native review; post-release |
| `build/scripts/tests/release-package.test.sh` | comment; later, identical-files assertion | graduation; post-release |

## 8. Known limits

- The version table of Section 3 is read from the source, not run. The release PR shows the real proposal before anything irreversible happens.
- The deploy jobs (staging, production, IIS) have never run; the proof stops at the image push.
- The edited release PR body becoming the GitHub Release text is not proven.
- The cross-version promise for `modulith-module` is enforced by review only.
- "Shown as Latest" depends on GitHub's default for a non-pre-release; it is checked, not assumed.

## 9. When something goes wrong

| What | Then |
|---|---|
| The release PR proposes something other than `1.0.0` | Do not merge; per-package `release-as: 1.0.0`, then its removal (`docs/releasing.md` §3 step 5) |
| A visible commit reached `main` before the graduation PR | Leave the `1.0.0-rc.12` release PR unmerged and read it again after the graduation PR is in. If it still says `1.0.0-rc.12`, check that the `release-please` job of that push ran; on 2026-11-19 an open release PR was not refreshed by a hidden merge, and `gh pr update-branch` on it was the remedy |
| The delivery proof fails | Diagnose, fix test-first in the template, repeat from a checkout install (Section 5.4) |
| The publish fails | `docs/releasing.md` §5 |
| 1.0.0 is bad on nuget.org | Unlist it and ship 1.0.1 (`docs/releasing.md` §6); the number 1.0.0 can never be used again |

## 10. Decisions recorded (the user, 2026-11-28)

1. Native review before 1.0, of everything the template ships in Malay and Chinese (not only the 42 marked entries).
2. Claude prepares one sheet per language with suggestions; a native speaker decides; Claude applies the result.
3. Both owner settings gate 1.0.
4. The delivery pipeline is proven in a private throwaway repository up to the image push, on a push and on the first release.
5. ADR 0043 defines the package's public surface.
6. The built-in modules' HTTP API is part of that surface.
7. Approach A: `prerelease: false`, with `release-as` as the fallback; the three keys go after the release.

## 11. Follow-ups (not in this work)

- An automated check that the current `modulith-module` adds a module to a project generated by the last published release of the same major.
- The three Plan 6 items left to the owner, the deferred minors, the flaky tests, .NET 11 and the rest of Section 2.
