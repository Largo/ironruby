#!/bin/bash
# Runs one ruby-bench gem benchmark with the harness's per-iteration times measured as process
# CPU (all threads: JIT and GC included) instead of wall clock, which is meaningless on a loaded box.
#   cpu-itr.sh <ruby-bench dir> <cruby|ir-tree> <benchmark> [itrs=6]
# Prints the iteration lines (#1 is the cold one) and "mean of last half".
RB=$1 ENG=$2 B=$3 N=${4:-6}
SHIM=$(mktemp -d -p /root/workspace/templates-scratch); trap 'rm -rf $SHIM' EXIT
if [ "$ENG" = cruby ]; then CMD=(ruby); else
  for n in ruby bundle bundler gem; do
    if [ $n = ruby ]; then printf '#!/bin/sh\nexec %s "$@"\n' "$ENG/ir.sh"; else printf '#!/bin/sh\nexec %s -S %s "$@"\n' "$ENG/ir.sh" $n; fi > $SHIM/$n; chmod +x $SHIM/$n
  done; CMD=("$ENG/ir.sh"); export GEM_HOME=/root/workspace/wt-perfbase/.gems PATH=$SHIM:$PATH IR_CONFIG=Release; fi
cd $RB && WARMUP_ITRS=0 MIN_BENCH_ITRS=$N MIN_BENCH_TIME=0 OUT_CSV_PATH=$SHIM/o.csv "${CMD[@]}" -I $RB/harness -r harness -e '
def realtime; r0 = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); yield; Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - r0; end
$0 = ARGV.shift; load $0' benchmarks/$B/benchmark.rb 2>&1 | grep -E '^ *#[0-9]+:' | awk -v n="$B" -v e="$(basename $ENG)" '{gsub("ms","",$2); t[NR]=$2; printf "%s ", $2} END {s=0; c=0; for(i=int(NR/2)+1;i<=NR;i++){s+=t[i];c++}; printf "| %s %s first=%d steady=%d\n", n, e, t[1], (c? s/c : -1)}'
