#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
kind=${1:?usage: check.sh integration [test args]}; shift
case "$kind" in
 integration) project=tests/WebApi.Integration.Tests/WebApi.Integration.Tests.csproj ;;
 domain) project=tests/WebApi.Domain.Tests/WebApi.Domain.Tests.csproj ;;
 gateway) project=tests/WebApi.Gateway.Tests/WebApi.Gateway.Tests.csproj ;;
 e2e) exec ./scripts/e2e.sh "$@" ;;
 *) echo 'Unknown check kind' >&2; exit 2 ;;
esac
[ -f "$project" ] || { echo "Missing test project: $project" >&2; exit 2; }
node_bin=${WEBAPI_NODE:-$(command -v node || true)}
if [ -z "$node_bin" ]; then node_bin=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node; fi
[ -x "$node_bin" ] || { echo 'Set WEBAPI_NODE to an existing Node executable.' >&2; exit 3; }
if [[ "$node_bin" != /* ]]; then node_bin="$PWD/$node_bin"; fi
export WEBAPI_CONTRACT_TEST_OWNER
WEBAPI_CONTRACT_TEST_OWNER=$("$node_bin" -e 'process.stdout.write(crypto.randomUUID())')

docker compose -f deploy/compose.test.yml up -d --wait postgres redis
repository=$PWD
result_dir=$(mktemp -d "${TMPDIR:-/tmp}/webapi-backend-test.XXXXXXXX")
cd "$result_dir"
result_dir=$PWD
cleanup_result() {
  local status=$? cleanup_status=0
  rm -f ./completion.json || cleanup_status=3
  if [[ ! -L "$result_dir" && . -ef "$result_dir" ]]; then
    rmdir "$result_dir" || cleanup_status=3
  else
    cleanup_status=3
  fi
  if (( status != 0 )); then exit "$status"; fi
  exit "$cleanup_status"
}
trap cleanup_result EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
(umask 077; set -C; : > ./completion.json)
(
  cd "$repository"
  docker compose -f deploy/compose.test.yml run --rm -e WEBAPI_CONTRACT_TEST_OWNER \
    -v "$result_dir/completion.json:/contract-completion.json" sdk \
    bash /workspace/scripts/contracts/run-with-completion.sh "$project" --configuration Release "$@"
)
"$node_bin" "$repository/scripts/contracts/check-completion.mjs" ./completion.json "$WEBAPI_CONTRACT_TEST_OWNER"
