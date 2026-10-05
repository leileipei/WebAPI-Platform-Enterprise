#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
idp_node=${WEBAPI_NODE:-$(command -v node || true)}
[ -x "$idp_node" ] || { echo 'Set WEBAPI_NODE to Node 22+ executable.' >&2; exit 3; }
exec "$idp_node" scripts/runtime/keycloak-cli.mjs "$@"
