#!/bin/bash
# ab.sh <bench> <tag>: runs the bench twice at once on wt-rails, A = with old.rb overlay, B = as built.
B=$1; TAG=$2; RB=/root/workspace/rails-scratch/ruby-bench; OUT=/root/workspace/rails-scratch/ab
# A loads the overlay from the scratch directory: without it A would fail inside its log and print an empty result.
[ -f "$OUT/old.rb" ] || { echo "missing $OUT/old.rb (the overlay: this directory's old.rb)" >&2; exit 1; }
run() { ( cd $RB && PATH=/root/workspace/rails-scratch/shim-rails:$PATH GEM_HOME=/root/workspace/wt-rails/.gems WARMUP_ITRS=${W:-3} MIN_BENCH_ITRS=${I:-6} MIN_BENCH_TIME=0 IR_CONFIG=Release RUBYOPT="$1" /root/workspace/wt-rails/ir.sh -I harness benchmarks/$B/benchmark.rb ) > $OUT/$B.$TAG.$2.log 2>&1; }
run "-r$OUT/old.rb" A & pa=$!; run "" B & pb=$!
wait $pa; sa=$?; wait $pb; sb=$?
[ $sa = 0 ] && [ $sb = 0 ] || { echo "A exited $sa, B exited $sb: see $OUT/$B.$TAG.A.log and .B.log" >&2; exit 1; }
for x in A B; do echo "$x: $(grep -a 'Average of last' $OUT/$B.$TAG.$x.log) | $(grep -aE '^ *#[0-9]+:' $OUT/$B.$TAG.$x.log | awk '{print $2}' | tr '\n' ' ')"; done
