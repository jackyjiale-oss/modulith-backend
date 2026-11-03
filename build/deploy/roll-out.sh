#!/usr/bin/env bash
# The one hook the CD workflow (.github/workflows/cd.yml, jobs deploy-staging and deploy-production) calls to put a new image in service:
#
#   bash build/deploy/roll-out.sh <environment> <image@digest>
#
#   <environment>   staging or production
#   <image@digest>  the image to run, by digest, for example ghcr.io/owner/repo/api@sha256:...
#
# By the time this runs the image has been verified (build/scripts/container-verify.sh) and its database migrated (`migrate` mode,
# with the environment's MIGRATION_CONNECTION_STRING secret). The workflow smoke-tests <URL>/health/ready afterwards, so this
# script only has to start the new version; a non-zero exit fails the deploy.
#
# This file is a stub. The template cannot know where a project runs, so it fails on purpose: a project that sets the repository
# variable DEPLOY_ENABLED to true without writing its roll-out gets a red job, not a deploy that did nothing.
# Replace the body with your own roll-out. Two examples to start from:
#
#   # Docker Compose on a host, over SSH (the host's compose file reads the image from IMAGE):
#   ssh deploy@host.example.com "IMAGE='$2' docker compose -f /srv/api/compose.yml pull && IMAGE='$2' docker compose -f /srv/api/compose.yml up -d"
#
#   # Kubernetes (the deployment and container are both named api; kubectl must already be authenticated):
#   kubectl set image deployment/api api="$2" --namespace "$1"
#   kubectl rollout status deployment/api --namespace "$1" --timeout=300s
#
# Keep credentials out of this file: use the environment's secrets, passed through `env:` on the workflow step.

set -euo pipefail

environment="${1:-}"
image="${2:-}"

echo "build/deploy/roll-out.sh is the template's placeholder: this project has not written its roll-out." >&2
echo "Replace this file with the commands that start '${image:-<image@digest>}' in '${environment:-<environment>}'" >&2
echo "(see the examples at the top of the file), or set the repository variable DEPLOY_ENABLED to something other than 'true'." >&2
exit 1
