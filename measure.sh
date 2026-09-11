#!/bin/bash
# Per-file mspec runner with a timeout; prints a per-file line plus a total.
# usage: ./measure.sh spec/core/xyz [outfile]
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
DIR="$1"
OUT="${2:-/tmp/measure-out.txt}"
: > "$OUT"
TEX=0; TF=0; TE=0; TTO=0
for f in $(find "$DIR" -name '*_spec.rb' | sort); do
  res=$(timeout 90 env RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run "$f" 2>&1)
  rc=$?
  line=$(echo "$res" | grep -E '[0-9]+ examples?,' | tail -1)
  if [ $rc -eq 124 ]; then
    echo "TIMEOUT $f" >> "$OUT"; TTO=$((TTO+1)); continue
  fi
  if [ -z "$line" ]; then
    echo "NOSUMMARY($rc) $f" >> "$OUT"; TTO=$((TTO+1)); continue
  fi
  ex=$(echo "$line" | grep -oE '[0-9]+ examples?' | grep -oE '^[0-9]+')
  fa=$(echo "$line" | grep -oE '[0-9]+ failures?' | grep -oE '^[0-9]+')
  er=$(echo "$line" | grep -oE '[0-9]+ errors?' | grep -oE '^[0-9]+'); er=${er:-0}
  TEX=$((TEX+ex)); TF=$((TF+fa)); TE=$((TE+er))
  printf '%4s ex %3s F %3s E   %s\n' "$ex" "$fa" "$er" "$f" >> "$OUT"
done
echo "=== TOTAL $DIR: $TEX examples, $TF failures, $TE errors, $TTO timeouts/nosummary" >> "$OUT"
tail -1 "$OUT"
