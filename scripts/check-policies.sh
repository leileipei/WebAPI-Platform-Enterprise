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
export WEBAPI_POLICY_TEST_OWNER
WEBAPI_POLICY_TEST_OWNER=$("$policy_node" -e 'process.stdout.write(crypto.randomUUID())')
export WEBAPI_POLICY_TEST_PROJECT="webapi-policies-test-$WEBAPI_POLICY_TEST_OWNER"
mkdir -p .runtime
export WEBAPI_POLICY_TEST_DIRECTORY="$PWD/.runtime/$WEBAPI_POLICY_TEST_PROJECT"
mkdir -m 700 "$WEBAPI_POLICY_TEST_DIRECTORY"
"$policy_node" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";fs.writeFileSync(process.env.WEBAPI_POLICY_TEST_DIRECTORY+"/postgres-password",crypto.randomBytes(32).toString("base64url"),{mode:0o600});'
compose_args=(-p "$WEBAPI_POLICY_TEST_PROJECT" -f deploy/compose.test.yml -f deploy/compose.policies-test.yml)
policy_seed=${WEBAPI_POLICY_NUGET_SEED:-webapi-enterprise-core-test_nuget-test}
seed_available=no
if [[ "$policy_seed" =~ ^[a-zA-Z0-9_.-]+$ ]] && docker volume inspect "$policy_seed" >/dev/null 2>&1; then
  seed_available=yes
  cat > "$WEBAPI_POLICY_TEST_DIRECTORY/nuget-seed.yml" <<YAML
services:
  sdk:
    volumes:
      - policy-nuget-seed:/nuget-seed:ro
volumes:
  policy-nuget-seed:
    external: true
    name: $policy_seed
YAML
  compose_args+=(-f "$WEBAPI_POLICY_TEST_DIRECTORY/nuget-seed.yml")
fi
compose(){ docker compose "${compose_args[@]}" "$@"; }
cleanup(){
  local prior=$?
  trap - EXIT
  local ids labels
  ids=$(compose ps --all --quiet) || { echo 'Cannot inspect policy test resources; retained for diagnosis.' >&2; exit 3; }
  for id in $ids; do
    labels=$(docker inspect --format '{{json .Config.Labels}}' "$id") || exit 3
    if ! printf '%s' "$labels" | "$policy_node" --input-type=module -e 'import fs from "node:fs";const labels=JSON.parse(fs.readFileSync(0,"utf8"));process.exit(labels["com.docker.compose.project"]===process.env.WEBAPI_POLICY_TEST_PROJECT&&labels["com.webapi.policies.owner"]===process.env.WEBAPI_POLICY_TEST_OWNER?0:1)'; then
      echo 'Policy test ownership mismatch; destructive cleanup rejected.' >&2
      exit 3
    fi
  done
  compose down --volumes --remove-orphans >/dev/null || exit 3
  rm -f "$WEBAPI_POLICY_TEST_DIRECTORY/postgres-password" "$WEBAPI_POLICY_TEST_DIRECTORY/nuget-seed.yml"
  rmdir "$WEBAPI_POLICY_TEST_DIRECTORY"
  exit "$prior"
}
trap cleanup EXIT
compose up -d --wait postgres redis
if [ "$seed_available" = yes ]; then
  compose run --rm sdk bash -c 'mkdir -p /root/.nuget/packages; cp -a /nuget-seed/. /root/.nuget/packages/; exec dotnet test "$@"' -- "$test_project" --configuration Release "$@"
else
  compose run --rm sdk dotnet test "$test_project" --configuration Release "$@"
fi
