#!/bin/bash
# Collect failure/error headers + first detail line for every spec file in a dir.
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
DIR="$1"
OUT="${2:-/tmp/failures.txt}"
: > "$OUT"
for f in $(find "$DIR" -name '*_spec.rb' | sort); do
  echo "##### $f" >> "$OUT"
  timeout 90 env RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run "$f" 2>&1 \
    | grep -A2 -E '(FAILED|ERROR)$' >> "$OUT"
done
wc -l "$OUT"
