#!/bin/bash
# build + run IronRuby.Tests from a clean bin/obj, print the failure count
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
rm -rf Src/IronRuby.Tests/bin Src/IronRuby.Tests/obj
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -p:DlrSourceDir=/root/workspace/dlr-agents/hash > /tmp/irtests-build.log 2>&1
if grep -qE 'error CS' /tmp/irtests-build.log; then
  grep -E 'error CS' /tmp/irtests-build.log | head
  echo "BUILD FAILED"; exit 1
fi
cd Src/IronRuby.Tests/bin/Debug/net8.0
./IronRuby.Tests > /tmp/irtests-run.log 2>&1
echo "FAILED count: $(grep -c '^> FAILED' /tmp/irtests-run.log)"
grep '^> FAILED' /tmp/irtests-run.log | sort | uniq -c | sort -rn | head -40
