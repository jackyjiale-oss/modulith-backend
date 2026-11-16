#!/usr/bin/env bash
# Test for build/scripts/release-package.sh: the tag check (version pattern, match against Version.props, CRLF and spaces), the
# pack command (the produced .nupkg path, a bad version) and the package contents (one real `dotnet pack`, about 6 seconds).
#
# Usage: bash build/scripts/tests/release-package.test.sh   (from any directory)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
script="$repo_root/build/scripts/release-package.sh"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

tmp="$(mktemp -d)"
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT

if [[ ! -f "$script" ]]; then
  echo "FAIL: $script does not exist" >&2
  exit 1
fi

# props <file> <version> [crlf|spaces]: writes a Version.props holding the version.
props() {
  local file="$1" version="$2" style="${3:-plain}" eol=$'\n' text="$2"
  [[ "$style" == crlf ]] && eol=$'\r\n'
  [[ "$style" == spaces ]] && text="  $2  "
  printf '<Project>%s  <PropertyGroup>%s    <Version>%s</Version>%s  </PropertyGroup>%s</Project>%s' \
    "$eol" "$eol" "$text" "$eol" "$eol" "$eol" > "$file"
}

# run_check <tag> <props file>: sets check_out and check_code (stdout and exit code of `check-tag`).
check_out=""
check_code=0
run_check() {
  check_code=0
  check_out="$(bash "$script" check-tag "$1" "$2" 2> "$tmp/check.err")" || check_code=$?
}

# expect_version <label> <tag> <version in the props> <style> <expected printed version>
expect_version() {
  local label="$1" tag="$2" version="$3" style="$4" expected="$5"
  props "$tmp/Version.props" "$version" "$style"
  run_check "$tag" "$tmp/Version.props"
  if [[ "$check_code" != 0 || "$check_out" != "$expected" ]]; then
    fail "check-tag $label: exit $check_code, printed '$check_out', expected exit 0 and '$expected'"
  fi
}

# expect_exit <label> <expected code> <tag> <version in the props>
expect_exit() {
  local label="$1" code="$2" tag="$3" version="$4"
  props "$tmp/Version.props" "$version"
  run_check "$tag" "$tmp/Version.props"
  if [[ "$check_code" != "$code" ]]; then
    fail "check-tag $label: exit $check_code, expected $code"
  elif [[ "$code" == 1 && ! -s "$tmp/check.err" ]]; then
    fail "check-tag $label: exit 1 without a message on stderr"
  fi
}

# --- check-tag: the accepted tags ----------------------------------------------------------------------------------------------
expect_version "v1.0.0" "v1.0.0" "1.0.0" plain "1.0.0"
expect_version "v1.0.0-rc.1" "v1.0.0-rc.1" "1.0.0-rc.1" plain "1.0.0-rc.1"
expect_version "CRLF line endings" "v1.0.0" "1.0.0" crlf "1.0.0"
expect_version "CRLF line endings with a pre-release" "v1.0.0-rc.1" "1.0.0-rc.1" crlf "1.0.0-rc.1"
expect_version "spaces around the element text" "v2.10.3" "2.10.3" spaces "2.10.3"

# --- check-tag: the rejected tags ----------------------------------------------------------------------------------------------
expect_exit "tag without v" 1 "1.0.0" "1.0.0"
expect_exit "v1.0" 1 "v1.0" "1.0.0"
expect_exit "trailing space" 1 "v1.0.0 " "1.0.0"
expect_exit "shell text after the version" 1 "v1.0.0;echo x" "1.0.0"
expect_exit "command substitution" 1 'v1.0.0-rc.1$(id)' "1.0.0"
expect_exit "empty tag" 1 "" "1.0.0"
expect_exit "v1.0.1 against 1.0.0" 1 "v1.0.1" "1.0.0"
expect_exit "v1.0.0-rc.1 against 1.0.0" 1 "v1.0.0-rc.1" "1.0.0"
expect_exit "v1.0.0 against 1.0.0-rc.1" 1 "v1.0.0" "1.0.0-rc.1"

# A Version.props that is missing, or holds no <Version> element, is a mismatch (exit 1), not a crash.
run_check "v1.0.0" "$tmp/no-such.props"
[[ "$check_code" == 1 ]] || fail "check-tag with a missing Version.props: exit $check_code, expected 1"
printf '<Project />\n' > "$tmp/empty.props"
run_check "v1.0.0" "$tmp/empty.props"
[[ "$check_code" == 1 ]] || fail "check-tag with no <Version> element: exit $check_code, expected 1"

# --- usage errors --------------------------------------------------------------------------------------------------------------
code=0
bash "$script" check-tag > /dev/null 2>&1 || code=$?
[[ "$code" == 2 ]] || fail "check-tag without an argument: exit $code, expected 2"
code=0
bash "$script" > /dev/null 2>&1 || code=$?
[[ "$code" == 2 ]] || fail "no command: exit $code, expected 2"
code=0
bash "$script" frobnicate > /dev/null 2>&1 || code=$?
[[ "$code" == 2 ]] || fail "unknown command: exit $code, expected 2"
code=0
bash "$script" pack 1.2.3 > /dev/null 2>&1 || code=$?
[[ "$code" == 2 ]] || fail "pack without an output folder: exit $code, expected 2"

# --- pack ----------------------------------------------------------------------------------------------------------------------
out="$tmp/out"
code=0
bad_out="$(bash "$script" pack 1.2 "$tmp/bad" 2> /dev/null)" || code=$?
[[ "$code" != 0 ]] || fail "pack 1.2 succeeded, it must reject a version that is not major.minor.patch"
[[ ! -e "$tmp/bad" ]] || fail "pack 1.2 touched the output folder before validating"

code=0
pack_out="$(bash "$script" pack 1.2.3-rc.1 "$out" 2> "$tmp/pack.err")" || code=$?
if [[ "$code" != 0 ]]; then
  cat "$tmp/pack.err" >&2
  echo "FAIL: pack 1.2.3-rc.1 exited $code" >&2
  exit 1
fi
nupkg="$(printf '%s\n' "$pack_out" | tail -n 1)"
[[ "$nupkg" == *Modulith.Backend.Templates.1.2.3-rc.1.nupkg ]] || fail "pack printed '$nupkg', expected a path ending in Modulith.Backend.Templates.1.2.3-rc.1.nupkg"
[[ -f "$nupkg" ]] || { echo "FAIL: the printed package '$nupkg' does not exist" >&2; exit 1; }

# --- package contents ----------------------------------------------------------------------------------------------------------
entries="$tmp/entries.txt"
unzip -Z1 "$nupkg" | tr -d '\r' | LC_ALL=C sort > "$entries"

for wanted in \
  content/.github/workflows/ci.yml \
  content/.editorconfig \
  content/templates/modulith-module/.modulith-module \
  content/build/docker/Dockerfile \
  content/.template.config/template.json \
  PACKAGE-README.md; do
  grep -qxF "$wanted" "$entries" || fail "the package does not contain $wanted"
done

# Every tracked file is in the package under content/, except the plan documents and the pack project itself.
sed -n 's|^content/||p' "$entries" > "$tmp/content-entries.txt"
(cd "$repo_root" && git ls-files | grep -vE '^(docs/superpowers/|build/pack/)' | LC_ALL=C sort) > "$tmp/tracked.txt"
missing="$(LC_ALL=C comm -23 "$tmp/tracked.txt" "$tmp/content-entries.txt")"
[[ -z "$missing" ]] || fail "tracked files missing from the package:"$'\n'"$missing"

for unwanted in content/docs/superpowers/ content/build/pack/ content/.git/; do
  found="$(grep -F "$unwanted" "$entries" | awk -v p="$unwanted" 'index($0, p) == 1' || true)"
  [[ -z "$found" ]] || fail "the package carries files under $unwanted:"$'\n'"$(printf '%s\n' "$found" | head -n 5)"
done

if ((failures > 0)); then
  echo "release-package: $failures failure(s)" >&2
  exit 1
fi

echo "release-package tests passed"
