#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
runtime_node=${WEBAPI_NODE:-$(command -v node || true)}
[ -x "$runtime_node" ] || { echo 'Set WEBAPI_NODE to Node 22+ executable.' >&2; exit 3; }
case "${1:-}" in
  unit) exec "$runtime_node" --test tests/runtime/*.test.mjs console/tests/*.test.mjs ;;
  integration) exec "$runtime_node" --test --test-concurrency=1 tests/runtime/integration/*.test.mjs ;;
  e2e) exec "$runtime_node" scripts/runtime/acceptance.mjs --browser ;;
  browser) exec "$runtime_node" scripts/runtime/acceptance.mjs --browser ;;
  *) echo 'Usage: check-local-runtime.sh unit|integration|e2e|browser' >&2; exit 2 ;;
esac
