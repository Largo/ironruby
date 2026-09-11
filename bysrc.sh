#!/bin/sh
# bysrc.sh <outdir> -- per-file summary, sorted by (failures+errors) descending
d="$1"
for l in "$d"/*.log; do
  case "$l" in *exits.txt) continue;; esac
  s=$(grep -a -oE '[0-9]+ examples?, [0-9]+ expectations?, [0-9]+ failures?, [0-9]+ errors?' "$l" | tail -1)
  [ -z "$s" ] && s="NO SUMMARY (hang/crash)"
  echo "$(basename "$l" .log) | $s"
done
