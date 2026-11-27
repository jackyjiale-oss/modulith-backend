#!/usr/bin/env bash
# Says whether this release adds a contract migration (maintenance screen design, section 6).
# It lists the migration files ADDED between a base ref and HEAD (renames off, so a moved file counts as added), skips
# *ModelSnapshot.cs, and answers "true" when any of the rest contains the word ContractMigration at HEAD. The word is matched
# anywhere, so a marker in a *.Designer.cs partial, in a list of attributes or fully qualified counts, as it does for the
# architecture test (which reads the attribute by reflection). A false positive (a comment that mentions it) turns maintenance
# mode on needlessly, the safe direction.
#
# Usage: bash build/scripts/detect-contract-migration.sh [base-ref]   (run inside the git work tree)
#   base-ref is the commit production last ran; cd.yml passes the one resolve-deploy-base.sh prints (the last successful
#   deployment to the production environment). Without base-ref no production deployment is on record, so nothing is serving
#   and there is nothing to contract from: the answer is "false" and stderr says why.
#   See docs/deployment/README.md, "Maintenance mode".
# Output: "true" or "false" on stdout, nothing else. Exit 0 either way; exit 1 only on a git error, a base ref that does not
# resolve or a shallow checkout (which has no history to compare with and would answer "false" silently). Everything else goes to stderr.
set -euo pipefail

fail() { echo "detect-contract-migration: $*" >&2; exit 1; }

(( $# <= 1 )) || fail "usage: detect-contract-migration.sh [base-ref]"

git rev-parse --is-inside-work-tree > /dev/null 2>&1 || fail "not inside a git work tree"
if [[ "$(git rev-parse --is-shallow-repository)" == "true" ]]; then
  fail "the checkout is shallow, so the answer would be false whatever the release holds; check out the whole history (actions/checkout with fetch-depth: 0)"
fi
git rev-parse --verify --quiet 'HEAD^{commit}' > /dev/null || fail "HEAD does not resolve to a commit"

if [[ $# -eq 0 ]]; then
  echo "detect-contract-migration: no production deployment on record; there is nothing to contract from" >&2
  echo "false"
  exit 0
fi
base="$(git rev-parse --verify --quiet "$1^{commit}")" || fail "base ref '$1' does not resolve to a commit"

# Captured into a variable first so that a git failure stops the script (a failure inside <(...) would go unnoticed).
added="$(git -c core.quotepath=off diff --no-renames --name-only --diff-filter=A "$base"..HEAD -- '*/Migrations/*.cs')"

while IFS= read -r file; do
  [[ -n "$file" ]] || continue
  case "$file" in
    *ModelSnapshot.cs) continue ;;
  esac
  content="$(git show "HEAD:$file")"
  if [[ "$content" == *ContractMigration* ]]; then
    echo "detect-contract-migration: $file mentions ContractMigration" >&2
    echo "true"
    exit 0
  fi
done <<< "$added"

echo "false"
