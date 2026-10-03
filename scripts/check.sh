#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
kind=${1:?usage: check.sh integration [test args]}; shift
case "$kind" in
 integration) project=tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj ;;
 domain) project=tests/WebApi.Domain.Tests/WebApi.Domain.Tests.csproj ;;
 gateway) project=tests/WebApi.Gateway.Tests/WebApi.Gateway.Tests.csproj ;;
 e2e) project=tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj ;;
 *) echo 'Unknown check kind' >&2; exit 2 ;;
esac
[ -f "$project" ] || { echo "Missing test project: $project" >&2; exit 2; }
docker compose -f deploy/compose.test.yml up -d --wait postgres redis
docker compose -f deploy/compose.test.yml run --rm sdk dotnet test "$project" --configuration Release "$@"
