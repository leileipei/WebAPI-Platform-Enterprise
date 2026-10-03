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
mkdir -p "$WEBAPI_E2E_DIRECTORY"; chmod 700 "$WEBAPI_E2E_DIRECTORY"
"$NODE" --input-type=module -e 'import fs from "node:fs";import crypto from "node:crypto";for(const name of ["password","postgres-password","node-a","node-b"])fs.writeFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/"+name,crypto.randomBytes(32).toString("base64url"),{mode:0o600});'
compose(){ docker compose -f deploy/compose.e2e.yml "$@"; }
cleanup(){ compose down --volumes --remove-orphans >/dev/null 2>&1; rm -f "$WEBAPI_E2E_DIRECTORY/password" "$WEBAPI_E2E_DIRECTORY/postgres-password" "$WEBAPI_E2E_DIRECTORY/node-a" "$WEBAPI_E2E_DIRECTORY/node-b" "$WEBAPI_E2E_DIRECTORY/credential"; }
trap cleanup EXIT
# No persistent test identities survive the process; only this randomly named project's volumes are removed.
compose up -d --wait postgres redis
compose run --rm sdk bash -c 'dotnet restore && dotnet restore --locked-mode && dotnet build -c Release --no-restore && dotnet src/WebApi.Migrator/bin/Release/net10.0/WebApi.Migrator.dll --bootstrap --seed-catalog'
compose up -d control-plane
"$NODE" scripts/wait-http.mjs http://127.0.0.1:5092/health/live
"$NODE" scripts/prepare-e2e.mjs
WEBAPI_ENVIRONMENT_ID=$("$NODE" --input-type=module -e 'import fs from "node:fs";console.log(JSON.parse(fs.readFileSync(process.env.WEBAPI_E2E_DIRECTORY+"/context.json")).environmentId)')
compose up -d --force-recreate control-plane
compose up -d worker backend-a backend-b gateway-a gateway-b console
"$NODE" scripts/wait-http.mjs http://127.0.0.1:5092/health/live
"$NODE" scripts/wait-nodes.mjs
if [ "${1:-}" = '--browser' ]; then
  "$NODE" --input-type=module -e 'import fs from "node:fs";fs.writeFileSync(".runtime/browser-e2e.json",JSON.stringify({project:process.env.WEBAPI_E2E_PROJECT,directory:process.env.WEBAPI_E2E_DIRECTORY,environmentId:process.env.WEBAPI_ENVIRONMENT_ID},null,2));'
  echo 'Disposable browser QA ready at 4182. Remove .runtime/browser-e2e.wait when finished; process then runs E2E and cleans its own data.'
  touch .runtime/browser-e2e.wait
  while [ -f .runtime/browser-e2e.wait ]; do sleep 2; done
fi
compose run --rm sdk dotnet test tests/WebApi.EndToEnd.Tests/WebApi.EndToEnd.Tests.csproj -c Release
"$NODE" scripts/e2e-faults.mjs
