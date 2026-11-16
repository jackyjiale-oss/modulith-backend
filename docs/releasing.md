# Releasing

How a release of this repository reaches users, and what the owner does by hand. This file is about the **template repository only** and is not copied into generated projects. The decision behind it is [ADR 0037](adr/0037-publishing-the-template-package.md); the design is `docs/superpowers/specs/2026-11-16-template-package-and-publish-design.md`.

A release does three things: release-please tags the commit and creates the GitHub Release, the workflow packs the template as the NuGet package `Modulith.Backend.Templates` and attaches it to that release, and, after the owner approves, pushes it to nuget.org. The package holds both templates (`modulith-backend` and `modulith-module`). Nothing has been published yet: the one-time setup below comes first.

## 1. One-time setup (the owner)

Only the owner of the repository (`jackyjiale-oss`) can do these. Do them in this order, before the first release PR is merged. The field names on nuget.org and GitHub may differ slightly from the words used here.

- [ ] **A nuget.org account.** Sign in at <https://www.nuget.org>. Note the **profile name** (the user name shown under your account menu). It is not the email address.
- [ ] **A trusted-publishing policy on nuget.org** (account menu, Trusted Publishing). It lets the workflow publish without an API key. Fill in:

  | Field | Value |
  |---|---|
  | Repository owner | `jackyjiale-oss` |
  | Repository | `modulith-backend` |
  | Workflow file | `release.yml` |
  | Environment | `nuget` |
  | Package scope | a scope that allows publishing new packages, for the ID `Modulith.Backend.Templates` |

  The scope that allows new packages is what lets the first publish create the package, so no API key is ever needed. The repository is public, so the policy is active at once (the 7-day activation window applies to private repositories only). A key the policy issues lasts one hour.
- [ ] **The GitHub environment `nuget`** (Settings, Environments, New environment). Add the owner as **required reviewer**. Leave "Prevent self-review" off: there is one maintainer, and with it on nobody could approve a release.
- [ ] **The environment secret `NUGET_USER`** in that environment: the nuget.org profile name from the first step, not the email address. It is an environment secret, not a repository secret, so only the publish job can read it.
- [ ] **Optional: reserve the `Modulith.` ID prefix** on nuget.org (an application to the nuget.org team, which they may decline). Another author's package named `Modulith` exists; ours does not collide with it, but a reserved prefix marks ours as the owner's.
- [ ] **Add the two package checks to the protection of `main`** after they have run green once on `main`: `package-smoke (all-on)` and `package-smoke (all-off)` ([`repository-management.md`](repository-management.md), Section 3).

## 2. How a release flows

1. Commits on `main` are Conventional Commits. release-please keeps one pull request, `chore(release): release x.y.z`, up to date. It changes `Version.props`, writes `CHANGELOG.md` and updates `.release-please-manifest.json`.
2. Merging that pull request creates the tag `vx.y.z` and the GitHub Release. The `release.yml` run then starts its jobs: `third-party-notices` attaches the notices, `cd` calls `cd.yml` (in this repository its `detect` job skips everything, so nothing is built or deployed), and `detect-package` finds `build/pack/Modulith.Templates.csproj`.
3. `publish-package` waits for the owner. It is in the environment `nuget`, so the run shows "Waiting" under Actions. Open the run, choose **Review deployments**, tick `nuget`, and **Approve and deploy**. Until then nothing reaches nuget.org.
4. After approval the job checks out `refs/tags/vx.y.z`, checks that the tag equals `<Version>` in `Version.props` at that tag, packs, proves the exact file with `template-smoke.sh --from-package <file> --generate-only`, attaches it to the GitHub Release, logs in to nuget.org with the trusted-publishing policy and pushes it. The push is the last step.
5. Check the result: the package page `https://www.nuget.org/packages/Modulith.Backend.Templates` lists the version (indexing can take a few minutes) and the GitHub Release holds `Modulith.Backend.Templates.x.y.z.nupkg`.

If nobody approves, the release stays a GitHub Release without a package, and the run stays waiting until it times out. Approve it later from the same run, or cancel it and then dispatch the tag (Section 5).

**A run waiting for approval blocks later runs of `release.yml`.** The workflow has one concurrency group (`cancel-in-progress: false`) shared by every run, dispatches included, so a waiting run holds it. A dispatch, and the release-please run for a later push to `main` (the update of the release PR), queue behind it and do nothing until the waiting run is approved, cancelled or has timed out; of several queued runs only the newest is kept. Approve or cancel a waiting release promptly.

## 3. Cutting a release candidate

The first published version is a release candidate, so that the whole path is proven on nuget.org before anything claims 1.0. `release-please-config.json` carries `"versioning": "prerelease"`, `"prerelease": true` and `"prerelease-type": "rc"`.

1. Complete Section 1.
2. **Read the release PR before merging it.** The title and `Version.props` should say `1.0.0-rc.1`. Whether release-please computes exactly that from `0.0.0` is not proven (its documentation does not say, and release PR #8 proposed 1.0.0 before the keys were added).
3. If it proposes another version, do not merge. Add a per-package `release-as` of `1.0.0-rc.1` to `release-please-config.json` (under `packages`, `.`), let the release PR update, and check it again. Remove the `release-as` in the next change after the release.
4. Merge the release PR (today #8, which is therefore the rc PR, not the 1.0 PR), then approve the `nuget` environment (Section 2) promptly: a waiting run blocks later runs of `release.yml`.
5. On a machine without a checkout: `dotnet new install Modulith.Backend.Templates::1.0.0-rc.1`, then `dotnet new modulith-backend -n Acme.Smoke`, then `cd Acme.Smoke` and `dotnet build`. This is the last proof the plan asks for.
6. Open a small pull request for the README: the install section (the prerelease form, and the stable form once 1.0 is out; update and uninstall commands) and a NuGet badge. It waits until now so that it is true when merged.

A candidate is installed by its version (`::1.0.0-rc.1`), as the README will say.

## 4. Cutting v1.0 (a checklist, not yet executed)

7c does not cut 1.0. The release PR that would cut 1.0 stays unmerged until the owner decides 1.0 is ready. Today that is #8; once the prerelease keys are in, release-please keeps updating it, and Section 3 merges it as the release candidate, so it is the rc PR. By the time this checklist is used the rc is out, and the PR to merge here is the one release-please then opens or updates for `1.0.0`. When the owner decides:

- [ ] **Settle the open decisions.** The list is in the owner's notes (the open-items list). The ones that concern 1.0: whether the production-hardening follow-ups gate 1.0 ([`blueprint-review.md`](blueprint-review.md), the unscheduled row); the maintenance-screen detection base (build the comparison against the Deployments API, or accept the interim rule, [ADR 0034](adr/0034-maintenance-mode-for-contract-phase-releases.md)); the delivery pipeline's open triage items and the fact that `cd.yml`, the image registry and a real IIS deploy have never run from a generated project ([ADR 0033](adr/0033-delivery-pipeline.md)); whether push and SMS channels and phone-number OTP login come before 1.0.
- [ ] **The release candidate is out and was installed** on a machine without a checkout (Section 3, step 5).
- [ ] **Remove the three keys** `versioning`, `prerelease` and `prerelease-type` from `release-please-config.json`, so that it equals `build/template-content/release-please-config.json`. The test `build/scripts/tests/release-package.test.sh` checks that the two are equal apart from those keys. Remove a `release-as` if one is still there.
- [ ] **Read the release PR.** It should propose `1.0.0`. If not, use a `release-as` of `1.0.0` for that release and remove it afterwards.
- [ ] **Merge it, then approve the `nuget` environment** (Section 2).
- [ ] **Check as in Section 3, step 5**, with `dotnet new install Modulith.Backend.Templates` and no version.
- [ ] **Update the README** (the stable install form, update and uninstall commands, the badge if it is not there) and the roadmap rows.

## 5. Recovering a failed publish

What a failure leaves behind:

| Failed step | State | Fix |
|---|---|---|
| Check the tag, Pack, Prove the package, Attach to the release | The GitHub Release exists without a package | Fix the cause, then dispatch the tag (below) |
| Push to nuget.org (login or push) | The release has the asset and nuget.org has nothing | Dispatch the tag |
| Nothing failed, but nobody approved | The release has no package | Approve the waiting run, or cancel it first and then dispatch the tag (a waiting run holds the workflow's concurrency group, so a dispatch made meanwhile stays queued) |

To publish an existing release again, run the workflow by hand with the tag:

```bash
gh workflow run release.yml -f tag=vX.Y.Z --ref main
```

(or Actions, Release, Run workflow, with the tag in the input). The dispatch skips `release-please`, runs `publish-package` for that tag and waits for the same approval. It starts only when no other `release.yml` run is waiting or running: cancel a run that is waiting for approval first, or the dispatch queues behind it. The tag must look like `v1.2.3` or `v1.2.3-rc.1` and equal `<Version>` in `Version.props` at that tag, or the job stops before packing. The job runs the workflow file from `main` but packs the code at the tag, so a fix to the workflow takes effect and a fix to the code does not: a code fix is a new version.

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

The environment `nuget` makes the owner approve the job before it reaches the push. There is one maintainer and self-review stays allowed, so the owner approves their own release. It is a **deliberate pause before an irreversible step**, a moment to check the release PR, the package contents and the version. It is **not a four-eyes control**: it does not stop the owner, or anyone who can act as the owner, from publishing. A second maintainer as required reviewer, with "Prevent self-review" on, would make it a control.
