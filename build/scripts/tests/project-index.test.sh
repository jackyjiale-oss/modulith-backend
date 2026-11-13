#!/usr/bin/env bash
# Tests for project-index.sh (project index design): the CLI, the markers and routes shards, the MAP.md checks and --check.
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
  if [[ "$1" == */* ]]; then must mkdir -p "$tree/${1%/*}"; fi
  printf '%s\n' "$2" > "$tree/$1" || exit 1
}

# put_raw <relative path> <printf format>: writes a file with exactly the bytes the format gives (for CRLF line endings).
put_raw() {
  if [[ "$1" == */* ]]; then must mkdir -p "$tree/${1%/*}"; fi
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
  printf '%s' "$body" > "$idx/MAP.md" || exit 1
}

# rows <shard>: the table rows (after the header and the separator) of a shard of idx.
rows() {
  local lines=()
  mapfile -t lines < "$idx/$1.md"
  if (( ${#lines[@]} > 8 )); then printf '%s
' "${lines[@]:8}"; fi
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
  local lines=()
  mapfile -t lines < "$idx/markers.md"
  assert_eq '<!-- Generated by build/scripts/project-index.sh; do not edit. Regenerate with: bash build/scripts/project-index.sh -->' \
    "${lines[0]}" "markers_shard: banner"
  assert_eq "" "${lines[1]}" "markers_shard: blank line after the banner"
  assert_eq "# Template switch markers" "${lines[2]}" "markers_shard: title"
  assert_eq "" "${lines[3]}" "markers_shard: blank line after the title"
  assert_eq "Rows: 3" "${lines[4]}" "markers_shard: Rows line"
  assert_eq "" "${lines[5]}" "markers_shard: blank line before the table"
  assert_eq "| file | kinds | expressions |" "${lines[6]}" "markers_shard: header row"
  assert_eq "| --- | --- | --- |" "${lines[7]}" "markers_shard: separator row"
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
  local s text
  for s in $shards; do
    if [[ -f "$idx/$s.md" ]]; then
      text="$(<"$idx/$s.md")"
      assert_contains "Rows: 0" "$text" "stub_shards: $s.md exists with zero rows"
    else
      echo "FAIL: stub_shards: $s.md was not written" >&2
      failures=$((failures + 1))
    fi
  done
  # The headers of the single-table stubs are the plan's column names; the two-table stubs show both tables.
  assert_contains $'\n| module | method | route | handler | permission | file |\n| --- | --- | --- | --- | --- | --- |' \
    "$(<"$idx/routes.md")" "stub_shards: routes header"
  assert_contains $'\n| code | module | admin default | file |\n' "$(<"$idx/permissions.md")" "stub_shards: permissions header"
  assert_contains $'\n| event | publisher | handlers |\n' "$(<"$idx/events.md")" "stub_shards: events header"
  assert_contains $'\n| job | module | config keys | file |\n' "$(<"$idx/jobs.md")" "stub_shards: jobs header"
  assert_contains $'\n| section | owner | documented in |\n' "$(<"$idx/config.md")" "stub_shards: config header"
  assert_eq $'Rows: 0\n\n## Declared codes\n\n| code | module | http | resource key | file |\n| --- | --- | --- | --- | --- |\n\n## Resource-only keys\n\n| key | http | resx file |\n| --- | --- | --- |' \
    "$(tail -n +5 "$idx/errors.md")" "stub_shards: errors shows both tables"
  assert_eq $'Rows: 0\n\n## Folders\n\n| test project | folder | source area | files |\n| --- | --- | --- | --- |\n\n## Name pairs\n\n| source | test |\n| --- | --- |' \
    "$(tail -n +5 "$idx/tests.md")" "stub_shards: tests shows both tables"
}

# Runs a snippet with the generator sourced, in a subshell with its own work and output directories: sets status and combined (the
# snippet's stdout and stderr). Used for the functions that need no tree.
sourced() {
  status=0
  combined="$( ( source "$gen"; work="$(mktemp -d "$tmp_root/work.XXXXXX")"; shard_dir="$work/out"; mkdir -p "$shard_dir"; eval "$1" ) 2>&1 )" || status=$?
}

case_two_table_shard() {
  sourced '
    printf "%s\n" "b|2" "a|1" "a|0" > "$work/r1"
    printf "%s\n" "z|y|x" > "$work/r2"
    shard_begin demo "Demo"
    shard_table "First" "" "k|v" "$work/r1"
    shard_table "Second" "A note, with a | pipe." "p|q|r" "$work/r2"
    shard_end
    cat "$shard_dir/demo.md"'
  local banner='<!-- Generated by build/scripts/project-index.sh; do not edit. Regenerate with: bash build/scripts/project-index.sh -->'
  assert_eq "0" "$status" "two_table_shard: exit code"
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 4\n\n## First\n\n| k | v |\n| --- | --- |\n| a | 0 |\n| a | 1 |\n| b | 2 |\n\n## Second\n\nA note, with a | pipe.\n\n| p | q | r |\n| --- | --- | --- |\n| z | y | x |' \
    "$combined" "two_table_shard: exact bytes (heading, note, tables, total of Rows)"
  # An empty first table still counts nothing and keeps its place; a note and a heading are both optional.
  sourced '
    : > "$work/r1"
    printf "%s\n" "a|1" > "$work/r2"
    shard_begin demo "Demo"
    shard_table "" "" "k|v" "$work/r1"
    shard_table "" "Only a note." "p|q" "$work/r2"
    shard_end
    cat "$shard_dir/demo.md"'
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 1\n\n| k | v |\n| --- | --- |\n\nOnly a note.\n\n| p | q |\n| --- | --- |\n| a | 1 |' \
    "$combined" "two_table_shard: empty first table, note without a heading"
  # write_shard keeps the one-table layout.
  sourced '
    printf "%s\n" "b|2" "a|1" > "$work/r1"
    write_shard demo "Demo" "k|v" "$work/r1"
    cat "$shard_dir/demo.md"'
  assert_eq "$banner"$'\n\n# Demo\n\nRows: 2\n\n| k | v |\n| --- | --- |\n| a | 1 |\n| b | 2 |' \
    "$combined" "two_table_shard: write_shard single-table layout unchanged"
}

# A parse error on a later shard must leave --out as it was: nothing is copied before every shard succeeded.
case_atomic_write() {
  make_tree
  put a.cs "$(printf '//#if (notifications)\n//#endif')"
  must mkdir -p "$idx"
  printf 'old\n' > "$idx/markers.md"
  sourced "emit_jobs() { fail_parse 'boom in jobs'; }; main --root '$tree' --out '$idx'"
  assert_eq "2" "$status" "atomic_write: exit code"
  assert_contains "boom in jobs" "$combined" "atomic_write: names the failure"
  assert_eq "old" "$(<"$idx/markers.md")" "atomic_write: the existing shard is untouched"
  local written=("$idx"/*)
  assert_eq "1" "${#written[@]}" "atomic_write: no other shard was written"
  # Without the failure the same run writes everything.
  sourced "main --root '$tree' --out '$idx'"
  assert_eq "0" "$status" "atomic_write: a run without a failure succeeds"
  assert_contains "| a.cs | //#if | notifications |" "$(<"$idx/markers.md")" "atomic_write: the shard is replaced"
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

# map_body <first-line> [omitted shard] [filler lines]: sets body to a MAP.md that has the first line, then one line naming each shard
# (except the omitted one), then the filler lines.
map_body() {
  local s i
  body="$1"$'\n'
  for s in $shards; do
    if [[ "$s" != "${2-}" ]]; then body+="\`$s.md\`"$'\n'; fi
  done
  for (( i = 1; i <= ${3-0}; i++ )); do body+="line $i"$'\n'; done
}

# map_check: runs check_map of the sourced script on the current tree and idx (no generator run); sets status and combined.
map_check() {
  status=0
  combined="$( ( source "$gen"; root="$tree"; out="$idx"; check_map ) 2>&1 )" || status=$?
}

case_map_rules() {
  make_tree
  put src/exists.cs 'int x;'
  must mkdir -p "$tree/docs/dir" "$tree/.github"
  # MAP.md missing.
  map_check
  assert_eq "1" "$status" "map_rules: MAP.md missing: exit code"
  assert_contains "MAP.md" "$combined" "map_rules: MAP.md missing: named"
  # A valid MAP.md.
  map_body '# Map: `src/exists.cs` and `docs/dir/` and `.github/` are real.'
  write_map "$body"
  map_check
  assert_eq "0" "$status" "map_rules: a valid MAP.md passes"
  # The one end-to-end run: the CLI path of --check (generate, compare, then the MAP.md rules) on a valid MAP.md.
  generate
  check
  assert_eq "0" "$status" "map_rules: --check end to end passes"
  # A path that does not exist.
  map_body '# Map `src/Nope.cs`'
  write_map "$body"
  map_check
  assert_eq "1" "$status" "map_rules: a missing path: exit code"
  assert_contains "src/Nope.cs" "$combined" "map_rules: a missing path: named"
  # A shard not named in MAP.md.
  map_body '# Map' tests
  write_map "$body"
  map_check
  assert_eq "1" "$status" "map_rules: an unnamed shard: exit code"
  assert_contains "tests.md" "$combined" "map_rules: an unnamed shard: named"
  # 121 lines fail, 120 pass.
  local lines=()
  map_body '# Map' '' 112
  write_map "$body"
  mapfile -t lines < "$idx/MAP.md"
  assert_eq "121" "${#lines[@]}" "map_rules: setup: 121 lines"
  map_check
  assert_eq "1" "$status" "map_rules: 121 lines: exit code"
  assert_contains "121" "$combined" "map_rules: 121 lines: says how many"
  map_body '# Map' '' 111
  write_map "$body"
  mapfile -t lines < "$idx/MAP.md"
  assert_eq "120" "${#lines[@]}" "map_rules: setup: 120 lines"
  map_check
  assert_eq "0" "$status" "map_rules: 120 lines pass"
  # Tokens the rule ignores: a brace, a star, an angle bracket, an ellipsis, a space, and other prefixes.
  map_body '# Map `src/{Module}/x.cs` `src/*/y.cs` `src/<name>/z.cs` `src/.../w.cs` `docs/a b.md` `elsewhere/none.md` `x`'
  write_map "$body"
  map_check
  assert_eq "0" "$status" "map_rules: tokens with {, *, <, ... or a space, and other prefixes, are ignored"
  # A CRLF MAP.md gives the same result.
  map_body '# Map `src/exists.cs`'
  printf '%s' "${body//$'\n'/$'\r\n'}" > "$idx/MAP.md"
  map_check
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

# routes_fixture [with-b]: a tree with a Host Program.cs that maps module extensions on `api` (/api/v1) and one at the root, and module
# A: a module file calling the endpoints extension, a root group "a" with a "/" route, a route split over three lines, an anonymous route
# and a child group extension in another file (CRLF) that takes its prefix from the caller. With "with-b", module B maps a JWKS-like
# route at the root through a const string, with no handler parameter.
routes_fixture() {
  local program='var app = builder.Build();
var api = app.MapGroup("/api/v1");
api.MapAModuleEndpoints();'
  if [[ "${1-}" == "with-b" ]]; then program+=$'\napp.MapBWellKnownEndpoints();'; fi
  program+=$'\napp.Run();'
  put src/Host/H/Program.cs "$program"
  put src/Modules/A/A/AModule.cs 'public static class AModule
{
    public static IEndpointRouteBuilder MapAModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAEndpoints();
        return app;
    }
}'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        // A comment that mentions group.MapPut("ignored", Nothing) is not a route.
        var group = app.MapGroup("a")
            .WithTags("A");

        group.MapGet("/", ListAsync).RequirePermission(APermissions.View);

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequirePermission(APermissions.Approve)
            .WithIdempotency();

        group.MapPost("login", LoginAsync).AllowAnonymous(); // trailing comment

        group.MapChildEndpoints();
        return app;
    }

    /// <summary>Lists.</summary>
    private static async Task<IResult> ListAsync(
        [AsParameters] ListRequest request,
        IQueryHandler<ListAQuery, CursorPage<AItem>> handler,
        CancellationToken cancellationToken)
    {
        return TypedResults.Ok();
    }

    private static async Task<IResult> ApproveAsync(Guid id, ICommandHandler<ApproveACommand> handler) => TypedResults.NoContent();

    private static async Task<IResult> LoginAsync(ICommandHandler<LoginCommand, LoginResult> handler) => TypedResults.Ok();
}'
  put_raw src/Modules/A/A/Endpoints/ChildEndpoints.cs 'internal static class ChildEndpoints\r\n{\r\n    internal static RouteGroupBuilder MapChildEndpoints(this RouteGroupBuilder a)\r\n    {\r\n        var child = a.MapGroup("child");\r\n        child.MapDelete("/{id}", RemoveAsync)\r\n            .RequireAuthorization();\r\n        return a;\r\n    }\r\n\r\n    private static async Task<IResult> RemoveAsync(Guid id, ICommandHandler<RemoveCommand> handler) => TypedResults.NoContent();\r\n}\r\n'
  if [[ "${1-}" == "with-b" ]]; then
    put src/Modules/B/B/BModule.cs 'public static class BModule
{
    public static IEndpointRouteBuilder MapBWellKnownEndpoints(this IEndpointRouteBuilder app) => app.MapJwksEndpoints();
}'
    put src/Modules/B/B/Endpoints/BEndpoints.cs 'internal static class BEndpoints
{
    internal const string Route = "/.well-known/jwks.json";

    internal static IEndpointRouteBuilder MapJwksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, GetKeySet)
            .AllowAnonymous();
        return app;
    }

    private static IResult GetKeySet(ISigningKeyProvider keys) => TypedResults.Ok();
}'
  fi
}

# routes_of_tree: runs emit_routes of the sourced script on the current tree (no full generator run); sets status and combined (the
# sorted rows, or the error output).
routes_of_tree() {
  sourced "root='$tree'; cd '$tree'; prepare_files; emit_routes > \"\$work/routes.out\"; sort \"\$work/routes.out\""
}

routes_a_rows='A|DELETE|/api/v1/a/child/{id}|RemoveCommand|authenticated|src/Modules/A/A/Endpoints/ChildEndpoints.cs
A|GET|/api/v1/a|ListAQuery|APermissions.View|src/Modules/A/A/Endpoints/AEndpoints.cs
A|POST|/api/v1/a/login|LoginCommand|anonymous|src/Modules/A/A/Endpoints/AEndpoints.cs
A|POST|/api/v1/a/{id:guid}/approve|ApproveACommand|APermissions.Approve|src/Modules/A/A/Endpoints/AEndpoints.cs'

case_routes() {
  make_tree
  routes_fixture with-b
  routes_of_tree
  assert_eq "0" "$status" "routes: exit code"
  assert_eq "$routes_a_rows"$'\nB|GET|/.well-known/jwks.json|-|anonymous|src/Modules/B/B/Endpoints/BEndpoints.cs' "$combined" \
    "routes: exact rows (root route without //, three-line chain, child group prefix from the caller, root-mapped const route)"
  # The one end-to-end run of the routes shard: the rendered table.
  generate
  assert_contains $'Rows: 5\n\n| module | method | route | handler | permission | file |\n| --- | --- | --- | --- | --- | --- |\n| A | DELETE | /api/v1/a/child/{id} | RemoveCommand | authenticated | src/Modules/A/A/Endpoints/ChildEndpoints.cs |' \
    "$(<"$idx/routes.md")" "routes: rendered shard"
}

case_routes_zero() {
  make_tree
  put src/Host/H/Program.cs $'var api = app.MapGroup("/api/v1");\napi.MapAEndpoints();'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.Map("x", HandleAsync);
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: exit code"
  assert_contains "no routes parsed in src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: names the file"
  # A file with some routes and one mapping of an unknown shape fails too, rather than emitting fewer rows.
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("a");
        group.MapGet("x", GetAsync).RequireAuthorization();
        group.MapMethods("y", ["HEAD"], HeadAsync);
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: an unknown mapping beside known ones: exit code"
  assert_contains "src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: an unknown mapping: names the file"
  # An extension that nothing calls has no prefix: a parse error, not a guessed route.
  put src/Host/H/Program.cs 'var api = app.MapGroup("/api/v1");'
  put src/Modules/A/A/Endpoints/AEndpoints.cs 'internal static class AEndpoints
{
    internal static IEndpointRouteBuilder MapAEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("x", GetAsync).RequireAuthorization();
        return app;
    }
}'
  routes_of_tree
  assert_eq "2" "$status" "routes_zero: an uncalled extension: exit code"
  assert_contains "src/Modules/A/A/Endpoints/AEndpoints.cs" "$combined" "routes_zero: an uncalled extension: names the file"
}

case_routes_module_absent() {
  make_tree
  routes_fixture
  routes_of_tree
  assert_eq "0" "$status" "routes_module_absent: exit code"
  assert_eq "$routes_a_rows" "$combined" "routes_module_absent: only module A rows"
  # A tree with no endpoint file at all has no rows and no error.
  make_tree
  put src/Host/H/Program.cs 'var api = app.MapGroup("/api/v1");'
  routes_of_tree
  assert_eq "0" "$status" "routes_module_absent: no endpoint file: exit code"
  assert_eq "" "$combined" "routes_module_absent: no endpoint file: no rows"
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
  case_two_table_shard case_atomic_write \
  case_determinism case_untracked_ignored case_git_file_marker case_find_excludes_build_dirs \
  case_check_stale case_check_missing_and_unknown case_map_rules case_need_rows \
  case_routes case_routes_zero case_routes_module_absent; do
  run_case "$c"
done

if (( failures > 0 )); then
  echo "project-index: $failures failure(s)" >&2
  exit 1
fi
echo "project-index: passed"
