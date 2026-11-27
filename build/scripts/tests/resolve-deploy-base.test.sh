#!/usr/bin/env bash
# Tests for resolve-deploy-base.sh (maintenance detection design, section 3.1).
# Each case builds a throwaway git repository, writes fixtures for a fake `gh` and runs the script from inside the repository.
# The fake `gh` ignores --jq and serves pre-rendered lines: `<id> <sha>` per deployment (newest first) and one state per status.
#
# Usage: bash build/scripts/tests/resolve-deploy-base.test.sh
set -euo pipefail

scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
resolve="$scripts_dir/resolve-deploy-base.sh"

failures=0
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

# The fake gh: `gh api <endpoint> --jq <expr>`. FAKE_GH_DIR holds deployments.<page> and statuses.<id>; every call is logged.
fake_gh="$tmp_root/fake-gh"
cat > "$fake_gh" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${FAKE_GH_LOG:-/dev/null}"
if [[ "${FAKE_GH_FAIL:-}" == "1" ]]; then echo "gh: HTTP 403: Resource not accessible by integration" >&2; exit 1; fi
if [[ "${FAKE_GH_GARBAGE:-}" == "1" ]]; then echo "not json"; exit 0; fi
endpoint="$2"
if [[ "${FAKE_GH_FAIL_STATUSES:-}" == "1" && "$endpoint" == repos/*/deployments/*/statuses* ]]; then
  echo "gh: HTTP 502: Bad Gateway" >&2; exit 1
fi
case "$endpoint" in
  repos/*/deployments/*/statuses*)
    id="${endpoint#*/deployments/}"; id="${id%%/*}"
    file="$FAKE_GH_DIR/statuses.$id" ;;
  repos/*/deployments\?*)
    page="${endpoint##*page=}"; page="${page%%&*}"
    file="$FAKE_GH_DIR/deployments.$page" ;;
  *) echo "fake gh: unexpected endpoint $endpoint" >&2; exit 2 ;;
esac
if [[ -f "$file" ]]; then cat "$file"; fi
exit 0
FAKE
chmod +x "$fake_gh"

must() {
  "$@" || { echo "FAIL: setup command failed: $*" >&2; exit 1; }
}

# make_repo: a repository with commits c1, c2, c3 on main; sets sha1, sha2, sha3 and the fixture directory.
make_repo() {
  local dir
  dir="$(mktemp -d "$tmp_root/repo.XXXXXX")" || exit 1
  must cd "$dir"
  must git init -q -b main
  must git config user.name "Test"
  must git config user.email "test@example.com"
  must git config commit.gpgsign false
  must git config core.autocrlf false
  for n in 1 2 3; do
    printf '%s\n' "$n" > "f$n.txt" || exit 1
    must git add -- "f$n.txt"
    must git commit -q -m "c$n"
  done
  sha1="$(git rev-parse HEAD~2)"; sha2="$(git rev-parse HEAD~1)"; sha3="$(git rev-parse HEAD)"
  FAKE_GH_DIR="$(mktemp -d "$tmp_root/fx.XXXXXX")" || exit 1
  FAKE_GH_LOG="$FAKE_GH_DIR/log"
  export FAKE_GH_DIR FAKE_GH_LOG RESOLVE_DEPLOY_BASE_GH="$fake_gh"
  unset FAKE_GH_FAIL FAKE_GH_GARBAGE FAKE_GH_FAIL_STATUSES
  export GITHUB_REPOSITORY="owner/repo"
}

# dep <page> <id> <sha>: appends a deployment line to a page fixture.   states <id> <state...>: the statuses, newest first.
dep() { printf '%s %s\n' "$2" "$3" >> "$FAKE_GH_DIR/deployments.$1"; }
states() { local id="$1"; shift; printf '%s\n' "$@" > "$FAKE_GH_DIR/statuses.$id"; }

assert_eq() {
  local expected="$1" actual="$2" name="$3"
  if [[ "$expected" == "$actual" ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected '$expected', got '$actual'" >&2
    failures=$((failures + 1))
  fi
}

assert_contains() {
  local needle="$1" haystack="$2" name="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected to contain '$needle', got '$haystack'" >&2
    failures=$((failures + 1))
  fi
}

# run_script <script> <args...>: sets out, err and status; stdout and stderr are captured separately.
run_script() {
  local errfile
  errfile="$(mktemp)"
  status=0
  out="$(bash "$@" 2> "$errfile")" || status=$?
  err="$(cat "$errfile")"
  rm -f "$errfile"
}

# fill_page <page> <first-id> <count> <sha>: a page of deployments that never succeed (no status fixtures).
fill_page() {
  local page="$1" first="$2" count="$3" sha="$4" i
  for (( i = 0; i < count; i++ )); do dep "$page" $(( first + i )) "$sha"; done
}

case_newest_success() {
  make_repo
  dep 1 3 "$sha2"; states 3 success
  run_script "$resolve" owner/repo
  assert_eq "$sha2" "$out" "newest_success: stdout"
  assert_eq "0" "$status" "newest_success: exit code"
}

case_failed_newer_than_success() {
  make_repo
  dep 1 3 "$sha3"; dep 1 2 "$sha2"
  states 3 failure; states 2 success
  run_script "$resolve" owner/repo
  assert_eq "$sha2" "$out" "failed_newer_than_success"
}

case_success_then_inactive_counts() {
  make_repo
  dep 1 3 "$sha3"; dep 1 2 "$sha2"
  states 3 failure; states 2 inactive success
  run_script "$resolve" owner/repo
  assert_eq "$sha2" "$out" "success_then_inactive_counts"
}

case_waiting_skipped() {
  make_repo
  dep 1 3 "$sha3"; dep 1 2 "$sha2"
  states 3 waiting; states 2 success
  run_script "$resolve" owner/repo
  assert_eq "$sha2" "$out" "waiting_skipped"
}

case_none_on_record() {
  make_repo
  run_script "$resolve" owner/repo
  assert_eq "" "$out" "none_on_record: stdout empty"
  assert_eq "0" "$status" "none_on_record: exit code"
  assert_contains "nothing deployed" "$err" "none_on_record: stderr"
}

case_no_success_in_short_history() {
  make_repo
  dep 1 3 "$sha3"; dep 1 2 "$sha2"; dep 1 1 "$sha1"
  states 3 failure; states 2 error; states 1 in_progress
  run_script "$resolve" owner/repo
  assert_eq "" "$out" "no_success_in_short_history: stdout empty"
  assert_eq "0" "$status" "no_success_in_short_history: exit code"
}

case_second_page_read() {
  make_repo
  fill_page 1 1000 100 "$sha3"
  dep 2 2000 "$sha2"; states 2000 success
  run_script "$resolve" owner/repo
  assert_eq "$sha2" "$out" "second_page_read: stdout"
  assert_contains "page=2" "$(cat "$FAKE_GH_LOG")" "second_page_read: asked for page 2"
}

case_exactly_200_no_success() {
  make_repo
  fill_page 1 1000 100 "$sha3"; fill_page 2 2000 100 "$sha3"
  run_script "$resolve" owner/repo
  assert_eq "" "$out" "exactly_200_no_success: stdout empty"
  assert_eq "0" "$status" "exactly_200_no_success: exit code"
}

case_over_200_no_success() {
  make_repo
  fill_page 1 1000 100 "$sha3"; fill_page 2 2000 100 "$sha3"; dep 3 3000 "$sha3"
  run_script "$resolve" owner/repo
  assert_eq "" "$out" "over_200_no_success: stdout empty"
  assert_eq "1" "$status" "over_200_no_success: exit code"
  assert_contains "maintenance: on" "$err" "over_200_no_success: names the bypass"
}

case_sha_not_in_checkout() {
  make_repo
  dep 1 5 "0000000000000000000000000000000000000000"; states 5 success
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "sha_not_in_checkout: exit code"
  assert_eq "" "$out" "sha_not_in_checkout: stdout empty"
  assert_contains "not a commit" "$err" "sha_not_in_checkout: cause"
  assert_contains "maintenance: on" "$err" "sha_not_in_checkout: names the bypass"
}

case_sha_not_ancestor() {
  make_repo
  must git switch -q -c side "$sha1"
  printf 'side\n' > side.txt
  must git add -- side.txt
  must git commit -q -m "side"
  side_sha="$(git rev-parse HEAD)"
  must git switch -q main
  dep 1 5 "$side_sha"; states 5 success
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "sha_not_ancestor: exit code"
  assert_eq "" "$out" "sha_not_ancestor: stdout empty"
  assert_contains "not an ancestor" "$err" "sha_not_ancestor: cause"
}

case_gh_missing() {
  make_repo
  export RESOLVE_DEPLOY_BASE_GH="$tmp_root/no-such-gh"
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "gh_missing: exit code"
  assert_eq "" "$out" "gh_missing: stdout empty"
  assert_contains "gh" "$err" "gh_missing: names gh"
  assert_contains "maintenance: on" "$err" "gh_missing: names the bypass"
}

case_gh_fails() {
  make_repo
  export FAKE_GH_FAIL=1
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "gh_fails: exit code"
  assert_eq "" "$out" "gh_fails: stdout empty"
  assert_contains "deployments: read" "$err" "gh_fails: names the permission"
  assert_contains "maintenance: on" "$err" "gh_fails: names the bypass"
}

# A statuses call that fails while the deployments list works must stop the lookup. Reading statuses runs in a function called from an
# `if`, where `set -e` does not apply, so a swallowed failure would read as "no success" and let detection answer false.
case_statuses_call_fails() {
  make_repo
  export FAKE_GH_FAIL_STATUSES=1
  dep 1 3 "$sha3"; dep 1 2 "$sha2"
  states 3 failure; states 2 success
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "statuses_call_fails: exit code"
  assert_eq "" "$out" "statuses_call_fails: stdout empty"
  assert_contains "maintenance: on" "$err" "statuses_call_fails: names the bypass"
}

case_unreadable_output() {
  make_repo
  export FAKE_GH_GARBAGE=1
  run_script "$resolve" owner/repo
  assert_eq "1" "$status" "unreadable_output: exit code"
  assert_eq "" "$out" "unreadable_output: stdout empty"
  assert_contains "unexpected" "$err" "unreadable_output: cause"
}

case_repository_missing() {
  make_repo
  unset GITHUB_REPOSITORY
  run_script "$resolve"
  assert_eq "1" "$status" "repository_missing: exit code"
  assert_eq "" "$out" "repository_missing: stdout empty"
  assert_contains "repository" "$err" "repository_missing: cause"
  assert_eq "" "$(cat "$FAKE_GH_LOG" 2> /dev/null || true)" "repository_missing: the API was not called"
}

case_request_shape() {
  make_repo
  dep 1 3 "$sha2"; states 3 success
  run_script "$resolve" owner/repo
  log="$(cat "$FAKE_GH_LOG")"
  assert_contains "environment=production" "$log" "request_shape: environment"
  assert_contains "per_page=100" "$log" "request_shape: page size"
  assert_contains "repos/owner/repo/deployments" "$log" "request_shape: repository"
}

# Each case runs in a subshell so that its repository, environment and working directory do not leak into the next.
run_case() {
  local name="$1" rc=0
  ( failures=0; "$name"; exit $(( failures > 0 ? 1 : 0 )) ) || rc=$?
  if (( rc != 0 )); then failures=$((failures + 1)); fi
}

for c in \
  case_newest_success case_failed_newer_than_success case_success_then_inactive_counts case_waiting_skipped \
  case_none_on_record case_no_success_in_short_history case_second_page_read case_exactly_200_no_success \
  case_over_200_no_success case_sha_not_in_checkout case_sha_not_ancestor case_gh_missing case_gh_fails \
  case_statuses_call_fails case_unreadable_output case_repository_missing case_request_shape; do
  # ONLY=<case name> runs one case (the 200-deployment cases make the full run slow on Windows).
  if [[ -z "${ONLY:-}" || "$c" == "$ONLY" ]]; then run_case "$c"; fi
done

if (( failures > 0 )); then
  echo "resolve-deploy-base: $failures failure(s)" >&2
  exit 1
fi
echo "resolve-deploy-base: passed"
