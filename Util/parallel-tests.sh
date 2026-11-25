#!/bin/bash
# Runs the IronRuby.Tests suite of one build as a queue of small processes, JOBS at a time,
# instead of one process that runs the 1,300 tests one after another: about nine minutes on a
# Release build, three on eight cores. It is not eight times faster because the one process
# already kept two cores busy (the CLR compiles in the background; the suite costs about twenty
# CPU-minutes either way), and because DeepNesting1 alone takes 75 s. The tests are
# independent: each gets a fresh runtime, and those that write files name them by GUID or pid.
#
# Usage:  Util/parallel-tests.sh [OUT_DIR]          (default: the test build's directory)
#   IR_CONFIG=Debug IR_TFM=net10.0    which build (Src/IronRuby.Tests/bin/$IR_CONFIG/$IR_TFM)
#   TEST_DIR=path                     or name the build's directory directly
#   JOBS=$(nproc)  CHUNK=20           processes at a time, tests per process
#   CHUNK_TIMEOUT=1200                seconds one process may take
#
# OUT_DIR gets
#   results.txt   every process's output, one after another, then the overall verdict:
#                 PASSED, or the failures and a Repro line with their names
#   failed.txt    the sorted "> FAILED: Name" lines of the tests that failed, run alone again
#                 as well, as the gate compares them
#   flaky.txt     the names of tests that failed beside the others and passed alone
#   chunks/       each process's list of tests and its log
# Exit status: 0 all passed, 1 a test failed, 2 a process ended without the driver's last line
# (crashed, killed, timed out) - a chunk, or a failed test's run alone - or `/list' failed, or a
# listed test did not run: the run is incomplete, and a failed.txt without its names would
# compare as clean.
set -u
cd "$(dirname "$0")/.."
ROOT=$PWD

IR_CONFIG=${IR_CONFIG:-Debug}
IR_TFM=${IR_TFM:-net10.0}
TEST_DIR=$(cd "${TEST_DIR:-Src/IronRuby.Tests/bin/$IR_CONFIG/$IR_TFM}" && pwd) || exit 2
OUT=${1:-$TEST_DIR}
JOBS=${JOBS:-$(nproc)}
CHUNK=${CHUNK:-20}
CHUNK_TIMEOUT=${CHUNK_TIMEOUT:-1200}
EXE=$TEST_DIR/IronRuby.Tests
[ -x "$EXE.exe" ] && EXE=$EXE.exe

# The tests that take several seconds each go first, one per process, so that the queue does
# not end with one of them running alone. Only the order depends on this list: a name that no
# longer exists is skipped, and a new slow test runs in a chunk like any other.
SLOW="DeepNesting1 Thread_IdReuseStartsFresh1 Puma_Tls1 BlockLoopNested1 NetHttp_Tls1
ModuleClassNew4 ErrorHighlight_Spots1 NetHttp_GemNetTls1 ErrorHighlight_Report1
NetHttp_TlsVerifyCallback1 Backtrace_CompiledLoopLineFrameReleased1 Method_ParkedScopeLeak1"

mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)
rm -rf "$OUT/chunks"
mkdir -p "$OUT/chunks"

# a test marked [Run] makes the driver ignore the names it is given: every chunk would run it alone
if grep -rqE '^\s*\[Run\]' "$ROOT/Src/IronRuby.Tests/Runtime" 2>/dev/null; then
  echo "a test is marked [Run]: the driver would run only it in every chunk" >&2
  exit 2
fi

# /list ends with exit 255 (the driver returns -1 after it); anything else is a driver that died
# part way through the list, and a short list would pass the coverage check below as complete
(cd "$TEST_DIR" && "$EXE" /list) > "$OUT/chunks/all.raw" 2> "$OUT/chunks/list.err" < /dev/null
code=$?
if [ $code -ne 255 ] && [ $code -ne 0 ]; then
  echo "$EXE /list failed (exit $code):" >&2; cat "$OUT/chunks/list.err" >&2; exit 2
fi
tr -d '\r' < "$OUT/chunks/all.raw" | grep -v '^$' > "$OUT/chunks/all.txt"
total=$(wc -l < "$OUT/chunks/all.txt")
[ "$total" -gt 0 ] || { echo "no tests listed by $EXE /list" >&2; exit 2; }

n=0
for t in $SLOW; do
  if grep -qx "$t" "$OUT/chunks/all.txt"; then
    printf '%s\n' "$t" > "$OUT/chunks/$(printf %03d $n).list"; n=$((n + 1))
  fi
done
# The rest are dealt out round-robin, not cut into consecutive runs: neighbours in the list are
# often one family (the eight Compat_BlockControlFlow_* take 6-7 s each), and one chunk holding
# them all would be the last to finish.
printf '%s\n' $SLOW | grep -vxF -f - "$OUT/chunks/all.txt" > "$OUT/chunks/rest.txt"
rest=$(wc -l < "$OUT/chunks/rest.txt")
k=$(( (rest + CHUNK - 1) / CHUNK ))
awk -v k="$k" -v n="$n" -v dir="$OUT/chunks" \
  '{ printf "%s\n", $0 > sprintf("%s/%03d.list", dir, n + (NR - 1) % k) }' "$OUT/chunks/rest.txt"
rm "$OUT/chunks/rest.txt"

run_chunk() {
  local list=$1 log=${1%.list}.log t=$SECONDS
  (cd "$TEST_DIR" && timeout -k 30 "$CHUNK_TIMEOUT" "$EXE" /verbose $(cat "$list")) > "$log" 2>&1 < /dev/null
  echo "$? $((SECONDS - t))" > "${list%.list}.exit"
}
export -f run_chunk
export TEST_DIR EXE CHUNK_TIMEOUT

start=$(date +%s)
printf '%s\0' "$OUT"/chunks/*.list | xargs -0 -P "$JOBS" -n 1 bash -c 'run_chunk "$1"' _
wall=$(( $(date +%s) - start ))

incomplete=()
for list in "$OUT"/chunks/*.list; do
  log=${list%.list}.log
  last=$(tr -d '\r' < "$log" | grep -v '^\s*$' | tail -1)
  # the driver's last line is PASSED or the Repro line of its failures; anything else is a
  # process that did not get there
  case "$last" in
    *PASSED*|Repro:*) ;;
    *) incomplete+=("$(basename "$list" .list) (exit $(cut -d' ' -f1 "${list%.list}.exit"))") ;;
  esac
done

# every listed test ran: a name the driver did not know would otherwise just not run
tr -d '\r' < <(cat "$OUT"/chunks/*.log) | sed -n 's/^Executing //p' | sort -u > "$OUT/chunks/ran.txt"
missing=$(sort -u "$OUT/chunks/all.txt" | comm -23 - "$OUT/chunks/ran.txt")

# A test that failed beside the others runs again alone: one that then passes is timing-dependent
# (BlockLoopNested1 fails about once in sixteen runs under this load, on master too). It goes to
# flaky.txt, not failed.txt, and is printed: a race the change brought would look the same, so
# the gate compares flaky.txt with the baseline's as well.
tr -d '\r' < <(cat "$OUT"/chunks/*.log) | sed -n 's/^> FAILED: //p' | sort -u > "$OUT/chunks/failed-once.txt"
: > "$OUT/failed.txt"; : > "$OUT/flaky.txt"
# A run alone that does not end as the driver ends (crashed, timed out) proves nothing either
# way: the test stays in failed.txt, and the run is incomplete.
while read -r t; do
  log=$OUT/chunks/alone-$t.log
  (cd "$TEST_DIR" && timeout -k 30 "$CHUNK_TIMEOUT" "$EXE" /verbose "$t") > "$log" 2>&1 < /dev/null
  code=$?
  last=$(tr -d '\r' < "$log" | grep -v '^\s*$' | tail -1)
  if [ $code -eq 0 ] && [[ $last == *PASSED* ]]; then
    echo "$t" >> "$OUT/flaky.txt"
  else
    echo "> FAILED: $t" >> "$OUT/failed.txt"
    [[ $last == Repro:* ]] || incomplete+=("alone-$t (exit $code)")
  fi
done < "$OUT/chunks/failed-once.txt"

{
  for list in "$OUT"/chunks/*.list; do cat "${list%.list}.log"; done
  echo
  if [ -s "$OUT/failed.txt" ]; then
    cat "$OUT/failed.txt"
    echo "Repro: $EXE /verbose $(sed 's/^> FAILED: //' "$OUT/failed.txt" | tr '\n' ' ')"
  elif [ ${#incomplete[@]} -eq 0 ] && [ -z "$missing" ]; then
    echo PASSED
  fi
} > "$OUT/results.txt"

chunks=$(ls "$OUT"/chunks/*.list | wc -l)
echo "$total tests in $chunks processes, $JOBS at a time: ${wall}s; results in $OUT/results.txt"
status=0
if [ -s "$OUT/flaky.txt" ]; then
  echo "FLAKY - failed beside the others, passed alone (logs in $OUT/chunks):" $(cat "$OUT/flaky.txt")
fi
if [ -s "$OUT/failed.txt" ]; then
  echo "$(wc -l < "$OUT/failed.txt") failed, alone too:"; cat "$OUT/failed.txt"; status=1
fi
if [ ${#incomplete[@]} -gt 0 ]; then
  echo "INCOMPLETE - these processes did not end as the driver ends: ${incomplete[*]}"
  echo "(their logs are in $OUT/chunks; run their lists again, alone - an alone-Name is that one test)"
  status=2
fi
if [ -n "$missing" ]; then
  echo "INCOMPLETE - listed but never run:" $missing
  status=2
fi
[ $status -eq 0 ] && echo PASSED
exit $status
