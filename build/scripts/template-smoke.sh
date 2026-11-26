#!/usr/bin/env bash
# Template smoke test: installs this repository as a `dotnet new` template, generates a project
# from it, checks the generated files, then builds it and runs its unit and architecture tests.
#
# Usage: bash build/scripts/template-smoke.sh [--notifications true|false] [--includeSample true|false] [--caching redis|memory]
#                                             [--add-module NAME [--module-contracts]] [--from-package [PATH]]
#                                             [--integration] [--gates] [--generate-only] [--output DIR]
#   --notifications, --includeSample, --caching  the template switches, passed to `dotnet new` (defaults: true, true, redis).
#   --add-module NAME  after the restore and the path-length guard, add the module NAME to the generated project with the item template
#                   (`dotnet new modulith-module -n NAME --prefix Acme.Smoke --allow-scripts yes`, from the same private hive); the
#                   format check, build and tests then run on the result. NAME is PascalCase (for example Billing or LeaveManagement).
#                   Before them, `dotnet restore --locked-mode` must pass and the same command run again must exit 73 and change nothing.
#   --module-contracts  give that module a .Contracts project too (`--contracts true`); only valid together with --add-module.
#   --from-package [PATH]  install the template from the NuGet package (Modulith.Backend.Templates) instead of from the checkout: PATH,
#                   an absolute path to an existing .nupkg, when given (the next argument is taken as PATH unless it starts with
#                   "--"); otherwise the checkout is packed as version 0.0.0-smoke into the temporary directory with
#                   `release-package.sh pack`. A package that lacks a file of the checkout (a dotfile, say) then fails the smoke:
#                   a step after the generation checks installs the checkout in a second private hive, generates the same project
#                   and compares the two trees (`diff -r`, ignoring the new UserSecretsId GUID), and does the same for the
#                   modulith-module item template (generated with --allow-scripts no, so the engine exits 104 from both installs).
#                   It composes with --generate-only, --add-module and the switches. The environment variable
#                   RELEASE_PACKAGE_PACK_ARGS reaches that pack unchanged (a test hook: the negative test runs with
#                   RELEASE_PACKAGE_PACK_ARGS='-p:NoDefaultExcludes=false', which drops the dotfiles, and the smoke must then fail).
#   --integration   also run the integration tests (they need Docker); without it only the unit and architecture tests run.
#   --gates         also run the quality gates of the generated project's CI (ci.yml): `dotnet format --verify-no-changes`, the tests
#                   with coverage and the 80 % line-coverage gate, and the licence check. The coverage covers the tests that ran, so
#                   without --integration it is a lower bound of what CI measures.
#   --generate-only stop after the generation checks (about 7 minutes; with --from-package about twice as long, because of the second
#                   hive and the second generation); no restore, build or tests, and none of the repo-only script
#                   tests either (see below). It cannot be combined with --add-module (the module is added after the restore).
#                   The full smoke takes 5 to 15 minutes, plus about 5 for the repo-only script tests.
#   --output DIR    keep the generated project in DIR, an absolute path that must not exist (use a short path such as C:/w/g1 on Windows).
# With no arguments it generates with every switch on, builds and runs the unit and architecture tests.
#
# A full run also runs the repo-only script tests (build/scripts/tests/static-rules.test.sh, module-anchors.test.sh,
# module-template-files.test.sh, add-module.test.sh and release-package.test.sh; add-module takes about 5 minutes) in one step,
# "Running the repo-only script tests".
#
# CI runs the integration tests of the default project in the build-test job; --integration is for a machine with Docker.
# Every `dotnet new` call uses a private template hive inside the temporary directory, so the user's global
# template cache is never touched; deleting the temporary directory removes the installed template too.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_name="Acme.Smoke"

notifications="true"
include_sample="true"
caching="redis"
run_integration=false
run_gates=false
generate_only=false
output_dir=""
add_module=""
module_contracts=false
from_package=false
package_path=""

usage_error() {
  echo "template-smoke: $1" >&2
  echo "usage: template-smoke.sh [--notifications true|false] [--includeSample true|false] [--caching redis|memory] [--add-module NAME [--module-contracts]] [--from-package [PATH]] [--integration] [--gates] [--generate-only] [--output DIR]" >&2
  exit 2
}

while (( $# > 0 )); do
  case "$1" in
    --notifications | --includeSample | --caching | --output | --add-module)
      (( $# >= 2 )) || usage_error "$1 needs a value"
      case "$1" in
        --notifications) notifications="$2" ;;
        --includeSample) include_sample="$2" ;;
        --caching) caching="$2" ;;
        --output) output_dir="$2" ;;
        --add-module) add_module="$2" ;;
      esac
      shift 2
      ;;
    --module-contracts) module_contracts=true; shift ;;
    --from-package)
      from_package=true
      # PATH is optional: the next argument is its value unless it is another option.
      if (( $# >= 2 )) && [[ "$2" != --* ]]; then package_path="$2"; shift 2; else shift; fi
      ;;
    --integration) run_integration=true; shift ;;
    --gates) run_gates=true; shift ;;
    --generate-only) generate_only=true; shift ;;
    *) usage_error "unknown argument '$1'" ;;
  esac
done
if [[ "$module_contracts" == "true" && -z "$add_module" ]]; then
  usage_error "--module-contracts is only valid together with --add-module NAME"
fi
if [[ -n "$add_module" && "$generate_only" == "true" ]]; then
  usage_error "--add-module cannot be combined with --generate-only: the module is added after the restore, which --generate-only skips"
fi
# The same rule as add-module.sh: a capital, then letters and digits (the name becomes folders, namespaces and type names).
if [[ -n "$add_module" ]]; then
  case "$add_module" in
    [ABCDEFGHIJKLMNOPQRSTUVWXYZ]*) ;;
    *) usage_error "--add-module '$add_module' is not a module name: use PascalCase letters and digits only, starting with a capital (for example LeaveManagement)" ;;
  esac
  case "$add_module" in
    *[!ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789]*)
      usage_error "--add-module '$add_module' is not a module name: use PascalCase letters and digits only, starting with a capital (for example LeaveManagement)" ;;
  esac
fi
case "$notifications" in true | false) ;; *) usage_error "--notifications must be 'true' or 'false', got '$notifications'" ;; esac
case "$include_sample" in true | false) ;; *) usage_error "--includeSample must be 'true' or 'false', got '$include_sample'" ;; esac
case "$caching" in redis | memory) ;; *) usage_error "--caching must be 'redis' or 'memory', got '$caching'" ;; esac
# The project is generated after `cd "$repo_root"`, so a relative path would land inside the checkout.
absolute_path='^(/|[A-Za-z]:[/\\])'
if [[ -n "$output_dir" && ! "$output_dir" =~ $absolute_path ]]; then
  usage_error "--output $output_dir must be an absolute path"
fi
if [[ -n "$output_dir" && -e "$output_dir" ]]; then
  usage_error "--output $output_dir already exists; give a directory that does not exist"
fi
if [[ -n "$package_path" ]]; then
  [[ "$package_path" =~ $absolute_path ]] || usage_error "--from-package $package_path must be an absolute path"
  [[ "$package_path" == *.nupkg ]] || usage_error "--from-package $package_path is not a .nupkg file"
  [[ -f "$package_path" ]] || usage_error "--from-package $package_path does not exist"
fi
all_on=true
if [[ "$notifications" != "true" || "$include_sample" != "true" || "$caching" != "redis" ]]; then
  all_on=false
fi

tmp="$(mktemp -d)"
out="$tmp/$project_name"
if [[ -n "$output_dir" ]]; then out="$output_dir"; fi
hive="$tmp/hive"

cleanup() {
  local status=$?
  rm -rf "$tmp"
  exit "$status"
}
trap cleanup EXIT

failures=0
required_count=0
fail() {
  echo "FAIL: $1" >&2
  failures=$((failures + 1))
}

step() {
  echo
  echo "==> $1"
}

step "Checking the Windows path-length guard in the template itself"
bash "$repo_root/build/scripts/path-guard-check.sh" "$repo_root"

step "Testing the dead-reference check (its own test)"
bash "$repo_root/build/scripts/tests/dead-reference-check.test.sh"

cd "$repo_root"
if [[ "$from_package" == "true" ]]; then
  if [[ -z "$package_path" ]]; then
    step "Packing the checkout as version 0.0.0-smoke into $tmp/pack"
    # release-package.sh pack prints the absolute path of the .nupkg as its last line; dotnet pack's own output goes to stderr.
    package_path="$(bash "$repo_root/build/scripts/release-package.sh" pack 0.0.0-smoke "$tmp/pack" | tail -n 1)"
  fi
  step "Installing the template from the package $package_path (private hive $hive)"
  dotnet new install "$package_path" --force --debug:custom-hive "$hive"
else
  step "Installing the template from $repo_root (private hive $hive)"
  dotnet new install . --force --debug:custom-hive "$hive"
fi

step "Generating $project_name (notifications=$notifications, includeSample=$include_sample, caching=$caching) into $out"
dotnet new modulith-backend -n "$project_name" -o "$out" --debug:custom-hive "$hive" \
  --notifications "$notifications" --includeSample "$include_sample" --caching "$caching"

step "Checking that no placeholder is left"
leftover_content="$(grep -rIil "TemplateName" "$out" --exclude-dir=bin --exclude-dir=obj || true)"
if [[ -n "$leftover_content" ]]; then
  fail "files still contain 'TemplateName':"$'\n'"$leftover_content"
fi
leftover_names="$(find "$out" -iname '*TemplateName*' -not -path '*/bin/*' -not -path '*/obj/*')"
if [[ -n "$leftover_names" ]]; then
  fail "file or folder names still contain 'TemplateName':"$'\n'"$leftover_names"
fi

step "Checking the generated files"
for required in \
  README.md \
  CHANGELOG.md \
  CLAUDE.md \
  CONTRIBUTING.md \
  .release-please-manifest.json \
  Version.props \
  Directory.Build.targets \
  release-please-config.json \
  .github/workflows/release.yml \
  build/licenses/allowed-licenses.json \
  docs/README.md \
  docs/architecture/overview.md \
  docs/services/api.md \
  docs/deployment/README.md \
  build/docker/Dockerfile \
  .dockerignore \
  build/scripts/container-verify.sh \
  build/scripts/add-module.sh \
  build/scripts/module-anchors.txt \
  build/scripts/deploy-iis.ps1 \
  build/deploy/roll-out.sh \
  build/deploy/maintenance.html \
  build/deploy/maintenance.sh \
  .github/workflows/cd.yml \
  .github/workflows/codeql.yml \
  .github/workflows/gitleaks.yml; do
  required_count=$((required_count + 1))
  [[ -f "$out/$required" ]] || fail "$required is missing"
done
echo "Checked $required_count required files"
# The image starts the generated API, so its entry point and health check name the renamed assembly.
if [[ -f "$out/build/docker/Dockerfile" ]] && ! grep -qF "$project_name.Api.dll" "$out/build/docker/Dockerfile"; then
  fail "build/docker/Dockerfile does not name $project_name.Api.dll"
fi
# The maintenance page is served by every deployment path (app_offline.htm on IIS, the Docker hook): all three languages in one
# self-contained file, kept out of search indexes, refreshing by itself.
maintenance_page="$out/build/deploy/maintenance.html"
if [[ -f "$maintenance_page" ]]; then
  for marker in '<section lang="en"' '<section lang="ms"' '<section lang="zh-Hans"' 'http-equiv="refresh"' 'noindex'; do
    grep -qF "$marker" "$maintenance_page" || fail "build/deploy/maintenance.html does not contain $marker"
  done
  if grep -nE 'https?://|src=|<link' "$maintenance_page" > /dev/null; then
    fail "build/deploy/maintenance.html must be self-contained: no http://, https://, src= or <link"
  fi
fi
# The Docker maintenance hook is a stub that fails on purpose, like roll-out.sh, and says why on stderr.
if [[ -f "$out/build/deploy/maintenance.sh" ]]; then
  maintenance_stderr="$tmp/maintenance-stderr.txt"
  if bash "$out/build/deploy/maintenance.sh" on production "$maintenance_page" > /dev/null 2> "$maintenance_stderr"; then
    fail "build/deploy/maintenance.sh must exit non-zero until the project writes its hook"
  elif [[ ! -s "$maintenance_stderr" ]]; then
    fail "build/deploy/maintenance.sh must explain on stderr why it failed"
  fi
fi
# A generated project must not look like the template repository: its delivery jobs are skipped where .template.config exists
# (cd.yml, job detect). The only references allowed are that guard and the skip of the template smoke test in ci.yml.
template_config_uses="$(cd "$out" && grep -rnF '.template.config' .github/workflows .github/actionlint.yaml build/docker build/deploy build/scripts .dockerignore \
  | grep -vE '^\.github/workflows/cd\.yml:[0-9]+: +if \[ -f \.template\.config/template\.json \]; then$' \
  | grep -vE '^\.github/workflows/ci\.yml:[0-9]+: +(# Projects generated from the template have no \.template\.config, so the step is skipped there\.|if: \$\{\{ hashFiles\(.\.template\.config/template\.json.\) != .. \}\})$' \
  || true)"
if [[ -n "$template_config_uses" ]]; then
  fail "a generated workflow or script refers to .template.config beyond the guard in cd.yml:"$'\n'"$template_config_uses"
fi
if [[ -f "$out/.github/workflows/cd.yml" ]] && [[ "$(grep -cF '.template.config' "$out/.github/workflows/cd.yml")" != 1 ]]; then
  fail ".github/workflows/cd.yml must refer to .template.config exactly once, in the template guard"
fi
if [[ -f "$out/.release-please-manifest.json" && "$(tr -d '[:space:]' < "$out/.release-please-manifest.json")" != '{".":"0.0.0"}' ]]; then
  fail ".release-please-manifest.json must be { \".\": \"0.0.0\" }, not the template's own version"
fi
if [[ -f "$out/Version.props" ]] && ! grep -qF "<Version>0.0.0</Version>" "$out/Version.props"; then
  fail "Version.props must contain <Version>0.0.0</Version>, not the template's own version"
fi
if [[ -f "$out/release-please-config.json" ]] && grep -qiE "prerelease|release-as" "$out/release-please-config.json"; then
  fail "release-please-config.json contains prerelease or release-as text: the release-candidate setting leaked into the generated project"
fi
if [[ -f "$out/docs/README.md" ]] && grep -qF "repository-management.md" "$out/docs/README.md"; then
  fail "docs/README.md links repository-management.md, which is not generated"
fi
if [[ -f "$out/README.md" && "$(head -n 1 "$out/README.md" | tr -d '\r')" != "# $project_name" ]]; then
  fail "README.md does not start with '# $project_name'"
fi
if [[ -f "$out/CHANGELOG.md" ]] && ! grep -qF "## [Unreleased]" "$out/CHANGELOG.md"; then
  fail "CHANGELOG.md has no '## [Unreleased]' section"
fi
if [[ -f "$out/CLAUDE.md" ]] && grep -qF "Maintaining the template" "$out/CLAUDE.md"; then
  fail "CLAUDE.md contains the template-maintenance section"
fi

for forbidden in \
  LICENSE \
  SECURITY.md \
  BACKEND_TEMPLATE_BLUEPRINT.md \
  docs/blueprint-review.md \
  docs/repository-management.md \
  docs/releasing.md \
  build/pack \
  docs/superpowers \
  build/template-content \
  build/scripts/configure-github-repo.sh \
  build/scripts/template-smoke.sh \
  build/scripts/path-guard-check.sh \
  build/scripts/dead-reference-check.sh \
  build/scripts/tests/dead-reference-check.test.sh \
  build/scripts/project-index.sh \
  build/scripts/tests/project-index.test.sh \
  build/scripts/tests/module-anchors.test.sh \
  build/scripts/tests/module-template-files.test.sh \
  build/scripts/tests/add-module.test.sh \
  build/scripts/release-package.sh \
  build/scripts/tests/release-package.test.sh \
  build/scripts/tests/static-rules.test.sh \
  templates \
  .github/workflows/template-matrix.yml \
  .github/workflows/project-index.yml \
  .template.config \
  .superpowers \
  .vs \
  TestResults; do
  if [[ -e "$out/$forbidden" ]]; then
    fail "$forbidden must not be generated"
  fi
done

template_secrets_id="$(sed -n 's:.*<UserSecretsId>\(.*\)</UserSecretsId>.*:\1:p' "$repo_root/src/Host/TemplateName.Api/TemplateName.Api.csproj")"
generated_csproj="$out/src/Host/$project_name.Api/$project_name.Api.csproj"
generated_secrets_id=""
if [[ -f "$generated_csproj" ]]; then
  generated_secrets_id="$(sed -n 's:.*<UserSecretsId>\(.*\)</UserSecretsId>.*:\1:p' "$generated_csproj")"
else
  fail "the generated Api project $generated_csproj is missing"
fi
if [[ -z "$generated_secrets_id" || "$generated_secrets_id" == "$template_secrets_id" ]]; then
  fail "the generated UserSecretsId ('$generated_secrets_id') must be new, not the template's ('$template_secrets_id')"
fi

if [[ -f "$out/Directory.Build.targets" ]] && ! grep -qF 'Name="CheckWindowsPathLength"' "$out/Directory.Build.targets"; then
  fail "Directory.Build.targets does not define the CheckWindowsPathLength target"
fi
if [[ -f "$out/README.md" ]] && ! grep -qF "TN0001" "$out/README.md"; then
  fail "README.md does not document the TN0001 long-path error"
fi

step "Checking that no template marker is left in the generated files"
# A conditional marker of the template engine ("#if", "#elseif", "#else", "#endif" after //, <!-- or %% (the Mermaid comment style used in
# docs/architecture/overview.md), or at the start of a line) that survives generation means the engine did not process it.
# Every file type is searched, not only code.
leftover_markers="$(grep -rInE --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
  -e '(//|<!--|%%|^[[:space:]]*)#(if|elseif|else|endif)([[:space:]]|\(|-->|$)' "$out" || true)"
if [[ -n "$leftover_markers" ]]; then
  fail "template conditional markers are left in the generated files:"$'\n'"$leftover_markers"
fi

step "Checking the module anchors in the generated files"
# The generated build/scripts/module-anchors.txt lists "key|path|anchor text" rows. The engine's sourceName replacement rewrites
# TemplateName inside this list too, so its paths already name the project (add-module.sh relies on that). add-module.sh edits only
# at those anchors, so every row must name a generated file that holds its anchor line (an anchor sits outside the switch markers, so it survives every switch combination), and no
# generated file may carry a "modulith:" anchor that the list does not name. The list is read with awk, not grep -iF, which aborts on
# some Git Bash builds. check_module_anchors DIR prints one "FAIL:" line per problem to standard error; it returns 1 on any.
check_module_anchors() {
  local dir="$1" list row_path row_text problems=0 anchored leaked file
  list="$dir/build/scripts/module-anchors.txt"
  if [[ ! -f "$list" ]]; then
    echo "FAIL: build/scripts/module-anchors.txt is missing" >&2
    return 1
  fi
  anchored="$(awk -F '|' 'NF >= 3 { sub(/\r$/, "", $2); print $2 }' "$list" | sort -u)"
  [[ -n "$anchored" ]] || { echo "FAIL: build/scripts/module-anchors.txt lists no anchor" >&2; return 1; }
  while IFS=$'\t' read -r row_path row_text; do
    if [[ ! -f "$dir/$row_path" ]]; then
      echo "FAIL: build/scripts/module-anchors.txt names $row_path, which is not generated" >&2
      problems=$((problems + 1))
    elif ! awk -v text="$row_text" '{ sub(/\r$/, "") } index($0, text) { found = 1; exit } END { exit !found }' "$dir/$row_path"; then
      echo "FAIL: $row_path has no line with the anchor '$row_text' that build/scripts/module-anchors.txt names" >&2
      problems=$((problems + 1))
    fi
  done < <(awk -F '|' 'NF >= 3 { sub(/\r$/, "", $2); sub(/\r$/, "", $3)
    sub(/^[ \t]+/, "", $3); sub(/[ \t]+$/, "", $3); print $2 "\t" $3 }' "$list")
  leaked="$(grep -rIl --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git -e 'modulith:' "$dir" || true)"
  while IFS= read -r file; do
    [[ -n "$file" ]] || continue
    file="${file#"$dir"/}"
    [[ "$file" == build/scripts/module-anchors.txt ]] && continue
    if ! grep -qxF -e "$file" <<< "$anchored"; then
      echo "FAIL: $file carries a 'modulith:' anchor but is not listed in build/scripts/module-anchors.txt" >&2
      problems=$((problems + 1))
    fi
  done <<< "$leaked"
  (( problems == 0 ))
}
check_module_anchors "$out" 2> "$tmp/anchor-problems.txt" || {
  failures=$((failures + 1))
  cat "$tmp/anchor-problems.txt" >&2
}

if [[ "$all_on" == "true" ]]; then
  step "Checking that the all-on project is the template source without its marker lines"
  # The engine reads an unknown symbol (a typo such as "notfications") as false, and it also reads a plain C# "#if" or a marker
  # quoted inside a line: any of them would silently drop content from every generation. So each generated file must equal its
  # source (the build/template-content/ file that replaces it, else the same path with the placeholder put back) with the whole
  # marker lines deleted and the placeholder renamed as the engine does. The UserSecretsId GUID is new in each generation (above).
  marker_line='^[[:space:]]*(\/\/|<!--|%%)?#(if|elseif|else|endif)([[:space:]]|\(|-->|$)'
  while IFS= read -r -d '' generated; do
    relative="${generated#"$out"/}"
    source="$repo_root/build/template-content/$relative"
    [[ -f "$source" ]] || source="$repo_root/${relative//"$project_name"/TemplateName}"
    printf '%s\t%s\n' "$generated" "$source"
  done < <(find "$out" -type f -not -path '*/bin/*' -not -path '*/obj/*' -print0) > "$tmp/drift-pairs.tsv"
  # One awk process for all files (a sed per file takes minutes on Windows); prints "generated<TAB>source<TAB>where" per mismatch.
  drift="$(MARKER="$marker_line" awk -F '\t' -v name="$project_name" -v lower="${project_name,,}" '
    function next_expected(   line) {
      while ((getline line < $2) > 0) if (line !~ ENVIRON["MARKER"]) {
        gsub(/TemplateName/, name, line); gsub(/templatename/, lower, line); expected = line; return 1 }
      return 0 }
    { if ((getline probe < $2) < 0) { print $1 "\t" $2 "\tno source file"; next } close($2); n = 0; bad = 0
      while (!bad && (getline actual < $1) > 0) { n++
        if (!next_expected() || (actual != expected && !(actual ~ /<UserSecretsId>/ && expected ~ /<UserSecretsId>/))) bad = 1 }
      if (!bad && next_expected()) { n++; bad = 1 }
      close($1); close($2); if (bad) print $1 "\t" $2 "\tfirst difference at line " n }' "$tmp/drift-pairs.tsv")"
  while IFS=$'\t' read -r generated source where; do
    [[ -n "$generated" ]] || continue
    excerpt="$(diff <(sed -E -e "/$marker_line/d" -e "s/TemplateName/$project_name/g; s/templatename/${project_name,,}/g" "$source" 2> /dev/null) \
      "$generated" | head -n 8 || true)"
    fail "${generated#"$out"/} is not ${source#"$repo_root"/} without its marker lines ($where):"$'\n'"$excerpt"
  done <<< "$drift"

  # The file list, the other way: a tracked file missing from the all-on project must be left out by an unconditional exclude of
  # template.json, or by the one modifier meant to apply with every switch on ((caching == "redis"): files only a memory project has).
  # A file that any other modifier left out means a condition is true with every switch on (a typo'd symbol, say).
  mapfile -t all_on_excludes < <(awk '/"modifiers"/ { modifiers = 1 } /"condition"/ { condition = $0 }
    /"exclude": \[/ { listing = 1; next } listing && /\]/ { listing = 0; next }
    listing && (!modifiers || condition ~ /\(caching == \\"redis\\"\)/) { gsub(/^[ \t]*"|",?[ \t\r]*$/, ""); print }' \
    "$repo_root/.template.config/template.json")
  # An empty list would make every excluded file look like a missing one, with no hint why; a git failure inside the loop's own
  # process substitution would not stop the script and would leave nothing to check. Both stop the check here instead.
  (( ${#all_on_excludes[@]} > 0 )) || fail "no exclude pattern was read from .template.config/template.json (one pattern per line is expected)"
  tracked_files="$(git -C "$repo_root" -c core.quotepath=off ls-files)" || fail "git ls-files failed in $repo_root"
  [[ -n "$tracked_files" ]] || fail "no tracked file was listed in $repo_root"
  while IFS= read -r tracked; do
    [[ -n "$tracked" ]] || continue
    [[ -e "$out/${tracked//TemplateName/$project_name}" ]] && continue
    excluded=false
    for pattern in "${all_on_excludes[@]}"; do
      # The engine's "**/" also matches no folder at all; a bash pattern's "*" already crosses "/".
      if [[ "$pattern" == '**/'* ]]; then rest="${pattern#'**/'}"; rest="${rest//'**'/*}"; [[ "$tracked" == $rest || "$tracked" == */$rest ]] && excluded=true
      else [[ "$tracked" == ${pattern//'**'/*} ]] && excluded=true; fi
      [[ "$excluded" == true ]] && break
    done
    [[ "$excluded" == true ]] || fail "$tracked is tracked but not generated with every switch on, and no unconditional exclude of template.json leaves it out"
  done <<< "$tracked_files"
fi

step "Checking which parts of the template were generated"
# expect_part <kept: true|false> <description> <path relative to the project>...: each path exists when the part is kept and does not when removed.
expect_part() {
  local kept="$1" description="$2" path
  shift 2
  for path in "$@"; do
    if [[ "$kept" == "true" && ! -e "$out/$path" ]]; then
      fail "$description: $path is missing"
    elif [[ "$kept" == "false" && -e "$out/$path" ]]; then
      fail "$description: $path must not be generated"
    fi
  done
}
expect_part "$notifications" "notifications=$notifications" \
  src/Modules/Notifications \
  "tests/$project_name.UnitTests/Notifications" \
  "tests/$project_name.IntegrationTests/Notifications" \
  "tests/$project_name.IntegrationTests/UpgradePath/Seeds/Notifications" \
  docs/modules/notifications.md \
  docs/adr/0019-embedded-scriban-notification-templates.md \
  docs/adr/0021-signalr-hub-tickets.md
expect_part "$include_sample" "includeSample=$include_sample" \
  src/Modules/Sample \
  "tests/$project_name.UnitTests/Sample" \
  "tests/$project_name.IntegrationTests/Sample" \
  "tests/$project_name.IntegrationTests/UpgradePath/Seeds/Sample" \
  docs/modules/sample.md
redis_kept=true
if [[ "$caching" == "memory" ]]; then redis_kept=false; fi
expect_part "$redis_kept" "caching=$caching" \
  "src/BuildingBlocks/$project_name.Platform.Redis" \
  "tests/$project_name.IntegrationTests/Redis" \
  "tests/$project_name.UnitTests/Platform" \
  docs/building-blocks/platform-redis.md \
  docs/adr/0027-optional-redis-through-one-project.md
# The OpenAPI snapshot describes every endpoint of the default project; with a module removed it would be wrong, so it is not generated.
snapshot_kept=false
if [[ "$notifications" == "true" && "$include_sample" == "true" ]]; then snapshot_kept=true; fi
expect_part "$snapshot_kept" "OpenAPI snapshot (notifications=$notifications, includeSample=$include_sample)" \
  "tests/$project_name.IntegrationTests/Documentation/OpenApiSnapshotTests.OpenApi_document_matches_snapshot.verified.json"

step "Checking that nothing refers to a part that was left out"
dead_status=0
dead_output="$(bash "$repo_root/build/scripts/dead-reference-check.sh" "$out" "$notifications" "$include_sample" "$caching")" || dead_status=$?
if (( dead_status != 0 )); then
  fail "dead-reference-check.sh exited $dead_status; the generated project still refers to a removed part:"$'\n'"$dead_output"
fi

step "Checking relative links in the generated documents"
# Markdown links ](target) to files in the project; web links and in-page anchors are skipped, #anchors are not checked.
link_count=0
while IFS= read -r document; do
  while IFS= read -r target; do
    target="${target%%#*}"
    [[ -z "$target" || "$target" =~ ^[a-zA-Z][a-zA-Z0-9+.-]*: ]] && continue
    link_count=$((link_count + 1))
    [[ -e "$(dirname "$document")/$target" ]] || fail "${document#"$out"/} links $target, which is not generated"
  done < <(grep -oE '\]\([^) ]+\)' "$document" | sed -E 's/^\]\((.*)\)$/\1/')
done < <(find "$out" -name '*.md' \( -path "$out/docs/*" -o -path "$out/README.md" -o -path "$out/CLAUDE.md" -o -path "$out/CONTRIBUTING.md" \))
(( link_count > 0 )) || fail "no relative links were found to check"

if [[ "$from_package" == "true" ]]; then
  step "Checking that the package and the checkout generate the same project"
  # What the package holds is decided by its pack project, not by the engine's rules for a checkout, so a file the pack leaves out
  # (a dotfile, a folder under a default exclude) only shows when the two generations are compared. The checkout goes into a second
  # private hive; both hives live in $tmp, which the EXIT trap removes.
  hive_checkout="$tmp/hive-checkout"
  compare_out="$tmp/compare/$project_name"

  # (a) one package installs both templates.
  template_list="$(dotnet new list --debug:custom-hive "$hive" 2>&1 || true)"
  for short_name in modulith-backend modulith-module; do
    grep -qF "$short_name" <<< "$template_list" || fail "the package installed no '$short_name' template; dotnet new list shows:"$'\n'"$template_list"
  done

  # (b) the same project from the checkout, compared file by file. The UserSecretsId GUID is new in each generation, so a hunk made
  # only of that line is ignored (-I); the non-brief form is used because only it is certain to honour -I in every diff version.
  dotnet new install . --force --debug:custom-hive "$hive_checkout" > "$tmp/checkout-install.txt" 2>&1 \
    || { cat "$tmp/checkout-install.txt" >&2; fail "installing the checkout into the second hive failed"; }
  checkout_status=0
  dotnet new modulith-backend -n "$project_name" -o "$compare_out" --debug:custom-hive "$hive_checkout" \
    --notifications "$notifications" --includeSample "$include_sample" --caching "$caching" > "$tmp/checkout-generate.txt" 2>&1 \
    || checkout_status=$?
  if (( checkout_status != 0 )); then
    cat "$tmp/checkout-generate.txt" >&2
    fail "generating $project_name from the checkout exited $checkout_status"
  else
    compare_status=0
    diff -r -I '<UserSecretsId>' "$compare_out" "$out" > "$tmp/compare-diff.txt" 2>&1 || compare_status=$?
    if (( compare_status != 0 )); then
      # "Only in DIR: name" lines and the header line of each differing file, shown relative to the two projects, at most 20.
      differing="$(awk '/^Only in / { print; next } /^diff / { print "differs: " $NF }' "$tmp/compare-diff.txt" \
        | sed -e "s|$compare_out|<checkout>|g" -e "s|$out|<package>|g" | head -n 20)"
      [[ -n "$differing" ]] || differing="$(head -n 20 "$tmp/compare-diff.txt")"
      fail "the project generated from the package differs from the one generated from the checkout (diff exit $compare_status); <checkout> is the checkout's, <package> the package's:"$'\n'"$differing"
    fi
  fi

  # (c) the item template: --allow-scripts no keeps the post-action (a restore) from running, so the engine exits 104 and leaves the
  # files; both installs must exit 104 and write the same files (including the dotfile .modulith-module).
  module_trees=()
  for module_install in package checkout; do
    module_hive="$hive"
    [[ "$module_install" == "checkout" ]] && module_hive="$hive_checkout"
    module_dir="$tmp/module-$module_install"
    module_status=0
    dotnet new modulith-module -n Billing --prefix "$project_name" --contracts true --allow-scripts no -o "$module_dir" \
      --debug:custom-hive "$module_hive" > "$tmp/module-$module_install.txt" 2>&1 || module_status=$?
    if [[ "$module_status" != 104 ]]; then
      cat "$tmp/module-$module_install.txt" >&2
      fail "dotnet new modulith-module from the $module_install exited $module_status, expected 104 (post-action not run)"
    fi
    # Equal trees prove nothing when both lack the same file, so the marker the post-action needs is asserted on its own: it is a
    # dotfile, the kind of file a pack drops, and the post-action was not run, so it must still be there.
    [[ -f "$module_dir/.modulith-module" ]] ||
      fail "dotnet new modulith-module from the $module_install wrote no .modulith-module marker file"
    module_trees+=("$module_dir")
  done
  if [[ -d "${module_trees[0]}" && -d "${module_trees[1]}" ]]; then
    module_diff_status=0
    diff -r "${module_trees[1]}" "${module_trees[0]}" > "$tmp/module-diff.txt" 2>&1 || module_diff_status=$?
    if (( module_diff_status != 0 )); then
      module_differing="$(awk '/^Only in / { print; next } /^diff / { print "differs: " $NF }' "$tmp/module-diff.txt" \
        | sed -e "s|${module_trees[1]}|<checkout>|g" -e "s|${module_trees[0]}|<package>|g" | head -n 20)"
      [[ -n "$module_differing" ]] || module_differing="$(head -n 20 "$tmp/module-diff.txt")"
      fail "the module generated from the package differs from the one generated from the checkout (diff exit $module_diff_status):"$'\n'"$module_differing"
    fi
  else
    fail "the module item template did not write its folder from both installs"
  fi
fi

if (( failures > 0 )); then
  echo
  echo "template-smoke: $failures check(s) failed" >&2
  exit 1
fi

if [[ "$generate_only" == "true" ]]; then
  echo
  echo "template-smoke: passed (generate only)"
  exit 0
fi

# The scripts of the module item template and the release-package script are repo-only (template.json leaves them and their tests out
# of every generated project), so they are tested here, in one step, and only by a full run: add-module.test.sh takes about 5 minutes,
# which --generate-only must not pay.
step "Running the repo-only script tests (static-rules, module-anchors, module-template-files, add-module, release-package: about 6 minutes)"
for repo_test in static-rules module-anchors module-template-files add-module release-package; do
  bash "$repo_root/build/scripts/tests/$repo_test.test.sh"
done

cd "$out"
if [[ "$all_on" == "true" ]]; then
  step "Restoring (locked mode) and checking the Windows path-length guard in $project_name"
  dotnet restore --locked-mode
else
  # With a switch off, the template leaves out the four lock files whose packages depend on the switches (the lock-file modifier in
  # template.json). The first restore writes them; it must write exactly those four and change none that the template ships.
  step "Restoring $project_name (a switch is off: the four lock files that depend on it are written now)"
  # "hash  path" per lock file; the sed drops the "*" that sha256sum prints before the name in binary mode on Windows.
  lock_hashes() {
    find . -name packages.lock.json -not -path '*/bin/*' -not -path '*/obj/*' -print0 | sort -z | xargs -0 sha256sum | sed -E 's/^([0-9a-f]+) [ *]/\1  /'
  }
  lock_before="$(lock_hashes)"
  dotnet restore
  lock_after="$(lock_hashes)"
  lock_expected="$(printf '%s\n' \
    "./src/Host/$project_name.Api/packages.lock.json" \
    "./tests/$project_name.UnitTests/packages.lock.json" \
    "./tests/$project_name.ArchitectureTests/packages.lock.json" \
    "./tests/$project_name.IntegrationTests/packages.lock.json" | sort)"
  lock_new="$(comm -13 <(awk '{print $2}' <<< "$lock_before" | sort) <(awk '{print $2}' <<< "$lock_after" | sort))"
  # Lines (hash and path) that existed before and are gone after: a lock file that changed or was removed.
  lock_changed="$(comm -23 <(sort <<< "$lock_before") <(sort <<< "$lock_after"))"
  if [[ "$lock_new" != "$lock_expected" ]]; then
    echo "FAIL: restore wrote these new lock files:"$'\n'"$lock_new"$'\n'"expected exactly:"$'\n'"$lock_expected" >&2
    exit 1
  fi
  if [[ -n "$lock_changed" ]]; then
    echo "FAIL: restore changed or removed lock files that the template ships:"$'\n'"$lock_changed" >&2
    exit 1
  fi
  echo "Restore wrote exactly the four expected lock files and changed none that the template ships"
  dotnet restore --locked-mode
  step "Checking the Windows path-length guard in $project_name"
fi
bash "$repo_root/build/scripts/path-guard-check.sh" "$out"

if [[ -n "$add_module" ]]; then
  # The restore above checked the lock files the template ships, so the module is added after it: the item template's post-action runs
  # a plain restore that gives the new projects their lock files and rewrites the ones that gain a project reference.
  # `dotnet new install .` registered the item template (templates/modulith-module) together with the solution template, in the same
  # private hive, so no second install is needed; the call below fails when it is not there.
  # --allow-scripts yes is required: without a flag a non-interactive run asks for the post-action's permission in a loop for ever.
  # No -o: an item template writes into the current directory, which is the project root here.
  module_options=(-n "$add_module" --prefix "$project_name" --allow-scripts yes --debug:custom-hive "$hive")
  if [[ "$module_contracts" == "true" ]]; then module_options+=(--contracts true); fi
  step "Adding the module $add_module (contracts=$module_contracts) with the item template"
  dotnet new modulith-module "${module_options[@]}"

  step "Checking the module $add_module"
  # The documentation page is named by the kebab-case rule of DocumentPaths.KebabCase: a hyphen before each capital after the first character.
  module_kebab="$(printf '%s' "$add_module" | sed -E ':again; s/([^-])([A-Z])/\1-\2/; t again' | tr 'A-Z' 'a-z')"
  module_project="src/Modules/$add_module/$project_name.Modules.$add_module"
  for added in \
    "$module_project/$project_name.Modules.$add_module.csproj" \
    "$module_project/packages.lock.json" \
    "$module_project/Infrastructure/Persistence/Migrations" \
    "docs/modules/$module_kebab.md"; do
    [[ -e "$added" ]] || fail "the module item template did not leave $added"
  done
  if [[ "$module_contracts" == "true" ]]; then
    contracts_project="src/Modules/$add_module/$project_name.Modules.$add_module.Contracts"
    [[ -f "$contracts_project/$project_name.Modules.$add_module.Contracts.csproj" ]] || fail "$contracts_project/ has no csproj, though --module-contracts was given"
    [[ -f "$contracts_project/packages.lock.json" ]] || fail "$contracts_project/packages.lock.json is missing: the post-action's restore did not write it"
  elif [[ -e "src/Modules/$add_module/$project_name.Modules.$add_module.Contracts" ]]; then
    fail "src/Modules/$add_module has a Contracts project, though --module-contracts was not given"
  fi
  [[ ! -e .modulith-module ]] || fail ".modulith-module is left behind: the post-action did not finish"
  grep -qF "Add${add_module}Module(" "src/Host/$project_name.Api/Program.cs" || fail "src/Host/$project_name.Api/Program.cs does not register the module"

  step "Restoring in locked mode with the new module (as the generated project's CI does)"
  # The post-action's restore wrote the new projects' lock files and rewrote the ones that gained a reference: all of them must agree.
  dotnet restore --locked-mode || fail "dotnet restore --locked-mode failed after adding the module: a packages.lock.json is missing or out of date"

  step "Running the module command a second time (the engine must stop with exit 73 and change nothing)"
  # The same command for a module that exists stops on the engine's own file-overwrite check before the post-action runs.
  module_tree() { find . -type f -not -path './.git/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path './TestResults/*' -exec cksum {} + | sort -k3; }
  tree_before="$(module_tree)"
  second_status=0
  dotnet new modulith-module "${module_options[@]}" || second_status=$?
  [[ "$second_status" == 73 ]] || fail "the second 'dotnet new modulith-module' exited $second_status, expected 73 (the engine's refusal to overwrite)"
  [[ "$tree_before" == "$(module_tree)" ]] || fail "the second 'dotnet new modulith-module' changed the project tree"
  if (( failures > 0 )); then
    echo
    echo "template-smoke: $failures check(s) failed" >&2
    exit 1
  fi
fi

# The gates are those of the generated project's ci.yml (build-test and licenses jobs), run in the same order and with the same options.
if [[ "$run_gates" == "true" ]]; then
  step "Checking formatting"
  dotnet format --verify-no-changes --no-restore
fi

step "Building $project_name"
dotnet build --no-restore -c Release

# With --gates every test project runs with coverage (coverage.testconfig.json) and writes its report to TestResults, as ci.yml does.
test_options=(--no-build -c Release)
if [[ "$run_gates" == "true" ]]; then
  test_options+=(--config-file coverage.testconfig.json --coverlet --results-directory TestResults)
fi

step "Running the unit and architecture tests"
dotnet test --project "tests/$project_name.UnitTests" "${test_options[@]}"
dotnet test --project "tests/$project_name.ArchitectureTests" "${test_options[@]}"
if [[ "$run_integration" == "true" ]]; then
  step "Running the integration tests (Docker needed)"
  dotnet test --project "tests/$project_name.IntegrationTests" "${test_options[@]}"
fi

if [[ "$run_gates" == "true" ]]; then
  step "Coverage gate (80 % line)"
  dotnet tool restore
  dotnet reportgenerator "-reports:TestResults/**/*.cobertura.*.xml" "-targetdir:coverage" "-reporttypes:JsonSummary"
  # The first "linecoverage" in Summary.json is the summary's (the file is pretty-printed, one property per line); no jq needed.
  line="$(sed -n 's/^ *"linecoverage": *\([0-9.]*\),\{0,1\}$/\1/p' coverage/Summary.json | head -n 1)"
  [[ -n "$line" ]] || { echo "FAIL: coverage/Summary.json has no line coverage" >&2; exit 1; }
  echo "Line coverage on the gated namespaces: ${line} %"
  if ! awk -v value="$line" 'BEGIN { exit !(value + 0 >= 80) }'; then
    echo "FAIL: line coverage ${line} % is below 80 %" >&2
    exit 1
  fi

  step "Checking dependency licences"
  dotnet nuget-license -i "$project_name.slnx" -t \
    -a build/licenses/allowed-licenses.json \
    -mapping build/licenses/license-url-mappings.json \
    -override build/licenses/package-overrides.json \
    -ignore build/licenses/ignored-packages.json \
    -err
fi

echo
echo "template-smoke: passed"
