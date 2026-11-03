#!/usr/bin/env bash
# Says whether this release adds a contract migration (maintenance screen design, section 6).
# It lists the migration files ADDED between a base ref and HEAD (renames off, so a moved file counts as added), skips
# *.Designer.cs and *ModelSnapshot.cs, and answers "true" when any of the rest carries [ContractMigration at HEAD.
#
# Usage: bash build/scripts/detect-contract-migration.sh [base-ref]   (run inside the git work tree)
#   Without base-ref the base is the highest v[0-9]* tag merged into HEAD whose commit is not HEAD (the release commit
#   carries its own tag). With no such tag (the first release) the answer is "false" and stderr says why.
# Output: "true" or "false" on stdout, nothing else. Exit 0 either way; exit 1 only on a git error or a base ref that does not
# resolve. Everything else goes to stderr.
set -euo pipefail

fail() { echo "detect-contract-migration: $*" >&2; exit 1; }

(( $# <= 1 )) || fail "usage: detect-contract-migration.sh [base-ref]"

git rev-parse --is-inside-work-tree > /dev/null 2>&1 || fail "not inside a git work tree"
head_sha="$(git rev-parse --verify --quiet 'HEAD^{commit}')" || fail "HEAD does not resolve to a commit"

base=""
if [[ $# -eq 1 ]]; then
  base="$(git rev-parse --verify --quiet "$1^{commit}")" || fail "base ref '$1' does not resolve to a commit"
else
  tags="$(git tag --merged HEAD --list 'v[0-9]*' --sort=-v:refname)"
  while IFS= read -r t; do
    [[ -n "$t" ]] || continue
    tag_sha="$(git rev-list -n1 "$t")"
    if [[ "$tag_sha" != "$head_sha" ]]; then
      base="$tag_sha"
      echo "detect-contract-migration: comparing with release tag $t" >&2
      break
    fi
  done <<< "$tags"
  if [[ -z "$base" ]]; then
    echo "detect-contract-migration: no previous release tag; there is nothing to contract from" >&2
    echo "false"
    exit 0
  fi
fi

# Captured into a variable first so that a git failure stops the script (a failure inside <(...) would go unnoticed).
added="$(git -c core.quotepath=off diff --no-renames --name-only --diff-filter=A "$base"..HEAD -- '*/Migrations/*.cs')"

while IFS= read -r file; do
  [[ -n "$file" ]] || continue
  case "$file" in
    *.Designer.cs | *ModelSnapshot.cs) continue ;;
  esac
  content="$(git show "HEAD:$file")"
  if [[ "$content" == *'[ContractMigration'* ]]; then
    echo "detect-contract-migration: $file carries [ContractMigration" >&2
    echo "true"
    exit 0
  fi
done <<< "$added"

echo "false"
