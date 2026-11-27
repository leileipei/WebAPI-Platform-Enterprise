#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:-}; shift || true
case "$mode" in
  domain) test_project=tests/WebApi.Domain.Tests/WebApi.Domain.Tests.csproj ;;
  integration) test_project=tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj ;;
  gateway) test_project=tests/WebApi.Gateway.Tests/WebApi.Gateway.Tests.csproj ;;
  console) exec ./scripts/check-console.sh "$@" ;;
  e2e|browser|verify) test_project= ;;
  *) echo 'Usage: check-policies.sh domain|integration|gateway|console|e2e|browser|verify [test args]' >&2; exit 2 ;;
esac
policy_node=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$policy_node" ]; then policy_node=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$policy_node" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
if [ "$mode" = verify ]; then exec python3 scripts/verify-policies-delivery.py "$@"; fi
if [ "$mode" = e2e ] || [ "$mode" = browser ]; then exec "$policy_node" scripts/policies/acceptance.mjs "--$mode" "$@"; fi
exec "$policy_node" scripts/policies/backend-tests.mjs "$test_project" "$@"
