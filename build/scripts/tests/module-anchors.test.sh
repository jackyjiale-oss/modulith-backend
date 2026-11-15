#!/usr/bin/env bash
# Guard test for build/scripts/module-anchors.txt: add-module.sh inserts text before these anchors in the shared files, so each
# anchor must exist exactly once in its file and sit outside every template switch region (a switch that removes the region would
# also remove the anchor, and the next module would have nowhere to wire itself).
# One line per anchor: key|relative path|exact anchor text. The paths name TemplateName, as in the source tree.
#
# Usage: bash build/scripts/tests/module-anchors.test.sh   (from any directory)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
anchors="$repo_root/build/scripts/module-anchors.txt"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

if [[ ! -f "$anchors" ]]; then
  echo "FAIL: $anchors does not exist" >&2
  exit 1
fi

# Switch markers: //#if (expr), <!--#if (expr)--> and the bare #if (expr) of the Dockerfile; each closes with the matching #endif form.
if_pattern='^[[:space:]]*(//|<!--)?#if[[:space:]]*\('
endif_pattern='^[[:space:]]*(//|<!--)?#endif'

# line_depth <file> <line number>: prints how many switch regions are open at that line.
line_depth() {
  local file="$1" target="$2" depth=0 number=0 line
  while IFS= read -r line || [[ -n "$line" ]]; do
    number=$((number + 1))
    line="${line%$'\r'}"
    if ((number == target)); then
      echo "$depth"
      return
    fi
    if [[ "$line" =~ $if_pattern ]]; then
      depth=$((depth + 1))
    elif [[ "$line" =~ $endif_pattern ]]; then
      depth=$((depth - 1))
    fi
  done < "$file"
  echo "-1"
}

expected_keys=(
  program-usings program-registrations program-endpoints
  api-references unit-references arch-references integration-references
  slnx-modules docker-copies
  assemblies-usings assemblies-modules assemblies-contracts assemblies-errors
  contributing-scopes
)
declare -A seen_keys=()

while IFS='|' read -r key relative anchor || [[ -n "$key" ]]; do
  anchor="${anchor%$'\r'}"
  [[ -z "$key" ]] && continue
  seen_keys["$key"]=1
  file="$repo_root/$relative"

  if [[ -z "$relative" || -z "$anchor" ]]; then
    fail "$key: the line needs key|path|anchor text"
    continue
  fi
  if [[ ! -f "$file" ]]; then
    fail "$key: $relative does not exist"
    continue
  fi

  count="$(grep -cF -- "$anchor" "$file" || true)"
  if [[ "$count" != "1" ]]; then
    fail "$key: '$anchor' occurs $count times in $relative (expected exactly once)"
    continue
  fi

  line_number="$(grep -nF -- "$anchor" "$file" | head -n 1 | cut -d: -f1)"
  depth="$(line_depth "$file" "$line_number")"
  if [[ "$depth" != "0" ]]; then
    fail "$key: '$anchor' in $relative (line $line_number) is inside a switch region (depth $depth)"
  fi
done < "$anchors"

for key in "${expected_keys[@]}"; do
  [[ -n "${seen_keys[$key]:-}" ]] || fail "$key: missing from module-anchors.txt"
done

if ((failures > 0)); then
  echo "module-anchors: $failures failure(s)" >&2
  exit 1
fi
echo "module-anchors: passed"
