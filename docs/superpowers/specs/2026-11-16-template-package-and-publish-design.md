# Template package and publish (7c) design

Status: draft for review, 2026-11-16. Row 7c of `docs/blueprint-review.md`; blueprint section 15.2. Builds on 6 (delivery pipeline), 5c (versioning, release-please), 7a (switches) and 7b (module item template, ADR 0036).

## 1. Goal

`dotnet new install Modulith.Backend.Templates` works for anyone, with no checkout, and installs both templates (`modulith-backend`, `modulith-module`). Every release of the repository publishes that package through the release workflow, after a human approves the one irreversible step. The first published version is a release candidate, so the pipeline is proven on nuget.org before anything claims 1.0.

## 2. Decisions already made (the user, 2026-11-16)

| Question | Decision |
|---|---|
| Where to publish | nuget.org, and the `.nupkg` attached to the GitHub Release |
| Does 7c cut v1.0 | No. 7c builds and proves the pipeline with a prerelease; v1.0 is cut later, by a written procedure. Release PR #8 stays unmerged until the user decides 1.0 is ready |
| nuget.org authentication | Trusted publishing (OIDC): no long-lived key. Needs a one-time setup on nuget.org that only the user can do |

Facts checked while designing: `Modulith.Backend.Templates` and `Modulith.Module.Templates` are both unused on nuget.org. A different package named `Modulith` (another author's template) exists; our short names (`modulith-backend`, `modulith-module`) do not collide with it. A trusted-publishing policy binds to the repository owner, the repository, the workflow file name and an optional environment, and carries a scope that can allow publishing new packages, so the policy can create the package and the first publish needs no API key. Keys it issues last one hour, one per token; the repository is public, so the 7-day activation window of private repositories does not apply.

## 3. Scope

In: the pack project, a `--from-package` mode of `template-smoke.sh`, a pull-request check that proves the package, the publish job in `release.yml` with its approval gate and a recovery input, the prerelease setting of release-please, ADR 0037, `docs/releasing.md`, the README install section, the package README, and the doc rows that name the package.

Out: package signing beyond what nuget.org does, a GitHub Packages mirror, an SBOM for the package, updating projects already generated from the template, and the other roadmap plans (push/SMS, phone OTP, production hardening). Cutting v1.0 is not part of this plan; only its procedure is.

## 4. The package

One package, `Modulith.Backend.Templates`, holds both templates. The module template keeps its own template identity (`Modulith.Module.Templates`) and is not a second package, so one ID is needed.

`build/pack/Modulith.Templates.csproj` is a pack-only project (`PackageType` `Template`, no build output, content under `content/`). It is not in `TemplateName.slnx`, and `build/pack/` carries its own empty `Directory.Build.props`, so the repository's `net10.0`, warnings-as-errors and lock-file settings do not apply to it. It imports the repository's `Version.props`, so the product version stays in one file. Metadata: authors `Modulith contributors`, licence expression `MIT`, the repository URL, tags, and a short readme (`build/pack/PACKAGE-README.md`) shown on nuget.org.

Content is the checkout tree minus folders that are only for developing the template: `.git`, `bin`, `obj`, `.vs`, `.superpowers`, `.claude`, `docs/superpowers`, `artifacts`, `build/pack`, test output and `.env`. The pack project does not copy the engine's exclude list from `.template.config/template.json`; the engine still applies that list when a project is generated, so the two cannot drift. `build/pack/**` and `docs/releasing.md` are added to the engine's excludes (repository-only files, like `docs/repository-management.md`).

## 5. How the package is proven

`template-smoke.sh` gains `--from-package [PATH]`. Without a path it packs the checkout (version `0.0.0-smoke`) into the scratch folder; with a path it uses that file. It installs the `.nupkg` into the private template hive instead of the folder, and then runs the existing generation checks unchanged (placeholders, required files, anchors, dead references, links). Then it proves equivalence:

1. Both short names are listed by `dotnet new list` from the package install.
2. A project generated from the package install and one generated from a checkout install, with the same switches, are identical (`diff -r`). The per-generation `UserSecretsId` GUID is normalised first; any other difference fails and names the file.
3. The module template, generated with `--allow-scripts no` from both installs (the 7b files test already relies on exit 104 and the files written), writes identical files.

If the trees are identical, builds and tests are identical, so the full build and test run already done by the existing legs does not need repeating for the package. `--from-package` composes with `--generate-only`, and the release job uses that pair.

CI: a job `package-smoke` in `.github/workflows/template-matrix.yml` (repository-only file) runs `--from-package --generate-only` on every pull request, push to `main` and manual run. It publishes nothing. It joins the required checks only after it has run green on `main`, as `template-matrix` did.

## 6. Release workflow

`release.yml` gains the job `publish-package`, after `release-please`, only when a release was created, in the GitHub environment `nuget`:

1. Check out the release tag (no persisted credentials).
2. Assert the tag without its `v` equals `<Version>` in `Version.props` at that tag; fail otherwise.
3. `dotnet pack` with that version.
4. `template-smoke.sh --from-package <that file> --generate-only` on the exact file to be published.
5. Upload the `.nupkg` to the GitHub Release (`gh release upload --clobber`).
6. `NuGet/login@v1` (needs `id-token: write`; the nuget.org profile name is the secret `NUGET_USER`), then `dotnet nuget push --skip-duplicate`. This is last: everything that can fail comes before it, and a re-run cannot double-publish.

Recovery: `release.yml` gains `workflow_dispatch` with an input `tag`. A dispatch skips `release-please` and runs `publish-package` for that existing release. The tag reaches the shell only through `env`, validated against `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` before use, as the matrix workflow passes its inputs.

Approval gate: the `nuget` environment lists the repository owner as required reviewer. Merging a release PR creates the tag and the GitHub Release, but nothing reaches nuget.org until the reviewer approves the job. Self-review stays allowed (there is one maintainer), so this is a deliberate pause before an irreversible step, not a four-eyes control; the runbook says so. The trusted-publishing policy is bound to repository `jackyjiale-oss/modulith-backend`, workflow `release.yml` and environment `nuget`.

What a failure leaves behind: pack, assert, smoke or upload failing leaves the GitHub Release without a package (re-run the job, or dispatch the tag). A failed push leaves the release with the asset and nothing on nuget.org (dispatch the tag). Nothing runs after a successful push. A bad published version can be unlisted on nuget.org but never deleted; the runbook covers unlisting and shipping a fixed version.

## 7. Versioning and the prerelease

`release-please-config.json` gains `"prerelease": true` and `"prerelease-type": "rc"`, so the next release PR proposes `1.0.0-rc.1`. The plan checks the proposal on the updated release PR before anything is merged; if release-please proposes another version, `release-as` is the fallback. Dropping the two keys later gives the 1.0.0 cut. A prerelease is installed with `dotnet new install Modulith.Backend.Templates::1.0.0-rc.1`; the plan confirms that form works on the pinned SDK and records the exact command in the README.

## 8. Documentation

- ADR 0037, "Publishing the template package": nuget.org by trusted publishing, one package, the release asset, the rc proving, the environment gate, what was not chosen (GitHub Packages, a hand-written nuspec, a manual-dispatch prerelease), and the costs.
- `docs/releasing.md` (repository-only): the one-time setup checklist for the user (nuget.org account, trusted-publishing policy fields, the `nuget` environment, the `NUGET_USER` secret, optional `Modulith.` prefix reservation); cutting an rc; the v1.0 cut procedure as a checklist, written and not executed; recovery by dispatch; unlisting a version; what is irreversible.
- README: the "once Plan 7 publishes" paragraph becomes install, update and uninstall commands (the stable form and the prerelease form); the checkout route stays for contributors. The NuGet badge is added after the first publish.
- `docs/repository-management.md`: the "Package (Plan 7)" row, the `nuget` environment, the `NUGET_USER` secret, the new check. `docs/blueprint-review.md`: row 7c. `docs/README.md` and `docs/architecture/overview.md`: the ADR row. `docs/superpowers/index/MAP.md` by `build/scripts/project-index.sh`.

## 9. Risks, checked first by a spike

- Dotfiles (`.github`, `.editorconfig`, `.gitkeep`, `.gitignore`) must survive the pack globs; the equivalence diff catches a loss.
- Windows path length when the engine unpacks the package under the user profile. The spike measures the longest path on this machine and the result goes into the spec before the plan.
- Package size against nuget.org limits.
- release-please prerelease behaviour with `release-type: simple` and a manifest (section 7).
- Whether the `Template` pack project is picked up by `dotnet build`, `dotnet format`, CodeQL or Dependabot at the repository root.

## 10. Acceptance

1. `bash build/scripts/template-smoke.sh --from-package --generate-only` passes locally, with identical trees and both short names present, for the default switches and for `--notifications false --includeSample false --caching memory`.
2. One full default smoke run on a project generated from the package passes (a one-off check that nothing needed at build time was lost).
3. `package-smoke` and `actionlint` are green on the pull request; the other existing checks stay green; `project-index` is regenerated.
4. The docs above exist and agree with each other; `dead-reference-check` and the smoke's documentation checks pass.
5. After merge, and after the user completes the setup checklist: the rc release PR is merged, the user approves the `nuget` environment, the package appears on nuget.org, and a clean `dotnet new install Modulith.Backend.Templates::1.0.0-rc.1` followed by `dotnet new modulith-backend -n Acme.Smoke` builds on a machine that has no checkout.

Criterion 5 cannot be proven before the user's one-time setup; the plan ends with it as the user's checklist.

## 11. Review focus

Inputs and conditions most likely to bite, each with the expected behaviour:

1. A release tag whose version differs from `Version.props` (a hand-edited tag or a prerelease suffix mismatch): the job fails before packing.
2. A re-run after a partial failure (the asset already attached, the package already pushed): the re-run succeeds and changes nothing.
3. A dispatch `tag` input that is not a release tag or carries shell text: rejected before any use.
4. A new file in the repository that the pack globs miss or wrongly include: the equivalence diff fails and names it.
5. A reviewer who never approves the `nuget` environment: the release stays a GitHub Release without a package, and the docs say how to find and approve it.
