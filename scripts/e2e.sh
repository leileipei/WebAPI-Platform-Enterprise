#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
NODE=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$NODE" ]; then NODE=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$NODE" ] || { echo 'Set WEBAPI_NODE to a Node.js executable.' >&2; exit 2; }
[ -f console/dist/index.html ] || { echo "Build console first with scripts/check-console.sh." >&2; exit 2; }
export WEBAPI_E2E_PROJECT="webapi-enterprise-e2e-$(date +%s)-$RANDOM"
export WEBAPI_E2E_RELATIVE_DIR=".runtime/e2e/$WEBAPI_E2E_PROJECT"
export WEBAPI_E2E_DIRECTORY="$PWD/$WEBAPI_E2E_RELATIVE_DIR"
export WEBAPI_ENVIRONMENT_ID
WEBAPI_ENVIRONMENT_ID=$("$NODE" -e 'console.log(crypto.randomUUID())')
mkdir -p .runtime/e2e
mkdir -m 700 "$WEBAPI_E2E_DIRECTORY"
compose(){ docker compose -f deploy/compose.e2e.yml "$@"; }
cleanup() {
  local run_status=$? cleanup_status=0
  trap - EXIT
  compose down --volumes --remove-orphans >/dev/null || cleanup_status=$?
  rm -f -- "$WEBAPI_E2E_DIRECTORY"/{password,postgres-password,node-a,node-b,credential,login-hmac} \
    "$WEBAPI_E2E_DIRECTORY"/password-e2e-{admin,reader,approver,security} || cleanup_status=$?
  if (( run_status != 0 )); then exit "$run_status"; fi
  exit "$cleanup_status"
}
trap cleanup EXIT
"$NODE" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";for(const name of ["password","postgres-password","node-a","node-b"])fs.writeFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/"+name,crypto.randomBytes(32).toString("base64url"),{mode:0o600});fs.writeFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/login-hmac",crypto.randomBytes(32).toString("base64"),{mode:0o400});'
# No persistent test identities survive the process; only this randomly named project's volumes are removed.
compose up -d --wait postgres redis
compose run --rm sdk bash -c 'chmod 700 /evidence && dotnet restore && dotnet restore --locked-mode && dotnet build -c Release --no-restore && dotnet src/WebApi.Migrator/bin/Release/net10.0/WebApi.Migrator.dll --bootstrap --seed-catalog'
compose up -d control-plane
"$NODE" scripts/wait-http.mjs http://127.0.0.1:5092/health/live
"$NODE" scripts/prepare-e2e.mjs
WEBAPI_ENVIRONMENT_ID=$("$NODE" --input-type=module -e 'import fs from "node:fs";console.log(JSON.parse(fs.readFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/context.json")).environmentId)')
compose up -d --force-recreate control-plane
compose up -d worker backend-a backend-b gateway-a gateway-b console
"$NODE" scripts/wait-http.mjs http://127.0.0.1:5092/health/live
"$NODE" scripts/wait-nodes.mjs
# EndToEnd rotation tests assert a named owner and a 40-hex source revision; supply them per run.
export WEBAPI_E2E_OWNER="e2e-owner"
export WEBAPI_SOURCE_REVISION="$(git rev-parse HEAD)"
if [ "${1:-}" = '--browser' ]; then
  "$NODE" --input-type=module -e 'import fs from "node:fs";fs.writeFileSync(".runtime/browser-e2e.json",JSON.stringify({project:process.env.WEBAPI_E2E_PROJECT,directory:process.env.WEBAPI_E2E_DIRECTORY,environmentId:process.env.WEBAPI_ENVIRONMENT_ID},null,2));'
  echo 'Disposable browser QA ready at 4182. Remove .runtime/browser-e2e.wait when finished; process then runs E2E and cleans its own data.'
  touch .runtime/browser-e2e.wait
  while [ -f .runtime/browser-e2e.wait ]; do sleep 2; done
fi
# These five scenarios consume receipts from their own stacks (see console-ci-phase1.md).
# The core stack runs every other test, including untagged tests added to the SDK project.
core_scenarios='Scenario!=Observability&Scenario!=Sso&Scenario!=Settings&Scenario!=Policies&Scenario!=NetworkPolicies'
compose run --rm -e WEBAPI_E2E_OWNER -e WEBAPI_SOURCE_REVISION sdk dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release --filter "$core_scenarios"
"$NODE" scripts/e2e-faults.mjs
