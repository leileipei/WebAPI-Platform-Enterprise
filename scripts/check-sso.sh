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
  *) echo 'Usage: check-sso.sh domain|integration|gateway|console|e2e|browser|verify [test args]' >&2; exit 2 ;;
esac
sso_node=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$sso_node" ]; then sso_node=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$sso_node" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
if [ "$mode" = verify ]; then exec "$sso_node" scripts/sso/verify.mjs "$@"; fi
if [ "$mode" = e2e ] || [ "$mode" = browser ]; then exec "$sso_node" scripts/sso/acceptance.mjs "--$mode" "$@"; fi
exec "$sso_node" scripts/sso/backend-tests.mjs "$test_project" "$@"
