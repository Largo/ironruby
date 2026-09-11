#!/bin/sh
# perfile.sh <outdir> <spec dir>   -- run each spec file separately with a 90s cap,
# so one hang/abort cannot lose the whole tally.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
cd "$IR_ROOT"
out="$1"; shift
mkdir -p "$out"
for f in $(find "$@" -name '*_spec.rb' | sort); do
  n=$(echo "$f" | tr '/' '_')
  RUBY_EXE="$IR_ROOT/ir.sh" timeout -k 5 90 "$IR_ROOT/ir.sh" -Imspec/lib mspec/bin/mspec-run "$f" > "$out/$n.log" 2>&1
  echo "$? $f" >> "$out/exits.txt"
done
"$IR_ROOT/tally.sh" "$out"
