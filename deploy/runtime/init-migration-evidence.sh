#!/usr/bin/env bash
set -euo pipefail
umask 077
path=${1:?migration evidence mount required}
if [ -L "$path" ] || [ ! -d "$path" ]; then
  echo 'Migration evidence mount is missing or unsafe.' >&2
  exit 3
fi
metadata=$(stat -c '%u:%g:%a' -- "$path")
if [ "$metadata" = '10001:10001:700' ]; then
  exit 0
fi
# This flag is supplied only after the runtime verifies a just-created,
# owner-labelled Docker volume and its fresh creation nonce.
if [ "${WEBAPI_MIGRATION_EVIDENCE_NEW_VOLUME:-}" != '1' ] || [[ "$metadata" != 0:0:* ]]; then
  echo 'Existing migration evidence ownership or mode is invalid; inspect without modifying it.' >&2
  exit 3
fi
if [ -n "$(find "$path" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
  echo 'New migration evidence volume is not empty; inspect without modifying it.' >&2
  exit 3
fi
chown 10001:10001 -- "$path"
chmod 700 -- "$path"
