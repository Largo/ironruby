#!/bin/sh
# Build and run the IronRuby.Tests suite, printing the failure count.
#
#   Util/run-tests.sh                  # Debug (the default everything else uses)
#   IR_CONFIG=Release Util/run-tests.sh # the optimized build, same as IR_CONFIG=Release ./ir.sh
#   IR_TFM=net10.0 Util/run-tests.sh   # the same IL on the .NET 10 runtime
#   DLR_SOURCE_DIR=<dlr>/src/core Util/run-tests.sh  # build against another DLR checkout
#   RUN_TESTS_LOG_DIR=<dir> Util/run-tests.sh          # write the logs somewhere else
#
# Runs in several trees (worktrees, clones) can go at once:
# - The logs are per tree, and in it per configuration and runtime, so no run clobbers another's.
#   The first line printed names the run log.
# - The builds take turns. Every tree builds the one DLR checkout in place, into its own obj
#   directories, and two builds of it at once fail with bogus errors (CS0246 and the like).
#   Only the build waits for the lock; the test runs themselves overlap.
# - Each run gets a TMPDIR of its own: the tests name some temp files and directories after the
#   test (Driver.MakeTempDir), so two runs of the same test would otherwise share them.
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
# /root/workspace/ironruby -> /tmp/rk/run-tests/root-workspace-ironruby
: "${RUN_TESTS_LOG_DIR:=/tmp/rk/run-tests/$(printf '%s' "${IR_ROOT#/}" | tr / -)}"
mkdir -p "$RUN_TESTS_LOG_DIR" || exit 1
build_log="$RUN_TESTS_LOG_DIR/tests-build-$IR_CONFIG-$IR_TFM.log"
run_log="$RUN_TESTS_LOG_DIR/tests-run-$IR_CONFIG-$IR_TFM.log"
echo "log=$run_log"
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

build() {
  dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c "$IR_CONFIG" -p:DlrSourceDir="$DLR_SOURCE_DIR" > "$build_log" 2>&1
}
mkdir -p /tmp/rk
if command -v flock >/dev/null; then
  # A flock is released when the process holding it dies, so a killed run leaves no stale lock.
  exec 9> /tmp/rk/run-tests-build.lock
  flock -n 9 || { echo "run-tests: waiting for another tree's build to finish" >&2; flock 9; }
  # Not inherited by the build: MSBuild leaves node processes running after it, and one holding
  # the descriptor would hold the lock for as long as it lives.
  build 9>&-; status=$?
  exec 9>&-
else
  build; status=$?
fi
[ $status -eq 0 ] || { tail -20 "$build_log"; exit 1; }

run_tmp=$(mktemp -d) || exit 1
cd "$IR_ROOT/Src/IronRuby.Tests/bin/$IR_CONFIG/$IR_TFM" || exit 1
TMPDIR=$run_tmp ./IronRuby.Tests > "$run_log" 2>&1
echo "exit=$?"
rm -rf "${run_tmp:?}"
grep -c '^> FAILED' "$run_log"
