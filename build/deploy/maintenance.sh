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
#   on   makes every request to the site answer <page-path> with status 503 until `off` runs. It is idempotent (running it twice is
#        the same as once) and must exit non-zero if it could not do it, so the release stops before the database is touched.
#   off  removes it, so the site answers normally again. It is idempotent too.
#
# `on` runs before `migrate`; `off` runs only after the smoke test of the new version passed. When the new version fails, the
# workflow leaves maintenance mode on, so users keep seeing the page and not an error; run `off` yourself once the site is healthy.
#
# This file is a stub. The template cannot know what sits in front of your API, so it fails on purpose: a project that sets the
# repository variable DEPLOY_ENABLED to true and releases a contract migration without writing its hook gets a red job, not a
# release that took the site down without a page. Replace the body with your own. Examples to start from:
#
#   # nginx on a host, over SSH (the site's config includes /etc/nginx/maintenance.conf, a file that returns 503 and the page):
#   case "$1" in
#     on)  scp "$3" deploy@host.example.com:/srv/maintenance/index.html && ssh deploy@host.example.com "touch /etc/nginx/maintenance.flag && sudo nginx -s reload" ;;
#     off) ssh deploy@host.example.com "rm -f /etc/nginx/maintenance.flag && sudo nginx -s reload" ;;
#   esac
#
#   # Caddy on a host, over SSH (Caddy loads a snippet that answers 503 with the page while the file maintenance.caddy exists):
#   case "$1" in
#     on)  scp "$3" deploy@host.example.com:/srv/maintenance/index.html && ssh deploy@host.example.com "cp /etc/caddy/maintenance.caddy.off /etc/caddy/maintenance.caddy && caddy reload --config /etc/caddy/Caddyfile" ;;
#     off) ssh deploy@host.example.com "rm -f /etc/caddy/maintenance.caddy && caddy reload --config /etc/caddy/Caddyfile" ;;
#   esac
#
#   # A load balancer's maintenance rule (a fixed response of 503 and the page in front of the target group):
#   #   on: add the rule with the page as its body and status 503, priority above the forwarding rule.  off: delete that rule.
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
echo "Or release without it: run the release with the input 'maintenance: off', only when no contract migration is in it." >&2
exit 1
