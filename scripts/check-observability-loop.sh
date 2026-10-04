#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
kind=${1:?e2e|faults|browser}; shift
case "$kind" in e2e|faults|browser) ;; *) exit 2;; esac
WEBAPI_OBS_NODE=${WEBAPI_NODE:-/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node}
export WEBAPI_E2E_PROJECT="webapi-enterprise-e2e-observability-$(uuidgen | tr '[:upper:]' '[:lower:]')"
export WEBAPI_E2E_RELATIVE_DIR=".runtime/e2e/$WEBAPI_E2E_PROJECT"
export WEBAPI_E2E_DIRECTORY="$PWD/$WEBAPI_E2E_RELATIVE_DIR"
export WEBAPI_ENVIRONMENT_ID
WEBAPI_ENVIRONMENT_ID=$("$WEBAPI_OBS_NODE" -e 'console.log(crypto.randomUUID())')
export WEBAPI_OBS_EXPECT_EMPTY=${WEBAPI_OBS_EXPECT_EMPTY:-false}
export WEBAPI_OBS_SCENARIO=true
mkdir -p "$WEBAPI_E2E_DIRECTORY"; chmod 700 "$WEBAPI_E2E_DIRECTORY"
"$WEBAPI_OBS_NODE" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";for(const name of ["password","postgres-password","node-a","node-b","ip-hmac","cursor-key"])fs.writeFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/"+name,crypto.randomBytes(32).toString(name==="ip-hmac"||name==="cursor-key"?"base64":"base64url"),{mode:0o600});'
"$WEBAPI_OBS_NODE" scripts/observability-ports.mjs
while IFS= read -r assignment; do export "$assignment"; done < "$WEBAPI_E2E_DIRECTORY/ports.env"
compose(){ docker compose -f deploy/compose.e2e.yml -f deploy/compose.observability.yml -f deploy/compose.observability-e2e.yml "$@"; }
cleanup(){
 local result=$?; trap - EXIT
 # No container logs: framework logs may include database connection diagnostics.
 if [ -f "$WEBAPI_E2E_DIRECTORY/cross-environment.yml" ]; then
  compose -f "$WEBAPI_E2E_DIRECTORY/cross-environment.yml" down --volumes --remove-orphans >/dev/null 2>&1 || result=1
 else
  compose down --volumes --remove-orphans >/dev/null 2>&1 || result=1
 fi
 # Invalid test overlays can prevent Compose down; remove only this generated project label.
 local abandoned
 while IFS= read -r abandoned; do
  [ -n "$abandoned" ] || continue
  docker rm -f "$abandoned" >/dev/null 2>&1 || result=1
 done < <(docker ps -aq --filter "label=com.docker.compose.project=$WEBAPI_E2E_PROJECT")
 while IFS= read -r abandoned; do
  [ -n "$abandoned" ] || continue
  docker network rm "$abandoned" >/dev/null 2>&1 || result=1
 done < <(docker network ls -q --filter "label=com.docker.compose.project=$WEBAPI_E2E_PROJECT")
 rm -f "$WEBAPI_E2E_DIRECTORY/password" "$WEBAPI_E2E_DIRECTORY/postgres-password" "$WEBAPI_E2E_DIRECTORY/node-a" "$WEBAPI_E2E_DIRECTORY/node-b" "$WEBAPI_E2E_DIRECTORY/node-cross" "$WEBAPI_E2E_DIRECTORY/node-cross2" "$WEBAPI_E2E_DIRECTORY/ip-hmac" "$WEBAPI_E2E_DIRECTORY/cursor-key" "$WEBAPI_E2E_DIRECTORY/credential" "$WEBAPI_E2E_DIRECTORY/browser-cookie"
 local containers volumes leftover
 # Compose may leave an unused base lkg volume after the temporary override.
 while IFS= read -r leftover; do
  [ -n "$leftover" ] || continue
  case "$leftover" in
   "${WEBAPI_E2E_PROJECT}_pg"|"${WEBAPI_E2E_PROJECT}_nuget"|"${WEBAPI_E2E_PROJECT}_lkg-a"|"${WEBAPI_E2E_PROJECT}_lkg-b"|"${WEBAPI_E2E_PROJECT}_lkg-cross"|"${WEBAPI_E2E_PROJECT}_lkg-cross2"|"${WEBAPI_E2E_PROJECT}_loki-data"|"${WEBAPI_E2E_PROJECT}_tempo-data"|"${WEBAPI_E2E_PROJECT}_prometheus-data") docker volume rm "$leftover" >/dev/null 2>&1 || result=1;;
   *) result=1;;
  esac
 done < <(docker volume ls -q --filter "label=com.docker.compose.project=$WEBAPI_E2E_PROJECT")
 containers=$(docker ps -aq --filter "label=com.docker.compose.project=$WEBAPI_E2E_PROJECT") || result=1
 volumes=$(docker volume ls -q --filter "label=com.docker.compose.project=$WEBAPI_E2E_PROJECT") || result=1
 [ -z "$containers$volumes" ] || result=1
 "$WEBAPI_OBS_NODE" scripts/observability-e2e.mjs cleanup "$result" "$(printf '%s' "$containers" | awk 'NF{n++}END{print n+0}')" "$(printf '%s' "$volumes" | awk 'NF{n++}END{print n+0}')" || result=1
 exit "$result"
}
trap cleanup EXIT
compose create >/dev/null 2>&1
# Only initialize the two random tmpfs source volumes as the existing smoke does.
docker run --rm --user 0:0 --entrypoint bash -v "${WEBAPI_E2E_PROJECT}_loki-data:/obs-loki" -v "${WEBAPI_E2E_PROJECT}_tempo-data:/obs-tempo" mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 -c 'set -e; for volume in /obs-loki /obs-tempo; do test "$(df --block-size=1 --output=size "$volume" | tail -1)" -eq 1073741824; done; chown 10001:10001 /obs-loki /obs-tempo' >/dev/null
compose up -d --wait postgres redis >/dev/null 2>&1
compose run --rm sdk bash -c 'dotnet restore --locked-mode && dotnet build -c Release --no-restore && dotnet src/WebApi.Migrator/bin/Release/net10.0/WebApi.Migrator.dll --bootstrap --seed-catalog'
compose up -d collector prometheus loki tempo control-plane >/dev/null 2>&1
endpoint(){ printf 'http://%s' "$(compose port "$1" "$2")"; }
export WEBAPI_E2E_CONTROL_PLANE_URL
WEBAPI_E2E_CONTROL_PLANE_URL=$(endpoint control-plane 8080)
"$WEBAPI_OBS_NODE" scripts/wait-http.mjs "$WEBAPI_E2E_CONTROL_PLANE_URL/health/live"
"$WEBAPI_OBS_NODE" scripts/prepare-e2e.mjs
WEBAPI_ENVIRONMENT_ID=$("$WEBAPI_OBS_NODE" --input-type=module -e 'import fs from "node:fs";console.log(JSON.parse(fs.readFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/context.json")).environmentId)')
compose up -d --force-recreate control-plane >/dev/null 2>&1
compose up -d worker worker-b backend-a backend-b gateway-a gateway-b console >/dev/null 2>&1
WEBAPI_E2E_CONTROL_PLANE_URL=$(endpoint control-plane 8080)
"$WEBAPI_OBS_NODE" scripts/wait-http.mjs "$WEBAPI_E2E_CONTROL_PLANE_URL/health/live"
"$WEBAPI_OBS_NODE" scripts/wait-nodes.mjs
export WEBAPI_OBS_COLLECTOR_URL WEBAPI_OBS_PROMETHEUS_URL WEBAPI_OBS_LOKI_URL WEBAPI_OBS_TEMPO_URL WEBAPI_OBS_CONSOLE_URL WEBAPI_OBS_GATEWAY_A_URL WEBAPI_OBS_GATEWAY_B_URL WEBAPI_OBS_BACKEND_A_URL WEBAPI_OBS_BACKEND_B_URL
WEBAPI_OBS_COLLECTOR_URL=$(endpoint collector 4318)
WEBAPI_OBS_PROMETHEUS_URL=$(endpoint prometheus 9090)
WEBAPI_OBS_LOKI_URL=$(endpoint loki 3100)
WEBAPI_OBS_TEMPO_URL=$(endpoint tempo 3200)
WEBAPI_OBS_CONSOLE_URL=$(endpoint console 8080)
WEBAPI_OBS_GATEWAY_A_URL=$(endpoint gateway-a 8080)
WEBAPI_OBS_GATEWAY_B_URL=$(endpoint gateway-b 8080)
WEBAPI_OBS_BACKEND_A_URL=$(endpoint backend-a 8080)
WEBAPI_OBS_BACKEND_B_URL=$(endpoint backend-b 8080)
"$WEBAPI_OBS_NODE" scripts/observability-e2e.mjs ready
if [ "$kind" = browser ]; then
 "$WEBAPI_OBS_NODE" scripts/observability-e2e.mjs browser
 while [ -f "$WEBAPI_E2E_DIRECTORY/browser.wait" ]; do sleep 2; done
else
 compose run --rm -e WEBAPI_OBS_EXPECT_EMPTY sdk dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter FullyQualifiedName~ObservabilityLoopTests
 if [ "${WEBAPI_OBS_ONLY_BOUNDARIES:-false}" != true ]; then "$WEBAPI_OBS_NODE" scripts/observability-rule-loop.mjs; fi
 "$WEBAPI_OBS_NODE" scripts/observability-boundaries.mjs
 if [ "$kind" = faults ]; then
  "$WEBAPI_OBS_NODE" scripts/observability-faults.mjs
  compose run --rm sdk dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter FullyQualifiedName~ObservabilityFaultTests
 fi
fi
