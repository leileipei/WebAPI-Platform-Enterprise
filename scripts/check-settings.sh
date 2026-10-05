#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:-}; shift || true
case "$mode" in
  domain|integration|gateway) exec bash scripts/check-policies.sh "$mode" -p:RestoreLockedMode=true "$@" ;;
  console) export pnpm_config_verify_deps_before_run=false; exec bash scripts/check-policies.sh console "$@" ;;
  verify) exec "${WEBAPI_NODE:-/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node}" scripts/settings/verify.mjs "$@" ;;
  e2e|browser|cleanup) settings_node=${WEBAPI_NODE:-/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node}; if [ "$mode" = e2e ]; then exec "$settings_node" scripts/settings/acceptance.mjs "$@"; else exec "$settings_node" "scripts/settings/$mode.mjs" "$@"; fi ;;
  *) echo 'Settings acceptance mode unavailable: domain|integration|gateway|console' >&2; exit 2 ;;
esac
