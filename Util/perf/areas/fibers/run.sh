#!/bin/bash
# run.sh IR_SH [names...]: CRuby vs the given ir.sh, ns/op and ratio
IR=${1:-/root/workspace/wt-perfbase/ir.sh}; shift
D=$(dirname "$0")
L=${@:-fiber_resume:200000 fiber_new:20000 enum_next_arr:500000 enum_next_gen:100000 enum_next_map:100000 lazy:500000 yielder_to_a:500000 thread_new:5000 mutex:2000000 monitor:1000000 queue:1000000 sized_queue:1000000 queue_pp:200000 tlocal:2000000 fiber_local:1000000 condvar:1000000}
for b in $L; do nm=${b%:*}; n=${b#*:}
  c=$(ruby $D/micro.rb $nm $n); i=$(IR_CONFIG=Release $IR $D/micro.rb $nm $n 2>&1 | tail -1)
  cn=$(echo $c | awk '{print $3}' | tr -d ns/op); in=$(echo $i | awk '{print $3}' | tr -d ns/op)
  echo "$nm n=$n cruby=${cn} ir=${in} ratio=$(ruby -e "print ($in/$cn).round(1)" 2>/dev/null) ans=$(echo $c|awk '{print $2}')/$(echo $i|awk '{print $2}')"
done
