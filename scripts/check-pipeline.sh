#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:-}; shift || true
pipeline_node=${WEBAPI_NODE:-$(command -v node || true)}
[ -x "$pipeline_node" ] || { echo 'Set WEBAPI_NODE to the Node runtime.' >&2; exit 3; }
case "$mode" in
 domain|integration|gateway) exec ./scripts/check-contracts.sh "$mode" "$@" ;;
 console) exec ./scripts/check-console.sh "$@" ;;
 e2e|faults|browser|verify) exec "$pipeline_node" scripts/delivery/pipeline-cli.mjs "$mode" "$@" ;;
 *) echo 'Usage: check-pipeline.sh domain|integration|gateway|console|e2e|faults|browser|verify [revision or evidence-directory]' >&2; exit 2 ;;
esac
