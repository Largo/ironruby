#!/bin/sh
# cruby.sh <outfile> <spec paths...>  -- run the same specs under real CRuby
IR_ROOT="$(dirname "$(readlink -f "$0")")"
cd "$IR_ROOT"
out="$1"; shift
RUBY_EXE=/usr/bin/ruby timeout -k 5 900 ruby -Imspec/lib mspec/bin/mspec-run "$@" > "$out" 2>&1
echo "exit=$?"
grep -E 'examples,' "$out"
