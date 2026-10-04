#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
runtime_node=${WEBAPI_NODE:-$(command -v node || true)}
[ -x "$runtime_node" ] || { echo 'Set WEBAPI_NODE to Node 22+ executable.' >&2; exit 3; }
exec "$runtime_node" scripts/runtime/cli.mjs "$@"
