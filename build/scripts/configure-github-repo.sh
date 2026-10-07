#!/usr/bin/env bash
# Applies the repository settings documented in docs/repository-management.md.
# Requires the GitHub CLI (https://cli.github.com), authenticated as a repo admin: gh auth login
set -euo pipefail

REPO="jackyjiale-oss/modulith-backend"
BRANCH="main"
CHECKS=""
APPROVALS=0
DRY_RUN=false

usage() {
  echo "usage: $0 [--repo owner/name] [--checks job1,job2] [--approvals n] [--dry-run]"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --checks) CHECKS="$2"; shift 2 ;;
    --approvals) APPROVALS="$2"; shift 2 ;;
    --dry-run) DRY_RUN=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage; exit 1 ;;
  esac
done

[[ "$APPROVALS" =~ ^[0-9]+$ ]] || { echo "--approvals must be a number"; exit 1; }

run() {
  if $DRY_RUN; then printf '[dry-run] %s\n' "$*"; else "$@"; fi
}

if ! $DRY_RUN; then
  command -v gh >/dev/null || { echo "gh not found: install it from https://cli.github.com"; exit 1; }
  gh auth status >/dev/null
fi

echo "== General settings ($REPO)"
run gh repo edit "$REPO" \
  --description "Modulith Backend: a production-ready ASP.NET Core 10 modular-monolith template" \
  --add-topic dotnet --add-topic aspnetcore --add-topic csharp --add-topic modular-monolith \
  --add-topic clean-architecture --add-topic dotnet-template --add-topic template --add-topic minimal-api \
  --enable-issues --enable-wiki=false --enable-projects=false \
  --enable-squash-merge --enable-merge-commit=false --enable-rebase-merge=false \
  --delete-branch-on-merge --enable-auto-merge --allow-update-branch
run gh api -X PATCH "repos/$REPO" --silent \
  -f squash_merge_commit_title=PR_TITLE -f squash_merge_commit_message=PR_BODY

echo "== Security"
run gh api -X PUT "repos/$REPO/vulnerability-alerts" --silent
run gh api -X PUT "repos/$REPO/automated-security-fixes" --silent
run gh api -X PUT "repos/$REPO/private-vulnerability-reporting" --silent
run gh api -X PATCH "repos/$REPO" --silent \
  -f 'security_and_analysis[secret_scanning][status]=enabled' \
  -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled'

echo "== Branch protection ($BRANCH)"
if [[ -n "$CHECKS" ]]; then
  contexts=$(printf '%s' "$CHECKS" | awk -F, '{ for (i = 1; i <= NF; i++) printf "%s\"%s\"", (i > 1 ? "," : ""), $i }')
  status_checks="{\"strict\":true,\"contexts\":[${contexts}]}"
else
  status_checks="null"
fi

body="{
  \"required_status_checks\": ${status_checks},
  \"enforce_admins\": false,
  \"required_pull_request_reviews\": { \"required_approving_review_count\": ${APPROVALS}, \"dismiss_stale_reviews\": true },
  \"restrictions\": null,
  \"required_linear_history\": true,
  \"allow_force_pushes\": false,
  \"allow_deletions\": false,
  \"required_conversation_resolution\": true
}"

if $DRY_RUN; then
  echo "[dry-run] gh api -X PUT repos/$REPO/branches/$BRANCH/protection --input -"
  echo "$body"
else
  printf '%s' "$body" | gh api -X PUT "repos/$REPO/branches/$BRANCH/protection" --input - --silent
fi

echo "Done."
