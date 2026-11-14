#!/bin/bash
# ab.sh <bench> <tag>: runs the bench twice at once on wt-rails, A = with old.rb overlay, B = as built.
B=$1; TAG=$2; RB=/root/workspace/rails-scratch/ruby-bench; OUT=/root/workspace/rails-scratch/ab
run() { ( cd $RB && PATH=/root/workspace/rails-scratch/shim-rails:$PATH GEM_HOME=/root/workspace/wt-rails/.gems WARMUP_ITRS=${W:-3} MIN_BENCH_ITRS=${I:-6} MIN_BENCH_TIME=0 IR_CONFIG=Release RUBYOPT="$1" /root/workspace/wt-rails/ir.sh -I harness benchmarks/$B/benchmark.rb ) > $OUT/$B.$TAG.$2.log 2>&1; }
run "-r$OUT/old.rb" A & run "" B & wait
for x in A B; do echo "$x: $(grep -a 'Average of last' $OUT/$B.$TAG.$x.log) | $(grep -aE '^ *#[0-9]+:' $OUT/$B.$TAG.$x.log | awk '{print $2}' | tr '\n' ' ')"; done
