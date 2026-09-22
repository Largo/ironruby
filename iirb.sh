#!/bin/sh
# IronRuby's irb under the i-prefixed name (IronRuby 1.x shipped iirb.bat; JRuby
# ships jirb).  irb.sh is the same thing under the plainer name.
IR_ROOT="$(dirname "$(readlink -f "$0")")"
exec "$IR_ROOT/ir.sh" -S irb "$@"
