#!/bin/sh
# Regenerate Src/Libraries/Initializers.Generated.cs
# Usage: ./geninit.sh          (bootstrap-build the generator, then regenerate in place)
set -e
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
cd "$(dirname "$(readlink -f "$0")")"
./b.sh Src/ClassInitGenerator/ClassInitGenerator.csproj -p:BootstrapInitializers=true
D=Src/ClassInitGenerator/bin/Debug/net8.0
dotnet $D/ClassInitGenerator.dll $D/IronRuby.Libraries.dll \
  "/libraries:IronRuby.Builtins;IronRuby.StandardLibrary.Threading;IronRuby.StandardLibrary.Sockets;IronRuby.StandardLibrary.OpenSsl;IronRuby.StandardLibrary.Digest;IronRuby.StandardLibrary.Zlib;IronRuby.StandardLibrary.StringIO;IronRuby.StandardLibrary.StringScanner;IronRuby.StandardLibrary.Enumerator;IronRuby.StandardLibrary.FunctionControl;IronRuby.StandardLibrary.FileControl;IronRuby.StandardLibrary.BigDecimal;IronRuby.StandardLibrary.Iconv;IronRuby.StandardLibrary.ParseTree;IronRuby.StandardLibrary.Open3;IronRuby.StandardLibrary.Win32API;IronRuby.StandardLibrary.Json" \
  /out:Src/Libraries/Initializers.Generated.cs
echo "regenerated"
