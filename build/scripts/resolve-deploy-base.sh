#!/usr/bin/env bash
# Prints the commit that production last ran: the SHA of the newest deployment to the `production` environment that ever had a
# `success` status (maintenance detection design, section 3.1). detect-contract-migration.sh compares a release with it, so a
# contract release that never reached production cannot hide from the next one.
#
# Usage: bash build/scripts/resolve-deploy-base.sh [repository]   (run inside the git work tree)
#   repository is owner/name and defaults to $GITHUB_REPOSITORY. The GitHub CLI (gh) must be signed in or have GH_TOKEN set, and the
#   token needs deployments: read. The command name comes from $RESOLVE_DEPLOY_BASE_GH (default gh); the tests point it at a fake.
# Output: one 40-hex SHA on stdout, or nothing when no deployment ever succeeded (exit 0, stderr says so). Exit 1 on every other
#   outcome: gh missing, an API error, output that is not what was asked for, a SHA that is not a commit in this checkout or not an
#   ancestor of HEAD, or 200 deployments read without a success while more remain. Everything but the SHA goes to stderr.
# Why "ever had a success": GitHub marks an older successful deployment `inactive` once a later one succeeds, so only the whole
#   status history says whether it ran.
set -euo pipefail

page_size=100
max_pages=2   # 200 deployments; a third page is read only to see whether more remain

fail() {
  echo "resolve-deploy-base: $*" >&2
  echo "resolve-deploy-base: Dispatch cd.yml by hand with \`maintenance: on\` to bypass this lookup." >&2
  exit 1
}

gh_cmd="${RESOLVE_DEPLOY_BASE_GH:-gh}"
command -v "$gh_cmd" > /dev/null 2>&1 || fail "the GitHub CLI ($gh_cmd) is not installed or not on PATH."

repo="${1:-${GITHUB_REPOSITORY:-}}"
[[ -n "$repo" ]] || fail "no repository given and GITHUB_REPOSITORY is not set."
git rev-parse --verify --quiet 'HEAD^{commit}' > /dev/null || fail "not inside a git work tree with a HEAD commit."

errfile="$(mktemp)"
trap 'rm -f "$errfile"' EXIT

# api <endpoint> <jq-expression>: the expression's output on stdout; a failed call ends the script.
api() {
  local result
  if ! result="$("$gh_cmd" api "$1" --jq "$2" 2> "$errfile")"; then
    fail "the call to $1 failed ($(head -n 1 "$errfile")). The workflow token needs deployments: read."
  fi
  printf '%s' "$result"
}

# succeeded <deployment-id>: true when any status of the deployment is success.
succeeded() {
  local states
  states="$(api "repos/$repo/deployments/$1/statuses?per_page=$page_size" '.[].state')"
  grep -qx 'success' <<< "$states"
}

found=""
page=0
while [[ -z "$found" ]] && (( page < max_pages )); do
  page=$((page + 1))
  lines="$(api "repos/$repo/deployments?environment=production&per_page=$page_size&page=$page" '.[] | "\(.id) \(.sha)"')"
  count=0
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    [[ "$line" =~ ^([0-9]+)\ ([0-9a-f]{40})$ ]] || fail "unexpected output from the deployments call: '$line'."
    count=$((count + 1))
    if succeeded "${BASH_REMATCH[1]}"; then
      found="${BASH_REMATCH[2]}"
      break
    fi
  done <<< "$lines"
  (( count == page_size )) || break   # a short page was the last one
  if [[ -z "$found" && $page -eq $max_pages ]]; then
    more="$(api "repos/$repo/deployments?environment=production&per_page=$page_size&page=$((page + 1))" '.[] | "\(.id) \(.sha)"')"
    [[ -z "$more" ]] || fail "the newest $((page_size * max_pages)) deployments to production have no success and more remain, so the last one that ran cannot be told."
  fi
done

if [[ -z "$found" ]]; then
  echo "resolve-deploy-base: no successful production deployment on record; there is nothing deployed to contract from." >&2
  exit 0
fi

git cat-file -e "$found^{commit}" 2> /dev/null || fail "the last deployed commit $found is not a commit in this checkout (history rewritten, or a shallow checkout)."
git merge-base --is-ancestor "$found" HEAD || fail "the last deployed commit $found is not an ancestor of HEAD (a deployment from another branch)."

echo "resolve-deploy-base: production last ran $found" >&2
echo "$found"
