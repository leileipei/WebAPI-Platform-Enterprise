#!/usr/bin/env bash
# G0-2: governance worktree audit — run BEFORE creating the merge branch.
#
# Usage: scripts/governance-merge/preflight-worktree-audit.sh [worktree] [expected_head]
#   worktree      defaults to .worktrees/merge-governance-24-20261104
#   expected_head defaults to the tip of local `main`
#
# Hard failures (exit 1): HEAD not at expected head, deleted files, leaked
# build/secret paths, oversized untracked files, modified migrations other
# than the model snapshot, duplicate migration prefixes.
# Warnings (exit 0, human review): secret-like patterns, console changes.
#
# POSIX-ish bash (no mapfile/associative arrays) — runs on macOS default bash.
set -uo pipefail
repo_root=$(cd "$(dirname "$0")/../.." && pwd)

# The default target lives under the main checkout; if this copy of the
# script runs from inside a worktree, resolve the main worktree via git.
default_wt=""
main_wt=$(git -C "$repo_root" worktree list --porcelain 2>/dev/null | awk '/^worktree /{w=substr($0,10)} /^branch refs\/heads\/main$/{print w; exit}')
for cand in "$repo_root/.worktrees/merge-governance-24-20261104" "${main_wt:+$main_wt/.worktrees/merge-governance-24-20261104}"; do
  [ -n "$cand" ] && [ -d "$cand" ] && { default_wt=$cand; break; }
done
wt=${1:-$default_wt}
[ -n "$wt" ] && [ -d "$wt" ] || { echo "worktree not found — pass its path as \$1" >&2; exit 2; }
expected=${2:-$(git -C "$repo_root" rev-parse --verify main)}
wt=$(cd "$wt" && pwd)
head_sha=$(git -C "$wt" rev-parse HEAD)
size_limit=$((5 * 1024 * 1024))

fails=0; warns=0
fail() { echo "  FAIL  $*"; fails=$((fails + 1)); }
warn() { echo "  WARN  $*"; warns=$((warns + 1)); }
ok()   { echo "  OK    $*"; }

echo "== G0-2 worktree audit: $wt"

# -- HEAD ---------------------------------------------------------------
echo "-- HEAD"
if [ "$head_sha" = "$expected" ]; then
  ok "HEAD $head_sha matches expected $expected (merge is a clean overlay of uncommitted work)"
else
  fail "HEAD $head_sha != expected $expected — rebase or re-apply the work before merging"
fi
branch=$(git -C "$wt" branch --show-current)
if [ -z "$branch" ]; then
  warn "worktree is detached HEAD — create the merge branch before any further work (git switch -c)"
else
  ok "worktree on branch: $branch"
fi

# -- change inventory ----------------------------------------------------
echo "-- change inventory"
paths=(); xys=()
untracked=0; modified=0; deleted=0
# Modified/deleted come from status; untracked is enumerated per file via
# ls-files, because `git status` collapses wholly-untracked directories into
# single entries and would skip their contents.
while IFS= read -r -d '' e; do
  xy=${e:0:2}; p=${e:3}
  [ -z "$p" ] && continue
  [ "$xy" = "??" ] && continue
  case "$xy" in
    " D"|"D ") deleted=$((deleted + 1)) ;;
    *) modified=$((modified + 1)) ;;
  esac
  paths+=("$p"); xys+=("$xy")
done < <(git -C "$wt" status -z)
while IFS= read -r -d '' p; do
  [ -z "$p" ] && continue
  untracked=$((untracked + 1)); paths+=("$p"); xys+=("??")
done < <(git -C "$wt" ls-files --others --exclude-standard -z)
total=${#paths[@]}
echo "  untracked=$untracked modified=$modified deleted=$deleted total=$total"
if [ "$deleted" -gt 0 ]; then
  fail "$deleted deleted file(s) — G-series merge is expected to be additive; list:"
  git -C "$wt" status -s | grep -E '^ ?D' | sed 's/^/    /'
else
  ok "no deleted files"
fi

# -- leak check -----------------------------------------------------------
echo "-- leak check (build/secret/runtime paths, oversized files)"
leak_re='^(bin/|obj/|\.runtime/|\.secrets/|node_modules/|\.git)|/(bin|obj|node_modules|\.runtime|\.secrets)/|\.(suo|user|zip|7z|dmg)$'
leaks=0
i=0
for p in "${paths[@]}"; do
  if [[ "$p" =~ $leak_re ]]; then fail "leaked path in merge set: $p"; leaks=$((leaks + 1)); i=$((i + 1)); continue; fi
  f="$wt/$p"
  if [ -f "$f" ]; then
    s=$(stat -f%z "$f" 2>/dev/null || stat -c%s "$f" 2>/dev/null || echo 0)
    [ "$s" -gt "$size_limit" ] && fail "oversized file in merge set (>5MB): $p ($s bytes)"
  fi
  i=$((i + 1))
done
[ "$leaks" -eq 0 ] && ok "no build/secret/runtime paths, no oversized files"

# -- migration integrity ---------------------------------------------------
echo "-- migration integrity"
mig_dir=src/WebApi.Infrastructure/Persistence/Migrations
bad_migs=0
i=0
for p in "${paths[@]}"; do
  case "$p" in
    "$mig_dir"/*)
      case "${xys[$i]}" in "??") : ;;
        *) if [ "$p" != "$mig_dir/WebApiDbContextModelSnapshot.cs" ]; then
             fail "existing migration modified (history rewrite): $p"; bad_migs=$((bad_migs + 1))
           fi ;;
      esac ;;
  esac
  i=$((i + 1))
done
if [ "$bad_migs" -eq 0 ]; then ok "no existing migrations modified (append-only)"; fi
new_migs=$(git -C "$wt" ls-files --others --exclude-standard -z | tr '\0' '\n' | grep -c "^$mig_dir/2026.*\.cs$" || true)
dup=$(git -C "$wt" ls-files --others --exclude-standard -z | tr '\0' '\n' | grep "^$mig_dir/2026.*\.cs$" | grep -v Designer | sed 's/.*Migrations\///; s/\.[^.]*$//' | sort | uniq -d)
if [ -n "$dup" ]; then
  fail "duplicate migration prefixes:"; echo "$dup" | sed 's/^/    /'
else
  ok "no duplicate migration prefixes ($new_migs new migration files)"
fi

# -- console scope ---------------------------------------------------------
echo "-- console scope"
console_changes=$(( $(git -C "$wt" ls-files --others --exclude-standard -z | tr '\0' '\n' | grep -c '^console/' || true) \
                 + $(git -C "$wt" status -z | tr '\0' '\n' | sed -n 's/^ M //p; s/^M  //p' | grep -c '^console/' || true) ))
if [ "$console_changes" -eq 0 ]; then
  ok "no console changes (expected for G-series)"
else
  warn "$console_changes console file(s) changed — review UI scope and run check-console.sh"
fi

# -- secret-like patterns (WARN, human review) -------------------------------
echo "-- secret-like patterns (review every hit)"
patterns=(
  '-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----'
  'AKIA[0-9A-Z]{16}'
  'eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'
  '[Pp]assword=[^;"'"'"'[:space:]]{6,}'
  'Bearer [A-Za-z0-9._~-]{24,}'
  '["'"'"']?[Ss]ecret["'"'"']?[[:space:]]*[:=][[:space:]]*["'"'"']?[A-Za-z0-9+/=_-]{32,}'
  '["'"'"']?(api_?key|access_?key|client_secret)["'"'"']?[[:space:]]*[:=][[:space:]]*["'"'"']?[A-Za-z0-9+/=_-]{32,}'
)
scan_files=()
for p in "${paths[@]}"; do
  f="$wt/$p"
  [ -f "$f" ] && [ -r "$f" ] && scan_files+=("$f")
done
hits=0
if [ "${#scan_files[@]}" -gt 0 ]; then
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    hits=$((hits + 1)); echo "    $line"
    [ "$hits" -ge 50 ] && { warn "output truncated at 50 hits — re-run a targeted grep"; break; }
  done < <(grep -InHE -e "${patterns[*]}" -- "${scan_files[@]}" 2>/dev/null | cut -c1-200)
fi
if [ "$hits" -eq 0 ]; then
  ok "no secret-like patterns"
else
  warn "$hits secret-like hit(s) above — verify each is a placeholder/fixture, never a real credential"
fi

# -- summary -----------------------------------------------------------------
echo "== summary: FAIL=$fails WARN=$warns"
if [ "$fails" -gt 0 ]; then
  echo "RESULT: DO NOT MERGE — fix hard failures first"
  exit 1
fi
echo "RESULT: hard checks passed — resolve WARNs (human review) before Phase 1"
exit 0
