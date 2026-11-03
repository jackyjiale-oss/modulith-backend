#!/usr/bin/env bash
# The hook the CD workflow (.github/workflows/cd.yml, job deploy-production) calls to put the site into and out of maintenance mode
# around a release that changes the database in a way the running version cannot work with:
#
#   bash build/deploy/maintenance.sh <on|off> <environment> <page-path>
#
#   <on|off>        on starts maintenance mode, off ends it
#   <environment>   staging or production (the workflow only calls this for production)
#   <page-path>     the file to serve, build/deploy/maintenance.html
#
# Contract:
#   on   makes every request to the site answer <page-path> with status 503 until `off` runs, EXCEPT /health/live and /health/ready,
#        which it must pass through to the API: the workflow's smoke test of the new version calls /health/ready while `on` is
#        still in place, and `off` runs only if that test passes. It is idempotent (running it twice is the same as once) and must
#        exit non-zero if it could not do it, so the release stops before the database is touched.
#   off  removes it, so the site answers normally again. It is idempotent too.
#
# `on` runs before `migrate`; `off` runs only after the smoke test of the new version passed. When the new version fails, the
# workflow leaves maintenance mode on, so users keep seeing the page and not an error; run `off` yourself once the site is healthy.
#
# A limit of the workflow's detection: it compares with the previous release TAG, not with what production last ran. A contract
# release that never reached production (this stub failing at `on` on the first one, a failed staging smoke test, a rejected
# approval) leaves its tag behind, and the NEXT release answers "no contract migration" and goes out without the page. Until a
# release has completed its production deploy, dispatch every later release by hand with `maintenance: on` (workflow_dispatch of
# cd.yml with `release` ticked, the version, and `maintenance` set to on).
#
# This file is a stub. The template cannot know what sits in front of your API, so it fails on purpose: a project that sets the
# repository variable DEPLOY_ENABLED to true and releases a contract migration without writing its hook gets a red job, not a
# release that took the site down without a page. Replace the body with your own. Examples to start from:
#
#   # nginx on a host, over SSH (the site's config includes /etc/nginx/maintenance.conf while maintenance.flag exists. That file
#   # exempts the two health paths: `location ~ ^/health/(live|ready)$ { proxy_pass http://api; }`, and answers 503 with the page
#   # for every other location):
#   case "$1" in
#     on)  scp "$3" deploy@host.example.com:/srv/maintenance/index.html && ssh deploy@host.example.com "touch /etc/nginx/maintenance.flag && sudo nginx -s reload" ;;
#     off) ssh deploy@host.example.com "rm -f /etc/nginx/maintenance.flag && sudo nginx -s reload" ;;
#   esac
#
#   # Caddy on a host, over SSH (Caddy loads a snippet that answers 503 with the page while the file maintenance.caddy exists. The
#   # snippet starts with `@health path /health/live /health/ready` and `handle @health { reverse_proxy api:8080 }`, so those two
#   # paths reach the API, and its other `handle` answers 503 with the page):
#   case "$1" in
#     on)  scp "$3" deploy@host.example.com:/srv/maintenance/index.html && ssh deploy@host.example.com "cp /etc/caddy/maintenance.caddy.off /etc/caddy/maintenance.caddy && caddy reload --config /etc/caddy/Caddyfile" ;;
#     off) ssh deploy@host.example.com "rm -f /etc/caddy/maintenance.caddy && caddy reload --config /etc/caddy/Caddyfile" ;;
#   esac
#
#   # A load balancer's maintenance rule (a fixed response of 503 and the page in front of the target group):
#   #   on: add the rule with the page as its body and status 503, priority above the forwarding rule, with a condition that
#   #   leaves out /health/live and /health/ready (or a higher-priority rule that forwards those two paths to the target group).
#   #   off: delete the rule(s).
#   #   Use the provider's CLI (aws, az, gcloud, ...) with credentials from the environment's secrets.
#
# Keep credentials out of this file: use the environment's secrets, passed through `env:` on the workflow step.

set -euo pipefail

mode="${1:-}"
environment="${2:-}"
page="${3:-}"

echo "build/deploy/maintenance.sh is the template's placeholder: this project has not written its maintenance hook." >&2
echo "Replace this file with the commands that switch maintenance mode '${mode:-<on|off>}' for '${environment:-<environment>}'," >&2
echo "serving '${page:-<page-path>}' (see the contract and the examples at the top of the file)." >&2
echo "Then run this release again from its tag. In auto mode this step runs only when a contract migration was detected, and this" >&2
echo "release has not reached production, so every later release needs the input 'maintenance: on' (a manual dispatch of cd.yml with" >&2
echo "'release' ticked) until one has completed its production deploy: the next detection compares with this release's tag." >&2
exit 1
