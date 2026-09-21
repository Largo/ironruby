#!/bin/sh
# Runs the built ir with the prism front end and the bundled standard library.
# Used as RUBY_EXE when running ruby/spec:
#
#   RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
#
IR_ROOT="$(dirname "$(readlink -f "$0")")"

# apphost needs DOTNET_ROOT when the SDK isn't on the default search path
if [ -z "$DOTNET_ROOT" ] && [ -x /usr/local/dotnet/dotnet ]; then
  DOTNET_ROOT=/usr/local/dotnet
  export DOTNET_ROOT
fi

# Ruby 4.0's standard library comes first; the 1.9 tree is still on the path
# behind it for the libraries 4.0 gemified or implements as C extensions.
# -X:StdLib puts them on $LOAD_PATH after -I, RUBYOPT's -I and RUBYLIB, where
# MRI keeps its own library directories.
S="$IR_ROOT/Src/StdLib"
# IR_CONFIG=Release runs the optimized build instead (build it with
# `dotnet build Src/Console/Ruby.Console.csproj -c Release ...`).  It is about 1.8x
# faster across Util/bench - the Debug build is compiled with optimizations off, which
# marks the assemblies non-optimizable, so the JIT leaves the whole runtime unoptimized
# too - and it passes the same specs and the same IronRuby.Tests.  Util/run-tests.sh and
# regen-initializers.sh read IR_CONFIG as well, so one export covers a whole session.
# Debug stays the default because every worktree and every agent script builds it.
: "${IR_CONFIG:=Debug}"

IR_BIN="$IR_ROOT/Src/Console/bin/$IR_CONFIG/net8.0/ir"
# Say which configuration is missing rather than let the shell's "not found" - which a
# filtered spec log swallows - be the only clue. Getting IR_CONFIG=Release past a tree
# that has only ever been built Debug is the whole reason this check is here.
if [ ! -x "$IR_BIN" ]; then
  echo "ir.sh: no $IR_CONFIG build at $IR_BIN" >&2
  echo "ir.sh: build it with: dotnet build Src/Console/Ruby.Console.csproj -c $IR_CONFIG -p:DlrSourceDir=..." >&2
  exit 127
fi

# -S searches RUBYPATH before PATH; Src/StdLib/bin holds the executables the
# bundled library ships (irb), so `./ir.sh -S irb` runs IronRuby's own rather
# than whatever binstub happens to be on PATH.  An existing RUBYPATH still wins
# for anything not shipped here.
RUBYPATH="$S/bin${RUBYPATH:+:$RUBYPATH}"
export RUBYPATH

exec "$IR_BIN" -X:UsePrism \
  "-X:StdLib=$S/ironruby:$S/ruby/4.0:$S/ruby/1.9.1" "$@"
