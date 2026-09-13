#!/bin/bash
# Runs a ruby/spec directory file by file and prints one combined tally.
#
#   Util/spec-sweep.sh spec/core/kernel
#   Util/spec-sweep.sh spec/core/kernel /tmp/kernel.tsv
#
# Why this exists rather than just `mspec-run spec/core/kernel`: one hanging
# example takes the whole directory with it, and mspec prints its tally only at
# the end, so a single wedged spec costs you the number for all 115 files.  Per
# file, a hang costs one file and is reported as TIMEOUT-OR-ABORT.
#
# Two traps that have each cost an agent a full run:
#
#   * The inner mspec-run MUST have its stdin redirected from /dev/null.  It
#     reads stdin, and in a `while read` loop that is the same file descriptor
#     the loop is reading the file list from - so mspec eats the list and the
#     sweep stops a third of the way through, silently, having reported a
#     plausible-looking tally for the files it did reach.
#
#   * mspec says "1 file" and "1 failure", not "1 files"/"1 failures", so a
#     tally regexp without the optional plural matches nothing and every file
#     looks like a timeout.
#
# Known wedges (skipped, listed in the summary):
#
#   spec/core/kernel/require_spec.rb
#       "Kernel#require (concurrently) blocks a second thread from returning
#       while the 1st is still requiring" deadlocks - a busy-wait with no
#       timeout, so raising mspec's cap does not help and never will.
#
#       Run Util/kernel-require-wedge.rb for the measured diagnosis.  In
#       short, and because these are the natural guesses and all three are
#       wrong: thread-locals ARE visible across threads, require's
#       cross-thread lock DOES work (the second thread really blocks and
#       really gets false), and the first thread DOES reach the required
#       file's body.  The thread that spins forever is the *first* one, on
#       the one term left: Thread#backtrace answers nil for any thread but
#       the caller.  IronRuby builds backtraces by walking the CLR stack on
#       demand rather than keeping a per-thread frame list, and .NET Core
#       removed the APIs that could capture another thread's managed stack,
#       so this is structural rather than a missing method.
#
#   spec/core/kernel/abort_spec.rb, spec/core/kernel/exit_spec.rb
#       process termination; owned elsewhere.

set -u

DIR=${1:?usage: spec-sweep.sh <spec-dir> [output.tsv]}
OUT=${2:-/tmp/spec-sweep.tsv}
TIMEOUT=${SPEC_SWEEP_TIMEOUT:-120}

cd "$(dirname "$0")/.."

EXCLUDE='spec/core/kernel/require_spec\.rb|spec/core/kernel/abort_spec\.rb|spec/core/kernel/exit_spec\.rb'

: > "$OUT"
skipped=0
while read -r file; do
  if printf '%s' "$file" | grep -qE "$EXCLUDE"; then
    printf '%s\tSKIPPED (known wedge)\n' "$file" >> "$OUT"
    skipped=$((skipped + 1))
    continue
  fi

  #                                                      vvvvvvvvvvvvv see above
  tally=$(RUBY_EXE=./ir.sh timeout -s KILL "$TIMEOUT" ./ir.sh -Imspec/lib \
            mspec/bin/mspec-run "$file" < /dev/null 2>&1 |
          grep -aoE "[0-9]+ files?, [0-9]+ examples?, [0-9]+ expectations?, [0-9]+ failures?, [0-9]+ errors?")
  printf '%s\t%s\n' "$file" "${tally:-TIMEOUT-OR-ABORT}" >> "$OUT"
done < <(find "$DIR" -name '*_spec.rb' | sort)

echo DONE >> "$OUT"

awk -F'\t' -v skipped="$skipped" '
  $2 == "TIMEOUT-OR-ABORT" { timeouts++; next }
  $2 ~ /^SKIPPED/          { next }
  $1 == "DONE"             { next }
  {
    split($2, part, ", ")
    e = part[2]; f = part[4]; r = part[5]
    gsub(/[^0-9]/, "", e); gsub(/[^0-9]/, "", f); gsub(/[^0-9]/, "", r)
    files++; examples += e; failures += f; errors += r
  }
  END {
    printf "%d files, %d examples, %d failures, %d errors, %d failing blocks",
           files, examples, failures, errors, failures + errors
    if (timeouts) printf ", %d TIMED OUT", timeouts
    if (skipped)  printf ", %d skipped", skipped
    printf "\n"
  }
' "$OUT"
