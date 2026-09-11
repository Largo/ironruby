#!/bin/sh
# build helper for this worktree (private DLR copy, no cross-agent races)
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
cd "$(dirname "$(readlink -f "$0")")"
exec dotnet build -p:DlrSourceDir=/root/workspace/dlr-private-filestat/src/core "$@"
