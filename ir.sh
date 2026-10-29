#!/bin/sh
# The short name for ./ironruby, which it runs with the same arguments.
exec "$(dirname "$(readlink -f "$0")")/ironruby" "$@"
