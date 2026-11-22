#!/bin/bash
# Runs every test/ruby/test_*.rb of a CRuby checkout on IronRuby, one process per file.
#
#   Util/cruby-test/sweep.sh <cruby> <out> [file...]     # files default to test/ruby/test_*.rb
#   ruby Util/cruby-test/summary.rb <out> [baseline_out]
#
# <out>/<name>.log holds a file's output and <out>/<name>.status its exit status, which is
# test-unit's failures+errors (mod 256) and so can be 124 or 137 without a timeout: a file that
# timeout(1) stopped also gets <out>/<name>.timedout.
# JOBS (default: the processor count) files run at once, each for at most TIMEOUT seconds (600).
# The tests run with -v, so the log of a file that hung or crashed ends at the test that did it,
# and --show-skip, so that the log names the skipped tests (summary.rb counts each test once).
# It measures the in-place build, as ir.sh does: do not rebuild while it runs.
set -u
root=$(cd "$(dirname "$0")/../.." && pwd)
cruby=$(cd "$1" && pwd); out=$2; shift 2
mkdir -p "$out"; out=$(cd "$out" && pwd)
if [ $# -eq 0 ]; then
  set -- "$cruby"/test/ruby/test_*.rb
else
  # each file runs in a directory of its own, so a relative path has to be made absolute here
  for f; do shift; set -- "$@" "$(cd "$(dirname "$f")" && pwd)/$(basename "$f")"; done
fi
export root cruby out TIMEOUT=${TIMEOUT:-600}
run_one() {
  name=$(basename "$1" .rb)
  # The tests run in a directory of their own: some leave files in the working directory.
  mkdir -p "$out/cwd/$name" && cd "$out/cwd/$name" || exit 1
  rm -f "$out/$name.timedout"
  start=$SECONDS
  timeout -k 10 "$TIMEOUT" "$root/ir.sh" "$root"/Util/cruby-test/runner.rb "$cruby" -v --show-skip "$1" > "$out/$name.log" 2>&1 < /dev/null
  status=$?
  echo $status > "$out/$name.status"
  # timeout(1) answers 124, or 137 once -k had to kill; only a run that lasted TIMEOUT was stopped
  if { [ $status -eq 124 ] || [ $status -eq 137 ]; } && [ $((SECONDS - start)) -ge "$TIMEOUT" ]; then
    : > "$out/$name.timedout"
  fi
}
export -f run_one
printf '%s\n' "$@" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'run_one "$1"' _ {}
