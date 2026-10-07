# Repository Management

How the Modulith GitHub repository is configured and run. This file is about the **template repository only** and is not copied into generated projects.

| | |
|---|---|
| Repository | <https://github.com/jackyjiale-oss/modulith-backend> |
| Clone | `git clone https://github.com/jackyjiale-oss/modulith-backend.git` |
| Visibility | Public |
| Default branch | `main` (protected) |
| Licence | MIT ([`LICENSE`](../LICENSE)) |
| Package (Plan 7) | `Modulith.Backend.Templates` → `dotnet new modulith-backend` |

## 1. Commit identity

Commits in this public repository use the maintainer's GitHub **noreply** address, so no work email is published. It is set per repository, so global git config is untouched:

```bash
git config user.name "jackyjiale-oss"
git config user.email "272155658+jackyjiale-oss@users.noreply.github.com"
```

Also tick **Settings → Emails → "Block command line pushes that expose my email"** on github.com. That way a commit made with the wrong identity is rejected instead of published.

## 2. Repository settings

Applied by [`build/scripts/configure-github-repo.sh`](../build/scripts/configure-github-repo.sh) (GitHub CLI). The script is idempotent; `--dry-run` prints what it would do.

| Setting | Value | Why |
|---|---|---|
| Description / topics | "Modulith Backend: a production-ready ASP.NET Core 10 modular-monolith template"; `dotnet`, `aspnetcore`, `csharp`, `modular-monolith`, `clean-architecture`, `dotnet-template`, `template`, `minimal-api` | Discoverability |
| Merge methods | **Squash only**; merge commits and rebase merges off | One Conventional Commit per PR on `main`, which feeds release-please |
| Squash commit message | title = **PR title**, body = PR body | The CI-checked PR title becomes the commit header and changelog entry |
| Auto-delete head branches | on | No stale branches |
| Auto-merge, "update branch" button | on | Merge as soon as checks pass |
| Wiki, Projects | off | Docs live in `docs/`; one source of truth |
| Dependabot alerts + security updates | on | Vulnerable dependency PRs (version updates come from `.github/dependabot.yml`) |
| Secret scanning + push protection | on | Blocks pushes that contain credentials |
| Private vulnerability reporting | on | Matches [`SECURITY.md`](../SECURITY.md) |

## 3. Branch protection on `main`, in phases

Required status checks must already exist (have run at least once), or GitHub blocks every merge. Protection is therefore tightened as the plan delivers CI:

| Phase | When | Command | Adds |
|---|---|---|---|
| 0 | now (repo bootstrap) | `bash build/scripts/configure-github-repo.sh` | PR required; 0 approvals (solo maintainer: GitHub doesn't let you approve your own PR); linear history; no force pushes or deletions; conversations must be resolved |
| 1 | after plan Task 14 is merged | `… --checks build-test,template-smoke,commit-lint` | required CI checks, branch must be up to date |
| 2 | after plan Task 17 is merged | `… --checks build-test,template-smoke,commit-lint,licenses` | licence check |
| 3 | when a second maintainer joins | `… --checks … --approvals 1`, plus a `.github/CODEOWNERS` file | human review |

`enforce_admins` stays **off** so the owner can repair a broken `main` in an emergency. Use it only for that.

## 4. Day-to-day workflow

- Every change goes through a branch and a PR ([CONTRIBUTING.md](../CONTRIBUTING.md)). Plan execution works **one task = one branch = one PR**. Branches are named after the task, e.g. `feature/task-01-repository-foundation`. The PR title is the task's commit header, and it's squash-merged when CI is green.
- Releases: release-please keeps a `chore(release): release x.y.z` PR open. Merging it tags `vx.y.z`, writes `CHANGELOG.md` and creates the GitHub Release (plan Task 17).
- Dependabot PRs: let CI run, check the changelog for breaking changes, and squash-merge with the Dependabot title (`chore(deps): …`).

## 5. Later (Plan 6)

`staging` and `production` environments with required reviewers, OIDC to the cloud (no long-lived secrets), CodeQL, and gitleaks.
