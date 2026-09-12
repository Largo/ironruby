#!/bin/sh
# Build and run the IronRuby.Tests suite, printing the failure count.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
cd "$IR_ROOT" || exit 1
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -p:DlrSourceDir=/root/workspace/dlr-agents/aborts > /tmp/rk/tests-build.log 2>&1 || {
  tail -20 /tmp/rk/tests-build.log; exit 1;
}
cd "$IR_ROOT/Src/IronRuby.Tests/bin/Debug/net8.0" || exit 1
./IronRuby.Tests > /tmp/rk/tests-run.log 2>&1
echo "exit=$?"
grep -c '^> FAILED' /tmp/rk/tests-run.log
