#!/bin/bash
# run.sh IR_SH [names...]: CRuby vs the given ir.sh, ns/op and ratio
IR=${1:-/root/workspace/wt-perfbase/ir.sh}; shift
D=$(dirname "$0")
L=${@:-fiber_resume:200000 fiber_new:20000 enum_next_arr:500000 enum_next_gen:100000 enum_next_map:100000 lazy:500000 yielder_to_a:500000 thread_new:5000 mutex:2000000 monitor:1000000 queue:1000000 sized_queue:1000000 queue_pp:200000 tlocal:2000000 fiber_local:1000000 condvar:1000000}
for b in $L; do nm=${b%%:*}
  # a name alone runs micro.rb's own count; a count that is no number would run none and print Infinity
  case $b in *:*) n=${b#*:};; *) n=;; esac
  case $n in *[!0-9]*) echo "$b: the count is not a number" >&2; exit 1;; esac
  # a run that fails stops here, with what it wrote to stderr, instead of having its last line parsed as a result
  c=$(ruby $D/micro.rb $nm $n) || { echo "$nm: ruby failed" >&2; exit 1; }
  i=$(IR_CONFIG=Release $IR $D/micro.rb $nm $n) || { echo "$nm: $IR failed" >&2; exit 1; }
  i=$(echo "$i" | tail -1)
  cn=$(echo $c | awk '{print $3}' | tr -d ns/op); in=$(echo $i | awk '{print $3}' | tr -d ns/op)
  echo "$nm n=${n:-default} cruby=${cn} ir=${in} ratio=$(ruby -e "print ($in/$cn).round(1)" 2>/dev/null) ans=$(echo $c|awk '{print $2}')/$(echo $i|awk '{print $2}')"
done
