#!/bin/bash
# perf-samples a ruby-bench gem benchmark at steady state (like Util/perf/profile.sh does AWFY).
#   prof-gem.sh <ruby-bench dir> <benchmark> [tree] ; PROFILE_SECS (15), PROFILE_WAIT (25)
set -e -o pipefail
ROOT="$(cd "$(dirname "$(readlink -f "$0")")/../../../.." && pwd)"
RB=$1 B=$2 TREE=${3:-$ROOT} SECS=${PROFILE_SECS:-15}
OUT=/root/workspace/templates-scratch/profiles; mkdir -p $OUT
PERF="$(ls /usr/lib/linux-tools-*/perf 2>/dev/null | tail -n 1 || true)"; PERF="${PERF:-$(command -v perf)}"
SHIM=$(mktemp -d -p /root/workspace/templates-scratch)
for n in ruby bundle bundler gem; do
  if [ $n = ruby ]; then printf '#!/bin/sh\nexec %s "$@"\n' "$TREE/ir.sh"; else printf '#!/bin/sh\nexec %s -S %s "$@"\n' "$TREE/ir.sh" $n; fi > $SHIM/$n; chmod +x $SHIM/$n
done
cd $RB
IR_SERVER=0 GEM_HOME=/root/workspace/wt-perfbase/.gems PATH=$SHIM:$PATH DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 IR_CONFIG=Release \
  WARMUP_ITRS=1 MIN_BENCH_ITRS=100000 MIN_BENCH_TIME=0 OUT_CSV_PATH=$SHIM/o.csv "$TREE/ir.sh" -I $RB/harness benchmarks/$B/benchmark.rb >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null; wait $PID 2>/dev/null; rm -rf $SHIM' EXIT
sleep ${PROFILE_WAIT:-25}
"$PERF" record -F 499 -g -e cpu-clock -p $PID -o $OUT/$B.perf -- sleep $SECS 2>&1 | tail -n 1
"$PERF" script -i $OUT/$B.perf -F ip,sym,dso > $OUT/$B.stacks 2>/dev/null
ruby "$ROOT/Util/perf/profile-agg.rb" $OUT/$B.stacks 30
