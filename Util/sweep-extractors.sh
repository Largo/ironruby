#!/bin/bash
# Runs every spec/core file once and scores each run with three different tally
# extractors, so the question "how much of a NO-TALLY count is the spec files and
# how much is the harness reading them" can be answered from one sweep.
#
#   Util/sweep-extractors.sh <outfile> [jobs]
#
# Columns: exit status, file, then one field per extractor holding either the
# tally it found or NO-TALLY:
#
#   A  grep -a -E '^[0-9]+ files?, [0-9]+ examples?'   - what Util/sweep-core.sh
#                                                        does now
#   B  grep    -E '^[0-9]+ files?, [0-9]+ examples?'   - the same without -a, so
#                                                        a log holding a NUL byte
#                                                        reads as "Binary file
#                                                        ... matches"
#   C  grep -a -E '^[0-9]+ files?, [0-9]+ examples'    - plural "examples" only,
#                                                        which is what the sweep
#                                                        committed at 87ce602
#                                                        must have used: that
#                                                        file has 213 rows
#                                                        reading "2 examples" and
#                                                        not one reading
#                                                        "1 example"
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
OUT="${1:-$IR_ROOT/Util/sweep-extractors.tsv}"
JOBS="${2:-12}"
TIMEOUT="${SWEEP_TIMEOUT:-240}"

cd "$IR_ROOT" || exit 1
IR="${IR:-$IR_ROOT/ir.sh}"
export RUBY_EXE="$IR"

run_one() {
  file="$1"
  log="$(mktemp)"
  timeout -s KILL "$TIMEOUT" "$IR" -Imspec/lib mspec/bin/mspec-run "$file" >"$log" 2>&1
  status=$?
  a="$(grep -a -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log")"
  b="$(grep    -m1 -E '^[0-9]+ files?, [0-9]+ examples?' "$log" 2>/dev/null)"
  c="$(grep -a -m1 -E '^[0-9]+ files?, [0-9]+ examples' "$log")"
  case "$b" in "Binary file"*) b="" ;; esac
  printf '%s\t%s\t%s\t%s\t%s\n' "$status" "$file" \
    "${a:-NO-TALLY}" "${b:-NO-TALLY}" "${c:-NO-TALLY}"
  rm -f "$log"
}
export -f run_one
export IR_ROOT TIMEOUT IR

find spec/core -name '*_spec.rb' | sort |
  xargs -P "$JOBS" -I{} bash -c 'run_one "$@"' _ {} > "$OUT"

for col in 3 4 5; do
  echo "extractor $col: $(cut -f$col "$OUT" | grep -c '^NO-TALLY') NO-TALLY of $(wc -l < "$OUT")"
done
