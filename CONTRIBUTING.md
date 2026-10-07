# Contributing

How changes get from your machine to `main`: branches, commit messages and pull requests. Code naming rules are in [`docs/coding-conventions.md`](docs/coding-conventions.md).

## One-time setup

```bash
git config core.hooksPath .githooks
```

This turns on the `commit-msg` hook, which rejects commit messages that break the rules below before they're created. CI runs the same check (`build/scripts/check-commit-msg.sh`) on every pull-request title.

---

## 1. Branches

- **Trunk-based.** `main` is always releasable and protected: PR required, CI green, no force push, squash merge only. One approving review is required once the repository has two or more maintainers (GitHub does not let authors approve their own PRs).
- Branch names are `{type}/{short-kebab-description}`, optionally starting the description with an issue number:

| Type | Use for | Example |
|---|---|---|
| `feature/` | new behavior | `feature/leave-cancellation` |
| `fix/` | bug fixes | `fix/123-refresh-token-reuse` |
| `refactor/` | behavior-preserving changes | `refactor/split-outbox-dispatcher` |
| `docs/` | documentation only | `docs/runbook-key-rotation` |
| `test/` | tests only | `test/idempotency-race` |
| `ci/` | pipelines and build scripts | `ci/template-matrix` |
| `chore/` | dependencies, tooling, housekeeping | `chore/bump-ef-core` |

- Keep branches short-lived (aim for under 3 days) and rebase on `main` rather than merging `main` in.

---

## 2. Commit messages: Conventional Commits 1.0.0

### 2.1 Format

```
<type>(<scope>)!: <subject>
<blank line>
<body>
<blank line>
<footer(s)>
```

- `(<scope>)` is optional; `!` marks a breaking change.
- **Header (first line) ≤ 72 characters.**
- **Subject:** imperative mood ("add", not "added" or "adds"), starts with a lower-case letter, no trailing period. It completes the sentence *"If applied, this commit will …"*.
- **Body (optional):** explains *what* and *why*, not *how*; wrap at 72 characters.
- **Footers (optional):** `BREAKING CHANGE: <description>`, `Refs: #123`, `Closes #123`, `Co-Authored-By: Name <email>`.
- One logical change per commit. Every commit that reaches `main` builds and passes tests (squash merge guarantees this).

### 2.2 Types

| Type | Meaning | Version bump |
|---|---|---|
| `feat` | a new capability for users of the API or template | minor |
| `fix` | a bug fix | patch |
| `perf` | a performance improvement without behavior change | patch |
| `refactor` | code change that neither fixes a bug nor adds a feature | — |
| `test` | adding or correcting tests only | — |
| `docs` | documentation only | — |
| `build` | build system, MSBuild props, packages | — |
| `ci` | GitHub Actions and CI scripts | — |
| `chore` | maintenance that fits nothing above | — |
| `style` | formatting only (no code meaning change) | — |
| `revert` | reverts a previous commit | depends |

`!` after the type/scope, or a `BREAKING CHANGE:` footer, means a **major** version bump.

### 2.3 Scopes

A scope is the area the change lives in. Use one from this list, or leave it out for repo-wide changes. To add a module scope, add it to `ALLOWED_SCOPES` in `build/scripts/check-commit-msg.sh` in the same PR that adds the module.

| Group | Scopes |
|---|---|
| Building blocks | `shared-kernel`, `application`, `infrastructure`, `web`, `host` |
| Cross-cutting | `persistence`, `outbox`, `idempotency`, `observability`, `security`, `i18n`, `architecture` |
| Modules | `sample`, `auth`, `notifications`, `audit` |
| Repository | `ci`, `deps`, `template`, `docker`, `adr`, `release` |

### 2.4 Examples

| ✅ Good | ❌ Bad | Why it's bad |
|---|---|---|
| `feat(sample): add leave request cancellation` | `Added cancel feature` | no type; past tense; capitalized |
| `fix(outbox): release lease when a handler throws` | `fix: bug fix` | says nothing |
| `refactor(persistence): extract UTC converter` | `refactor(persistence): Extract UTC converter.` | capitalized subject; trailing period |
| `feat(auth)!: require MFA for admin roles` | `feat(auth): require MFA for admin roles (BREAKING)` | breaking change not marked with `!` / footer |
| `chore(deps): bump Serilog to 4.3.0` | `update packages` | no type; unclear |
| `test(idempotency): cover concurrent duplicate keys` | `wip` | not descriptive; WIP never reaches `main` |

Breaking change with body and footer:

```
feat(auth)!: return 403 instead of 401 for inactive accounts

Clients could not tell "wrong password" from "account disabled",
which caused support tickets. Inactive accounts now get 403 with
code auth.account_inactive.

BREAKING CHANGE: clients must handle 403 on POST /api/v1/auth/login.
Refs: #142
```

Revert:

```
revert: feat(sample): add leave request cancellation

This reverts commit 3f2c1ab.
```

### 2.5 Validation rule

The hook and CI apply this check (source of truth: `build/scripts/check-commit-msg.sh`):

```
^(feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert)(\((<allowed-scope>)\))?!?: [^A-Z\s].*[^.\s]$
```

They also check that the header is at most 72 characters. Locally the hook lets through `Merge …`, `fixup! …` and `squash! …` messages; they disappear when the PR is squash-merged.

---

## 3. Pull requests

- **PR title = the squash-merge commit header**, so it must follow Section 2. CI checks it.
- One concern per PR. Big features land as a stack of small PRs.
- Fill in `.github/pull_request_template.md`: tests added, migration reviewed, ADR updated, no secrets, breaking API change noted, conventions followed.
- Merge requires CI green (plus one approval once there are two or more maintainers). Merge with **squash**: the PR title becomes the commit header. The branch is deleted automatically.

## 4. Documentation is part of the change

Update the matching document **in the same PR** when you change any of these:

| You changed… | Update |
|---|---|
| an endpoint, error code, event, table, configuration key or background job of a module | `docs/modules/{module}.md` |
| a building block (SharedKernel, Application.Common, Infrastructure.Common, Web.Common) | `docs/building-blocks/{name}.md` |
| host pipeline, configuration sections, health checks, hosting | `docs/services/api.md` |
| module boundaries or cross-module flows | `docs/architecture/overview.md` |
| a significant or hard-to-reverse decision | a new `docs/adr/NNNN-*.md` |
| how agents should work in this repo | `CLAUDE.md` |

A new module gets `docs/modules/{module}.md` copied from `docs/modules/_template.md`. Tests fail when a module or building block has no document, a module document misses a required section, or an error code or endpoint isn't documented.

## 5. Changelog, versioning and releases

- **Don't edit `CHANGELOG.md` by hand.** release-please generates it from the Conventional Commits on `main`, which is why commit and PR titles matter. You may polish wording inside the release PR.
- On every push to `main`, release-please updates a PR titled `chore(release): release <version>`. Merging it tags `v<version>`, updates `CHANGELOG.md`, and bumps `<Version>` in `Directory.Build.props`. Add a repository secret `RELEASE_PLEASE_TOKEN` (a fine-grained token for this repository only, with Contents and Pull requests read/write). Without it, release PRs are opened by `GITHUB_TOKEN` and CI does not run on them.
- SemVer: `feat` → minor, `fix`/`perf` → patch, `!` or `BREAKING CHANGE:` → major (Section 2.2). Database schema history is the EF migrations listed in each module document.
- Release checklist: the `ms` and `zh-Hans` error messages are drafts until a native speaker has reviewed them. Before a release, have every `*.ms.resx` and `*.zh-Hans.resx` entry still commented `Draft – needs native review` reviewed, and remove the comment from each entry that passes.

## 6. Licences

- This repository is MIT-licensed (`LICENSE`). Projects generated from the template don't receive this licence; their owners choose their own.
- New NuGet dependencies must have a licence in `build/licenses/allowed-licenses.json` (MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause). CI fails otherwise. To allow another licence, add an ADR and update the allow-list in the same PR.
- `THIRD-PARTY-NOTICES.md` is generated at release time.
