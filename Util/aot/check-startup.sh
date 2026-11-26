#!/bin/sh
# Checks that the startup library (IronRuby.Startup.dll, README.md part D) changes nothing a program sees:
#
#   1. Util/aot/startup-checks.rb prints the same with the library as without it (IR_STARTUP_DLL=none):
#      coverage of files required after Coverage.start, frame labels, frozen literals, interpolation,
#      and other threads' backtraces (Thread#backtrace) through precompiled iterators.
#   2. In a copy of the tree elsewhere (as an unpacked release): the library is used there, and `load' of
#      one of its files that was edited since runs the edit.
#   0. Before both: the library matches its sources. A file edited since the library was built is compiled from
#      source whenever it is loaded - correct, but as slow as without the library (an edited ruby4.rb: `ir -e 1' 486 ->
#      564 ms) - and nothing else says so. The fix is a rebuild (a Release build, or Util/aot/build-startup.sh).
#   With --list, also whether startup-files.txt still lists every file the commands it was made from load
#   (ruby Util/aot/startup-files.rb --check, about a minute; a warning, not a failure).
#
#   Util/aot/check-startup.sh [--list]   # the R2R host of this tree (IR_CONFIG=R2R)
#
# Exit status 0 if all hold. Needs the library built (a Linux Release build makes it).
IR_ROOT="$(dirname "$(dirname "$(dirname "$(readlink -f "$0")")")")"
IR_CONFIG="${IR_CONFIG:-R2R}"
export IR_CONFIG
TFM="${IR_TFM:-net10.0}"
HOST="Src/Console/bin/$IR_CONFIG/$TFM"
if [ ! -f "$IR_ROOT/$HOST/IronRuby.Startup.dll" ]; then
  echo "check-startup.sh: no library in $IR_ROOT/$HOST (build -c Release on Linux)" >&2
  exit 2
fi
status=0
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# 0. the library matches its sources (IR_STARTUP_DLL_TRACE=1 checks every file it holds, and names the stale ones)
IR_STARTUP_DLL_TRACE=1 "$IR_ROOT/ir.sh" -e 1 2>"$TMP/trace0"
if ! grep -q "IronRuby.Startup: 0 of [0-9]* files stale" "$TMP/trace0"; then
  echo "FAIL: the library does not match its sources; those files are compiled from source at every load until it is rebuilt"
  echo "      (a Release build, or Util/aot/build-startup.sh):"
  grep "IronRuby.Startup: \(stale\|[0-9]* of\|compiled \(for\|against\)\)" "$TMP/trace0"
  status=1
else
  echo "ok: the library matches its sources ($(sed -n 's/.*0 of \([0-9]*\) files stale.*/\1/p' "$TMP/trace0") files)"
fi

# 1. with and without the library
IR_STARTUP_DLL_TRACE=1 "$IR_ROOT/ir.sh" "$IR_ROOT/Util/aot/startup-checks.rb" >"$TMP/on" 2>"$TMP/trace"
IR_STARTUP_DLL=none "$IR_ROOT/ir.sh" "$IR_ROOT/Util/aot/startup-checks.rb" >"$TMP/off" 2>&1
if ! grep -q "IronRuby.Startup: loaded" "$TMP/trace" || grep -q "not used" "$TMP/trace"; then
  echo "FAIL: the library was not used, or not for all of its files"
  grep "IronRuby.Startup: .*not used" "$TMP/trace"
  status=1
elif ! diff "$TMP/off" "$TMP/on" >"$TMP/diff"; then
  echo "FAIL: startup-checks.rb differs with the library (< without, > with):"
  cat "$TMP/diff"
  status=1
else
  echo "ok: startup-checks.rb ($(wc -l <"$TMP/on") lines) is the same with and without the library"
fi

# 2. a copy of the tree elsewhere, with a library file edited
COPY="$TMP/tree"
mkdir -p "$COPY/$HOST"
cp "$IR_ROOT/ir.sh" "$IR_ROOT/ironruby" "$COPY/"
cp -r "$IR_ROOT/Src/StdLib" "$COPY/Src/"
cp -r "$IR_ROOT/$HOST/." "$COPY/$HOST/"
cat >"$TMP/edit.rb" <<'EOF'
f = $LOAD_PATH.map { |d| File.join(d, "prettyprint.rb") }.find { |p| File.exist?(p) }
load f
src = File.read(f)
File.write(f, src.sub("class PrettyPrint\n", "class PrettyPrint\n  def startup_checks_edit = :edited\n"))
load f
p PrettyPrint.method_defined?(:startup_checks_edit)
EOF
out="$(IR_STARTUP_DLL_TRACE=1 "$COPY/ir.sh" "$TMP/edit.rb" 2>"$TMP/trace2")"
if ! grep -q "IronRuby.Startup: loaded" "$TMP/trace2" || grep -q "IronRuby.Startup: compiled \(for\|against\)" "$TMP/trace2"; then
  echo "FAIL: the library was not used in a copy of the tree"
  status=1
elif [ "$out" != "true" ]; then
  echo "FAIL: load of an edited library file ran the compiled code ($out)"
  status=1
else
  echo "ok: in a copy of the tree the library is used, and load of an edited file runs the edit"
fi
if [ "$1" = "--list" ]; then
  ruby "$IR_ROOT/Util/aot/startup-files.rb" --check || echo "WARNING: startup-files.txt is not what the commands load"
fi
exit $status
