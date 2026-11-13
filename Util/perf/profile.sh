#!/bin/bash
# Samples an AWFY benchmark at steady state with perf and prints where the time is.
#
#   Util/perf/profile.sh Richards            # this checkout's Release build, 15 s
#   Util/perf/profile.sh DeltaBlue ../wt-x   # another tree
#   PROFILE_SECS=30 Util/perf/profile.sh Json
#
# Needs perf (`apt-get install -y linux-tools-generic`; the binary lands under
# /usr/lib/linux-tools-*/). Leaves NAME.stacks in $PERF_HOME/profiles for
# `ruby Util/perf/profile-agg.rb NAME.stacks [top] [callers-of-regex]`.
#
# Use this and not dotnet-trace to say where time goes: EventPipe's sampler stops threads
# at GC safepoints, so it blames allocation sites (it put 80% of Json in Array.new).
# DOTNET_EnableWriteXorExecute=0 is what makes JIT frames resolve (otherwise they are all
# /memfd:doublemapper). Compiled Ruby method bodies still come out as [unknown], about 40%
# of the samples: their perf-map lines are damaged, probably by the non-ASCII characters in
# the DynamicMethod names. Until that is fixed the profile attributes the runtime's
# helpers, not Ruby methods.
set -e
ROOT="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
PERF_HOME="${PERF_HOME:-$(dirname "$ROOT")/ironruby-perf}"
NAME="${1:?usage: profile.sh Benchmark [tree]}"; TREE="${2:-$ROOT}"; SECS="${PROFILE_SECS:-15}"
# /usr/bin/perf is a wrapper that refuses to run unless a build for the running kernel is installed: take the real one.
PERF="${PERF:-$(ls /usr/lib/linux-tools-*/perf 2>/dev/null | tail -n 1)}"; PERF="${PERF:-$(command -v perf)}"
[ -x "$PERF" ] || { echo "no perf: apt-get install -y linux-tools-generic" >&2; exit 1; }
declare -A INNER=([DeltaBlue]=1200 [Richards]=10 [Json]=10 [CD]=100 [Bounce]=150 [List]=150 [Mandelbrot]=500
                  [NBody]=250000 [Permute]=100 [Queens]=100 [Sieve]=300 [Storage]=100 [Towers]=60)
OUT="$PERF_HOME/profiles"; mkdir -p "$OUT"
DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 IR_CONFIG=Release "$TREE/ir.sh" \
  -r"$ROOT/Util/perf/sync.rb" "$PERF_HOME/awfy/benchmarks/Ruby/harness.rb" "$NAME" 100000 "${INNER[$NAME]}" > /dev/null 2>&1 &
sleep 10                                    # past start-up and the JIT tiers
PID=$(pgrep -n -f "harness.rb $NAME 100000")
"$PERF" record -F 999 -g -e cpu-clock -p "$PID" -o "$OUT/$NAME.perf" -- sleep "$SECS" 2>&1 | tail -n 1
kill "$PID" 2>/dev/null || true
"$PERF" script -i "$OUT/$NAME.perf" -F ip,sym > "$OUT/$NAME.stacks" 2>/dev/null
ruby "$ROOT/Util/perf/profile-agg.rb" "$OUT/$NAME.stacks" 25
