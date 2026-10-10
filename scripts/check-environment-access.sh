#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:-}; shift || true
node_bin=${WEBAPI_NODE:-$(command -v node || true)}
case "$mode" in
  domain|integration|gateway) exec ./scripts/check-contracts.sh "$mode" "$@";;
  console) exec ./scripts/check-console.sh "$@";;
  e2e|browser) [ -x "$node_bin" ] || exit 2; exec "$node_bin" scripts/delivery/environment-access-scenario.mjs "$@";;
  verify) [ -x "$node_bin" ] || exit 2; exec "$node_bin" scripts/delivery/environment-access-scenario.mjs verify "$@";;
  *) echo 'Usage: check-environment-access.sh domain|integration|gateway|console|e2e|browser SHA|verify EVIDENCE_DIRECTORY' >&2; exit 2;;
esac
