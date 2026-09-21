#!/bin/sh
# IronRuby's irb.  The same thing as `./ir.sh -S irb`, which also works; this is
# here so that starting a REPL does not need to know about -S.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ir.sh" -S irb "$@"
