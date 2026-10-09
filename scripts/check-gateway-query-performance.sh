#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
performance_node=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$performance_node" ]; then performance_node=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$performance_node" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
exec "$performance_node" scripts/performance/cli.mjs "$@"
