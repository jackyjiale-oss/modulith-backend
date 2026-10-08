#!/usr/bin/env bash
# Checks the Windows path-length guard (CheckWindowsPathLength in Directory.Build.targets) without a real build.
# It evaluates the target on the Infrastructure.Common project with the test hooks, so it also runs on Linux:
#   PathLengthCheckForce=true, PathLengthCheckLongPathsEnabled and PathLengthCheckProjectDirectory.
#
# Usage: bash build/scripts/path-guard-check.sh [project-root]   (default: this repository)
set -euo pipefail

root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

[[ -f "$root/Directory.Build.targets" ]] || { echo "FAIL: $root/Directory.Build.targets is missing" >&2; exit 1; }

shopt -s nullglob
projects=("$root"/src/BuildingBlocks/*.Infrastructure.Common/*.Infrastructure.Common.csproj)
(( ${#projects[@]} == 1 )) || { echo "FAIL: expected one Infrastructure.Common project under $root, found ${#projects[@]}" >&2; exit 1; }
project="${projects[0]}"

long_dir="$(printf 'a%.0s' $(seq 1 260))"   # far beyond the limit for any project name
short_dir="w"
# The fake directories have no leading slash so that Git Bash on Windows does not rewrite them as paths.
failures=0

# check <name> <expected exit: 0|nonzero> <expected output pattern or ""> <msbuild args...>
check() {
  local name="$1" expect="$2" pattern="$3"
  shift 3
  local output status=0
  output="$(dotnet msbuild "$project" -t:CheckWindowsPathLength -nologo -p:PathLengthCheckForce=true "$@" 2>&1)" || status=$?
  if [[ "$expect" == "0" && $status -ne 0 ]] || [[ "$expect" == "nonzero" && $status -eq 0 ]]; then
    echo "FAIL: $name: unexpected exit code $status" >&2
    echo "$output" >&2
    failures=$((failures + 1))
  elif [[ -n "$pattern" ]] && ! grep -qF "$pattern" <<< "$output"; then
    echo "FAIL: $name: output does not contain '$pattern'" >&2
    echo "$output" >&2
    failures=$((failures + 1))
  else
    echo "ok: $name"
  fi
}

check "long path, long paths disabled: fails with TN0001" nonzero "TN0001" \
  -p:PathLengthCheckLongPathsEnabled=0 "-p:PathLengthCheckProjectDirectory=$long_dir"
check "short path, long paths disabled: passes" 0 "" \
  -p:PathLengthCheckLongPathsEnabled=0 "-p:PathLengthCheckProjectDirectory=$short_dir"
check "long path, long paths enabled: passes" 0 "" \
  -p:PathLengthCheckLongPathsEnabled=1 "-p:PathLengthCheckProjectDirectory=$long_dir"
check "long path, SkipPathLengthCheck=true: passes" 0 "" \
  -p:PathLengthCheckLongPathsEnabled=0 -p:SkipPathLengthCheck=true "-p:PathLengthCheckProjectDirectory=$long_dir"

if (( failures > 0 )); then
  echo "path-guard-check: $failures check(s) failed" >&2
  exit 1
fi
echo "path-guard-check: passed"
