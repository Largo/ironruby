#!/bin/bash
# Optional test and benchmark: the DOOM renderer of khasinski/doom (a pure-Ruby port of the
# 1993 engine) drawing E1M1 from four viewpoints, under CRuby and IronRuby side by side.
#
#   Util/doom/run.sh --fetch            # clone the pinned commit and the shareware WAD, once
#   Util/doom/run.sh                    # 3 runs each, Release; checksum + ms per frame
#   Util/doom/run.sh --runs 5 --warmup 40 --reps 60
#   Util/doom/run.sh --ironruby-only    # skip CRuby (its numbers do not move)
#   Util/doom/run.sh --ir-arg -X:NoOSR  # any IronRuby flag, repeatable
#   Util/doom/run.sh --kernels          # also Util/bench/render.rb, the renderer's loops on their own
#
# A test as well as a benchmark: every viewpoint's framebuffer checksum must be CRuby's (the
# script exits 1 if one is not). Nothing is vendored - the game is GPL-2.0 and the WAD is id's
# shareware - so --fetch puts both under DOOM_DIR (default ~/.cache/ironruby-doom), the game
# at a pinned commit and the WAD checked against its MD5. See Util/doom/README.md.
set -u
HERE="$(dirname "$(readlink -f "$0")")"
ROOT="$(dirname "$(dirname "$HERE")")"

DOOM_REPO=https://github.com/khasinski/doom
DOOM_COMMIT=89c9252980663a00b6f12484ccb4cb559ac79e17   # doom 0.11.2, 2026-11-01
WAD_URL=https://raw.githubusercontent.com/Akbar30Bill/DOOM_wads/master/doom1.wad
WAD_MD5=f0cefca49926d00903cf57551d901abe              # shareware DOOM 1.9

DOOM_DIR="${DOOM_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/ironruby-doom}"
RUBY="${RUBY:-ruby}"
IR="${IR:-$ROOT/ir.sh}"
export IR_CONFIG="${IR_CONFIG:-Release}"
export IR_TFM="${IR_TFM:-net10.0}"

runs=3 warmup=20 reps=60 fetch=0 cruby=1 kernels=0
ir_args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --fetch) fetch=1 ;;
    --runs) runs="$2"; shift ;;
    --warmup) warmup="$2"; shift ;;
    --reps) reps="$2"; shift ;;
    --ironruby-only) cruby=0 ;;
    --ir-arg) ir_args+=("$2"); shift ;;
    --kernels) kernels=1 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "doom: unknown argument $1" >&2; exit 2 ;;
  esac
  shift
done

src="$DOOM_DIR/doom"
wad="$DOOM_DIR/doom1.wad"

if [ $fetch = 1 ]; then
  mkdir -p "$DOOM_DIR" || exit 1
  if [ ! -d "$src/.git" ]; then
    git clone --quiet "$DOOM_REPO" "$src" || exit 1
  fi
  git -C "$src" fetch --quiet origin "$DOOM_COMMIT" 2>/dev/null || git -C "$src" fetch --quiet origin
  git -C "$src" -c advice.detachedHead=false checkout --quiet "$DOOM_COMMIT" || exit 1
  if [ ! -f "$wad" ] || [ "$(md5sum < "$wad" | cut -d' ' -f1)" != "$WAD_MD5" ]; then
    curl -fsSL -o "$wad.part" "$WAD_URL" || exit 1
    got=$(md5sum < "$wad.part" | cut -d' ' -f1)
    [ "$got" = "$WAD_MD5" ] || { echo "doom: WAD MD5 is $got, expected $WAD_MD5" >&2; rm -f "$wad.part"; exit 1; }
    mv "$wad.part" "$wad"
  fi
  echo "doom: $src at $(git -C "$src" rev-parse --short HEAD), $wad"
fi

[ -f "$src/bench/real_frame_bench.rb" ] && [ -f "$wad" ] || {
  echo "doom: nothing at $DOOM_DIR; run Util/doom/run.sh --fetch first" >&2; exit 2;
}
[ "$(git -C "$src" rev-parse HEAD)" = "$DOOM_COMMIT" ] ||
  echo "doom: WARNING: $src is not at the pinned $DOOM_COMMIT" >&2

out=$(mktemp -d "${TMPDIR:-/tmp}/doom-XXXXXX") || exit 1
echo "doom: logs in $out; IronRuby $IR_CONFIG $IR_TFM ${ir_args[*]:-}; warmup=$warmup reps=$reps; load $(cut -d' ' -f1-3 /proc/loadavg 2>/dev/null)"

# One process: per-frame milliseconds -> "mean median best" over the timed frames.
stats() {
  awk '/^FRAME_MS/ { v[n++] = $2; s += $2 }
       END { if (n == 0) { print "- - -"; exit }
             asort(v); m = (n % 2) ? v[(n + 1) / 2] : (v[n / 2] + v[n / 2 + 1]) / 2
             printf "%.2f %.2f %.2f\n", s / n, m, v[1] }' "$1"
}

run_one() { # label log interpreter...
  local label="$1" log="$2"; shift 2
  (cd "$src" && WAD="$wad" WARMUP="$warmup" REPS="$reps" timeout 1800 "$@" -Ilib bench/real_frame_bench.rb) > "$log" 2>&1
}

status=0
declare -A checksum
for k in $(seq 1 "$runs"); do
  for who in cruby ironruby; do
    [ $who = cruby ] && [ $cruby = 0 ] && continue
    log="$out/$who-$k.txt"
    if [ $who = cruby ]; then run_one $who "$log" "$RUBY"; else run_one $who "$log" "$IR" "${ir_args[@]}"; fi
    ck=$(awk '/^CHECKSUM/ { print $2 }' "$log")
    [ -n "$ck" ] || { echo "doom: $who run $k produced no checksum, see $log" >&2; tail -5 "$log" >&2; status=1; continue; }
    checksum[$who]="$ck"
    printf "%-9s run %d  mean/median/best ms per frame: %s  checksum %s\n" "$who" "$k" "$(stats "$log")" "$ck"
  done
done

if [ $cruby = 1 ] && [ -n "${checksum[cruby]:-}" ] && [ -n "${checksum[ironruby]:-}" ]; then
  if [ "${checksum[cruby]}" != "${checksum[ironruby]}" ]; then
    echo "doom: FAILED: IronRuby's framebuffer checksum ${checksum[ironruby]} is not CRuby's ${checksum[cruby]}" >&2
    for vp in spawn hallway corner reverse; do
      a=$(awk -v vp=$vp '$1 == "VP_CHECKSUM" && $2 == vp { print $3 }' "$out/cruby-1.txt")
      b=$(awk -v vp=$vp '$1 == "VP_CHECKSUM" && $2 == vp { print $3 }' "$out/ironruby-1.txt")
      [ "$a" = "$b" ] || echo "doom:   $vp: CRuby $a, IronRuby $b" >&2
    done
    status=1
  else
    echo "doom: checksums match"
  fi
fi

if [ $kernels = 1 ]; then
  echo "== Util/bench/render.rb (best ms per frame)"
  [ $cruby = 1 ] && { echo "-- cruby"; "$RUBY" "$ROOT/Util/bench/render.rb" each 30; }
  echo "-- ironruby"; "$IR" "${ir_args[@]}" "$ROOT/Util/bench/render.rb" each 30
fi
exit $status
