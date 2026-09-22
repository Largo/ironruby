#!/bin/sh
# Build and run the IronRuby.Tests suite, printing the failure count.
#
#   Util/run-tests.sh                  # Debug (the default everything else uses)
#   IR_CONFIG=Release Util/run-tests.sh # the optimized build, same as IR_CONFIG=Release ./ir.sh
#   IR_TFM=net10.0 Util/run-tests.sh   # the same IL on the .NET 10 runtime
#
# The logs are per configuration so a Release run does not clobber a Debug one.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
: "${IR_CONFIG:=Debug}"
# IR_TFM=net10.0 runs the suite on the .NET 10 runtime; see ir.sh.  The logs carry both
# so a net10.0 run does not clobber the net8.0 one.
: "${IR_TFM:=net8.0}"
mkdir -p /tmp/rk
cd "$IR_ROOT" || exit 1
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c "$IR_CONFIG" -p:DlrSourceDir=/root/workspace/dlr/src/core > "/tmp/rk/tests-build-$IR_CONFIG-$IR_TFM.log" 2>&1 || {
  tail -20 "/tmp/rk/tests-build-$IR_CONFIG-$IR_TFM.log"; exit 1;
}
cd "$IR_ROOT/Src/IronRuby.Tests/bin/$IR_CONFIG/$IR_TFM" || exit 1
./IronRuby.Tests > "/tmp/rk/tests-run-$IR_CONFIG-$IR_TFM.log" 2>&1
echo "exit=$?"
grep -c '^> FAILED' "/tmp/rk/tests-run-$IR_CONFIG-$IR_TFM.log"
