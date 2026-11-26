/* Instruments only what lies between cg_start() and cg_stop() (Util/perf/callgrind/count.sh). */
#include <valgrind/callgrind.h>
void cg_start(void) { CALLGRIND_START_INSTRUMENTATION; CALLGRIND_ZERO_STATS; }
void cg_stop(void) { CALLGRIND_DUMP_STATS; CALLGRIND_STOP_INSTRUMENTATION; }
