#!/usr/bin/env bash
# Template smoke test: installs this repository as a `dotnet new` template, generates a project
# from it, checks the generated files, then builds it and runs its unit and architecture tests.
#
# Usage: bash build/scripts/template-smoke.sh
#
# Integration tests are not run here (they need Docker); CI runs them in the build-test job.
# The template is always uninstalled again, so the global template cache is left as it was.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_name="Acme.Smoke"
tmp="$(mktemp -d)"
out="$tmp/$project_name"
installed=false

cleanup() {
  local status=$?
  if [[ "$installed" == true ]]; then
    (cd "$repo_root" && dotnet new uninstall . >/dev/null) || echo "template-smoke: could not uninstall the template" >&2
  fi
  rm -rf "$tmp"
  exit "$status"
}
trap cleanup EXIT

failures=0
fail() {
  echo "FAIL: $1" >&2
  failures=$((failures + 1))
}

step() {
  echo
  echo "==> $1"
}

step "Installing the template from $repo_root"
cd "$repo_root"
installed=true
dotnet new install . --force

step "Generating $project_name"
dotnet new modulith-backend -n "$project_name" -o "$out"

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
  release-please-config.json \
  .github/workflows/release.yml \
  build/licenses/allowed-licenses.json \
  docs/README.md \
  docs/architecture/overview.md \
  docs/services/api.md; do
  [[ -f "$out/$required" ]] || fail "$required is missing"
done
if [[ -f "$out/.release-please-manifest.json" && "$(tr -d '[:space:]' < "$out/.release-please-manifest.json")" != '{".":"0.0.0"}' ]]; then
  fail ".release-please-manifest.json must be { \".\": \"0.0.0\" }, not the template's own version"
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

step "Restoring (locked mode) and building $project_name"
cd "$out"
dotnet restore --locked-mode
dotnet build --no-restore -c Release

step "Running the unit and architecture tests"
dotnet test --project "tests/$project_name.UnitTests" --no-build -c Release
dotnet test --project "tests/$project_name.ArchitectureTests" --no-build -c Release

echo
echo "template-smoke: passed"
