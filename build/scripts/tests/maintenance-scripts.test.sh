#!/usr/bin/env bash
# Tests for detect-contract-migration.sh and resolve-maintenance.sh (maintenance screen design, sections 5 and 6).
# Each case builds its own throwaway git repository, runs the script from inside it and checks stdout, stderr and the exit code.
#
# Usage: bash build/scripts/tests/maintenance-scripts.test.sh
set -euo pipefail

scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
detect="$scripts_dir/detect-contract-migration.sh"
resolve="$scripts_dir/resolve-maintenance.sh"

failures=0
# Every case repository lives under one root, created here so that the trap (which subshells do not inherit) removes it.
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

migration_dir="src/Modules/A/Infrastructure/Persistence/Migrations"
contract_content='[ContractMigration("drop old column")]'
expand_content='public partial class AddX { }'

# must <command...>: runs a setup command and ends the case (it runs in a subshell) when the command fails. A case runs on the
# left of ||, where bash ignores set -e, so a broken setup would otherwise carry on and let the negative cases pass vacuously.
must() {
  "$@" || { echo "FAIL: setup command failed: $*" >&2; exit 1; }
}

# make_repo: creates a temporary repository, enters it and leaves it as the current directory.
make_repo() {
  local dir
  dir="$(mktemp -d "$tmp_root/repo.XXXXXX")" || exit 1
  must cd "$dir"
  must git init -q -b main
  must git config user.name "Test"
  must git config user.email "test@example.com"
  must git config commit.gpgsign false
  must git config core.autocrlf false
}

# commit_file <path> <content> <message>
commit_file() {
  must mkdir -p "$(dirname "$1")"
  printf '%s\n' "$2" > "$1" || exit 1
  must git add -- "$1"
  must git commit -q -m "$3"
}

tag() { must git tag "$1"; }

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

# --- detect-contract-migration.sh ---

case_no_previous_tag() {
  make_repo
  commit_file README.md "first" "initial"
  run_script "$detect"
  assert_eq "false" "$out" "no_previous_tag: stdout"
  assert_contains "no previous release tag" "$err" "no_previous_tag: stderr"
  assert_eq "0" "$status" "no_previous_tag: exit code"
}

case_tag_with_no_change() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file README.md "second" "docs"
  run_script "$detect"
  assert_eq "false" "$out" "tag_with_no_change"
  assert_contains "comparing with release tag v1.0.0" "$err" "tag_with_no_change: base"
}

case_added_expand_migration() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$expand_content" "add expand migration"
  run_script "$detect"
  assert_eq "false" "$out" "added_expand_migration"
  assert_contains "comparing with release tag v1.0.0" "$err" "added_expand_migration: base"
}

case_added_contract_migration() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  run_script "$detect"
  assert_eq "true" "$out" "added_contract_migration: stdout"
  assert_eq "0" "$status" "added_contract_migration: exit code"
}

case_modified_not_added() {
  make_repo
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content // edited" "edit the applied migration"
  run_script "$detect"
  assert_eq "false" "$out" "modified_not_added"
  assert_contains "comparing with release tag v1.0.0" "$err" "modified_not_added: base"
}

# A migration's marker is matched as the word ContractMigration anywhere in an added file under Migrations, the way the architecture
# test (reflection) accepts it: in a .Designer.cs partial, in a list of attributes, fully qualified. A false positive is the safe direction.
case_designer_file_counts() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.Designer.cs" "$contract_content" "add designer"
  run_script "$detect"
  assert_eq "true" "$out" "designer_file_counts"
  assert_contains "comparing with release tag v1.0.0" "$err" "designer_file_counts: base"
}

case_combined_attribute_list_counts() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" '[Obsolete, ContractMigration("x")]' "add combined list"
  run_script "$detect"
  assert_eq "true" "$out" "combined_attribute_list_counts"
}

case_fully_qualified_attribute_counts() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" '[Some.Namespace.ContractMigration("x")]' "add qualified attribute"
  run_script "$detect"
  assert_eq "true" "$out" "fully_qualified_attribute_counts"
}

case_model_snapshot_ignored() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/ADbContextModelSnapshot.cs" "$contract_content" "add snapshot"
  run_script "$detect"
  assert_eq "false" "$out" "model_snapshot_ignored"
  assert_contains "comparing with release tag v1.0.0" "$err" "model_snapshot_ignored: base"
}

case_head_carries_its_own_tag() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  tag v1.1.0
  run_script "$detect"
  assert_eq "true" "$out" "head_carries_its_own_tag"
}

case_unmerged_tag_ignored() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  must git checkout -q -b side
  commit_file side.txt "side" "side work"
  tag v9.9.9
  must git checkout -q main
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  run_script "$detect"
  assert_eq "true" "$out" "unmerged_tag_ignored"
  assert_contains "comparing with release tag v1.0.0" "$err" "unmerged_tag_ignored: base"
}

# A pre-release tag ranks above its release in version order and can never be deployed to production, so it is never the base.
case_prerelease_tag_never_the_base() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  commit_file README.md "second" "docs"
  tag v1.1.0-rc.1
  commit_file README.md "third" "more docs"
  run_script "$detect"
  assert_eq "true" "$out" "prerelease_tag_never_the_base: stdout"
  assert_contains "comparing with release tag v1.0.0" "$err" "prerelease_tag_never_the_base: base"
}

# A shallow clone has no tags and no history to diff, and would silently answer false.
case_shallow_clone_refused() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  local origin="$PWD"
  must git clone -q --depth 1 "file://$origin" "$tmp_root/shallow.$$"
  must cd "$tmp_root/shallow.$$"
  run_script "$detect"
  assert_eq "1" "$status" "shallow_clone_refused: exit code"
  assert_eq "" "$out" "shallow_clone_refused: stdout empty"
  assert_contains "fetch-depth: 0" "$err" "shallow_clone_refused: stderr names fetch-depth"
  assert_contains "fetch-tags: true" "$err" "shallow_clone_refused: stderr names fetch-tags"
}

case_explicit_base_ref() {
  make_repo
  commit_file README.md "first" "initial"
  tag v1.0.0
  commit_file "$migration_dir/20261101000000_AddX.cs" "$contract_content" "add contract migration"
  run_script "$detect" v1.0.0
  assert_eq "true" "$out" "explicit_base_ref: tag"
  run_script "$detect" HEAD
  assert_eq "false" "$out" "explicit_base_ref: HEAD"
}

case_unresolvable_base_ref() {
  make_repo
  commit_file README.md "first" "initial"
  run_script "$detect" nope
  assert_eq "1" "$status" "unresolvable_base_ref: exit code"
  assert_eq "" "$out" "unresolvable_base_ref: stdout empty"
}

# --- resolve-maintenance.sh ---

case_resolve_auto() {
  run_script "$resolve" "" true;    assert_eq "true" "$out" "resolve_auto: empty mode, detected"
  run_script "$resolve" "" false;   assert_eq "false" "$out" "resolve_auto: empty mode, not detected"
  run_script "$resolve" auto true;  assert_eq "true" "$out" "resolve_auto: auto, detected"
  run_script "$resolve" auto false; assert_eq "false" "$out" "resolve_auto: auto, not detected"
  run_script "$resolve" on false;   assert_eq "true" "$out" "resolve_auto: on, not detected"
  run_script "$resolve" off false;  assert_eq "false" "$out" "resolve_auto: off, not detected"
}

case_resolve_off_warns() {
  run_script "$resolve" off true
  assert_eq "false" "$out" "resolve_off_warns: stdout"
  assert_contains "::warning" "$err" "resolve_off_warns: stderr"
}

case_resolve_rejects_unknown_mode() {
  run_script "$resolve" maybe true
  assert_eq "1" "$status" "resolve_rejects_unknown_mode: exit code"
  assert_eq "" "$out" "resolve_rejects_unknown_mode: stdout empty"
  assert_contains "'auto'" "$err" "resolve_rejects_unknown_mode: names auto"
  assert_contains "'on'" "$err" "resolve_rejects_unknown_mode: names on"
  assert_contains "'off'" "$err" "resolve_rejects_unknown_mode: names off"
}

# Each case runs in a subshell so that its repository and working directory do not leak into the next.
# The subshell reports its failures through its exit code, since variables do not cross the boundary.
run_case() {
  local name="$1" rc=0
  ( failures=0; "$name"; exit $(( failures > 0 ? 1 : 0 )) ) || rc=$?
  if (( rc != 0 )); then failures=$((failures + 1)); fi
}

for c in \
  case_no_previous_tag case_tag_with_no_change case_added_expand_migration case_added_contract_migration \
  case_modified_not_added case_designer_file_counts case_combined_attribute_list_counts \
  case_fully_qualified_attribute_counts case_model_snapshot_ignored case_head_carries_its_own_tag case_unmerged_tag_ignored \
  case_prerelease_tag_never_the_base case_shallow_clone_refused \
  case_explicit_base_ref case_unresolvable_base_ref case_resolve_auto case_resolve_off_warns \
  case_resolve_rejects_unknown_mode; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "maintenance-scripts: $failures failure(s)" >&2
  exit 1
fi
echo "maintenance-scripts: passed"
