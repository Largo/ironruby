#!/bin/bash
# prof.sh <tree> <bench> <name> [delay] [secs]
TREE=$1; case $TREE in *perfbase*) SHIM=shim;; *) SHIM=shim-rails;; esac; B=$2; NAME=$3; D=${4:-25}; S=${5:-20}
RB=/root/workspace/rails-scratch/ruby-bench; OUT=/root/workspace/rails-scratch/prof
( cd $RB && PATH=/root/workspace/rails-scratch/$SHIM:$PATH GEM_HOME=$TREE/.gems WARMUP_ITRS=100000 MIN_BENCH_ITRS=1 MIN_BENCH_TIME=0 IR_SERVER=0 DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 IR_CONFIG=Release exec $TREE/ir.sh -I harness benchmarks/$B/benchmark.rb ) > $OUT/$NAME.run.log 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null' EXIT
sleep $D
echo "before: $(grep -c '^itr' $OUT/$NAME.run.log) pid $PID"
/usr/bin/perf record -F 499 -g -e cpu-clock -p $PID -o $OUT/$NAME.perf -- sleep $S 2>&1 | tail -1
echo "after: $(grep -c '^itr' $OUT/$NAME.run.log)"
kill $PID; wait $PID 2>/dev/null
# the capture is kept when the conversion fails, so that it can be tried again
/usr/bin/perf script -i $OUT/$NAME.perf -F ip,sym,dso > $OUT/$NAME.stacks 2>/dev/null || { echo "perf script failed: $OUT/$NAME.perf is kept" >&2; exit 1; }
rm -f $OUT/$NAME.perf
ruby /root/workspace/wt-rails/Util/perf/profile-agg.rb $OUT/$NAME.stacks 40
