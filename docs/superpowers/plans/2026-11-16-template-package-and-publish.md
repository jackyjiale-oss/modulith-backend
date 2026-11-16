# Template Package and Publish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Modulith.Backend.Templates` as a NuGet template package, prove on every pull request that it generates exactly what a checkout generates, and publish it from the release workflow behind an approval gate, first as a release candidate.

**Architecture:** A pack-only project (`build/pack/`) packs the checkout tree; `template-smoke.sh --from-package` installs the `.nupkg` and compares its output with the checkout's; a small script (`build/scripts/release-package.sh`) holds the logic the workflow needs (tag check, pack) so it is testable; `release.yml` gets a `publish-package` job in the `nuget` environment using nuget.org trusted publishing.

**Tech Stack:** .NET SDK 10.0.401 (`dotnet pack`, `dotnet new`), bash scripts and their bash tests, GitHub Actions, release-please, nuget.org trusted publishing (`NuGet/login@v1`).

**Spec:** `docs/superpowers/specs/2026-11-16-template-package-and-publish-design.md` (section 12 = spike results, which override the earlier sections).

## Global Constraints

- Worktree `C:\w\c7` (short path, branch `docs/package-publish-spec`); run commands from `/c/w/c7`. Nothing is pushed or merged without the user's approval, each time.
- Commit headers are Conventional Commits, 72 characters at most, lower-case imperative, scope from `build/scripts/check-commit-msg.sh` (`ci`, `release`, `template`, `adr`, ...). End every commit message with the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Workflow inputs and matrix values reach shell only through `env:`, never interpolated into `run:`. Actions use their latest major tag (`actions/checkout@v7`, `actions/setup-dotnet@v6`, `NuGet/login@v1`).
- `TemplateName` is the placeholder; never hard-code a concrete generated-project name (the smoke's `Acme.Smoke` is the only name used). The product name `Modulith.Backend.Templates` is fine.
- Nothing repository-only may reach a generated project: new repo-only files go into the root `sources.exclude` of `.template.config/template.json` and into the smoke's `forbidden` list. No markdown link from a shipped document (an ADR, `docs/README.md`, `docs/architecture/overview.md`) to a repo-only file such as `docs/releasing.md`; name it in plain text.
- The pack project's values are fixed by spike section 12: `NoDefaultExcludes` true, content item `Include="$(RepoRoot)**\*"` with `Pack="true" PackagePath="content\"` (folder only), its own `build/pack/Directory.Build.props` importing only `..\..\Version.props`, excludes including `coverage` and `**\*.user`.
- Run after each task that touches what the smoke checks: `bash build/scripts/template-smoke.sh --generate-only` (about 7 minutes). The three switch-off runs of the 7b plan are not needed (no marked region changes).
- After adding a test file or a switch marker, `bash build/scripts/project-index.sh --check`; if it fails run `bash build/scripts/project-index.sh` and commit the result (stage new files first, the generator reads tracked files only).

## Review Focus

Failure modes the spec implies; each has a test or check in the task named in brackets.

1. A release tag whose version differs from `Version.props` (including a prerelease suffix), or that is not a release tag at all (empty, no `v`, shell text): `check-tag` fails before anything is packed. [Task 2]
2. A pack that silently loses dotfiles or extensionless files, or carries developer-only folders: the content assertions of the test and the equivalence diff fail and name the file. [Tasks 2, 3]
3. A re-run after a partial failure (asset already attached, package already pushed): `--clobber` and `--skip-duplicate` are present, checked statically. [Task 5]
4. The dispatch `tag` input reaching a `run:` script: asserted to appear only in `env:`. [Task 5]
5. The repo-only publish job or the rc setting leaking into a generated project (release.yml and release-please-config.json both ship): a `detect-package` guard and a `build/template-content` copy of the config; the smoke's drift check and a sync test fail otherwise. [Tasks 5, 6]

## File Structure

| File | Responsibility |
|---|---|
| `build/pack/Modulith.Templates.csproj`, `build/pack/Directory.Build.props`, `build/pack/PACKAGE-README.md` | The pack project (repo-only). |
| `build/scripts/release-package.sh` | `check-tag TAG [VERSION_PROPS]` and `pack VERSION OUTDIR`; used by the smoke and by `release.yml` (repo-only). |
| `build/scripts/tests/release-package.test.sh` | Tests of the script, the package contents, the workflow's shape and the config copy (repo-only; run by every full smoke). |
| `build/scripts/template-smoke.sh` | Gains `--from-package [PATH]`. |
| `.github/workflows/template-matrix.yml` | Gains the job `package-smoke`. |
| `.github/workflows/release.yml` | Gains `detect-package`, `publish-package`, a dispatch input. |
| `release-please-config.json`, `build/template-content/release-please-config.json` | The rc setting (root) and the plain copy that generated projects get. |
| `docs/adr/0037-publishing-the-template-package.md`, `docs/releasing.md`, README, `docs/repository-management.md`, `docs/blueprint-review.md`, `CLAUDE.md`, index rows | Documentation. |

---

### Task 1: The pack project and the engine excludes

**Files:**
- Create: `build/pack/Modulith.Templates.csproj`, `build/pack/Directory.Build.props`, `build/pack/PACKAGE-README.md`
- Modify: `.template.config/template.json` (root `sources.exclude`), `build/scripts/template-smoke.sh` (`forbidden` list)

**Interfaces:**
- Produces: `dotnet pack build/pack/Modulith.Templates.csproj -c Release -p:Version=X -o DIR` writes `DIR/Modulith.Backend.Templates.X.nupkg` holding `content/<every file of the checkout except the developer-only folders>`, `PACKAGE-README.md` at the package root, `PackageType` `Template`.

- [ ] **Step 1: Create the three files.** `Directory.Build.props` is `<Project><Import Project="$(MSBuildThisFileDirectory)..\..\Version.props" /></Project>`. The csproj has exactly these properties (values verified by the spike): `TargetFramework` `net10.0`, `PackageType` `Template`, `PackageId` `Modulith.Backend.Templates`, `Authors` `Modulith contributors`, `Description` (the root template's description), `PackageTags` `dotnet-new;templates;aspnetcore;modular-monolith;sql-server`, `PackageLicenseExpression` `MIT`, `PackageReadmeFile` `PACKAGE-README.md`, `PackageProjectUrl` `https://github.com/jackyjiale-oss/modulith-backend`, `RepositoryUrl` that URL plus `.git`, `RepositoryType` `git`, `IncludeBuildOutput` false, `IncludeContentInPack` true, `NoDefaultExcludes` true, `ContentTargetFolders` `content`, `EnableDefaultItems` false, `GenerateAssemblyInfo` false, `SuppressDependenciesWhenPacking` true, `NoWarn` `$(NoWarn);NU5128;NU5110;NU5111;CS2008`, and `RepoRoot` = `$([MSBuild]::EnsureTrailingSlash('$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)..\..'))'))`. Its `ItemGroup` has `<None Include="PACKAGE-README.md" Pack="true" PackagePath="\" />` and one `<Content Include="$(RepoRoot)**\*" Exclude="..." Pack="true" PackagePath="content\" />` whose `Exclude` lists, each prefixed with `$(RepoRoot)`: `.git`, `.git\**`, `**\bin\**`, `**\obj\**`, `.vs\**`, `.superpowers\**`, `.claude\**`, `docs\superpowers\**`, `artifacts\**`, `build\pack\**`, `coverage\**`, `**\TestResults\**`, `**\*.user`, `.env`. `PACKAGE-README.md` says in a few lines what the package is, how to install it (`dotnet new install Modulith.Backend.Templates`, and the prerelease form `Modulith.Backend.Templates::<version>`), how to generate (`dotnet new modulith-backend -n Acme.Hr`, `dotnet new modulith-module` inside a project) and links the repository.
- [ ] **Step 2: `git add` the three files** (the smoke's file-list check reads `git ls-files`, so untracked files are invisible to it), then run `bash build/scripts/template-smoke.sh --generate-only`; expected pass, not a failure: the file-list check only flags tracked files that are missing from the generated project, and these files are generated. The guard against that is the `forbidden` entries added in step 3, which fail the smoke if the generated project ever contains those paths.
- [ ] **Step 3: Add `build/pack/**` and `docs/releasing.md` to the root `sources.exclude`** (next to the other repo-only files), and `build/pack` and `docs/releasing.md` to the smoke's `forbidden` list. Re-run the generate-only smoke; expected `template-smoke: passed (generate only)`.
- [ ] **Step 4: Pack once** with `dotnet pack build/pack/Modulith.Templates.csproj -c Release -p:Version=0.0.0-test -o <scratch dir>` and confirm `Modulith.Backend.Templates.0.0.0-test.nupkg` is written with no warning other than the suppressed ones.
- [ ] **Step 5: Commit** `build(template): add the template package project`.

### Task 2: `release-package.sh` and its test

**Files:**
- Create: `build/scripts/release-package.sh`, `build/scripts/tests/release-package.test.sh`
- Modify: `.template.config/template.json` (exclude both), `build/scripts/template-smoke.sh` (`forbidden` list; add `release-package` to the repo-only test loop and its comment)

**Interfaces:**
- Produces: `bash build/scripts/release-package.sh check-tag TAG [VERSION_PROPS]` prints the version (the tag without its `v`) and exits 0 when `TAG` matches `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` and the `<Version>` element of `VERSION_PROPS` (default `Version.props` in the current directory) equals that version; exits 1 with a message on stderr otherwise; exits 2 for a missing argument or unknown command.
- Produces: `bash build/scripts/release-package.sh pack VERSION OUTDIR` validates `VERSION` against the same pattern without the `v`, runs `dotnet pack build/pack/Modulith.Templates.csproj -c Release -p:Version=VERSION -o OUTDIR` (plus the words of `$RELEASE_PACKAGE_PACK_ARGS`, a test hook), and prints the absolute path of the produced `.nupkg` as its last stdout line; exits 1 when no `.nupkg` results.
- Consumes: the pack project of Task 1. Pattern to follow for the test's helpers and output: `build/scripts/tests/module-template-files.test.sh`.

- [ ] **Step 1: Write the test first** (`release-package.test.sh`, prints `release-package tests passed`, non-zero exit on any failure) with these named cases, then run it; expected FAIL (no script):
  - `check-tag` prints `1.0.0` for `v1.0.0` and `1.0.0-rc.1` for `v1.0.0-rc.1` against a temp `Version.props` holding the same; handles CRLF line endings and spaces around the element text.
  - `check-tag` exits 1 for: `1.0.0`, `v1.0`, `v1.0.0 ` (trailing space), `v1.0.0;echo x`, `v1.0.0-rc.1$(id)`, the empty string; and for a mismatch (`v1.0.1` against `1.0.0`; `v1.0.0-rc.1` against `1.0.0`; `v1.0.0` against `1.0.0-rc.1`). It exits 2 with no argument.
  - `pack 1.2.3-rc.1 <tmp>` prints a path ending in `Modulith.Backend.Templates.1.2.3-rc.1.nupkg` that exists; `pack 1.2 <tmp>` exits non-zero.
  - Package contents (`unzip -Z1` on that file): contains `content/.github/workflows/ci.yml`, `content/.editorconfig`, `content/templates/modulith-module/.modulith-module`, `content/build/docker/Dockerfile`, `content/.template.config/template.json`, `PACKAGE-README.md`; contains every file of `git ls-files` except those under `docs/superpowers/` and `build/pack/` (missing ones are listed; untracked extras are ignored); contains nothing under `content/docs/superpowers/`, `content/build/pack/` or `content/.git/`.
- [ ] **Step 2: Implement `release-package.sh`** (`set -euo pipefail`; `check-tag` reads the `<Version>` element with awk or sed tolerant of CRLF and spaces; both commands validate before touching anything). Run the test; expected `release-package tests passed`.
- [ ] **Step 3: Wire it in**: add `build/scripts/release-package.sh` and `build/scripts/tests/release-package.test.sh` to the root excludes and the smoke's `forbidden` list; add `release-package` to the repo-only loop. Run `bash build/scripts/template-smoke.sh --generate-only`; expected pass.
- [ ] **Step 4: `bash build/scripts/project-index.sh --check`**; regenerate and commit the result if it fails.
- [ ] **Step 5: Commit** `ci(release): add the release-package script and its test`.

### Task 3: `template-smoke.sh --from-package`

**Files:**
- Modify: `build/scripts/template-smoke.sh` (usage text, parser, install step, one new check step)

**Interfaces:**
- Consumes: `release-package.sh pack` (Task 2).
- Produces: `--from-package [PATH]`: `PATH`, when given, is an absolute path to an existing `.nupkg` (the optional value is taken when the next argument does not start with `--`); without it the smoke packs the checkout with version `0.0.0-smoke` into `$tmp/pack` through `release-package.sh pack`. It composes with `--generate-only`, `--add-module` and the switches. The environment variable `RELEASE_PACKAGE_PACK_ARGS` reaches the pack unchanged (documented as a test hook in the usage comment).

- [ ] **Step 1: Parser, usage and validation**: a bad `PATH` (relative, missing, not `*.nupkg`) is a usage error (exit 2); update the header comment and `usage_error` text.
- [ ] **Step 2: Install from the package** instead of `dotnet new install .` when the flag is set (same `--force --debug:custom-hive "$hive"`); log which file was installed.
- [ ] **Step 3: Add the check step "Checking that the package and the checkout generate the same project"**, run when the flag is set, after the relative-link check and before the `--generate-only` exit: (a) `dotnet new list --debug:custom-hive "$hive"` shows both `modulith-backend` and `modulith-module`; (b) a second private hive `$tmp/hive-checkout` gets `dotnet new install .` from the checkout, the same project is generated with the same switches into `$tmp/compare/$project_name`, and `diff -r -I '<UserSecretsId>'` of it against `$out` is empty (any other difference fails and prints up to 20 file names); (c) `dotnet new modulith-module -n Billing --prefix "$project_name" --contracts true --allow-scripts no -o <dir> --debug:custom-hive <hive>` run against both hives exits 104 each and writes identical trees (`diff -r`). Use `fail`, so every problem prints before the script exits.
- [ ] **Step 4: Run positive cases**: `bash build/scripts/template-smoke.sh --from-package --generate-only` and the same with `--notifications false --includeSample false --caching memory`; expected `template-smoke: passed (generate only)` each.
- [ ] **Step 5: Run the negative case**: `RELEASE_PACKAGE_PACK_ARGS='-p:NoDefaultExcludes=false' bash build/scripts/template-smoke.sh --from-package --generate-only`; expected non-zero exit, with the output naming a missing dotfile (for example `.github/workflows/release.yml` or `.modulith-module`). Record both outcomes in the commit body.
- [ ] **Step 6: Commit** `ci(template): let the smoke test install the package`.

### Task 4: `package-smoke` on every pull request

**Files:**
- Modify: `.github/workflows/template-matrix.yml` (header comment, new job)

**Interfaces:**
- Produces: job `package-smoke`, legs `all-on` (no switches) and `all-off` (`--notifications false --includeSample false --caching memory`), check names `package-smoke (all-on)` and `package-smoke (all-off)`; same triggers as the other jobs; `ubuntu-latest`, `timeout-minutes: 20`.

- [ ] **Step 1: Add the job** after `module-template`, in the style of that job: checkout with `persist-credentials: false`, `setup-dotnet` with `global-json-file`, one step whose `env` carries `SWITCHES` and whose script runs `read -r -a args <<< "$SWITCHES"` then `bash build/scripts/template-smoke.sh --from-package --generate-only "${args[@]}"`. Extend the header comment: the job packs the checkout, installs the package and compares its output with the checkout's; it publishes nothing.
- [ ] **Step 2: Check the file**: `actionlint` runs in CI (job `actionlint`); run it locally only if it is already installed (do not download it). Confirm the YAML parses (`python -c "import yaml,sys; yaml.safe_load(open(sys.argv[1]))" .github/workflows/template-matrix.yml` if PyYAML is present).
- [ ] **Step 3: Commit** `ci: prove the template package on every pull request`.

### Task 5: The publish job in `release.yml`

**Files:**
- Modify: `.github/workflows/release.yml`, `build/scripts/tests/release-package.test.sh` (workflow-shape cases)

**Interfaces:**
- Consumes: `release-package.sh check-tag` and `pack` (Task 2), `template-smoke.sh --from-package PATH --generate-only` (Task 3).
- Produces: `workflow_dispatch` with a required string input `tag`; job `detect-package` (output `has_pack`, true when `build/pack/Modulith.Templates.csproj` exists, so generated projects, which lack `build/pack`, skip publishing); job `publish-package` in the environment `nuget`.

- [ ] **Step 1: Write the static cases first** in `release-package.test.sh`, then run it; expected FAIL. They read `.github/workflows/release.yml` with awk/grep and assert: the `publish-package` job block contains `environment: nuget`, `id-token: write`, `NuGet/login@v1`, `--skip-duplicate`, `--clobber`, `release-package.sh check-tag` and `needs:` naming `detect-package`; every line of the file containing `inputs.tag` is an `env:` assignment (no `${{ inputs.tag }}` inside a `run:` block); the `release-please` job has `if: github.event_name == 'push'`; no job other than `publish-package` has `id-token: write`.
- [ ] **Step 2: Edit `release.yml`**: (a) `on:` gains `workflow_dispatch: inputs: tag: {description: "Existing release tag (vX.Y.Z) to publish again", required: true, type: string}`; (b) the `release-please` job gets `if: github.event_name == 'push'`; (c) add `detect-package` (a step with `if [ -f build/pack/Modulith.Templates.csproj ]` writing `has_pack` to `$GITHUB_OUTPUT`, after a checkout); (d) add `publish-package`:

```yaml
  publish-package:
    name: publish-package
    needs: [release-please, detect-package]
    if: ${{ !cancelled() && needs.detect-package.outputs.has_pack == 'true' && (needs.release-please.outputs.release_created == 'true' || github.event_name == 'workflow_dispatch') }}
    runs-on: ubuntu-latest
    timeout-minutes: 20
    environment: nuget
    permissions:
      contents: write
      id-token: write
    env:
      TAG: ${{ needs.release-please.outputs.tag_name || inputs.tag }}
```

  with these steps in order: checkout `ref: refs/tags/${{ env.TAG }}` (`persist-credentials: false`; a bare name could resolve to a branch); `setup-dotnet` (`global-json-file`); `Check the tag` (`id: version`, `run`: `version="$(bash build/scripts/release-package.sh check-tag "$TAG")"` followed by `echo "version=$version" >> "$GITHUB_OUTPUT"`, so a failing check fails the step); `Pack` (`env: VERSION: ${{ steps.version.outputs.version }}`; the script begins with `set -o pipefail`, then `PACKAGE=$(bash build/scripts/release-package.sh pack "$VERSION" "$RUNNER_TEMP/package" | tail -n 1)` written to `$GITHUB_ENV` as `PACKAGE`); `Prove the package` (`bash build/scripts/template-smoke.sh --from-package "$PACKAGE" --generate-only`); `Attach to the release` (`env: GH_TOKEN: ${{ github.token }}`, `gh release upload "$TAG" "$PACKAGE" --clobber`); `NuGet login` (`id: login`, `uses: NuGet/login@v1`, `with: user: ${{ secrets.NUGET_USER }}`); `Push to nuget.org` (`env: NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}`, `dotnet nuget push "$PACKAGE" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate`). Update the file's header comment (the third job list) to describe the new job and the dispatch recovery.
- [ ] **Step 3: Run the test; expected `release-package tests passed`.** Run `bash build/scripts/template-smoke.sh --generate-only`; expected pass (the generated `release.yml` is the same file; the smoke's `.template.config` rule is not triggered because the guard names `build/pack`).
- [ ] **Step 4: Commit** `ci(release): publish the package from the release workflow`.

### Task 6: The release-candidate setting, without leaking it

**Files:**
- Modify: `release-please-config.json`, `.template.config/template.json` (root excludes: add `release-please-config.json`), `build/scripts/tests/release-package.test.sh` (sync case)
- Create: `build/template-content/release-please-config.json`

**Interfaces:**
- Produces: the root config carries `"versioning": "prerelease"`, `"prerelease": true`, `"prerelease-type": "rc"`, each on its own line, placed after `"bump-minor-pre-major": true,` with trailing commas, so removing those three lines leaves valid JSON; `build/template-content/release-please-config.json` is the current config unchanged (what generated projects get; the engine's second source replaces the root file, as for `Version.props`).

- [ ] **Step 1: Write the sync case first** in `release-package.test.sh`: the root config with its `versioning`, `prerelease` and `prerelease-type` lines deleted (`grep -vE`) equals the `build/template-content` copy byte for byte; both are valid JSON (`python -m json.tool`); then run the test; expected FAIL.
- [ ] **Step 2: Create the copy** (verbatim current content), **add the three keys** to the root file, **add the root exclude**. Run the test; expected pass.
- [ ] **Step 3: `bash build/scripts/template-smoke.sh --generate-only`**; expected pass (its drift check compares the generated config with the template-content copy, and a generated project has no `prerelease` key: assert that with a `grep -c prerelease` of the generated file equal to 0 in the smoke's "Checking the generated files" step, next to the `Version.props` checks).
- [ ] **Step 4: Commit** `ci(release): publish release candidates until 1.0`.
- Note for the reader of the PR: whether release-please then proposes `1.0.0-rc.1` cannot be proven here; Task 10 reads the proposal on the real release PR. The fallback is a per-package `release-as` of `1.0.0-rc.1` in the root config only (and its removal after that release).

### Task 7: Documentation

**Files:**
- Create: `docs/adr/0037-publishing-the-template-package.md`, `docs/releasing.md`
- Modify: `README.md`, `docs/repository-management.md`, `docs/blueprint-review.md`, `docs/README.md`, `docs/architecture/overview.md`, `CLAUDE.md` (repo root), `docs/superpowers/index/MAP.md` (generated)

**Interfaces:**
- Consumes: every earlier task's names (script commands, job names `package-smoke`, `publish-package`, `detect-package`, environment `nuget`, secret `NUGET_USER`, package ID).

- [ ] **Step 1: ADR 0037** in the format of ADR 0036 (Status, Date 2026-11-16, Deciders, Context, Options considered, Decision, Consequences, Follow-ups): the decision of spec sections 2, 4, 5 and 6 (nuget.org by trusted publishing and a release asset; one package; `NoDefaultExcludes` and the folder-only package path, with why; equivalence proven by `--from-package`; the `nuget` environment gate and what it is not; the rc proving; `detect-package` and the config copy so nothing leaks into generated projects), the options not chosen (GitHub Packages, a hand-written nuspec, a manual-dispatch prerelease), the costs (the package re-reads the whole tree, a published version is permanent, one maintainer so the gate is a pause), and the follow-ups. No markdown link to `docs/releasing.md`; name it in plain text.
- [ ] **Step 2: `docs/releasing.md`** (repo-only) with these sections: the one-time setup checklist for the owner (nuget.org account; the trusted-publishing policy with owner `jackyjiale-oss`, repository `modulith-backend`, workflow file `release.yml`, environment `nuget` and a scope that allows new packages for `Modulith.Backend.Templates`; the GitHub environment `nuget` with the owner as required reviewer; the environment secret `NUGET_USER` holding the nuget.org profile name, not the email address; optional `Modulith.` ID-prefix reservation); how a release flows (release PR, tag, GitHub Release, the pending approval under Actions, Review deployments); cutting a release candidate; the v1.0 cut procedure as a written checklist, not executed (the open decisions of the user's notes, removing the three keys from the root config so it equals the template-content copy, merging the release PR, approving); recovering a failed publish with `gh workflow run release.yml -f tag=vX.Y.Z --ref main`; unlisting a bad version on nuget.org (never deleted) and shipping a fixed one; what is irreversible; that self-review is allowed so the gate is a pause, not four eyes.
- [ ] **Step 3: README**: replace the "Once Plan 7 publishes the package" paragraph with text that is true at merge: the release workflow publishes the package (see `docs/releasing.md`), and until the first release candidate is out the checkout route above is the way to install; no NuGet badge yet.
- [ ] **Step 4: Rows and notes**: `docs/repository-management.md` (the "Package (Plan 7)" row now names nuget.org and `release.yml`; the `nuget` environment and the `NUGET_USER` secret; the `package-smoke` checks, "not yet required, to be added after their first green run on `main`"); `docs/blueprint-review.md` row 7c (what is built, the spec and plan links, "done when merged; first publish pending the owner's setup"); the ADR row in `docs/README.md` and `docs/architecture/overview.md`; one line in the repository `CLAUDE.md` "Maintaining the template": after changing `build/pack/` or the package excludes run `bash build/scripts/template-smoke.sh --from-package --generate-only`.
- [ ] **Step 5: `bash build/scripts/project-index.sh`**, commit its result, then `--check` exits 0. Run `bash build/scripts/template-smoke.sh --generate-only`; expected pass (links in shipped docs resolve).
- [ ] **Step 6: Commit** `docs: document publishing the template package`.

### Task 8: Whole-branch verification

- [ ] **Step 1:** `bash build/scripts/template-smoke.sh --from-package --generate-only` with the default switches and with `--notifications false --includeSample false --caching memory`; expected pass for both.
- [ ] **Step 2:** one full run on a project generated from the package, in the background (about 25 minutes, Docker is needed for the integration part): `bash build/scripts/template-smoke.sh --from-package --integration`; expected `template-smoke: passed`.
- [ ] **Step 3:** `bash build/scripts/project-index.sh --check` exits 0; `git status` is clean; `git log --oneline origin/main..HEAD` lists the spec, plan and task commits only.

### Task 9: Handover

- [ ] **Step 1:** ask the user before pushing `docs/package-publish-spec` and opening the PR (spec, plan and implementation go up together).
- [ ] **Step 2:** on the PR, confirm `package-smoke (all-on)`, `package-smoke (all-off)`, `actionlint`, `project-index`, `template-smoke`, the five `template-matrix` legs and both `module-template` legs are green; read the logs of any failure before changing code. Use the ccd_pr tools after opening the PR; never poll CI.
- [ ] **Step 3:** tell the user which checks the owner should add to branch protection once they have run green on `main` (`package-smoke (all-on)`, `package-smoke (all-off)`); never change protection myself.

### Task 10: The first release candidate (after merge, with the user)

- [ ] **Step 1 (the user):** complete the setup checklist in `docs/releasing.md`.
- [ ] **Step 2:** read what release-please proposes on the updated release PR #8 (read-only). If it is not `1.0.0-rc.1`, add the per-package `release-as` fallback in a small PR; do not merge the release PR until the proposal is right.
- [ ] **Step 3 (the user):** merge the release PR; approve the pending `nuget` environment deployment.
- [ ] **Step 4:** verify the package on nuget.org and, on a machine or hive without a checkout, `dotnet new install Modulith.Backend.Templates::1.0.0-rc.1` then `dotnet new modulith-backend -n Acme.Smoke` and a build. Then open a small docs PR: the README install section (stable and `::version` forms, update, uninstall) and the NuGet badge.
- [ ] **Step 5:** clean up: remove worktree `C:\w\c7`, the local and remote branches (on the user's word), and `C:\w\c7-spike`.
