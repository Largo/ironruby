#!/bin/bash
# Fetches what the perf skills (.claude/skills/perf-*) measure against, once:
# TruffleRuby and the Are-We-Fast-Yet benchmarks, into PERF_HOME (default: ../ironruby-perf,
# a sibling of this checkout like ../dlr and ../prism), and builds IronRuby in Release if
# that build is missing.
#
#   Util/perf/setup.sh                 # TruffleRuby 40.0.0
#   TRUFFLE_VERSION=41.0.0 Util/perf/setup.sh
set -e
ROOT="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
PERF_HOME="${PERF_HOME:-$(dirname "$ROOT")/ironruby-perf}"
V="${TRUFFLE_VERSION:-40.0.0}"
case "$(uname -m)" in aarch64|arm64) ARCH=aarch64 ;; *) ARCH=amd64 ;; esac
mkdir -p "$PERF_HOME"

if [ ! -x "$PERF_HOME/truffleruby/bin/truffleruby" ]; then
  # Unpacked in a directory of its own: a downloaded archive is not trusted with ours.
  mkdir -p "$PERF_HOME/truffleruby-$V"
  curl -fsSL -o "$PERF_HOME/truffleruby.tgz" \
    "https://github.com/truffleruby/truffleruby/releases/download/graal-$V/truffleruby-$V-linux-$ARCH.tar.gz"
  tar -xzf "$PERF_HOME/truffleruby.tgz" -C "$PERF_HOME/truffleruby-$V" --strip-components=1
  rm "$PERF_HOME/truffleruby.tgz"
  ln -sfn "truffleruby-$V" "$PERF_HOME/truffleruby"
fi
[ -d "$PERF_HOME/awfy" ] || git clone -q --depth 1 https://github.com/smarr/are-we-fast-yet "$PERF_HOME/awfy"

if [ ! -x "$ROOT/Src/Console/bin/Release/net10.0/ir" ]; then
  (cd "$ROOT" && dotnet build Src/Console/Ruby.Console.csproj -c Release -p:IronRubyTargetFrameworks=net10.0 -v q | tail -3)
fi
LANG=C.UTF-8 "$PERF_HOME/truffleruby/bin/truffleruby" -v
IR_CONFIG=Release "$ROOT/ir.sh" -e 'puts RUBY_DESCRIPTION'
echo "PERF_HOME=$PERF_HOME"
