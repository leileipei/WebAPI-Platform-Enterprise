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
case "${WEBAPI_CONTRACT_SPLIT_POSTGRES:-0}" in
  0) ;;
  1) [ "$mode" = integration ] || { echo 'PostgreSQL groups require the integration suite.' >&2; exit 2; } ;;
  *) echo 'WEBAPI_CONTRACT_SPLIT_POSTGRES must be 0 or 1.' >&2; exit 2 ;;
esac
contract_node=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$contract_node" ]; then contract_node=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$contract_node" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
if [[ "$contract_node" != /* ]]; then contract_node="$PWD/$contract_node"; fi
if [ -n "${WEBAPI_CONTRACT_TEST_SUBNET:-}" ]; then
  "$contract_node" --input-type=module -e 'const value=process.env.WEBAPI_CONTRACT_TEST_SUBNET;const match=/^10\.251\.(\d{1,3})\.0\/24$/.exec(value??"");if(!match||Number(match[1])>255){process.stderr.write("Invalid explicit contract test subnet; use an inspected free 10.251.x.0/24 subnet.\n");process.exit(3);}'
fi
export WEBAPI_CONTRACT_TEST_OWNER
WEBAPI_CONTRACT_TEST_OWNER=$("$contract_node" -e 'process.stdout.write(crypto.randomUUID())')
export WEBAPI_CONTRACT_TEST_PROJECT="webapi-contracts-test-$WEBAPI_CONTRACT_TEST_OWNER"
repository=$PWD
mkdir -p .runtime
export WEBAPI_CONTRACT_TEST_DIRECTORY="$PWD/.runtime/$WEBAPI_CONTRACT_TEST_PROJECT"
result_file="$WEBAPI_CONTRACT_TEST_DIRECTORY/completion.json"
compose_args=(-p "$WEBAPI_CONTRACT_TEST_PROJECT" -f "$repository/deploy/compose.test.yml" -f "$repository/deploy/compose.contracts-test.yml")
project_volumes=(pg-test nuget-test)
compose_started=0
active_pid=
password_open=0
result_created=0
same_directory(){ [[ ! -L "$WEBAPI_CONTRACT_TEST_DIRECTORY" && . -ef "$WEBAPI_CONTRACT_TEST_DIRECTORY" ]]; }
compose(){ docker compose "${compose_args[@]}" "$@" 8>&-; }
run_compose(){
  local status=0
  same_directory || { echo 'Contract test directory identity changed.' >&2; return 3; }
  docker compose "${compose_args[@]}" "$@" <&0 8>&- &
  active_pid=$!
  wait "$active_pid" || status=$?
  active_pid=
  return "$status"
}
check_labels(){
  if ! printf '%s' "$1" | "$contract_node" --input-type=module -e 'import fs from "node:fs";const labels=JSON.parse(fs.readFileSync(0,"utf8"));process.exit(labels?.["com.docker.compose.project"]===process.env.WEBAPI_CONTRACT_TEST_PROJECT&&labels?.["com.webapi.contracts.owner"]===process.env.WEBAPI_CONTRACT_TEST_OWNER?0:1)'; then
    echo 'Contract test ownership mismatch; destructive cleanup rejected.' >&2
    return 1
  fi
}
check_resources(){
  local kind=$1 listed name suffix labels
  shift
  # Include exact declared names even when a foreign resource lacks the Compose project label.
  listed=$(docker "$kind" ls --filter "name=$WEBAPI_CONTRACT_TEST_PROJECT" --format '{{.Name}}') || return 1
  for name in $listed; do
    for suffix in "$@"; do
      [ "$name" = "${WEBAPI_CONTRACT_TEST_PROJECT}_$suffix" ] || continue
      labels=$(docker "$kind" inspect --format '{{json .Labels}}' "$name") || return 1
      check_labels "$labels" || return 1
      break
    done
  done
}
cleanup_docker(){
  local ids id labels
  ids=$(compose ps --all --quiet) || { echo 'Cannot inspect contract test containers.' >&2; return 1; }
  for id in $ids; do
    labels=$(docker inspect --format '{{json .Config.Labels}}' "$id") || return 1
    check_labels "$labels" || return 1
  done
  check_resources volume "${project_volumes[@]}" || return 1
  check_resources network default || return 1
  same_directory || { echo 'Contract test directory identity changed before Docker teardown.' >&2; return 1; }
  compose down --volumes --remove-orphans >/dev/null
}
cleanup(){
  local prior=$? signal_status=0 cleanup_status=0
  trap - EXIT
  trap 'signal_status=129' HUP
  trap 'signal_status=130' INT
  trap 'signal_status=143' TERM
  if [ "$password_open" = 1 ]; then
    # Docker may retain a bind mount on this inode when ownership inspection prevents teardown.
    "$contract_node" "$repository/scripts/contracts/revoke-password.mjs" || cleanup_status=3
    exec 8>&-
  fi
  if [ -n "$active_pid" ]; then
    kill -TERM "$active_pid" 2>/dev/null || true
    wait "$active_pid" 2>/dev/null || true
  fi
  if [ "$result_created" = 1 ]; then rm -f ./completion.json || cleanup_status=3; fi
  if ! same_directory; then
    echo 'Contract test directory identity changed; replacement retained.' >&2
    cleanup_status=3
  elif [ "$compose_started" = 1 ] && ! cleanup_docker; then
    echo "Contract test Docker cleanup incomplete for $WEBAPI_CONTRACT_TEST_PROJECT." >&2
    cleanup_status=3
  else
    rm -f ./nuget-seed.yml ./network.json || cleanup_status=3
    if same_directory; then
      rmdir "$WEBAPI_CONTRACT_TEST_DIRECTORY" || cleanup_status=3
    else
      echo 'Contract test directory identity changed during Docker teardown; replacement retained.' >&2
      cleanup_status=3
    fi
  fi
  if [ "$prior" -ne 0 ]; then exit "$prior"; fi
  if [ "$signal_status" -ne 0 ]; then exit "$signal_status"; fi
  exit "$cleanup_status"
}
mkdir -m 700 "$WEBAPI_CONTRACT_TEST_DIRECTORY"
cd "$WEBAPI_CONTRACT_TEST_DIRECTORY"
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
umask 077
(set -C; : > ./completion.json)
result_created=1
postgres_services=(postgres)
sdk_options=(--rm -e WEBAPI_CONTRACT_TEST_OWNER -v "$result_file:/contract-completion.json")
if [ "${WEBAPI_CONTRACT_SPLIT_POSTGRES:-0}" = 1 ]; then
  compose_args+=(-f "$repository/deploy/compose.contracts-postgres-groups.yml")
  project_volumes+=(pg-security-test pg-credential-test pg-import-test)
  postgres_services+=(postgres-security postgres-credential postgres-import)
  sdk_options+=(-e WEBAPI_CONTRACT_SPLIT_POSTGRES=1)
fi
if [ -n "${WEBAPI_CONTRACT_TEST_SUBNET:-}" ]; then
  "$contract_node" --input-type=module -e 'import fs from "node:fs";const subnet=process.env.WEBAPI_CONTRACT_TEST_SUBNET;fs.writeFileSync("network.json",JSON.stringify({networks:{default:{ipam:{config:[{subnet}]}}}}),{mode:0o600,flag:"wx"});'
  compose_args+=(-f "$WEBAPI_CONTRACT_TEST_DIRECTORY/network.json")
fi
contract_seed=${WEBAPI_CONTRACT_NUGET_SEED:-webapi-enterprise-core-test_nuget-test}
if [[ "$contract_seed" =~ ^[a-zA-Z0-9_.-]+$ ]] && docker volume inspect "$contract_seed" >/dev/null 2>&1; then
  cat > ./nuget-seed.yml <<YAML
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
if [[ -e ./postgres-password || -L ./postgres-password ]]; then
  echo 'Contract test password path already exists.' >&2
  exit 3
fi
set -C
exec 8>./postgres-password
set +C
password_open=1
"$contract_node" -e 'require("node:fs").writeFileSync(1,require("node:crypto").randomBytes(32).toString("base64url"))' >&8 8>&-
compose_started=1
run_compose up -d --wait "${postgres_services[@]}" redis
run_compose run "${sdk_options[@]}" sdk bash -c '
  set -euo pipefail
  mkdir -p /test-src /root/.nuget/packages
  tar --exclude=bin --exclude=obj --exclude=node_modules -cf - -C /workspace Directory.Build.props Directory.Packages.props WebApi.Enterprise.sln .config src tests third_party deploy scripts tools | tar -xf - -C /test-src
  if [ -d /nuget-seed ]; then cp -a /nuget-seed/. /root/.nuget/packages/; fi
  cd /test-src
  dotnet restore WebApi.Enterprise.sln --locked-mode
  dotnet restore src/WebApi.RuntimeTool/WebApi.RuntimeTool.csproj --locked-mode
  dotnet build src/WebApi.RuntimeTool/WebApi.RuntimeTool.csproj --no-restore --configuration Release --output /runtime-tool
  dotnet build src/WebApi.Migrator/WebApi.Migrator.csproj --no-restore --configuration Release --output /migrator
  dotnet build scripts/contracts/runner/WebApi.Contracts.TestLogger.csproj --configuration Release --output /contract-test-logger -p:RestoreLockedMode=true
  export WEBAPI_RUNTIME_TOOL_DLL=/runtime-tool/WebApi.RuntimeTool.dll
  export WEBAPI_SOURCE_ROOT=/test-src
  export WEBAPI_MIGRATOR_DLL=/migrator/WebApi.Migrator.dll
  export WEBAPI_TEST_SQL_MIGRATION_LIMITS=1
  export WEBAPI_MIGRATION_TRANSACTION_SECONDS=900
  export WEBAPI_MIGRATION_SETTINGS_MAX_BYTES=65536
  export WEBAPI_MIGRATION_RULE_MAX_BYTES=16384
  export WEBAPI_MIGRATION_RULE_MAX_COUNT=100000
  export WEBAPI_MIGRATION_TOTAL_MAX_BYTES=67108864
  export WEBAPI_MIGRATION_RECEIPT_MAX_ROWS=100000
  result_directory=$(mktemp -d)
  export WEBAPI_CONTRACT_RESULT="$result_directory/completion.json"
  dotnet test --no-restore --test-adapter-path /contract-test-logger --logger WebApiContractCompletion "$@"
  cat "$WEBAPI_CONTRACT_RESULT" > /contract-completion.json
' -- "$test_project" --configuration Release "$@"
"$contract_node" "$repository/scripts/contracts/check-completion.mjs" ./completion.json "$WEBAPI_CONTRACT_TEST_OWNER" 8>&-
