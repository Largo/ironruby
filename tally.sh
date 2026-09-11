#!/bin/sh
# tally.sh <outdir>  -- sum the mspec summary lines of a perfile run
d="$1"
grep -h -oE '[0-9]+ examples?, [0-9]+ expectations?, [0-9]+ failures?, [0-9]+ errors?' "$d"/*.log |
awk '{e+=$1; f+=$5; r+=$7} END {printf "examples=%d failures=%d errors=%d\n", e, f, r}'
echo "files with no summary line (crash/hang):"
for l in "$d"/*.log; do
  case "$l" in *exits.txt) continue;; esac
  grep -qE '[0-9]+ examples?, ' "$l" || echo "  $l"
done
