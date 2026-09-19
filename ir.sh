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
exec "$IR_ROOT/Src/Console/bin/Debug/net8.0/ir" -X:UsePrism \
  "-X:StdLib=$S/ironruby:$S/ruby/4.0:$S/ruby/1.9.1" "$@"
