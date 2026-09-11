#!/bin/bash
# private build helper for this worktree
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
exec dotnet build Src/Console/Ruby.Console.csproj -p:DlrSourceDir=/root/workspace/dlr-agents/hash "$@"
