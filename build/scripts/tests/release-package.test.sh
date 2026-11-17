#!/usr/bin/env bash
# Test for build/scripts/release-package.sh: the tag check (version pattern, match against Version.props, CRLF and spaces), the
# pack command (the produced .nupkg path, a bad version) and the package contents. The whole test takes about 35 seconds, mostly
# the one real `dotnet pack`.
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

# A failed content assertion also prints the start of the package listing, once, so a CI log shows what the package really holds.
listing_shown=0
fail_package() {
  fail "$@"
  if ((listing_shown == 0)); then
    listing_shown=1
    echo "the first 20 of $(wc -l < "$entries" | tr -d ' ') entries in the package:" >&2
    head -n 20 "$entries" >&2
  fi
}

for wanted in \
  content/.github/workflows/ci.yml \
  content/.editorconfig \
  content/templates/modulith-module/.modulith-module \
  content/build/docker/Dockerfile \
  content/.template.config/template.json \
  PACKAGE-README.md; do
  grep -qxF "$wanted" "$entries" || fail_package "the package does not contain $wanted"
done

# Every tracked file is in the package under content/, except the plan documents and the pack project itself.
sed -n 's|^content/||p' "$entries" > "$tmp/content-entries.txt"
(cd "$repo_root" && git ls-files | grep -vE '^(docs/superpowers/|build/pack/)' | LC_ALL=C sort) > "$tmp/tracked.txt"
missing="$(LC_ALL=C comm -23 "$tmp/tracked.txt" "$tmp/content-entries.txt")"
[[ -z "$missing" ]] || fail_package "$(printf '%s\n' "$missing" | wc -l | tr -d ' ') tracked files missing from the package, the first 10:"$'\n'"$(printf '%s\n' "$missing" | head -n 10)"

for unwanted in content/docs/superpowers/ content/build/pack/ content/.git/; do
  found="$(grep -F "$unwanted" "$entries" | awk -v p="$unwanted" 'index($0, p) == 1' || true)"
  [[ -z "$found" ]] || fail_package "the package carries files under $unwanted:"$'\n'"$(printf '%s\n' "$found" | head -n 5)"
done

# --- pack: a package without template content is refused ------------------------------------------------------------------------
# RepoRoot pointing at an empty folder makes dotnet pack succeed with no content (what a path-separator mistake in build/pack does on
# Linux); pack must then exit 1 and say so on stderr, instead of printing the path of a useless package.
empty_root="$tmp/empty-root"
mkdir -p "$empty_root"
empty_root_arg="$(cygpath -m "$empty_root" 2> /dev/null || printf '%s' "$empty_root")/"
code=0
RELEASE_PACKAGE_PACK_ARGS="-p:RepoRoot=$empty_root_arg" bash "$script" pack 1.2.4 "$tmp/out-empty" > "$tmp/empty-pack.out" 2> "$tmp/empty-pack.err" || code=$?
if [[ "$code" != 1 ]]; then
  fail "pack of a package without template content: exit $code, expected 1"
elif ! grep -qi 'no template content' "$tmp/empty-pack.err"; then
  fail "pack of a package without template content: stderr does not say the package has no template content:"$'\n'"$(tail -n 5 "$tmp/empty-pack.err")"
fi

# --- release.yml: the shape of the publish job (checks of text, not of how GitHub runs the file) -------------------------------
workflow="$repo_root/.github/workflows/release.yml"

# job_block <workflow> <job>: the job's lines, from its two-space-indented key up to the next job key or the end of the file.
job_block() {
  awk -v job="$2" '
    $0 == "  " job ":" { on = 1; print; next }
    on && /^  [A-Za-z0-9_-]+:/ { on = 0 }
    on { print }
  ' "$1"
}

# tag_input_misuse <workflow>: prints the lines naming the dispatch input that are not an env-style assignment (a KEY: value line
# whose key is upper case). The input may reach a step only through the environment; inside a run: block it would be script text.
tag_input_misuse() {
  grep -n 'inputs\.tag' "$1" | grep -vE '^[0-9]+:[[:space:]]*[A-Z][A-Z0-9_]*:[[:space:]]+\$\{\{[^}]*inputs\.tag[^}]*\}\}[[:space:]]*$' || true
}

# checkout_ref <workflow>: prints the value of the ref: line of the publish-package job's actions/checkout step (nothing if it has none).
# A ref without a refs/ prefix lets actions/checkout pick a branch over a tag of the same name.
checkout_ref() {
  job_block "$1" publish-package | awk '
    /^      - / { in_checkout = ($0 ~ /uses: actions\/checkout@/) }
    in_checkout && /^[[:space:]]+ref:[[:space:]]/ { sub(/^[[:space:]]+ref:[[:space:]]+/, ""); print; exit }
  '
}

if [[ ! -f "$workflow" ]]; then
  fail "$workflow does not exist"
else
  publish="$(job_block "$workflow" publish-package)"
  for wanted in 'environment: nuget' 'id-token: write' 'NuGet/login@v1' '--skip-duplicate' '--clobber' \
    'release-package.sh check-tag'; do
    grep -qF -- "$wanted" <<< "$publish" || fail "release.yml: the publish-package job does not contain '$wanted'"
  done
  grep -qE '^    needs:.*detect-package' <<< "$publish" || fail "release.yml: publish-package does not list detect-package in needs:"

  misuse="$(tag_input_misuse "$workflow")"
  [[ -z "$misuse" ]] || fail "release.yml: inputs.tag is used outside an env assignment:"$'\n'"$misuse"

  grep -qF "if: github.event_name == 'push'" <<< "$(job_block "$workflow" release-please)" ||
    fail "release.yml: the release-please job does not have if: github.event_name == 'push'"

  # Only publish-package may ask for an OIDC token: every id-token line in the file sits inside that job.
  total="$(grep -c 'id-token:' "$workflow" || true)"
  inside="$(grep -c 'id-token:' <<< "$publish" || true)"
  [[ "$total" == "$inside" ]] || fail "release.yml: id-token appears outside publish-package ($total in the file, $inside in the job)"

  # The guard that keeps a generated project from publishing: detect-package tests for the pack project.
  grep -qF 'build/pack/Modulith.Templates.csproj' <<< "$(job_block "$workflow" detect-package)" ||
    fail "release.yml: detect-package does not test for build/pack/Modulith.Templates.csproj"

  # The inputs.tag check must reject a run: step that uses the input and accept an env assignment (scratch files, never the real one).
  printf '%s\n' '  scratch:' '    steps:' '      - run: echo "${{ inputs.tag }}"' > "$tmp/bad-run.yml"
  [[ -n "$(tag_input_misuse "$tmp/bad-run.yml")" ]] || fail "the inputs.tag check accepted an expression inside a run: step"
  printf '%s\n' '  scratch:' '    env:' '      TAG: ${{ needs.release-please.outputs.tag_name || inputs.tag }}' > "$tmp/good-env.yml"
  [[ -z "$(tag_input_misuse "$tmp/good-env.yml")" ]] || fail "the inputs.tag check rejected an env assignment"

  # The publish job checks out the tag, not a branch that happens to have the tag's name.
  ref="$(checkout_ref "$workflow")"
  [[ "$ref" == refs/tags/* ]] || fail "release.yml: the publish-package checkout ref is '$ref', expected a value beginning with refs/tags/"

  # The ref check must reject the bare tag and accept the refs/tags/ form (scratch copies of the job, never the real file).
  printf '%s\n' '  publish-package:' '    steps:' '      - uses: actions/checkout@v7' '        with:' '          ref: ${{ env.TAG }}' \
    '          persist-credentials: false' > "$tmp/bad-ref.yml"
  [[ "$(checkout_ref "$tmp/bad-ref.yml")" != refs/tags/* ]] || fail "the checkout ref check accepted a bare tag ref"
  printf '%s\n' '  publish-package:' '    steps:' '      - uses: actions/checkout@v7' '        with:' '          ref: refs/tags/${{ env.TAG }}' \
    '          persist-credentials: false' > "$tmp/good-ref.yml"
  [[ "$(checkout_ref "$tmp/good-ref.yml")" == refs/tags/* ]] || fail "the checkout ref check rejected a refs/tags/ ref"

  # The job refuses a tag whose commit is not on main: a full-history checkout (so origin/main exists) and an ancestry check that
  # runs before the first step that runs a repository script (check-tag).
  grep -qF 'fetch-depth: 0' <<< "$publish" || fail "release.yml: the publish-package checkout does not set fetch-depth: 0"
  ancestry_line="$(grep -nF 'merge-base --is-ancestor' <<< "$publish" | head -n 1 | cut -d: -f1 || true)"
  check_tag_line="$(grep -nF 'release-package.sh check-tag' <<< "$publish" | head -n 1 | cut -d: -f1 || true)"
  if [[ -z "$ancestry_line" ]]; then
    fail "release.yml: the publish-package job has no 'merge-base --is-ancestor' check"
  elif [[ -z "$check_tag_line" || "$ancestry_line" -ge "$check_tag_line" ]]; then
    fail "release.yml: the ancestry check (line $ancestry_line) does not come before release-package.sh check-tag (line ${check_tag_line:-none})"
  fi
fi

# find_python <candidate>...: prints the first candidate that really runs python (a Windows Store stub or a missing name does not), else fails.
find_python() {
  local candidate
  for candidate in "$@"; do
    if "$candidate" -c 'import json' > /dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

# The release-candidate keys belong to this repository only: the root release-please config minus those three lines is the copy a
# generated project receives (build/template-content replaces the root file in the generated output). A "release-as" line is not in
# the list, so one left in the root config fails the comparison below: it forces one version and must not stay after that release.
root_config="$repo_root/release-please-config.json"
content_config="$repo_root/build/template-content/release-please-config.json"
if [[ ! -f "$content_config" ]]; then
  fail "$content_config does not exist"
else
  for key in versioning prerelease prerelease-type; do
    grep -qE "^[[:space:]]*\"$key\"" "$root_config" || fail "release-please-config.json does not contain the \"$key\" key"
    ! grep -qE "^[[:space:]]*\"$key\"" "$content_config" || fail "build/template-content/release-please-config.json contains the \"$key\" key"
  done
  grep -vE '^[[:space:]]*"(versioning|prerelease|prerelease-type)"' "$root_config" > "$tmp/config-without-rc.json" || true
  cmp -s "$tmp/config-without-rc.json" "$content_config" ||
    fail "release-please-config.json without its release-candidate lines differs from build/template-content/release-please-config.json"
  if ! python_cmd="$(find_python ${PYTHON_CANDIDATES:-python3 python})"; then
    fail "no working python interpreter found (tried ${PYTHON_CANDIDATES:-python3 python}); the JSON check needs one"
  else
    "$python_cmd" -m json.tool "$root_config" > /dev/null || fail "release-please-config.json is not valid JSON"
    "$python_cmd" -m json.tool "$content_config" > /dev/null || fail "build/template-content/release-please-config.json is not valid JSON"
  fi
fi

if ((failures > 0)); then
  echo "release-package: $failures failure(s)" >&2
  exit 1
fi

echo "release-package tests passed"
