#!/bin/sh
# Build and run the IronRuby.Tests suite, printing the failure count.
#
#   Util/run-tests.sh                  # Debug (the default everything else uses)
#   IR_CONFIG=Release Util/run-tests.sh # the optimized build, same as IR_CONFIG=Release ./ir.sh
#   IR_TFM=net10.0 Util/run-tests.sh   # the same IL on the .NET 10 runtime
#   DLR_SOURCE_DIR=<dlr>/src/core Util/run-tests.sh  # build against another DLR checkout
#
# The logs are per configuration so a Release run does not clobber a Debug one.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
: "${IR_CONFIG:=Debug}"
# IR_TFM=net10.0 runs the suite on the .NET 10 runtime; see ir.sh.  The logs carry both
# so a net10.0 run does not clobber the net8.0 one.
: "${IR_TFM:=net8.0}"
: "${DLR_SOURCE_DIR:=/root/workspace/dlr/src/core}"
# Absolute, as the caller meant it: the check below runs from $IR_ROOT and MSBuild resolves a
# relative DlrSourceDir from Src/IronRuby.Tests, so a relative path would name two other places.
dlr_dir=$(cd "$DLR_SOURCE_DIR" 2>/dev/null && pwd) || {
  echo "run-tests: no DLR at $DLR_SOURCE_DIR" >&2; exit 1;
}
DLR_SOURCE_DIR=$dlr_dir
mkdir -p /tmp/rk
cd "$IR_ROOT" || exit 1

# CI builds against the DLR with Build/dlr-patches applied. Without them this is a different
# DLR, and the results are not CI's: DeepNesting1, for one, takes the whole process down with
# a stack overflow. A later patch can change the lines next to an earlier one, so a patch is
# not checked on its own: the series is taken back off, newest first, from a copy of the files
# it touches.
dlr_root=$(git -C "$DLR_SOURCE_DIR" rev-parse --show-toplevel 2>/dev/null) && {
  check=$(mktemp -d)
  for p in Build/dlr-patches/*.patch; do
    git apply --numstat "$p" | cut -f3
  done | sort -u | while read -r f; do
    [ -f "$dlr_root/$f" ] && mkdir -p "$check/$(dirname "$f")" && cp "$dlr_root/$f" "$check/$f"
  done
  for p in $(ls -r Build/dlr-patches/*.patch); do
    (cd "$check" && git apply --reverse "$IR_ROOT/$p" 2>/dev/null) || {
      echo "run-tests: WARNING: $dlr_root does not have all of Build/dlr-patches ($(basename "$p") is not applied);" >&2
      echo "run-tests: apply them, or point DLR_SOURCE_DIR at a DLR that has them (see Build/dlr-patches/README.md)" >&2
      break
    }
  done
  rm -rf "$check"
}

dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c "$IR_CONFIG" -p:DlrSourceDir="$DLR_SOURCE_DIR" > "/tmp/rk/tests-build-$IR_CONFIG-$IR_TFM.log" 2>&1 || {
  tail -20 "/tmp/rk/tests-build-$IR_CONFIG-$IR_TFM.log"; exit 1;
}
cd "$IR_ROOT/Src/IronRuby.Tests/bin/$IR_CONFIG/$IR_TFM" || exit 1
./IronRuby.Tests > "/tmp/rk/tests-run-$IR_CONFIG-$IR_TFM.log" 2>&1
echo "exit=$?"
grep -c '^> FAILED' "/tmp/rk/tests-run-$IR_CONFIG-$IR_TFM.log"
