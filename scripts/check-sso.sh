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
export WEBAPI_SSO_TEST_OWNER
WEBAPI_SSO_TEST_OWNER=$("$sso_node" -e 'process.stdout.write(crypto.randomUUID())')
export WEBAPI_SSO_TEST_PROJECT="webapi-sso-test-$WEBAPI_SSO_TEST_OWNER"
mkdir -p .runtime
export WEBAPI_SSO_TEST_DIRECTORY="$PWD/.runtime/$WEBAPI_SSO_TEST_PROJECT"
mkdir -m 700 "$WEBAPI_SSO_TEST_DIRECTORY"
"$sso_node" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";fs.writeFileSync(process.env.WEBAPI_SSO_TEST_DIRECTORY+"/owner.json",JSON.stringify({owner:process.env.WEBAPI_SSO_TEST_OWNER,project:process.env.WEBAPI_SSO_TEST_PROJECT}),{mode:0o600});fs.writeFileSync(process.env.WEBAPI_SSO_TEST_DIRECTORY+"/postgres-password",crypto.randomBytes(32).toString("base64url"),{mode:0o600});'
compose_args=(-p "$WEBAPI_SSO_TEST_PROJECT" -f deploy/compose.test.yml -f deploy/compose.sso-test.yml)
sso_seed=${WEBAPI_SSO_NUGET_SEED:-webapi-enterprise-core-test_nuget-test}
seed_available=no
if [[ "$sso_seed" =~ ^[a-zA-Z0-9_.-]+$ ]] && docker volume inspect "$sso_seed" >/dev/null 2>&1; then
  seed_available=yes
  cat > "$WEBAPI_SSO_TEST_DIRECTORY/nuget-seed.yml" <<YAML
services:
  sdk:
    volumes:
      - sso-nuget-seed:/nuget-seed:ro
volumes:
  sso-nuget-seed:
    external: true
    name: $sso_seed
YAML
  compose_args+=(-f "$WEBAPI_SSO_TEST_DIRECTORY/nuget-seed.yml")
fi
compose(){ docker compose "${compose_args[@]}" "$@"; }
cleanup(){
  local prior=$?
  trap - EXIT
  local ids labels
  ids=$(compose ps --all --quiet) || { echo 'Cannot inspect SSO test resources; retained for diagnosis.' >&2; exit 3; }
  for id in $ids; do
    labels=$(docker inspect --format '{{json .Config.Labels}}' "$id") || exit 3
    if ! printf '%s' "$labels" | "$sso_node" --input-type=module -e 'import fs from "node:fs";const labels=JSON.parse(fs.readFileSync(0,"utf8"));process.exit(labels["com.docker.compose.project"]===process.env.WEBAPI_SSO_TEST_PROJECT&&labels["com.webapi.sso.owner"]===process.env.WEBAPI_SSO_TEST_OWNER?0:1)'; then
      echo 'SSO test ownership mismatch; destructive cleanup rejected.' >&2
      exit 3
    fi
  done
  "$sso_node" --input-type=module -e 'import {validateSsoDirectory} from "./scripts/sso/evidence.mjs";await validateSsoDirectory({directory:process.env.WEBAPI_SSO_TEST_DIRECTORY,owner:process.env.WEBAPI_SSO_TEST_OWNER,project:process.env.WEBAPI_SSO_TEST_PROJECT});' || exit 3
  local volumes volume
  volumes=$(docker volume ls -q --filter "label=com.docker.compose.project=$WEBAPI_SSO_TEST_PROJECT") || exit 3
  for volume in $volumes; do
    labels=$(docker volume inspect --format '{{json .Labels}}' "$volume") || exit 3
    printf '%s' "$labels" | "$sso_node" --input-type=module -e 'import fs from "node:fs";import {assertOwnedSsoResources} from "./scripts/sso/evidence.mjs";assertOwnedSsoResources({owner:process.env.WEBAPI_SSO_TEST_OWNER,project:process.env.WEBAPI_SSO_TEST_PROJECT},[JSON.parse(fs.readFileSync(0,"utf8"))]);' || exit 3
  done
  compose down --volumes --remove-orphans >/dev/null || exit 3
  rm -f "$WEBAPI_SSO_TEST_DIRECTORY/postgres-password" "$WEBAPI_SSO_TEST_DIRECTORY/nuget-seed.yml" "$WEBAPI_SSO_TEST_DIRECTORY/owner.json"
  rmdir "$WEBAPI_SSO_TEST_DIRECTORY"
  exit "$prior"
}
trap cleanup EXIT
compose up -d --wait postgres redis
if [ "$seed_available" = yes ]; then
  compose run --rm sdk bash -c 'mkdir -p /root/.nuget/packages; cp -a /nuget-seed/. /root/.nuget/packages/; exec dotnet "$@"' -- test "$test_project" --configuration Release "$@"
else
  compose run --rm sdk dotnet test "$test_project" --configuration Release "$@"
fi
