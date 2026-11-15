#!/bin/bash
# vs.sh <bench>: IronRuby (wt-rails) and CRuby at the same time, so both see the same machine load.
B=$1; RB=/root/workspace/rails-scratch/ruby-bench; OUT=/root/workspace/rails-scratch/ab
( cd $RB && PATH=/root/workspace/rails-scratch/shim-rails:$PATH GEM_HOME=/root/workspace/wt-rails/.gems WARMUP_ITRS=${W:-3} MIN_BENCH_ITRS=${I:-6} MIN_BENCH_TIME=0 IR_CONFIG=Release /root/workspace/wt-rails/ir.sh -I harness benchmarks/$B/benchmark.rb ) > $OUT/$B.vs.ir.log 2>&1 &
pi=$!
( cd $RB && WARMUP_ITRS=${W:-3} MIN_BENCH_ITRS=${CI:-${I:-6}} MIN_BENCH_TIME=0 ruby -I harness benchmarks/$B/benchmark.rb ) > $OUT/$B.vs.cruby.log 2>&1 &
pc=$!
wait $pi; si=$?; wait $pc; sc=$?
[ $si = 0 ] && [ $sc = 0 ] || { echo "IronRuby exited $si, CRuby exited $sc: see $OUT/$B.vs.ir.log and .cruby.log" >&2; exit 1; }
ir=$(grep -a 'Average of last' $OUT/$B.vs.ir.log | grep -oE '[0-9]+ms'); cr=$(grep -a 'Average of last' $OUT/$B.vs.cruby.log | grep -oE '[0-9]+ms')
echo "$B ir=$ir cruby=$cr ratio=$(awk "BEGIN{printf \"%.1f\", ${ir%ms}/${cr%ms}}" 2>/dev/null)"
