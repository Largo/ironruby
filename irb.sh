#!/bin/sh
# IronRuby's irb.  The same thing as `./ironruby -S irb`, which also works; this is
# here so that starting a REPL does not need to know about -S.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ironruby" -S irb "$@"
