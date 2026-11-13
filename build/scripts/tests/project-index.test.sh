#!/usr/bin/env bash
# Tests for project-index.sh (project index design): the CLI, the markers shard, the MAP.md checks and --check.
# Each case builds its own throwaway tree, runs the script on it and checks the files it writes, its output and its exit code.
#
# Usage: bash build/scripts/tests/project-index.test.sh
set -euo pipefail

scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gen="$scripts_dir/project-index.sh"

failures=0
# Every case tree lives under one root, created here so that the trap (which subshells do not inherit) removes it.
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

shards="routes permissions errors events jobs config tests markers"

# must <command...>: runs a setup command and ends the case (it runs in a subshell) when the command fails. A case runs on the
# left of ||, where bash ignores set -e, so a broken setup would otherwise carry on and let the negative cases pass vacuously.
must() {
  "$@" || { echo "FAIL: setup command failed: $*" >&2; exit 1; }
}

# make_tree: creates an empty tree and an empty output directory beside it; sets tree and idx to their paths.
make_tree() {
  local base
  base="$(mktemp -d "$tmp_root/case.XXXXXX")" || exit 1
  tree="$base/tree"
  idx="$base/idx"
  must mkdir -p "$tree"
}

# put <relative path> <content>: writes a file into the current tree.
put() {
  must mkdir -p "$tree/$(dirname "$1")"
  printf '%s\n' "$2" > "$tree/$1" || exit 1
}

# put_raw <relative path> <printf format>: writes a file with exactly the bytes the format gives (for CRLF line endings).
put_raw() {
  must mkdir -p "$tree/$(dirname "$1")"
  printf "$2" > "$tree/$1" || exit 1
}

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

assert_not_contains() {
  local needle="$1" haystack="$2" name="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "ok: $name"
  else
    echo "FAIL: $name: expected not to contain '$needle', got '$haystack'" >&2
    failures=$((failures + 1))
  fi
}

# run_gen <arguments...>: runs the script; sets out (stdout), err (stderr) and status.
run_gen() {
  local errfile
  errfile="$(mktemp "$tmp_root/err.XXXXXX")"
  status=0
  out="$(bash "$gen" "$@" 2> "$errfile")" || status=$?
  err="$(cat "$errfile")"
  rm -f "$errfile"
}

# generate: writes the shards of the current tree into idx.
generate() {
  run_gen --root "$tree" --out "$idx"
  assert_eq "0" "$status" "setup: generate exit code"
}

# check: runs --check on the current tree against idx; sets out, err and status; combined holds stdout and stderr.
check() {
  run_gen --check --root "$tree" --out "$idx"
  combined="$out$err"
}

# write_map <body>: writes MAP.md into idx; without a body, a valid one that names every shard.
write_map() {
  local body="${1-}" s
  must mkdir -p "$idx"
  if [[ -z "$body" ]]; then
    body="# Map"$'\n'
    for s in $shards; do body+="- \`$s.md\`"$'\n'; done
  fi
  printf '%s\n' "$body" > "$idx/MAP.md" || exit 1
}

# rows <shard>: the table rows (after the header and the separator) of a shard of idx.
rows() {
  tail -n +9 "$idx/$1.md"
}

# make_git_tree: makes the current tree a git repository (the index is enough: nothing is committed).
make_git_tree() {
  must git -C "$tree" init -q
  must git -C "$tree" config core.autocrlf false
}

case_usage_errors() {
  make_tree
  local rc=0
  bash "$gen" --bogus > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: unknown argument"
  rc=0
  bash "$gen" --root > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --root without a value"
  rc=0
  bash "$gen" --root "$tree" --out > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --out without a value"
  rc=0
  bash "$gen" --check --root "$tree/missing" --out "$idx" > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: --check with a missing --root directory"
  rc=0
  bash "$gen" --root "$tree/missing" --out "$idx" > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: a missing --root directory in write mode"
  rc=0
  bash "$gen" stray > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "usage_errors: a stray word"
}

case_markers_shard() {
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put b.md "$(printf '<!--#if (includeSample)-->\nsample text\n<!--#endif-->\nWrite `//#if (x)` to switch a part off.')"
  put c.yml "$(printf 'top: 1\n  #if (caching == "redis")\n  redis: x\n  #endif')"
  put d.txt 'nothing here'
  generate
  local expected
  expected="$(printf '%s\n' '| a.cs | //#if | notifications |' '| b.md | <!--#if | includeSample |' '| c.yml | #if | caching == "redis" |')"
  assert_eq "$expected" "$(rows markers)" "markers_shard: the rows"
  assert_contains "Rows: 3" "$(cat "$idx/markers.md")" "markers_shard: row count"
  assert_eq '<!-- Generated by build/scripts/project-index.sh; do not edit. Regenerate with: bash build/scripts/project-index.sh -->' \
    "$(sed -n '1p' "$idx/markers.md")" "markers_shard: banner"
  assert_eq "" "$(sed -n '2p' "$idx/markers.md")" "markers_shard: blank line after the banner"
  assert_eq "| file | kinds | expressions |" "$(sed -n '7p' "$idx/markers.md")" "markers_shard: header row"
  assert_eq "| --- | --- | --- |" "$(sed -n '8p' "$idx/markers.md")" "markers_shard: separator row"
  assert_eq "" "$(sed -n '6p' "$idx/markers.md")" "markers_shard: blank line before the table"
  assert_eq "Rows: 3" "$(sed -n '5p' "$idx/markers.md")" "markers_shard: Rows line"
}

case_markers_kinds_and_expressions() {
  make_tree
  put e.txt "$(printf '%%%%#if (a && b)\n%%%%#endif\n#if (c)\n#else\n#endif\n//#if (c)\n//#endif\n<!--#if (d || e)-->\n<!--#endif-->')"
  put f.cs "$(printf '//#if (x && (y))\n//#endif')"
  put g.cs "$(printf '//#else\n//#endif')"
  generate
  local expected
  expected="$(printf '%s\n' '| e.txt | #if, %%#if, //#if, <!--#if | a && b; c; d \|\| e |' '| f.cs | //#if | x && (y) |' '| g.cs | //#if | - |')"
  assert_eq "$expected" "$(rows markers)" "markers_kinds_and_expressions: joined kinds and expressions"
}

case_markers_zero_rows() {
  make_tree
  put a.cs 'int x;'
  generate
  assert_contains "Rows: 0" "$(cat "$idx/markers.md")" "markers_zero_rows: Rows: 0"
  assert_eq "" "$(rows markers)" "markers_zero_rows: no rows"
}

case_stub_shards() {
  make_tree
  put a.cs 'int x;'
  generate
  local s
  for s in $shards; do
    if [[ -f "$idx/$s.md" ]]; then
      assert_contains "Rows: 0" "$(cat "$idx/$s.md")" "stub_shards: $s.md exists with zero rows"
    else
      echo "FAIL: stub_shards: $s.md was not written" >&2
      failures=$((failures + 1))
    fi
  done
}

case_determinism() {
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put_raw dir/crlf.cs 'int x;\r\n//#if (includeSample)\r\nint y;\r\n//#endif\r\n'
  put_raw dir/crlf.md '<!--#if (notifications && includeSample)-->\r\ntext\r\n<!--#endif-->\r\n'
  generate
  local first second crlf_tree="$tree"
  first="$(cat "$idx"/*.md)"
  rm -rf "$idx"
  generate
  second="$(cat "$idx"/*.md)"
  assert_eq "$first" "$second" "determinism: two runs give identical bytes"
  assert_not_contains $'\r' "$(cat "$idx"/markers.md)" "determinism: no carriage return in the output"
  # The same files with LF line endings give the same output.
  make_tree
  put a.cs "$(printf 'int x;\n//#if (notifications)\nint y;\n//#endif')"
  put dir/crlf.cs "$(printf 'int x;\n//#if (includeSample)\nint y;\n//#endif')"
  put dir/crlf.md "$(printf '<!--#if (notifications && includeSample)-->\ntext\n<!--#endif-->')"
  generate
  assert_eq "$first" "$(cat "$idx"/*.md)" "determinism: CRLF files give the same output as LF files"
  # --check passes on the CRLF tree against the shards generated from the LF tree.
  write_map
  run_gen --check --root "$crlf_tree" --out "$idx"
  assert_eq "0" "$status" "determinism: --check passes across line endings"
}

case_untracked_ignored() {
  make_tree
  make_git_tree
  put tracked.cs "$(printf '//#if (notifications)\n//#endif')"
  put .gitignore 'obj/'
  must git -C "$tree" add tracked.cs .gitignore
  put untracked.cs "$(printf '//#if (includeSample)\n//#endif')"
  put obj/ignored.cs "$(printf '//#if (caching == "redis")\n//#endif')"
  generate
  assert_eq "| tracked.cs | //#if | notifications |" "$(rows markers)" "untracked_ignored: only the tracked file is listed"
  assert_contains "Rows: 1" "$(cat "$idx/markers.md")" "untracked_ignored: row count"
}

case_git_file_marker() {
  # In a git worktree .git is a file (gitdir: ...), not a directory: the file list must still come from git.
  make_tree
  must git init -q --separate-git-dir "$tmp_root/gitdir.$$" "$tree"
  must git -C "$tree" config core.autocrlf false
  [[ -f "$tree/.git" ]] || { echo "FAIL: setup: .git is not a file" >&2; exit 1; }
  put tracked.cs "$(printf '//#if (notifications)\n//#endif')"
  must git -C "$tree" add tracked.cs
  put untracked.cs "$(printf '//#if (includeSample)\n//#endif')"
  generate
  assert_eq "| tracked.cs | //#if | notifications |" "$(rows markers)" "git_file_marker: .git as a file still lists tracked files only"
  rm -rf "$tmp_root/gitdir.$$"
}

case_find_excludes_build_dirs() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  put bin/b.cs "$(printf '//#if (includeSample)\n//#endif')"
  put src/obj/c.cs "$(printf '//#if (includeSample)\n//#endif')"
  generate
  assert_eq "| a.cs | //#if | notifications |" "$(rows markers)" "find_excludes_build_dirs: bin and obj are skipped without git"
}

case_check_stale() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  generate
  write_map
  check
  assert_eq "0" "$status" "check_stale: a fresh tree passes"
  printf 'extra\n' >> "$idx/markers.md"
  check
  assert_eq "1" "$status" "check_stale: exit code"
  assert_contains "stale: markers" "$combined" "check_stale: names the shard"
  # The check rewrites nothing.
  assert_contains "extra" "$(cat "$idx/markers.md")" "check_stale: shard left as it was"
  # A change in the source makes the shard stale too.
  generate
  put b.cs "$(printf '//#if (includeSample)\n//#endif')"
  check
  assert_eq "1" "$status" "check_stale: a changed source"
  assert_contains "stale: markers" "$combined" "check_stale: names the shard after a source change"
}

case_check_missing_and_unknown() {
  make_tree
  put a.cs 'int x;'
  generate
  write_map
  rm -f "$idx/jobs.md"
  check
  assert_eq "1" "$status" "check_missing_and_unknown: exit code for a missing shard"
  assert_contains "missing: jobs" "$combined" "check_missing_and_unknown: names the missing shard"
  generate
  printf 'x\n' > "$idx/x.md"
  check
  assert_eq "1" "$status" "check_missing_and_unknown: exit code for an unknown file"
  assert_contains "unknown shard: x.md" "$combined" "check_missing_and_unknown: names the file"
  # A file that is not markdown is not a shard.
  rm -f "$idx/x.md"
  printf 'x\n' > "$idx/notes.txt"
  check
  assert_eq "0" "$status" "check_missing_and_unknown: a non-markdown file is ignored"
}

case_map_rules() {
  make_tree
  put src/exists.cs 'int x;'
  mkdir -p "$tree/docs/dir"
  generate
  # MAP.md missing.
  check
  assert_eq "1" "$status" "map_rules: MAP.md missing: exit code"
  assert_contains "MAP.md" "$combined" "map_rules: MAP.md missing: named"
  # A valid MAP.md.
  write_map "$(printf '# Map\n`src/exists.cs` and `docs/dir/` and `.github/` are real.\n%s' "$(for s in $shards; do printf '`%s.md`\n' "$s"; done)")"
  mkdir -p "$tree/.github"
  check
  assert_eq "0" "$status" "map_rules: a valid MAP.md passes"
  # A path that does not exist.
  write_map "$(printf '# Map\n`src/Nope.cs`\n%s' "$(for s in $shards; do printf '`%s.md`\n' "$s"; done)")"
  check
  assert_eq "1" "$status" "map_rules: a missing path: exit code"
  assert_contains "src/Nope.cs" "$combined" "map_rules: a missing path: named"
  # A shard not named in MAP.md.
  write_map "$(printf '# Map\n%s' "$(for s in routes permissions errors events jobs config markers; do printf '`%s.md`\n' "$s"; done)")"
  check
  assert_eq "1" "$status" "map_rules: an unnamed shard: exit code"
  assert_contains "tests.md" "$combined" "map_rules: an unnamed shard: named"
  # 121 lines.
  write_map "$(printf '# Map\n%s\n%s' "$(for s in $shards; do printf '`%s.md`\n' "$s"; done)" "$(for i in $(seq 1 112); do printf 'line %s\n' "$i"; done)")"
  [[ "$(wc -l < "$idx/MAP.md")" -eq 121 ]] || { echo "FAIL: setup: MAP.md is not 121 lines" >&2; exit 1; }
  check
  assert_eq "1" "$status" "map_rules: 121 lines: exit code"
  assert_contains "121" "$combined" "map_rules: 121 lines: says how many"
  # 120 lines pass.
  write_map "$(printf '# Map\n%s\n%s' "$(for s in $shards; do printf '`%s.md`\n' "$s"; done)" "$(for i in $(seq 1 111); do printf 'line %s\n' "$i"; done)")"
  [[ "$(wc -l < "$idx/MAP.md")" -eq 120 ]] || { echo "FAIL: setup: MAP.md is not 120 lines" >&2; exit 1; }
  check
  assert_eq "0" "$status" "map_rules: 120 lines pass"
  # Tokens the rule ignores: a brace, a star, an angle bracket, an ellipsis, a space, and other prefixes.
  write_map "$(printf '# Map\n`src/{Module}/x.cs` `src/*/y.cs` `src/<name>/z.cs` `src/.../w.cs` `docs/a b.md` `elsewhere/none.md` `x`\n%s' \
    "$(for s in $shards; do printf '`%s.md`\n' "$s"; done)")"
  check
  assert_eq "0" "$status" "map_rules: tokens with {, *, <, ... or a space, and other prefixes, are ignored"
  # A CRLF MAP.md gives the same result.
  printf '# Map\r\n`src/exists.cs`\r\n' > "$idx/MAP.md"
  for s in $shards; do printf '`%s.md`\r\n' "$s" >> "$idx/MAP.md"; done
  check
  assert_eq "0" "$status" "map_rules: a CRLF MAP.md passes"
}

case_need_rows() {
  # need_rows is a function of the script; source it (the script runs main only when executed).
  local rc=0
  ( source "$gen"; need_rows routes 0 3 ) > /dev/null 2>&1 || rc=$?
  assert_eq "2" "$rc" "need_rows: zero rows with a source is a parse error"
  rc=0
  ( source "$gen"; need_rows routes 0 0 ) > /dev/null 2>&1 || rc=$?
  assert_eq "0" "$rc" "need_rows: zero rows without a source is fine"
  rc=0
  ( source "$gen"; need_rows routes 5 3 ) > /dev/null 2>&1 || rc=$?
  assert_eq "0" "$rc" "need_rows: rows present is fine"
  local msg
  msg="$( ( source "$gen"; need_rows routes 0 3 ) 2>&1 || true)"
  assert_contains "routes" "$msg" "need_rows: names the shard"
}

# Each case runs in a subshell so that its tree and variables do not leak into the next.
# The subshell reports its failures through its exit code, since variables do not cross the boundary.
run_case() {
  local name="$1" rc=0
  ( failures=0; "$name"; exit $(( failures > 0 ? 1 : 0 )) ) || rc=$?
  if (( rc != 0 )); then failures=$((failures + 1)); fi
}

for c in \
  case_usage_errors case_markers_shard case_markers_kinds_and_expressions case_markers_zero_rows case_stub_shards \
  case_determinism case_untracked_ignored case_git_file_marker case_find_excludes_build_dirs \
  case_check_stale case_check_missing_and_unknown case_map_rules case_need_rows; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "project-index: $failures failure(s)" >&2
  exit 1
fi
echo "project-index: passed"
