#!/usr/bin/env bash
set -euo pipefail
umask 077
for pair in pg:70 lkg-a:10001 lkg-b:10001 dp-keys:10001 prometheus-data:65534 loki-data:10001 tempo-data:10001 bootstrap-password:10001; do
  name=${pair%:*}; uid=${pair#*:}
  test -d "/volumes/$name"
  chown "$uid:$uid" "/volumes/$name"
  chmod 700 "/volumes/$name"
done
copy_secret() {
  local source=$1 role=$2 uid=$3 destination="/volumes/secrets-$2/$1"
  test -f "/input/$source"
  if [ -e "$destination" ]; then cmp -s "/input/$source" "$destination" || { echo 'Existing runtime secret differs; rotation requires an explicit procedure.' >&2; exit 3; }
  else install -m 600 -o "$uid" -g "$uid" "/input/$source" "$destination"; fi
}
for role in postgres control-plane worker gateway-a gateway-b migrator; do
  uid=10001; [ "$role" != postgres ] || uid=70
  chown "$uid:$uid" "/volumes/secrets-$role"; chmod 700 "/volumes/secrets-$role"
  case "$role" in
    postgres|migrator) copy_secret postgres-password "$role" "$uid" ;;
    control-plane) for secret in postgres-password node-a node-b ip-hmac cursor-signing; do copy_secret "$secret" "$role" "$uid"; done ;;
    worker) for secret in postgres-password ip-hmac cursor-signing; do copy_secret "$secret" "$role" "$uid"; done ;;
    gateway-a) for secret in node-a ip-hmac; do copy_secret "$secret" "$role" "$uid"; done ;;
    gateway-b) for secret in node-b ip-hmac; do copy_secret "$secret" "$role" "$uid"; done ;;
  esac
done
chown 10001:10001 /volumes/secrets-cache; chmod 700 /volumes/secrets-cache
copy_secret cache-hmac cache 10001
if [ -f /input/bootstrap-password ]; then install -m 600 -o 10001 -g 10001 /input/bootstrap-password /volumes/bootstrap-password/password; fi
echo 'Owned runtime volumes and role-specific secret permissions initialized.'
