#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = local-runtime ]; then
 exec python3 scripts/package-local-runtime.py "${2:?Immutable source commit required}"
fi
if [ "${1:-}" = observability ]; then
 shift
 exec python3 scripts/package-observability.py "$@"
fi
exec python3 scripts/package-core.py "$@"
