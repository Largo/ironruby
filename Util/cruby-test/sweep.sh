#!/bin/bash
# Runs every test/ruby/test_*.rb of a CRuby checkout on IronRuby, one process per file.
#
#   Util/cruby-test/sweep.sh <cruby> <out> [file...]     # files default to test/ruby/test_*.rb
#   ruby Util/cruby-test/summary.rb <out> [baseline_out]
#
# <out>/<name>.log holds a file's output and <out>/<name>.status its exit status (124: timed out).
# JOBS (default: the processor count) files run at once, each for at most TIMEOUT seconds (600).
# The tests run with -v, so the log of a file that hung or crashed ends at the test that did it.
# It measures the in-place build, as ir.sh does: do not rebuild while it runs.
set -u
root=$(cd "$(dirname "$0")/../.." && pwd)
cruby=$(cd "$1" && pwd); out=$2; shift 2
mkdir -p "$out"; out=$(cd "$out" && pwd)
if [ $# -eq 0 ]; then set -- "$cruby"/test/ruby/test_*.rb; fi
export root cruby out TIMEOUT=${TIMEOUT:-600}
run_one() {
  name=$(basename "$1" .rb)
  cd "$root" || exit 1
  timeout -k 10 "$TIMEOUT" ./ir.sh Util/cruby-test/runner.rb "$cruby" -v "$1" > "$out/$name.log" 2>&1 < /dev/null
  echo $? > "$out/$name.status"
}
export -f run_one
printf '%s\n' "$@" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'run_one "$1"' _ {}
