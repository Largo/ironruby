#!/bin/sh
# runspec.sh <outfile> <spec paths...>
IR_ROOT="$(dirname "$(readlink -f "$0")")"
cd "$IR_ROOT"
out="$1"; shift
RUBY_EXE="$IR_ROOT/ir.sh" timeout -k 5 1800 "$IR_ROOT/ir.sh" -Imspec/lib mspec/bin/mspec-run "$@" > "$out" 2>&1
echo "exit=$?"
tail -4 "$out"
