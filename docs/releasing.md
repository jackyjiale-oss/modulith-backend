# Releasing

How a release of this repository reaches users, and what the owner does by hand. This file is about the **template repository only** and is not copied into generated projects. The decision behind it is [ADR 0037](adr/0037-publishing-the-template-package.md); the design is `docs/superpowers/specs/2026-11-16-template-package-and-publish-design.md`.

A release does three things: release-please tags the commit and creates the GitHub Release, the workflow packs the template as the NuGet package `Modulith.Backend.Templates` and attaches it to that release, and, after the owner approves, pushes it to nuget.org. The package holds both templates (`modulith-backend` and `modulith-module`). The first candidate, `1.0.0-rc.1`, is on nuget.org since 2026-11-17 (tag `v1.0.0-rc.1` of 2026-11-16). Its first three publish attempts failed at the trusted-publishing login because no policy had been created yet (Section 5); the fourth, a dispatch of the tag, pushed it.

## 1. One-time setup (the owner)

Only the owner of the repository (`jackyjiale-oss`) can do these. Do them in this order, before the first release PR is merged. The field names on nuget.org and GitHub may differ slightly from the words used here.

- [ ] **A nuget.org account.** Sign in at <https://www.nuget.org>. Note the **profile name** (the user name shown under your account menu). It is not the email address.
- [ ] **A trusted-publishing policy on nuget.org** (account menu, Trusted Publishing). It lets the workflow publish without an API key. The form is under the heading **Create**, which is collapsed when the page opens; the setup is done only when the policy is listed under **Manage** (a page that says "You don't have any Trusted Publisher Policies" means there is none, and every login then fails). Fill in:

  | Field | Value |
  |---|---|
  | Policy name | any name, for example `modulith-backend-release` |
  | Package owner | your own nuget.org user name |
  | Repository Owner | `jackyjiale-oss` |
  | Repository | `modulith-backend` |
  | Workflow File | `release.yml` (the file name only) |
  | Environment | `nuget` |
  | Select Scopes | **Push**, with **Push new packages and package versions** |
  | Glob Patterns and Packages | `Modulith.Backend.Templates` (a pattern without `*` allows that one ID; the form refuses an empty box) |

  The scope that allows new packages is what let the first publish create the package, so no API key was needed: proven on 2026-11-17 with `1.0.0-rc.1`. The repository is public, so the policy is active at once (the 7-day activation window applies to private repositories only). A key the policy issues lasts one hour.

  **The last two statements (the 7-day window for private repositories only, one-hour keys) come from nuget.org's documentation and were not checked against the live site.** Section 5 keeps a manual fallback for a first push that is refused; it needs no change to the workflow.
- [ ] **The GitHub environment `nuget`** (Settings, Environments, New environment). Add the owner as **required reviewer**. Leave "Prevent self-review" off: there is one maintainer, and with it on nobody could approve a release. Then set **Deployment branches and tags** to **Selected branches and tags** and allow **`main` only**. The trusted-publishing policy checks the workflow file name and the environment, not the branch: without this limit a collaborator with write access could push a branch with a modified `release.yml`, dispatch it, and one approval click would publish whatever that file packs. Both publish paths run from `main` (the release-please push, and `gh workflow run release.yml -f tag=vX.Y.Z --ref main`), so the limit costs nothing. Optional: add a tag ruleset (Settings, Rules) that restricts who may create `v*` tags. The job also refuses a tag whose commit is not on `main` (Section 2, step 4), but a ruleset stops the tag earlier.
- [ ] **The environment secret `NUGET_USER`** in that environment: the nuget.org profile name from the first step, not the email address. It is an environment secret, not a repository secret, so only the publish job can read it.
- [ ] **Optional: reserve the `Modulith.` ID prefix** on nuget.org (an application to the nuget.org team, which they may decline). Another author's package named `Modulith` exists; ours does not collide with it, but a reserved prefix marks ours as the owner's.
- [ ] **Add the two package checks to the protection of `main`** after they have run green once on `main`: `package-smoke (all-on)` and `package-smoke (all-off)` ([`repository-management.md`](repository-management.md), Section 3).

## 2. How a release flows

1. Commits on `main` are Conventional Commits. release-please keeps one pull request, `chore(release): release x.y.z`, up to date. It changes `Version.props`, writes `CHANGELOG.md` and updates `.release-please-manifest.json`.
2. Merging that pull request creates the tag `vx.y.z` and the GitHub Release. The `release.yml` run then starts its jobs: `third-party-notices` attaches the notices, `cd` calls `cd.yml` (in this repository its `detect` job skips everything, so nothing is built or deployed), and `detect-package` finds `build/pack/Modulith.Templates.csproj`.
3. `publish-package` waits for the owner. It is in the environment `nuget`, so the run shows "Waiting" under Actions. Open the run, choose **Review deployments**, tick `nuget`, and **Approve and deploy**. Until then nothing reaches nuget.org.
4. After approval the job checks out `refs/tags/vx.y.z` with the full history, refuses the tag unless its commit is on `main` (`git merge-base --is-ancestor HEAD origin/main`; release-please tags are on `main`), checks that the tag equals `<Version>` in `Version.props` at that tag, packs, proves the exact file with `template-smoke.sh --from-package <file> --generate-only`, attaches it to the GitHub Release, logs in to nuget.org with the trusted-publishing policy and pushes it. The push is the last step.
5. Check the result: the package page `https://www.nuget.org/packages/Modulith.Backend.Templates` lists the version (indexing can take a few minutes) and the GitHub Release holds `Modulith.Backend.Templates.x.y.z.nupkg`.

If nobody approves, the release stays a GitHub Release without a package, and the run stays waiting until it times out. Approve it later from the same run, or cancel it and then dispatch the tag (Section 5).

**A run waiting for approval blocks later runs of `release.yml`.** The workflow has one concurrency group (`cancel-in-progress: false`) shared by every run, dispatches included, so a waiting run holds it. A dispatch, and the release-please run for a later push to `main` (the update of the release PR), queue behind it and do nothing until the waiting run is approved, cancelled or has timed out; of several queued runs only the newest is kept. Approve or cancel a waiting release promptly.

## 3. Cutting a release candidate

The first published version is a release candidate, so that the whole path is proven on nuget.org before anything claims 1.0. `release-please-config.json` carries `"versioning": "prerelease"`, `"prerelease": true` and `"prerelease-type": "rc"`. Those three keys alone did not change the proposal (observed 2026-11-16: release PR #8 still proposed 1.0.0 after they were merged), so the first candidate was forced with a per-package `"release-as": "1.0.0-rc.1"` (under `packages`, `.`). That worked: #8 then proposed `1.0.0-rc.1`, and merging it on 2026-11-16 created the tag `v1.0.0-rc.1` and the GitHub Release. The `release-as` was removed again right after (step 5). The three keys stay. They did count the later candidates, `rc.2` to `rc.11`, without a `release-as`; read every release PR before merging it all the same.

1. Complete Section 1.
2. **Read the release PR before merging it.** The title and `Version.props` should say the candidate you expect (`1.0.0-rc.1` for the first one). A release PR opened without the repository secret `RELEASE_PLEASE_TOKEN` is authored by `github-actions[bot]`, and its workflow runs wait for an approval (observed on #8): choose **Approve workflows to run** on the pull request, or its required checks never report and it cannot be merged. That repeats on every update of the release PR until the token is saved ([`repository-management.md`](repository-management.md) describes it).
3. If it proposes another version, do not merge. Force the version with a per-package `"release-as"` in `release-please-config.json` (a small pull request, which also adds `release-as` to the key list in `build/scripts/tests/release-package.test.sh`: the test fails on a `release-as` it does not expect), let the release PR update, and check it again.
4. Merge the release PR, then approve the `nuget` environment (Section 2) promptly: a waiting run blocks later runs of `release.yml`. #8 was merged this way as the first candidate.
5. **If a `release-as` was used, do this first once the release exists, before step 6: remove the `release-as` line in a small pull request.** The `release-as` stays active after the tag exists, so until the removal PR is in, any `feat` or `fix` merged to `main` makes release-please open or update a release PR for the same version again, and merging that PR would try to tag a version that already exists and rerun the publish job (`--skip-duplicate` would hide the repeat on nuget.org). So merge no other `feat` or `fix`, and no release PR, until the removal PR is in. A bad PR is recognisable by a title with a version that `.release-please-manifest.json` already holds: close it. Use a `ci` or `chore` type for the removal PR: those are hidden changelog types and open no release PR (observed on 2026-11-17: the `ci` removal commit opened none). The same PR also drops `release-as` from the key list in `build/scripts/tests/release-package.test.sh`, or the `release-package` check goes red, because the test requires every listed key to be present in the root config. For the first candidate this was done on 2026-11-16.
6. On a machine without a checkout (or with a throwaway template hive, `--debug:custom-hive <folder>` on every command): `dotnet new install Modulith.Backend.Templates@1.0.0-rc.1`, then `dotnet new modulith-backend -n Acme.Smoke`, then `cd Acme.Smoke` and `dotnet build`. This is the last proof the plan asks for. Done for `1.0.0-rc.1` on 2026-11-17: the install listed both templates, and the generated project built in Release with no warning.
7. Open a small pull request for the README: the install section (the prerelease form, and the stable form once 1.0 is out; update and uninstall commands) and a NuGet badge. It waits until now so that it is true when merged. Done for the first candidate.

A candidate is installed by its version (`@1.0.0-rc.1`; the SDK prints a deprecation notice for the older `::` separator). While no stable version exists, `dotnet new install Modulith.Backend.Templates` without a version installs the newest candidate (observed on 2026-11-17).

nuget.org validates a package before it serves it, so step 6 does not work the minute the job is green: `1.0.0-rc.1` could be downloaded about 12 minutes after the push, and `dotnet new install` found it a minute or two after that, once the registration index listed it. Until then the install answers "the package does not exist".

## 4. Cutting v1.0

The decision and the evidence are [ADR 0043](adr/0043-version-1-0-and-what-the-package-promises.md); the design is `docs/superpowers/specs/2026-11-28-v1-graduation-design.md`. In release-please 17.6.0 (the version `release-please-action@v5` bundles), `"prerelease": false` with `"versioning": "prerelease"` kept turns `1.0.0-rc.N` into `1.0.0` for any change; removing the three keys instead would propose `1.0.1-rc.N` or `1.1.0-rc.N`. A release PR opens only for a visible commit (`feat`, `fix`, `perf`, `revert`).

**Gates.** All four are met before the 1.0.0 release PR is merged:

- [ ] **G1. The owner's settings:** the repository secret `RELEASE_PLEASE_TOKEN` (Section 3, step 2) and the required checks `package-smoke (all-on)`, `package-smoke (all-off)`, `module-template (all-on-contracts)` and `module-template (all-off-multiword)` on `main`.
- [ ] **G2. Native review:** every Malay and Chinese string the template ships (the `.ms.resx` and `.zh-Hans.resx` files, the Notifications templates, the maintenance page) has been reviewed by a native speaker and the result is merged.
- [ ] **G3. Delivery proof:** `ci.yml` and `cd.yml` have run in a generated project, in a throwaway repository, through the image push, on a push to `main` and on its first release.
- [ ] **G4. The graduation configuration:** the pull request that sets `"prerelease": false` is merged. It is a `ci` commit, so it opens no release PR.

**Order.** G4 is merged before any visible commit reaches `main`: a `fix` merged earlier opens a release PR for `1.0.0-rc.N+1` (if that happened, leave it unmerged and read it again after G4 is in; if it still shows the candidate, check that release-please ran for that push, or refresh it with `gh pr update-branch`). After G4, every visible commit lands in one release PR that proposes `1.0.0`; it stays unmerged until G1 to G3 are met.

**The cut.**

- [ ] **Read the release PR.** Title `chore(release): release 1.0.0`, `<Version>1.0.0</Version>` in `Version.props`, `".": "1.0.0"` in `.release-please-manifest.json`, a `## [1.0.0]` entry in `CHANGELOG.md` that compares with the last candidate. Its checks start without "Approve workflows to run" once the token is saved. If it proposes anything else, do not merge: use the fallback below.
- [ ] **Last, just before merging:** add a two-line introduction to the 1.0.0 entry, in `CHANGELOG.md` on the release branch and in the release PR's body (the first stable release; ADR 0043 for what a major version protects). It must be the last step because release-please rewrites the branch on every push to `main`. That the edited body becomes the GitHub Release text is not proven: check after the release and edit the Release by hand if it did not.
- [ ] **Merge it, then approve the `nuget` environment** (Section 2), and confirm the deployment left "waiting".
- [ ] **Check:** the GitHub Release `v1.0.0` is not a pre-release, is shown as Latest and carries `Modulith.Backend.Templates.1.0.0.nupkg`; nuget.org lists 1.0.0; once the registration index lists it (10 to 14 minutes after the push), `dotnet new install Modulith.Backend.Templates` with no version, in a throwaway hive with a fresh `NUGET_HTTP_CACHE_PATH`, installs 1.0.0, and a generated project builds in Release with no warning (Section 3, step 6).

**After the release** (only once `v1.0.0` exists and the manifest says `1.0.0`; before that, the default strategy would propose `1.0.1-rc.N`), one `ci` pull request:

- [ ] Remove `versioning`, `prerelease` and `prerelease-type` from `release-please-config.json`, so that it equals `build/template-content/release-please-config.json`; `build/scripts/tests/release-package.test.sh` drops its key list and its `prerelease` assertion and checks that the two files are identical.
- [ ] Update the README (the stable install form, the status) and the roadmap rows, and mark this section executed.

**Fallback** (the release PR proposes something other than `1.0.0`): add a per-package `"release-as": "1.0.0"` (under `packages`, `.`) and `release-as` to the key list of `release-package.test.sh`, let the release PR update and check it again; after the release, remove the `release-as` with the care of Section 3, step 5.

## 5. Recovering a failed publish

What a failure leaves behind:

| Failed step | State | Fix |
|---|---|---|
| Check the tag is on main, Check the tag, Pack, Prove the package, Attach to the release | The GitHub Release exists without a package | Fix the cause (a failure in the tag-sourced files needs a new version, see below), then dispatch the tag (below) |
| Push to nuget.org (login or push) | The release has the asset and nuget.org has nothing | Dispatch the tag |
| `NuGet login` fails with HTTP 401, "No matching trust policy owned by user ..." (the first three attempts for `v1.0.0-rc.1`, 2026-11-16 and 17: no policy had been created, because the form sits under the collapsed heading **Create**) | The release has the asset (the upload comes before the login) and nuget.org has nothing | nuget.org gives this one message when the user name is unknown, when that user created no policy, and when any field of a policy that has never been used differs, so the message does not say which. Do not dispatch again blind: open the Trusted Publishing page signed in as the account whose user name is in `NUGET_USER` (the profile name; not the email address and not an organisation's name) and compare with Section 1. A policy must be listed under **Manage**, created by that account, and its repository owner, repository, workflow file (`release.yml`) and environment (`nuget`) must match. Correct the policy or the secret, then dispatch the tag |
| The first push is refused because the package does not exist yet, or the policy has no scope for new packages (this did not happen for `1.0.0-rc.1`: a policy with **Push new packages and package versions** created the package) | The release has the asset (the upload comes before the login) and nuget.org has nothing | Push the asset by hand once, then fix the policy (below) |
| Nothing failed, but nobody approved | The release has no package | Approve the waiting run, or cancel it first and then dispatch the tag (a waiting run holds the workflow's concurrency group, so a dispatch made meanwhile stays queued) |

To publish an existing release again, run the workflow by hand with the tag:

```bash
gh workflow run release.yml -f tag=vX.Y.Z --ref main
```

(or Actions, Release, Run workflow, with the tag in the input). The dispatch skips `release-please`, runs `publish-package` for that tag and waits for the same approval. It starts only when no other `release.yml` run is waiting or running: cancel a run that is waiting for approval first, or the dispatch queues behind it. The tag must look like `v1.2.3` or `v1.2.3-rc.1` and equal `<Version>` in `Version.props` at that tag, or the job stops before packing. The job runs the workflow file from `main` but takes everything else from the tag: `build/scripts/release-package.sh`, `build/scripts/template-smoke.sh`, `build/pack/`, `global.json` and the files the proof generates. Only the workflow YAML comes from `main`. So a fix to the workflow takes effect on a dispatch, and a failure in any of the tag-sourced files does not go away: it needs a new version (fix `main`, merge the next release PR).

**The manual first push** (when nuget.org refuses the first push as described in the table). It needs no change to the workflow, and the key never enters GitHub:

1. On nuget.org create an API key with a short expiry (a day), scope **Push new packages and package versions**, and a glob of `Modulith.Backend.Templates` as the package pattern.
2. Download the asset the failed run attached: `gh release download vX.Y.Z --pattern "*.nupkg"`.
3. From your machine: `dotnet nuget push Modulith.Backend.Templates.X.Y.Z.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json --skip-duplicate`.
4. Delete the key on nuget.org. Then correct the trusted-publishing policy (the scope for new packages, the field values of Section 1) so that the next version publishes through the workflow. The package now exists, so even a policy limited to existing packages works from then on.

The job's step that pushes (`Push to nuget.org`) is the one that would otherwise need the key; giving it a stored key would mean changing the workflow and keeping a secret, so the manual push is preferred.

Re-running is safe, and the job's rules are:

- A re-run packs again and **replaces the release asset with the new build** (`gh release upload --clobber`).
- It **skips the nuget.org push if that version is already there** (`--skip-duplicate`). nuget.org keeps the first push.
- So the asset on the GitHub Release and the package on nuget.org can hold different bytes if the first push succeeded and a later run built something else. Download the package from nuget.org and compare it with the asset if it matters.

## 6. A bad version on nuget.org

A published version cannot be deleted and its number cannot be used again.

1. **Unlist it** on nuget.org (Manage Packages, `Modulith.Backend.Templates`, the version, clear "List in search results"). Unlisted versions are hidden from nuget.org search; they stay available to whoever names the exact version.
2. **Ship a fixed version**: fix `main`, merge the release PR that results (a new version number, the next candidate or patch) and approve it. Check the number on the release PR first; it is not reused.
3. Say so in the release notes of the fixed version if the bad one did harm.

## 7. What is irreversible

- A version pushed to nuget.org: it can be unlisted, never deleted, and its bytes and number are permanent.
- The package ID: once created under the owner's account, nobody else can publish under it.
- A tag and a GitHub Release can be edited or removed by the owner, but a nuget.org version published from them stays.

## 8. What the approval is and is not

The environment `nuget` makes the owner approve the job before it reaches the push. There is one maintainer and self-review stays allowed, so the owner approves their own release. It is a **deliberate pause before an irreversible step**. The gate fires before the job starts, so at approval no package exists yet: nothing is packed, proven or attached. What the owner can check then is the release PR and the version; the package is built and proven only after the approval. It is **not a four-eyes control**: it does not stop the owner, or anyone who can act as the owner, from publishing. A second maintainer as required reviewer, with "Prevent self-review" on, would make it a control.
