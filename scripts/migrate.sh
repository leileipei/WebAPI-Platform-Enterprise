#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose -f deploy/compose.test.yml up -d --wait postgres
docker compose -f deploy/compose.test.yml run --rm sdk dotnet run --project src/WebApi.Migrator -c Release "$@"
