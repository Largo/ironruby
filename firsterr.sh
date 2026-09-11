#!/bin/sh
# firsterr.sh <dir> <name...> -- print the first reported problem of each named log
d="$1"; shift
for f in "$@"; do
  echo "=== $f ==="
  grep -a -A5 '^1)' "$d/$f.log" | head -8
done
