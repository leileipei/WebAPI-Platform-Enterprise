#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
kind=${1:-}
case "$kind" in
 smoke) ;;
 *) echo 'Unknown check kind; usage: check-observability.sh smoke' >&2; exit 2 ;;
esac
WEBAPI_OBS_NODE=${WEBAPI_NODE:-node}
WEBAPI_OBS_PROJECT="webapi-obs-smoke-$(uuidgen | tr '[:upper:]' '[:lower:]')"
WEBAPI_OBS_SECRET_DIR=$(mktemp -d "${TMPDIR:-/tmp}/webapi-obs-secrets.XXXXXX")
WEBAPI_OBS_EVIDENCE=${WEBAPI_OBS_EVIDENCE:-.superpowers/sdd/2026-10-04-observability-alerting-implementation/task-2-smoke.json}
compose() { docker compose -p "$WEBAPI_OBS_PROJECT" -f deploy/compose.observability.yml "$@"; }
cleanup() {
  local result=$?
  trap - EXIT
  if [ "$result" -ne 0 ]; then compose logs --tail=50 >&2 || true; fi
  compose down --volumes --remove-orphans > /dev/null || result=1
  rm -f "$WEBAPI_OBS_SECRET_DIR/ip-hmac"
  rmdir "$WEBAPI_OBS_SECRET_DIR" || result=1
  local containers volumes
  containers=$(docker ps -aq --filter "label=com.docker.compose.project=$WEBAPI_OBS_PROJECT") || result=1
  volumes=$(docker volume ls -q --filter "label=com.docker.compose.project=$WEBAPI_OBS_PROJECT") || result=1
  if [ -n "$containers$volumes" ] || [ -e "$WEBAPI_OBS_SECRET_DIR" ]; then result=1; fi
  "$WEBAPI_OBS_NODE" scripts/observability-smoke.mjs cleanup "$WEBAPI_OBS_EVIDENCE" "$WEBAPI_OBS_PROJECT" "$result" || result=1
  exit "$result"
}
trap cleanup EXIT
umask 077
openssl rand -base64 32 > "$WEBAPI_OBS_SECRET_DIR/ip-hmac"
mkdir -p "$(dirname "$WEBAPI_OBS_EVIDENCE")"
compose create > /dev/null
# The one-off pinned SDK helper only initializes this random project's two empty volumes.
# Runtime collectors and storage servers remain non-root.
docker run --rm --user 0:0 --entrypoint bash \
  -v "${WEBAPI_OBS_PROJECT}_loki-data:/obs-loki" \
  -v "${WEBAPI_OBS_PROJECT}_tempo-data:/obs-tempo" \
  mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 \
  -c 'set -e; for volume in /obs-loki /obs-tempo; do test "$(df --block-size=1 --output=size "$volume" | tail -1)" -eq 1073741824; done; chown 10001:10001 /obs-loki /obs-tempo' > /dev/null
compose up -d > /dev/null
endpoint() { local address; address=$(compose port "$1" "$2"); printf 'http://%s' "$address"; }
"$WEBAPI_OBS_NODE" scripts/observability-smoke.mjs smoke "$WEBAPI_OBS_EVIDENCE" "$WEBAPI_OBS_PROJECT" \
  "$(endpoint collector 4318)" "$(endpoint collector 13133)" \
  "$(endpoint prometheus 9090)" "$(endpoint loki 3100)" "$(endpoint tempo 3200)"
