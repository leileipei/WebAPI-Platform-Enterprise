#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
node_bin=${WEBAPI_NODE:-$(command -v node || true)}
pnpm_bin=${WEBAPI_PNPM:-$(command -v pnpm || true)}
[ -x "$node_bin" ] && [ -x "$pnpm_bin" ] || { echo 'Set WEBAPI_NODE and WEBAPI_PNPM to installed executables.' >&2; exit 2; }
export PATH="$(dirname "$node_bin"):$PATH"
"$node_bin" --test console/tests/*.test.mjs
"$pnpm_bin" --dir console build
