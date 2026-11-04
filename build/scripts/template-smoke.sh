#!/usr/bin/env bash
# Template smoke test: installs this repository as a `dotnet new` template, generates a project
# from it, checks the generated files, then builds it and runs its unit and architecture tests.
#
# Usage: bash build/scripts/template-smoke.sh [--notifications true|false] [--includeSample true|false] [--caching redis|memory]
#                                             [--integration] [--generate-only] [--output DIR]
#   --notifications, --includeSample, --caching  the template switches, passed to `dotnet new` (defaults: true, true, redis).
#   --integration   also run the integration tests (they need Docker); without it only the unit and architecture tests run.
#   --generate-only stop after the generation checks (about two minutes); no restore, build or tests.
#   --output DIR    keep the generated project in DIR, which must not exist (use a short path such as C:/w/g1 on Windows).
# With no arguments it generates with every switch on, builds and runs the unit and architecture tests.
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
generate_only=false
output_dir=""

usage_error() {
  echo "template-smoke: $1" >&2
  echo "usage: template-smoke.sh [--notifications true|false] [--includeSample true|false] [--caching redis|memory] [--integration] [--generate-only] [--output DIR]" >&2
  exit 2
}

while (( $# > 0 )); do
  case "$1" in
    --notifications | --includeSample | --caching | --output)
      (( $# >= 2 )) || usage_error "$1 needs a value"
      case "$1" in
        --notifications) notifications="$2" ;;
        --includeSample) include_sample="$2" ;;
        --caching) caching="$2" ;;
        --output) output_dir="$2" ;;
      esac
      shift 2
      ;;
    --integration) run_integration=true; shift ;;
    --generate-only) generate_only=true; shift ;;
    *) usage_error "unknown argument '$1'" ;;
  esac
done
case "$notifications" in true | false) ;; *) usage_error "--notifications must be 'true' or 'false', got '$notifications'" ;; esac
case "$include_sample" in true | false) ;; *) usage_error "--includeSample must be 'true' or 'false', got '$include_sample'" ;; esac
case "$caching" in redis | memory) ;; *) usage_error "--caching must be 'redis' or 'memory', got '$caching'" ;; esac
if [[ -n "$output_dir" && -e "$output_dir" ]]; then
  usage_error "--output $output_dir already exists; give a directory that does not exist"
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

step "Installing the template from $repo_root (private hive $hive)"
cd "$repo_root"
dotnet new install . --force --debug:custom-hive "$hive"

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
  docs/superpowers \
  build/template-content \
  build/scripts/configure-github-repo.sh \
  build/scripts/template-smoke.sh \
  build/scripts/path-guard-check.sh \
  build/scripts/dead-reference-check.sh \
  build/scripts/tests/dead-reference-check.test.sh \
  .github/workflows/template-matrix.yml \
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
  docs/modules/notifications.md \
  docs/adr/0019-embedded-scriban-notification-templates.md \
  docs/adr/0021-signalr-hub-tickets.md
expect_part "$include_sample" "includeSample=$include_sample" \
  src/Modules/Sample \
  "tests/$project_name.UnitTests/Sample" \
  "tests/$project_name.IntegrationTests/Sample" \
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

step "Building $project_name"
dotnet build --no-restore -c Release

step "Running the unit and architecture tests"
dotnet test --project "tests/$project_name.UnitTests" --no-build -c Release
dotnet test --project "tests/$project_name.ArchitectureTests" --no-build -c Release
if [[ "$run_integration" == "true" ]]; then
  step "Running the integration tests (Docker needed)"
  dotnet test --project "tests/$project_name.IntegrationTests" --no-build -c Release
fi

echo
echo "template-smoke: passed"
