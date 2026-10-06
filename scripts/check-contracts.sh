#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=${1:-}; shift || true
case "$mode" in
  domain) test_project=tests/WebApi.Domain.Tests/WebApi.Domain.Tests.csproj ;;
  integration) test_project=tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj ;;
  gateway) test_project=tests/WebApi.Gateway.Tests/WebApi.Gateway.Tests.csproj ;;
  console) exec ./scripts/check-console.sh "$@" ;;
  *) echo 'Usage: check-contracts.sh domain|integration|gateway|console [test args]' >&2; exit 2 ;;
esac
contract_node=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$contract_node" ]; then contract_node=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$contract_node" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
export WEBAPI_CONTRACT_TEST_OWNER
WEBAPI_CONTRACT_TEST_OWNER=$("$contract_node" -e 'process.stdout.write(crypto.randomUUID())')
export WEBAPI_CONTRACT_TEST_PROJECT="webapi-contracts-test-$WEBAPI_CONTRACT_TEST_OWNER"
mkdir -p .runtime
export WEBAPI_CONTRACT_TEST_DIRECTORY="$PWD/.runtime/$WEBAPI_CONTRACT_TEST_PROJECT"
mkdir -m 700 "$WEBAPI_CONTRACT_TEST_DIRECTORY"
"$contract_node" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";fs.writeFileSync(process.env.WEBAPI_CONTRACT_TEST_DIRECTORY+"/postgres-password",crypto.randomBytes(32).toString("base64url"),{mode:0o600});'
compose_args=(-p "$WEBAPI_CONTRACT_TEST_PROJECT" -f deploy/compose.test.yml -f deploy/compose.contracts-test.yml)
contract_seed=${WEBAPI_CONTRACT_NUGET_SEED:-webapi-enterprise-core-test_nuget-test}
seed_available=no
if [[ "$contract_seed" =~ ^[a-zA-Z0-9_.-]+$ ]] && docker volume inspect "$contract_seed" >/dev/null 2>&1; then
  seed_available=yes
  cat > "$WEBAPI_CONTRACT_TEST_DIRECTORY/nuget-seed.yml" <<YAML
services:
  sdk:
    volumes:
      - contract-nuget-seed:/nuget-seed:ro
volumes:
  contract-nuget-seed:
    external: true
    name: $contract_seed
YAML
  compose_args+=(-f "$WEBAPI_CONTRACT_TEST_DIRECTORY/nuget-seed.yml")
fi
compose(){ docker compose "${compose_args[@]}" "$@"; }
cleanup(){
  local prior=$?
  trap - EXIT
  local ids labels
  ids=$(compose ps --all --quiet) || { echo 'Cannot inspect contract test resources; retained for diagnosis.' >&2; exit 3; }
  for id in $ids; do
    labels=$(docker inspect --format '{{json .Config.Labels}}' "$id") || exit 3
    if ! printf '%s' "$labels" | "$contract_node" --input-type=module -e 'import fs from "node:fs";const labels=JSON.parse(fs.readFileSync(0,"utf8"));process.exit(labels["com.docker.compose.project"]===process.env.WEBAPI_CONTRACT_TEST_PROJECT&&labels["com.webapi.contracts.owner"]===process.env.WEBAPI_CONTRACT_TEST_OWNER?0:1)'; then
      echo 'Contract test ownership mismatch; destructive cleanup rejected.' >&2
      exit 3
    fi
  done
  compose down --volumes --remove-orphans >/dev/null || exit 3
  rm -f "$WEBAPI_CONTRACT_TEST_DIRECTORY/postgres-password" "$WEBAPI_CONTRACT_TEST_DIRECTORY/nuget-seed.yml"
  rmdir "$WEBAPI_CONTRACT_TEST_DIRECTORY"
  exit "$prior"
}
trap cleanup EXIT
compose up -d --wait postgres redis
if [ "$seed_available" = yes ]; then
  compose run --rm sdk bash -c 'mkdir -p /root/.nuget/packages; cp -a /nuget-seed/. /root/.nuget/packages/; exec dotnet test "$@"' -- "$test_project" --configuration Release "$@"
else
  compose run --rm sdk dotnet test "$test_project" --configuration Release "$@"
fi
