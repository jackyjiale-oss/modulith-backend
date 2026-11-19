#!/usr/bin/env bash
# Static rules for the repository's own workflows and scripts. Nothing is built or run: the files are read as text, so the whole
# test takes about a second. Each rule is first proven on scratch files (it must reject the bad form and accept the good one) and
# then applied to the real files.
#
#   1. No "${{ ... }}" expression inside a run: step of a workflow. GitHub pastes an expression into the script text before the
#      shell starts, so a value an outsider can choose (a branch name, a pull request title, a dispatch input) would run as code.
#      A value reaches a step through env: and is read there as a shell variable.
#   2. Every `git ls-files` reads its names with -z or with core.quotepath=off. By default git prints a path that holds a
#      non-ASCII character in quotes with escape sequences, and a script that compares that text with a file name gets it wrong.
#   3. No switch marker (//#if, //#endif and the like, ADR 0035) between two lines of one C# doc comment (///). In the template's
#      own tree the marker line splits the comment into two halves that are not valid XML; the generated project never sees it.
#      A sentence that depends on a switch goes after the doc comment, as a plain comment inside the marker.
#
# Usage: bash build/scripts/tests/static-rules.test.sh   (from any directory)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
self="build/scripts/tests/static-rules.test.sh"

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}

tmp="$(mktemp -d)"
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT

# run_expressions <workflow>...: prints "file:line: text" for each line of a run: step (the inline form or a block scalar) that
# holds "${{". A block ends at the first line that is not indented deeper than its run: key.
run_expressions() {
  awk '
    function indent_of(text) { match(text, /^ */); return RLENGTH }
    FNR == 1 { in_block = 0 }
    {
      line = $0
      sub(/\r$/, "", line)
      if (in_block) {
        if (line ~ /^[[:space:]]*$/ || indent_of(line) > block_indent) {
          if (index(line, "${{")) print FILENAME ":" FNR ": " line
          next
        }
        in_block = 0
      }
      if (match(line, /^[[:space:]]*(- )?run:[[:space:]]*/)) {
        rest = substr(line, RLENGTH + 1)
        key = line
        sub(/run:.*/, "", key)
        block_indent = length(key)
        if (rest ~ /^[|>][0-9+-]*[[:space:]]*(#.*)?$/) in_block = 1
        else if (index(rest, "${{")) print FILENAME ":" FNR ": " line
      }
    }
  ' "$@"
}

# unsafe_ls_files <file>...: prints "file:line: text" for each line that calls `git ... ls-files` with neither -z nor
# core.quotepath=off. Comment lines are skipped.
unsafe_ls_files() {
  awk '
    /^[[:space:]]*#/ { next }
    /git( [^|;&]*)? ls-files/ && !/ls-files( [^|;&)]*)? -z/ && !/core\.quotepath=off/ { print FILENAME ":" FNR ": " $0 }
  ' "$@"
}

# Rule 1, proven on scratch workflows.
printf '%s\n' 'jobs:' '  scratch:' '    steps:' '      - run: echo "${{ github.head_ref }}"' > "$tmp/bad-inline.yml"
[[ "$(run_expressions "$tmp/bad-inline.yml" | wc -l)" -eq 1 ]] || fail "the run: rule accepted an expression in an inline run: step"

printf '%s\n' 'jobs:' '  scratch:' '    steps:' '      - name: block' '        run: |' '          echo one' '' \
  '          echo "${{ inputs.tag }}"' '        env:' '          OTHER: ${{ github.sha }}' > "$tmp/bad-block.yml"
found="$(run_expressions "$tmp/bad-block.yml")"
[[ "$(wc -l <<< "$found")" -eq 1 && "$found" == *":8: "* ]] ||
  fail "the run: rule did not report exactly line 8 of a block scalar (the env: line after the block is not part of it); it printed: $found"

printf '%s\n' 'jobs:' '  scratch:' '    steps:' '      - run: >-' '          echo "${{ github.ref_name }}"' > "$tmp/bad-folded.yml"
[[ -n "$(run_expressions "$tmp/bad-folded.yml")" ]] || fail "the run: rule accepted an expression in a folded (>-) run: step"

printf '%s\n' 'jobs:' '  scratch:' '    if: ${{ always() }}' '    steps:' '      - uses: actions/checkout@v7' '        with:' \
  '          ref: refs/tags/${{ env.TAG }}' '      - name: good' '        env:' '          TAG: ${{ inputs.tag }}' '        run: |' \
  '          echo "$TAG"' '      - run: echo plain' > "$tmp/good.yml"
found="$(run_expressions "$tmp/good.yml")"
[[ -z "$found" ]] || fail "the run: rule rejected expressions outside a run: step: $found"

# Rule 1 on the real workflows.
mapfile -t workflows < <(find "$repo_root/.github/workflows" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) | LC_ALL=C sort)
if ((${#workflows[@]} == 0)); then
  fail "no workflow file was found under .github/workflows"
else
  found="$(run_expressions "${workflows[@]}")"
  [[ -z "$found" ]] || fail "an expression is used inside a run: step (pass the value through env: instead):"$'\n'"${found//"$repo_root/"/}"
fi

# Rule 2, proven on scratch scripts.
printf '%s\n' 'done < <(git -C "$repo_root" ls-files)' > "$tmp/bad-loop.sh"
[[ -n "$(unsafe_ls_files "$tmp/bad-loop.sh")" ]] || fail "the ls-files rule accepted a plain 'git -C ... ls-files'"
printf '%s\n' '(cd "$repo_root" && git ls-files | sort) > list.txt' > "$tmp/bad-pipe.sh"
[[ -n "$(unsafe_ls_files "$tmp/bad-pipe.sh")" ]] || fail "the ls-files rule accepted a plain 'git ls-files' in a pipe"
printf '%s\n' '# git ls-files in a comment' 'git -c core.quotepath=off ls-files' 'git -C "$root" ls-files -z --deleted > deleted.z' \
  'git -C "$root" ls-files --others --exclude-standard -z -- src' > "$tmp/good.sh"
found="$(unsafe_ls_files "$tmp/good.sh")"
[[ -z "$found" ]] || fail "the ls-files rule rejected a safe call or a comment: $found"

# Rule 2 on the real scripts, hooks and workflows (this file's own scratch lines aside).
mapfile -t sources < <(cd "$repo_root" && { find build/scripts .githooks .github/workflows -type f 2> /dev/null || true; } |
  grep -vxF "$self" | LC_ALL=C sort)
if ((${#sources[@]} == 0)); then
  fail "no script was found under build/scripts"
else
  found="$(cd "$repo_root" && unsafe_ls_files "${sources[@]}")"
  [[ -z "$found" ]] || fail "git ls-files is read without -z and without core.quotepath=off (a non-ASCII path would come back quoted):"$'\n'"$found"
fi

# markers_in_doc_comments <file>...: prints "file:line: text" for each switch marker line whose nearest lines above and below that
# are not marker lines are both doc comment lines (///).
markers_in_doc_comments() {
  awk '
    FNR == 1 { after_doc = 0; pending = "" }
    {
      line = $0
      sub(/\r$/, "", line)
      if (line ~ /^[[:space:]]*\/\/#(if|elseif|else|endif)([[:space:]]|\(|$)/) {
        if (after_doc) pending = pending FILENAME ":" FNR ": " line "\n"
        next
      }
      is_doc = (line ~ /^[[:space:]]*\/\/\//)
      if (is_doc && pending != "") printf "%s", pending
      pending = ""
      after_doc = is_doc
    }
  ' "$@"
}

# Rule 3, proven on scratch files.
printf '%s\n' '/// <summary>' '/// Always.' '//#if (notifications)' '/// Only with Notifications.' '//#endif' '/// </summary>' \
  'public sealed class A;' > "$tmp/bad-doc.cs"
found="$(markers_in_doc_comments "$tmp/bad-doc.cs")"
[[ "$(wc -l <<< "$found")" -eq 2 && "$found" == *":3: "* && "$found" == *":5: "* ]] ||
  fail "the doc-comment rule did not report lines 3 and 5 of a doc comment split by a marker; it printed: $found"
printf '%s\n' '/// <summary>' '/// Always.' '/// </summary>' '//#if (notifications)' '// Only with Notifications.' '//#endif' \
  'public sealed class A' '{' '    //#if (notifications)' '    private int b;' '    //#endif' '    /// <summary>One.</summary>' \
  '    //#if (includeSample)' '    private int c;' '    //#endif' '}' > "$tmp/good-doc.cs"
found="$(markers_in_doc_comments "$tmp/good-doc.cs")"
[[ -z "$found" ]] || fail "the doc-comment rule rejected a marker outside a doc comment: $found"

# Rule 3 on the real C# files.
mapfile -t csharp_files < <(cd "$repo_root" && git -c core.quotepath=off ls-files -- '*.cs')
if ((${#csharp_files[@]} == 0)); then
  fail "no tracked C# file was found"
else
  found="$(cd "$repo_root" && markers_in_doc_comments "${csharp_files[@]}")"
  [[ -z "$found" ]] || fail "a switch marker splits a doc comment (put the switch-dependent sentence after it as a plain comment):"$'\n'"$found"
fi

if ((failures > 0)); then
  echo "static-rules: $failures failure(s)" >&2
  exit 1
fi

echo "static-rules tests passed"
