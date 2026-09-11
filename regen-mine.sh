#!/bin/bash
# Same as regen-initializers.sh, but (a) pinned to this worktree's private DLR copy
# and (b) using private temp files - the shared /tmp/Initializers.Generated.*.cs
# paths in the original are a cross-agent hazard.
set -e
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
export DOTNET_ROOT=${DOTNET_ROOT:-/usr/local/dotnet}
DOTNET="$DOTNET_ROOT/dotnet"
DLR=/root/workspace/dlr-agents/hash
GEN=Src/Libraries/Initializers.Generated.cs
TMP=/root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969/.regen-tmp
mkdir -p "$TMP"
LIBS="IronRuby.Builtins;IronRuby.StandardLibrary.Threading;IronRuby.StandardLibrary.Sockets;IronRuby.StandardLibrary.OpenSsl;IronRuby.StandardLibrary.Digest;IronRuby.StandardLibrary.Zlib;IronRuby.StandardLibrary.StringIO;IronRuby.StandardLibrary.StringScanner;IronRuby.StandardLibrary.Enumerator;IronRuby.StandardLibrary.FunctionControl;IronRuby.StandardLibrary.FileControl;IronRuby.StandardLibrary.BigDecimal;IronRuby.StandardLibrary.Iconv;IronRuby.StandardLibrary.ParseTree;IronRuby.StandardLibrary.Open3;IronRuby.StandardLibrary.Win32API;IronRuby.StandardLibrary.Json"

cp "$GEN" "$TMP/Initializers.Generated.bak.cs"
python3 - "$GEN" "$LIBS" <<'PY'
import sys
path, libs = sys.argv[1], sys.argv[2]
out = ['// Skeleton written by regen-initializers.sh; overwritten by the generator below.\r']
names = libs.split(';')
for n in names:
    out.append(f'[assembly: IronRuby.Runtime.RubyLibraryAttribute(typeof({n}.{n.split(".")[-1]}LibraryInitializer))]\r')
out.append('\r')
for n in names:
    cls = n.split('.')[-1] + 'LibraryInitializer'
    out += [f'namespace {n} {{\r',
            f'    public sealed class {cls} : IronRuby.Builtins.LibraryInitializer {{\r',
            '        protected override void LoadModules() {\r',
            '        }\r',
            '    }\r',
            '}\r',
            '\r']
open(path, 'wb').write('\n'.join(out).encode('utf-8'))
PY

if ! $DOTNET build Src/ClassInitGenerator/ClassInitGenerator.csproj -p:DlrSourceDir="$DLR" > "$TMP/build.log" 2>&1; then
  cp "$TMP/Initializers.Generated.bak.cs" "$GEN"
  echo "generator build failed; $GEN restored; see $TMP/build.log" >&2
  tail -20 "$TMP/build.log" >&2
  exit 1
fi

D=Src/ClassInitGenerator/bin/Debug/net8.0
if ! $DOTNET $D/ClassInitGenerator.dll $D/IronRuby.Libraries.dll "/libraries:$LIBS" /out:"$TMP/Initializers.Generated.new.cs" > "$TMP/gen.log" 2>&1; then
  cp "$TMP/Initializers.Generated.bak.cs" "$GEN"
  echo "generator run failed; $GEN restored; see $TMP/gen.log" >&2
  tail -20 "$TMP/gen.log" >&2
  exit 1
fi
# the checked-in file uses CRLF
python3 -c "
d = open('$TMP/Initializers.Generated.new.cs','rb').read().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
assert len(d) > 500000, 'generated file suspiciously small: %d bytes' % len(d)
open('$GEN','wb').write(d)
print('wrote $GEN:', len(d), 'bytes')
"
