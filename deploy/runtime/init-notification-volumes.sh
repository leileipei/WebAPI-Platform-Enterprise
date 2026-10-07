#!/bin/bash
set -euo pipefail
umask 077
for role in sender fixture; do
  target=/volumes/$role
  if [ -z "$(ls -A "$target")" ]; then
    cp /input/$role/* "$target/"
  else
    # Restart/init is verification, never secret rotation or overwrite.
    test "$(find "$target" -mindepth 1 -maxdepth 1 | wc -l)" -eq "$(find /input/$role -mindepth 1 -maxdepth 1 | wc -l)"
    for file in /input/$role/*; do cmp "$file" "$target/$(basename "$file")"; done
  fi
  chown -R 10001:10001 "$target"
  chmod 700 "$target"
  chmod 600 "$target"/*
done
chown 10001:10001 /volumes/data
chmod 700 /volumes/data
