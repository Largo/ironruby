#!/bin/bash
# Instructions per call where the CPU's counter cannot be opened (Util/perf/instructions.rb needs perf_event_open,
# which most containers and VMs refuse): runs TREE/ir.sh under callgrind, instrumenting only the shape files' timed
# sections, and counting only the main thread. A drop-in for ab.rb -i's commands:
#
#   ruby Util/perf/ab.rb -i -r 1 -n 20000 Util/bench/regex-shapes.rb \
#        "off=env IR_LIGHT_X=0 Util/perf/callgrind/count.sh ." "on=Util/perf/callgrind/count.sh ." -- gsub size
#
# What it measures is not what instructions.rb measures: .NET's tiered compilation is off (DOTNET_TieredCompilation=0:
# fully optimized code from the first call, no PGO), because under valgrind tier-up depends on how slowly everything
# runs; the GC is not concurrent, so that a collection is counted on the thread that caused it. Counts are steady to
# 0.01% from section to section and from process to process, but they are larger than instructions.rb's and differ
# from them shape by shape. Compare arms of this tool with each other only.
#
# Mind the shape file's driver: its `while` loop is a block run by the DLR interpreter, where one more expression node
# in a light frame's call costs about 30 instructions an iteration - which a change to every light site pays in the
# driver too (perf/lsite: +37 on every shape, the controls included). A control shape that moves says so.
#
# A process takes 2-3 minutes (start-up runs under valgrind, uninstrumented); N=20000 is plenty. Needs valgrind and
# its headers (valgrind/callgrind.h) and a C compiler.
TREE=$(cd "$1" && pwd); shift
HERE=$(cd "$(dirname "$0")" && pwd)
LIBDIR="${TMPDIR:-/tmp}/ir-callgrind"
mkdir -p "$LIBDIR"
[ "$LIBDIR/libcgctl.so" -nt "$HERE/cgctl.c" ] || cc -O2 -shared -fPIC -o "$LIBDIR/libcgctl.so" "$HERE/cgctl.c" || exit 1
D=$(mktemp -d "$LIBDIR/out.XXXXXX")
trap 'rm -rf "$D"' EXIT
export DOTNET_TieredCompilation=0 DOTNET_gcConcurrent=0 DOTNET_EnableWriteXorExecute=0
export CG_LIB="$LIBDIR/libcgctl.so" CG_DIR="$D" CG_REAL="$TREE/Util/perf/instructions.rb"
valgrind -q --log-file="$D/valgrind.log" --tool=callgrind --trace-children=yes --instr-atstart=no --separate-threads=yes \
  --callgrind-out-file="$D/cg.%p" "$TREE/ir.sh" -r "$HERE/instructions.rb" "$@"
