#!/usr/bin/env bash
set -euo pipefail
umask 077
source_dir=${1:?evidence directory required}
archive=${2:?archive path required}
[[ "$source_dir" = /* && "$archive" = /* && ! -L "$source_dir" && -d "$source_dir" ]] || { echo 'Unsafe evidence directory.' >&2; exit 3; }
lock="$source_dir/.journal.lock"
[[ ! -L "$lock" && -f "$lock" ]] || { echo 'Evidence lock is unavailable.' >&2; exit 3; }
directory_identity=$(stat -c '%d:%i:%u:%g:%a' -- "$source_dir")
lock_identity=$(stat -c '%d:%i:%u:%g:%a:%h:%s' -- "$lock")
IFS=: read -r _ _ directory_uid directory_gid directory_mode <<< "$directory_identity"
IFS=: read -r _ _ lock_uid lock_gid lock_mode lock_links lock_size <<< "$lock_identity"
[[ "$directory_mode" = 700 && "$lock_mode" = 600 && "$lock_links" = 1 && "$lock_size" = 0 &&
   "$directory_uid" = "$lock_uid" && "$directory_gid" = "$lock_gid" ]] || { echo 'Unsafe evidence ownership or mode.' >&2; exit 3; }
exec 9<"$lock"
flock -x -w 5 9
[[ "$(stat -Lc '%d:%i:%u:%g:%a:%h:%s' "/proc/$$/fd/9")" = "$lock_identity" &&
   "$(stat -c '%d:%i:%u:%g:%a:%h:%s' -- "$lock")" = "$lock_identity" &&
   "$(stat -c '%d:%i:%u:%g:%a' -- "$source_dir")" = "$directory_identity" ]] || { echo 'Evidence identity changed.' >&2; exit 3; }
set -o noclobber
exec 8>"$archive"
set +o noclobber
tar --numeric-owner -cf - -C "$source_dir" . >&8
exec 8>&-
[[ "$(stat -c '%d:%i:%u:%g:%a:%h:%s' -- "$lock")" = "$lock_identity" &&
   "$(stat -c '%d:%i:%u:%g:%a' -- "$source_dir")" = "$directory_identity" ]] || { echo 'Evidence identity changed.' >&2; exit 3; }
sync "$archive"
sync "$(dirname "$archive")"
