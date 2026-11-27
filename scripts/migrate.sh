#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

usage() {
  cat <<'USAGE'
Usage: scripts/migrate.sh [--bootstrap] [--seed-catalog]
       scripts/migrate.sh --run-sql
       scripts/migrate.sh --inspect-evidence-archive | --inspect-evidence-archive-stdin
       scripts/migrate.sh --verify-evidence-restore | --verify-evidence-restore-digest
       scripts/migrate.sh --help | -h
USAGE
}
invalid_arguments() { usage >&2; exit 2; }

needs_database=true
case $# in
  0) ;;
  1) case "$1" in
       --help|-h) usage; exit 0 ;;
       --inspect-evidence-archive|--inspect-evidence-archive-stdin) needs_database=false ;;
       --bootstrap|--seed-catalog|--run-sql|--verify-evidence-restore|--verify-evidence-restore-digest) ;;
       *) invalid_arguments ;;
     esac ;;
  2) [[ ( $1 == --bootstrap && $2 == --seed-catalog ) || ( $1 == --seed-catalog && $2 == --bootstrap ) ]] || invalid_arguments ;;
  *) invalid_arguments ;;
esac

compose=(docker compose -f deploy/compose.test.yml)
if "$needs_database"; then
  "${compose[@]}" up -d --wait postgres
fi
# Compose forwards these settings by name. Values and credentials stay out of the command line.
sdk_environment=(
  --env WEBAPI_MIGRATION_TRANSACTION_SECONDS
  --env WEBAPI_MIGRATION_SETTINGS_MAX_BYTES
  --env WEBAPI_MIGRATION_RULE_MAX_BYTES
  --env WEBAPI_MIGRATION_RULE_MAX_COUNT
  --env WEBAPI_MIGRATION_TOTAL_MAX_BYTES
  --env WEBAPI_MIGRATION_RECEIPT_MAX_ROWS
  --env WEBAPI_MIGRATION_EVIDENCE_DIR
  --env WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES
  --env WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES
  --env WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES
  --env WEBAPI_MIGRATION_SQL_FILE
  --env WEBAPI_MIGRATION_SQL_SHA256
  --env WEBAPI_MIGRATION_ARCHIVE_FILE
  --env WEBAPI_MIGRATION_ARCHIVE_SHA256
  --env WEBAPI_MIGRATION_ARCHIVE_CONTENT_SHA256
  --env WEBAPI_MIGRATION_ARCHIVE_ATTEMPTS
  --env WEBAPI_MIGRATION_ARCHIVE_PENDING
  --env WEBAPI_BOOTSTRAP_USERNAME
  --env WEBAPI_BOOTSTRAP_PASSWORD_FILE
)
exec "${compose[@]}" run --rm --no-deps "${sdk_environment[@]}" sdk dotnet run --project src/WebApi.Migrator -c Release -- "$@"
